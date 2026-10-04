#!/bin/bash
# PSX XMB Manager - WSL2 NBD backend bootstrap.
#
# Run by the application only after the user clicks "Install / Repair WSL Backend":
#     wsl.exe -d DISTRO -u root --exec /bin/bash -s   < this script
# The application replaces the two placeholders below before sending the script.
#
# Modes:
#   full         Ubuntu/Debian only: apt packages, pinned hdl_dump and pfsshell/pfsfuse builds, helper.
#   helper-only  Any distro whose tools are already installed: installs the helper only, never runs apt-get.
#
# Safe to run repeatedly: pinned builds are skipped when the installed binaries already match.
#
# Everything runs inside main(), which is called on the last line with stdin from /dev/null, so bash has read
# the whole script before any command (apt, git, make) could consume the rest of it from stdin.

main() {
    set -Eeuo pipefail
    # One ordered stream: the application shows the last lines of stdout, so errors must not sit in a separate stderr.
    exec 2>&1
    # Name the command that stopped the setup instead of ending silently.
    trap 'echo "PSX_XMB_BOOTSTRAP_FAILED: \"$BASH_COMMAND\" exited with code $? (bootstrap line $LINENO)."' ERR

    local MODE="__PSX_XMB_MODE__"
    local HELPER_B64="__PSX_XMB_HELPER_B64__"

    local HDL_DUMP_REPO="https://github.com/ps2homebrew/hdl-dump.git"
    local HDL_DUMP_COMMIT="32c296c69cf9c263fcbe035004aa28c345b3b279"
    local PFSSHELL_REPO="https://github.com/ps2homebrew/pfsshell.git"
    local PFSSHELL_COMMIT="8c92467b3d715c3698f1f8ce63a8a07e214d6c73"
    local MESON_REPO="https://github.com/mesonbuild/meson.git"
    local MESON_TAG="1.3.2"
    local MESON_COMMIT="614d436232d3a86518164cbe2b8af12db3bde009"
    local SRC_ROOT="/opt/psx-xmb-manager-src"
    local HELPER_DIR="/usr/local/lib/psx-xmb-manager"
    local HELPER_PATH="$HELPER_DIR/psx-xmb-helper.py"
    local STAMP_DIR="$HELPER_DIR/build-stamps"
    local PROTOCOL_VERSION_EXPECTED="__PSX_XMB_PROTOCOL_VERSION__"

    step() { echo "==> $*"; }
    fail() { trap - ERR; echo "PSX_XMB_BOOTSTRAP_FAILED: $*"; exit 1; }

    [ "$(id -u)" = "0" ] || fail "the bootstrap must run as root (wsl.exe -u root)."
    case "$MODE" in
        full|helper-only) ;;
        *) fail "unknown mode '$MODE'." ;;
    esac

    local OS_ID=""
    if [ -r /etc/os-release ]; then
        OS_ID="$(. /etc/os-release && echo "${ID:-}")"
    fi
    step "Distribution: ${OS_ID:-unknown}, mode: $MODE"

    if [ "$MODE" = "full" ]; then
        case "$OS_ID" in
            ubuntu|debian) ;;
            *) fail "automatic setup supports Ubuntu and Debian only (this distribution is '${OS_ID:-unknown}')." ;;
        esac

        # 1. Packages.
        # The plan's list also names 'fuse' (FUSE 2 fusermount). On current Ubuntu/Debian 'fuse3' Breaks/Replaces
        # 'fuse', so apt refuses to install both. fuse3 ships the /bin/fusermount compatibility link that
        # libfuse2 (libfuse-dev, used by pfsfuse) executes, so 'fuse' is left out. Both FUSE generations remain
        # usable: fusermount3 for nbdfuse, fusermount for pfsfuse.
        step "Installing packages"
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y \
            ca-certificates \
            git \
            build-essential \
            pkg-config \
            meson \
            ninja-build \
            python3 \
            libnbd-bin \
            fuse3 \
            libfuse-dev

        if ! command -v fusermount >/dev/null 2>&1; then
            step "Linking fusermount to fusermount3 for libfuse2"
            ln -sf "$(command -v fusermount3)" /usr/local/bin/fusermount
        fi

        # The Windows \\wsl.localhost share must be allowed into user FUSE mounts (pfsfuse -o allow_other).
        if [ -f /etc/fuse.conf ] && grep -Eq '^[[:space:]]*#[[:space:]]*user_allow_other' /etc/fuse.conf; then
            sed -i -E 's/^[[:space:]]*#[[:space:]]*user_allow_other.*/user_allow_other/' /etc/fuse.conf
        elif ! grep -Eq '^[[:space:]]*user_allow_other' /etc/fuse.conf 2>/dev/null; then
            echo "user_allow_other" >> /etc/fuse.conf
        fi

        mkdir -p "$SRC_ROOT" "$STAMP_DIR"

        # 2. hdl_dump, pinned.
        # The plan builds with 'make RELEASE=yes'. That build only accepts block devices on Unix
        # (osal_unix.c: osal_map_device_name rejects regular files unless _DEBUG is defined) and fails on
        # nbdfuse's raw file with "Input or output is unsupported.". The upstream default build
        # (DEBUG=yes: -O2 -g -D_DEBUG) treats files as devices, so it is used instead.
        local HDL_STAMP="$STAMP_DIR/hdl_dump"
        local HDL_STAMP_VALUE="$HDL_DUMP_COMMIT debug-build"
        if [ -x /usr/local/bin/hdl_dump ] && [ "$(cat "$HDL_STAMP" 2>/dev/null)" = "$HDL_STAMP_VALUE" ]; then
            step "hdl_dump $HDL_DUMP_COMMIT already installed"
        else
            step "Building hdl_dump $HDL_DUMP_COMMIT"
            if [ ! -d "$SRC_ROOT/hdl-dump/.git" ]; then
                rm -rf "$SRC_ROOT/hdl-dump"
                git clone "$HDL_DUMP_REPO" "$SRC_ROOT/hdl-dump"
            fi
            cd "$SRC_ROOT/hdl-dump"
            git fetch --all --tags
            git reset --hard
            git clean -fdx
            git -c advice.detachedHead=false checkout -q "$HDL_DUMP_COMMIT"
            make clean || true
            make RELEASE=no DEBUG=yes
            install -m 0755 hdl_dump /usr/local/bin/hdl_dump
            echo "$HDL_STAMP_VALUE" > "$HDL_STAMP"
        fi

        # 3. pfsshell and pfsfuse (FUSE 2, unchanged), pinned.
        local PFS_STAMP="$STAMP_DIR/pfsshell"
        if [ -x /usr/local/bin/pfsshell ] && [ -x /usr/local/bin/pfsfuse ] && [ "$(cat "$PFS_STAMP" 2>/dev/null)" = "$PFSSHELL_COMMIT" ]; then
            step "pfsshell/pfsfuse $PFSSHELL_COMMIT already installed"
        else
            step "Building pfsshell and pfsfuse $PFSSHELL_COMMIT"
            if [ ! -d "$SRC_ROOT/pfsshell/.git" ]; then
                rm -rf "$SRC_ROOT/pfsshell"
                git clone --recursive "$PFSSHELL_REPO" "$SRC_ROOT/pfsshell"
            fi
            cd "$SRC_ROOT/pfsshell"
            git fetch --all --tags
            git reset --hard
            git clean -fdx
            git -c advice.detachedHead=false checkout -q "$PFSSHELL_COMMIT"
            git submodule sync --recursive
            git submodule update --init --recursive
            # pfsshell's subprojects reach into external/ps2sdk through symlinks. Meson 0.59 to 0.61 rejects that
            # ("Sandbox violation: Tried to grab file ... outside current (sub)project"), and Ubuntu 22.04 ships
            # meson 0.61.2. pfsshell is therefore configured with a pinned meson run from its source tree
            # (meson.py needs only python3 and ninja), the release Ubuntu 24.04 ships, on every distribution.
            local MESON_DIR="$SRC_ROOT/meson-$MESON_TAG"
            if [ "$(git -C "$MESON_DIR" rev-parse HEAD 2>/dev/null)" != "$MESON_COMMIT" ]; then
                step "Getting meson $MESON_TAG"
                rm -rf "$MESON_DIR"
                git -c advice.detachedHead=false clone -q --depth 1 --branch "$MESON_TAG" "$MESON_REPO" "$MESON_DIR"
                [ "$(git -C "$MESON_DIR" rev-parse HEAD)" = "$MESON_COMMIT" ] || fail "meson tag $MESON_TAG is not commit $MESON_COMMIT."
            fi
            rm -rf build
            python3 "$MESON_DIR/meson.py" setup build -Denable_pfsfuse=true -Denable_pfs2tar=true
            python3 "$MESON_DIR/meson.py" compile -C build
            install -m 0755 build/pfsshell /usr/local/bin/pfsshell
            install -m 0755 build/pfsfuse /usr/local/bin/pfsfuse
            echo "$PFSSHELL_COMMIT" > "$PFS_STAMP"
        fi
        cd /
    else
        command -v python3 >/dev/null 2>&1 || fail "python3 is required for the helper; install it with the distribution's package manager."
    fi

    # 4. Helper.
    step "Installing psx-xmb-helper"
    mkdir -p "$HELPER_DIR"
    local TMP_HELPER
    TMP_HELPER="$(mktemp)"
    printf '%s' "$HELPER_B64" | base64 -d > "$TMP_HELPER"
    install -m 0755 "$TMP_HELPER" "$HELPER_PATH"
    rm -f "$TMP_HELPER"
    ln -sf "$HELPER_PATH" /usr/local/bin/psx-xmb-helper

    # 5. Verify.
    step "Verifying"
    local missing=""
    for tool in python3 nbdinfo nbdfuse hdl_dump pfsshell pfsfuse fusermount3 fusermount wslpath dd; do
        command -v "$tool" >/dev/null 2>&1 || missing="$missing $tool"
    done
    [ -c /dev/fuse ] || missing="$missing /dev/fuse"
    [ -z "$missing" ] || fail "still missing:$missing"

    # hdl_dump has no version switch; it must start (it prints its usage and exits non-zero).
    /usr/local/bin/hdl_dump >/dev/null 2>&1 || true
    if ldd "$(command -v pfsfuse)" | grep -q "not found"; then
        fail "pfsfuse is missing shared libraries: $(ldd "$(command -v pfsfuse)" | grep 'not found' | tr '\n' ' ')"
    fi
    local installed_version
    installed_version="$(python3 "$HELPER_PATH" --version)"
    [ "$installed_version" = "$PROTOCOL_VERSION_EXPECTED" ] || fail "helper protocol version is '$installed_version', expected '$PROTOCOL_VERSION_EXPECTED'."

    echo "nbdfuse: $(nbdfuse --version 2>/dev/null | head -n 1)"
    echo "nbdinfo: $(nbdinfo --version 2>/dev/null | head -n 1)"
    echo "hdl_dump: $HDL_DUMP_COMMIT"
    echo "pfsshell/pfsfuse: $PFSSHELL_COMMIT"
    echo "helper protocol: $installed_version"
    echo "PSX_XMB_BOOTSTRAP_OK"
}

main "$@" < /dev/null
