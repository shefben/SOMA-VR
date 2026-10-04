#!/bin/bash
# Manual alternative to the "Install / Repair WSL Backend" button: installs the same Linux tools and helper.
# Run from the PSX XMB Manager folder in PowerShell:
#     wsl -d Ubuntu-24.04 -u root -- bash ./WSL/install-wsl-backend.sh
# It fills in bootstrap-wsl.sh exactly like the application does and runs it as root.
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
HELPER="$DIR/psx-xmb-helper.py"
BOOTSTRAP="$DIR/bootstrap-wsl.sh"
[ "$(id -u)" = "0" ] || { echo "Run this as root: wsl -d <distro> -u root -- bash ./WSL/install-wsl-backend.sh" >&2; exit 1; }
[ -f "$HELPER" ] && [ -f "$BOOTSTRAP" ] || { echo "psx-xmb-helper.py and bootstrap-wsl.sh must be next to this script." >&2; exit 1; }

VERSION="$(tr -d '\r' < "$HELPER" | sed -n 's/^PROTOCOL_VERSION = \([0-9][0-9]*\)$/\1/p')"
[ -n "$VERSION" ] || { echo "Could not read PROTOCOL_VERSION from $HELPER." >&2; exit 1; }
B64="$(tr -d '\r' < "$HELPER" | base64 -w0)"

tr -d '\r' < "$BOOTSTRAP" \
    | sed -e "s|__PSX_XMB_MODE__|full|" -e "s|__PSX_XMB_PROTOCOL_VERSION__|$VERSION|" -e "s|__PSX_XMB_HELPER_B64__|$B64|" \
    | bash -s
