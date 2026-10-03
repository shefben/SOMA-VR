#!/usr/bin/env python3
"""psx-xmb-helper: the Linux side of PSX XMB Manager's WSL2 NBD backend.

Usage (always started by the Windows application through wsl.exe):

    python3 psx-xmb-helper.py VERB   < request.json   > response.json

The request is one JSON object on stdin. Exactly one JSON response object is written to
stdout:

    {"ok": true, "code": "OK", "message": "", "stdout": "", "stderr": "", "data": {}}

Progress output of long running tools (hdl_dump, dd) is forwarded to stderr while they run.

Rules this helper follows:
  * tools run through subprocess with argument lists, never through a shell;
  * the remote HDD is reached only through nbdfuse's raw file (no /dev/nbd*, no kernel NBD);
  * one disk operation at a time (flock), state kept under ~/.local/state/psx-xmb-manager;
  * nbdfuse is never killed as normal cleanup; it is unmounted with fusermount3 -u.

Python 3.8 compatible.
"""

import errno
import fcntl
import hashlib
import ipaddress
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time

PROTOCOL_VERSION = 1

HOME = os.path.expanduser("~")
STATE_DIR = os.path.join(HOME, ".local", "state", "psx-xmb-manager")
DATA_DIR = os.path.join(HOME, ".local", "share", "psx-xmb-manager")
NBD_DIR = os.path.join(DATA_DIR, "nbd")
RAW_PATH = os.path.join(NBD_DIR, "nbd")
PFS_ROOT = os.path.join(DATA_DIR, "pfs")
PID_FILE = os.path.join(STATE_DIR, "nbdfuse.pid")
NBD_LOG = os.path.join(STATE_DIR, "nbdfuse.log")
CONNECTION_FILE = os.path.join(STATE_DIR, "connection.json")
PFS_MOUNTS_FILE = os.path.join(STATE_DIR, "pfs-mounts.json")
ACTIVE_OP_FILE = os.path.join(STATE_DIR, "active-op.json")
LOCK_FILE = os.path.join(STATE_DIR, "helper.lock")
HELPER_LOG = os.path.join(STATE_DIR, "helper.log")
WORK_DIR = os.path.join(STATE_DIR, "work")

# Timeouts (seconds), matching the Windows side.
NBDINFO_TIMEOUT = 5
NBDFUSE_READY_TIMEOUT = 10
HDL_TOC_TIMEOUT = 30
UNMOUNT_TIMEOUT = 10
PFS_MOUNT_TIMEOUT = 10
PROCESS_EXIT_TIMEOUT = 10

ALLOWED_HDL_VERBS = ("toc", "hdl_toc", "inject_cd", "inject_dvd", "modify", "modify_header")
READ_ONLY_HDL_VERBS = ("toc", "hdl_toc")
CANCELLABLE_HDL_VERBS = ("toc", "hdl_toc", "inject_cd", "inject_dvd")
READ_ONLY_PFS_VERBS = ("device", "mount", "umount", "ls", "lcd", "cd", "pwd", "get", "help", "exit", "lspart")
HEADER_FILES = ("system.cnf", "icon.sys", "list.ico", "del.ico", "boot.elf", "boot.kelf", "boot.kirx")
MAX_CAPTURE = 8 * 1024 * 1024

_REAL_STDOUT = sys.stdout


class HelperError(Exception):
    def __init__(self, code, message, stderr="", stdout="", data=None):
        Exception.__init__(self, message)
        self.code = code
        self.message = message
        self.stderr = stderr or ""
        self.stdout = stdout or ""
        self.data = data or {}


# --------------------------------------------------------------------------- output / logging

def respond(ok, code="OK", message="", stdout="", stderr="", data=None):
    payload = {
        "ok": bool(ok),
        "code": code,
        "message": message or "",
        "stdout": stdout or "",
        "stderr": stderr or "",
        "data": data or {},
    }
    _REAL_STDOUT.write(json.dumps(payload))
    _REAL_STDOUT.write("\n")
    _REAL_STDOUT.flush()


def log(message):
    try:
        os.makedirs(STATE_DIR, exist_ok=True)
        with open(HELPER_LOG, "a", encoding="utf-8") as handle:
            handle.write("%s [%d] %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), os.getpid(), message))
    except OSError:
        pass


def progress(text):
    try:
        sys.stderr.write(text)
        sys.stderr.flush()
    except (OSError, ValueError):
        pass


# --------------------------------------------------------------------------- small utilities

def which(name):
    found = shutil.which(name)
    if found:
        return found
    for folder in ("/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"):
        candidate = os.path.join(folder, name)
        if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
            return candidate
    return ""


def tool_paths():
    return {name: which(name) for name in
            ("nbdfuse", "nbdinfo", "hdl_dump", "pfsshell", "pfsfuse", "fusermount3", "fusermount", "wslpath", "dd")}


def require_tool(name, code):
    path = which(name)
    if not path:
        raise HelperError(code, "%s is not installed in this WSL distribution. Run Install / Repair WSL Backend." % name)
    return path


def read_json_file(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def write_json_file(path, value):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(value, handle, indent=2)
    os.replace(temp, path)


def remove_file(path):
    try:
        os.remove(path)
    except OSError:
        pass


def unescape_mountinfo(value):
    return re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), value)


def mount_points():
    points = set()
    try:
        with open("/proc/self/mountinfo", "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                fields = line.split(" ")
                if len(fields) > 4:
                    points.add(unescape_mountinfo(fields[4]))
    except OSError:
        pass
    return points


def is_mounted(path):
    return os.path.normpath(path) in mount_points()


def pid_alive(pid):
    if not pid or pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except OSError as error:
        return error.errno == errno.EPERM
    # A zombie still answers kill(0); treat it as gone.
    try:
        with open("/proc/%d/stat" % pid, "r") as handle:
            return handle.read().split(") ")[-1][:1] != "Z"
    except OSError:
        return False


def process_cmdline(pid):
    try:
        with open("/proc/%d/cmdline" % pid, "rb") as handle:
            return [part.decode("utf-8", "replace") for part in handle.read().split(b"\0") if part]
    except OSError:
        return []


def find_processes(executable_name, argument):
    """PIDs whose argv[0] ends with executable_name and whose argv contains argument."""
    found = []
    for entry in os.listdir("/proc"):
        if not entry.isdigit():
            continue
        argv = process_cmdline(int(entry))
        if argv and os.path.basename(argv[0]) == executable_name and argument in argv[1:]:
            found.append(int(entry))
    return found


def wait_for_exit(pids, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if not any(pid_alive(pid) for pid in pids):
            return True
        time.sleep(0.1)
    return not any(pid_alive(pid) for pid in pids)


def read_pid_file():
    try:
        with open(PID_FILE, "r") as handle:
            return int(handle.read().strip() or "0")
    except (OSError, ValueError):
        return 0


def raw_size():
    try:
        return os.stat(RAW_PATH).st_size
    except OSError:
        return -1


def os_release():
    values = {}
    try:
        with open("/etc/os-release", "r", encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if "=" in line and not line.startswith("#"):
                    key, value = line.split("=", 1)
                    values[key] = value.strip().strip('"').strip("'")
    except OSError:
        pass
    return values


def user_allow_other():
    try:
        with open("/etc/fuse.conf", "r", encoding="utf-8") as handle:
            return any(line.strip() == "user_allow_other" for line in handle)
    except OSError:
        return False


def first_line(command, timeout=5):
    try:
        result = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                timeout=timeout)
        text = result.stdout.decode("utf-8", "replace").strip().splitlines()
        return text[0] if text else ""
    except (OSError, subprocess.SubprocessError):
        return ""


def valid_ipv4(text):
    if not isinstance(text, str) or not re.match(r"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", text):
        return False
    try:
        ipaddress.IPv4Address(text)
        return True
    except ValueError:
        return False


def valid_partition_name(name):
    return isinstance(name, str) and 0 < len(name) <= 32 and all(0x20 <= ord(c) <= 0x7E for c in name)


def make_mount_id(partition):
    """Stable, filesystem-safe directory name for a partition (never the raw partition name)."""
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", partition)[:40].lstrip(".") or "partition"
    digest = hashlib.sha1(partition.encode("utf-8")).hexdigest()[:8]
    return "%s-%s" % (safe, digest)


def valid_mount_id(mount_id):
    return isinstance(mount_id, str) and mount_id not in (".", "..") and re.match(r"^[A-Za-z0-9._-]{1,64}$", mount_id) is not None


# --------------------------------------------------------------------------- locking and the active operation

class DiskLock(object):
    """Exclusive, non-blocking lock: a second disk operation gets BACKEND_BUSY instead of waiting."""

    def __init__(self, verb):
        self.verb = verb
        self.handle = None

    def __enter__(self):
        os.makedirs(STATE_DIR, exist_ok=True)
        self.handle = open(LOCK_FILE, "a+")
        try:
            fcntl.flock(self.handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.handle.close()
            active = active_operation()
            raise HelperError("BACKEND_BUSY", "Another PSX HDD operation is running in WSL%s." %
                              (" (%s)" % active["verb"] if active else ""))
        return self

    def __exit__(self, *exc):
        try:
            fcntl.flock(self.handle.fileno(), fcntl.LOCK_UN)
        finally:
            self.handle.close()
        return False


def active_operation():
    entry = read_json_file(ACTIVE_OP_FILE)
    if not entry or not pid_alive(int(entry.get("helper_pid", 0))):
        return None
    return entry


def register_operation(verb, child_pid, cancellable, write):
    write_json_file(ACTIVE_OP_FILE, {"verb": verb, "pid": child_pid, "helper_pid": os.getpid(),
                                     "cancellable": bool(cancellable), "write": bool(write), "started": time.time()})


def clear_operation():
    entry = read_json_file(ACTIVE_OP_FILE)
    if entry and int(entry.get("helper_pid", 0)) == os.getpid():
        remove_file(ACTIVE_OP_FILE)


# --------------------------------------------------------------------------- running tools

class ToolRun(object):
    def __init__(self):
        self.exit_code = -1
        self.stdout = ""
        self.stderr = ""
        self.timed_out = False
        self.cancelled = False


def run_tool(command, cwd=None, stdin_bytes=None, timeout=0, forward_output=False, register_as=None,
             cancellable=False, write=False):
    """Runs one tool with an argument list (no shell). Optionally forwards its output to our stderr as
    progress and registers it as the active operation so the 'cancel' verb can interrupt it."""
    run = ToolRun()
    process = subprocess.Popen(command, cwd=cwd or None, stdin=subprocess.PIPE if stdin_bytes is not None else subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, close_fds=True)
    if register_as:
        register_operation(register_as, process.pid, cancellable, write)

    captured = {"out": bytearray(), "err": bytearray()}

    def pump(stream, key):
        while True:
            chunk = os.read(stream.fileno(), 65536)
            if not chunk:
                break
            buffer = captured[key]
            if len(buffer) < MAX_CAPTURE:
                buffer.extend(chunk[:MAX_CAPTURE - len(buffer)])
            if forward_output:
                progress(chunk.decode("utf-8", "replace"))

    threads = [threading.Thread(target=pump, args=(process.stdout, "out")),
               threading.Thread(target=pump, args=(process.stderr, "err"))]
    for thread in threads:
        thread.daemon = True
        thread.start()

    if stdin_bytes is not None:
        try:
            process.stdin.write(stdin_bytes)
        except (BrokenPipeError, OSError):
            pass
        try:
            process.stdin.close()
        except OSError:
            pass

    deadline = time.time() + timeout if timeout and timeout > 0 else None
    try:
        while True:
            try:
                process.wait(timeout=0.2)
                break
            except subprocess.TimeoutExpired:
                if deadline is not None and time.time() > deadline:
                    run.timed_out = True
                    stop_process(process)
                    break
    finally:
        for thread in threads:
            thread.join(timeout=10)
        if register_as:
            clear_operation()

    run.exit_code = process.returncode if process.returncode is not None else -1
    # SIGINT from the cancel verb: hdl_dump exits 100+RET_INTERRUPTED, dd dies with SIGINT.
    entry_cancel = os.path.exists(ACTIVE_OP_FILE + ".cancelled-%d" % process.pid)
    if entry_cancel:
        run.cancelled = True
        remove_file(ACTIVE_OP_FILE + ".cancelled-%d" % process.pid)
    run.stdout = captured["out"].decode("utf-8", "replace")
    run.stderr = captured["err"].decode("utf-8", "replace")
    return run


def stop_process(process):
    """Graceful first: SIGINT (hdl_dump and dd stop cleanly), then SIGTERM, then SIGKILL."""
    for sig, wait in ((signal.SIGINT, 10), (signal.SIGTERM, 5), (signal.SIGKILL, 5)):
        if process.poll() is not None:
            return
        try:
            process.send_signal(sig)
        except OSError:
            return
        try:
            process.wait(timeout=wait)
            return
        except subprocess.TimeoutExpired:
            continue


def tool_response(run, failure_code, tool_name, extra_data=None):
    data = {"exit_code": run.exit_code}
    if extra_data:
        data.update(extra_data)
    if run.timed_out:
        raise HelperError("OPERATION_TIMEOUT", "%s did not finish in time and was stopped." % tool_name,
                          stderr=run.stderr, stdout=run.stdout, data=data)
    if run.cancelled:
        raise HelperError("OPERATION_CANCELLED", "%s was cancelled." % tool_name,
                          stderr=run.stderr, stdout=run.stdout, data=data)
    if run.exit_code != 0:
        raise HelperError(failure_code, "%s exited with code %d." % (tool_name, run.exit_code),
                          stderr=run.stderr, stdout=run.stdout, data=data)
    return {"stdout": run.stdout, "stderr": run.stderr, "data": data}


def translate_windows_path(windows_path, must_exist):
    if not isinstance(windows_path, str) or not windows_path.strip():
        raise HelperError("PATH_TRANSLATION_FAILED", "No Windows path was given.")
    wslpath = require_tool("wslpath", "PATH_TRANSLATION_FAILED")
    try:
        result = subprocess.run([wslpath, "-a", "-u", windows_path], stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=5)
    except subprocess.TimeoutExpired:
        raise HelperError("PATH_TRANSLATION_FAILED", "wslpath did not answer for '%s'." % windows_path)
    linux_path = result.stdout.decode("utf-8", "replace").strip()
    if result.returncode != 0 or not linux_path.startswith("/"):
        raise HelperError("PATH_TRANSLATION_FAILED", "'%s' could not be translated to a WSL path." % windows_path,
                          stderr=result.stderr.decode("utf-8", "replace"))
    if must_exist and not os.path.exists(linux_path):
        raise HelperError("INPUT_FILE_NOT_VISIBLE_IN_WSL",
                          "'%s' is not visible inside WSL as '%s'." % (windows_path, linux_path))
    return linux_path


def resolve_cwd(cwd):
    if cwd:
        if not isinstance(cwd, str) or not cwd.startswith("/") or not os.path.isdir(cwd):
            raise HelperError("PATH_TRANSLATION_FAILED", "The working directory '%s' does not exist in WSL." % cwd)
        return cwd
    os.makedirs(WORK_DIR, exist_ok=True)
    return WORK_DIR


# --------------------------------------------------------------------------- connection state

def pfs_mount_records():
    records = read_json_file(PFS_MOUNTS_FILE)
    return records if isinstance(records, list) else []


def save_pfs_mount_records(records):
    if records:
        write_json_file(PFS_MOUNTS_FILE, records)
    else:
        remove_file(PFS_MOUNTS_FILE)


def active_pfs_mounts():
    points = mount_points()
    return [record for record in pfs_mount_records()
            if valid_mount_id(record.get("mount_id")) and os.path.join(PFS_ROOT, record["mount_id"]) in points]


def mounted_partition_names():
    return set(record.get("partition") for record in active_pfs_mounts())


def compute_status():
    connection = read_json_file(CONNECTION_FILE)
    mounted = is_mounted(NBD_DIR)
    pid = read_pid_file()
    alive = pid_alive(pid)
    size = raw_size() if mounted else -1
    pfs = active_pfs_mounts()

    if not mounted:
        state = "disconnected" if connection is None and not os.path.exists(PID_FILE) and not pfs else "stale-state-file"
    elif connection is None:
        state = "mounted-without-state"
    elif not alive:
        state = "nbd-process-gone"
    elif size <= 0:
        state = "raw-file-missing"
    else:
        state = "healthy"

    active = active_operation()
    return {
        "state": state,
        "nbd_mounted": mounted,
        "nbdfuse_pid": pid if alive else 0,
        "raw_path": RAW_PATH,
        "nbd_dir": NBD_DIR,
        "raw_size": max(size, 0),
        "ip": (connection or {}).get("ip", ""),
        "port": (connection or {}).get("port", 0),
        "pfs_mounts": pfs,
        "pfs_child_mounts_active": bool(pfs),
        "active_operation": {"verb": active.get("verb"), "write": active.get("write")} if active else None,
    }


def require_healthy_connection():
    status = compute_status()
    if status["state"] == "disconnected":
        raise HelperError("NOT_CONNECTED", "The PSX is not connected.")
    if status["state"] != "healthy":
        raise HelperError("STALE_MOUNT_STATE", "The NBD connection is not healthy (%s)." % status["state"],
                          data={"state": status["state"]})
    return status


def nbd_log_tail(lines=20):
    try:
        with open(NBD_LOG, "r", encoding="utf-8", errors="replace") as handle:
            return "".join(handle.readlines()[-lines:])
    except OSError:
        return ""


def fuse_unmount(path, fusermount, timeout=UNMOUNT_TIMEOUT, lazy=False):
    """Repeats 'fusermount -u' until the path is no longer mounted (a busy mount can take a moment)."""
    deadline = time.time() + timeout
    last_error = ""
    while is_mounted(path):
        args = [fusermount, "-u"] + (["-z"] if lazy else []) + [path]
        result = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        last_error = (result.stdout + result.stderr).decode("utf-8", "replace")
        if not is_mounted(path):
            break
        if time.time() > deadline:
            return False, last_error
        time.sleep(0.5)
    return True, last_error


def unmount_nbd(lazy=False):
    fusermount3 = require_tool("fusermount3", "FUSE_UNAVAILABLE")
    pid = read_pid_file()
    ok, error = fuse_unmount(NBD_DIR, fusermount3, lazy=lazy)
    if ok and pid:
        wait_for_exit([pid], PROCESS_EXIT_TIMEOUT)
    return ok, error


def unmount_pfs_record(record, lazy=False):
    mount_id = record.get("mount_id")
    if not valid_mount_id(mount_id):
        return True, ""
    path = os.path.join(PFS_ROOT, mount_id)
    fusermount = which("fusermount")
    if not fusermount:
        raise HelperError("BACKEND_SETUP_REQUIRED",
                          "fusermount (FUSE 2) is not installed, so pfsfuse mounts cannot be unmounted. Run Install / Repair WSL Backend.")
    daemons = find_processes("pfsfuse", path)
    ok, error = fuse_unmount(path, fusermount, lazy=lazy)
    if not ok:
        return False, error
    # pfsfuse flushes the PFS filesystem while it exits; nothing may touch the HDD before that is done.
    if daemons and not wait_for_exit(daemons, PROCESS_EXIT_TIMEOUT):
        return False, "pfsfuse did not exit after unmounting %s" % path
    try:
        os.rmdir(path)
    except OSError:
        pass
    return True, error


def cleanup_failed_nbd_mount():
    if is_mounted(NBD_DIR):
        ok, _ = unmount_nbd()
        if not ok:
            unmount_nbd(lazy=True)
    remove_file(PID_FILE)


# --------------------------------------------------------------------------- verbs

def verb_probe(request):
    tools = tool_paths()
    release = os_release()
    data = {
        "protocol_version": PROTOCOL_VERSION,
        "user": os.environ.get("USER") or first_line(["id", "-un"]),
        "uid": os.getuid(),
        "home": HOME,
        "os_id": release.get("ID", ""),
        "os_version": release.get("VERSION_ID", ""),
        "os_name": release.get("PRETTY_NAME", ""),
        "dev_fuse": os.path.exists("/dev/fuse"),
        "user_allow_other": user_allow_other(),
        "tools": tools,
        "nbdfuse_version": first_line([tools["nbdfuse"], "--version"]) if tools["nbdfuse"] else "",
        "nbdinfo_version": first_line([tools["nbdinfo"], "--version"]) if tools["nbdinfo"] else "",
        "status": compute_status(),
    }
    return {"data": data}


def verb_status(request):
    return {"data": compute_status()}


def verb_connect(request):
    ip = request.get("ip")
    port = request.get("port", 10809)
    if not valid_ipv4(ip):
        raise HelperError("INVALID_IP", "'%s' is not a valid IPv4 address." % ip)
    if not isinstance(port, int) or isinstance(port, bool) or not 1 <= port <= 65535:
        raise HelperError("INVALID_PORT", "Port %r is not between 1 and 65535." % (port,))
    if not os.path.exists("/dev/fuse"):
        raise HelperError("FUSE_UNAVAILABLE", "/dev/fuse is not available in this WSL distribution.")
    nbdinfo = require_tool("nbdinfo", "NBDINFO_MISSING")
    nbdfuse = require_tool("nbdfuse", "NBDFUSE_MISSING")
    hdl_dump = require_tool("hdl_dump", "HDL_DUMP_MISSING")
    require_tool("fusermount3", "FUSE_UNAVAILABLE")
    uri = "nbd://%s:%d" % (ip, port)

    with DiskLock("connect"):
        status = compute_status()
        if status["nbd_mounted"]:
            if status["ip"] == ip and status["port"] == port and status["state"] == "healthy":
                return {"data": {"raw_path": RAW_PATH, "nbd_dir": NBD_DIR, "size": status["raw_size"], "ip": ip,
                                 "port": port, "home": HOME, "reused": True}}
            if status["ip"] and (status["ip"] != ip or status["port"] != port):
                raise HelperError("NBD_ALREADY_CONNECTED_OTHER_ENDPOINT",
                                  "Already connected to nbd://%s:%s. Disconnect first." % (status["ip"], status["port"]))
            raise HelperError("STALE_MOUNT_STATE",
                              "An old NBD mount (%s) is still present. Use Recover WSL Connection first." % status["state"])

        # Not mounted: stale pid/state files may be removed.
        if status["pfs_mounts"]:
            raise HelperError("STALE_MOUNT_STATE", "PFS partitions are still mounted from an old connection. Use Recover WSL Connection first.")
        remove_file(PID_FILE)
        remove_file(CONNECTION_FILE)
        save_pfs_mount_records([])

        # 1. Is the OPL NBD server there at all?
        try:
            probe = subprocess.run([nbdinfo, uri], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, timeout=NBDINFO_TIMEOUT)
            if probe.returncode != 0:
                raise HelperError("NBD_SERVER_UNREACHABLE", "The NBD server at %s did not answer." % uri,
                                  stderr=probe.stderr.decode("utf-8", "replace"))
        except subprocess.TimeoutExpired:
            raise HelperError("NBD_SERVER_UNREACHABLE", "The NBD server at %s did not answer within %d seconds." % (uri, NBDINFO_TIMEOUT))

        # 2. nbdfuse in the background, own session, log to the state directory.
        os.makedirs(NBD_DIR, exist_ok=True)
        os.makedirs(STATE_DIR, exist_ok=True)
        with open(NBD_LOG, "ab") as log_handle:
            log_handle.write(("\n--- %s connect %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), uri)).encode("utf-8"))
            log_handle.flush()
            try:
                process = subprocess.Popen([nbdfuse, "-P", PID_FILE, NBD_DIR, uri], stdin=subprocess.DEVNULL,
                                           stdout=log_handle, stderr=log_handle, close_fds=True, start_new_session=True)
            except OSError as error:
                raise HelperError("NBDFUSE_START_FAILED", "nbdfuse could not be started: %s" % error)

        # 3. Readiness: pid file, mountpoint mounted, raw file present with a size. No fixed sleep.
        deadline = time.time() + NBDFUSE_READY_TIMEOUT
        ready = False
        while time.time() < deadline:
            if process.poll() is not None:
                cleanup_failed_nbd_mount()
                raise HelperError("NBDFUSE_START_FAILED", "nbdfuse exited with code %d while connecting to %s." % (process.returncode, uri),
                                  stderr=nbd_log_tail())
            if os.path.exists(PID_FILE) and is_mounted(NBD_DIR) and raw_size() > 0:
                ready = True
                break
            time.sleep(0.1)
        if not ready:
            cleanup_failed_nbd_mount()
            raise HelperError("NBD_MOUNT_NOT_READY", "nbdfuse did not expose %s within %d seconds." % (RAW_PATH, NBDFUSE_READY_TIMEOUT),
                              stderr=nbd_log_tail())

        # 4. The export must be a PS2/PSX HDD.
        validation = run_tool([hdl_dump, "toc", RAW_PATH], cwd=resolve_cwd(""), timeout=HDL_TOC_TIMEOUT)
        toc_ok = validation.exit_code == 0 and not validation.timed_out and \
            any(line.startswith("Total") for line in validation.stdout.splitlines())
        if not toc_ok:
            cleanup_failed_nbd_mount()
            raise HelperError("PSX_HDD_VALIDATION_FAILED", "hdl_dump did not recognise a PS2/PSX HDD at %s." % uri,
                              stderr=validation.stderr, stdout=validation.stdout)

        # 5. Only now record the connection.
        size = raw_size()
        write_json_file(CONNECTION_FILE, {"ip": ip, "port": port, "uri": uri, "raw_path": RAW_PATH, "nbd_dir": NBD_DIR,
                                          "nbdfuse_pid": read_pid_file(), "size": size, "connected_at": time.time()})
        return {"stdout": validation.stdout,
                "data": {"raw_path": RAW_PATH, "nbd_dir": NBD_DIR, "size": size, "ip": ip, "port": port,
                         "home": HOME, "reused": False}}


def verb_disconnect(request):
    with DiskLock("disconnect"):
        active = active_operation()
        if active and active.get("write"):
            raise HelperError("BACKEND_BUSY", "A write operation (%s) is still running." % active.get("verb"))

        unmounted = []
        for record in pfs_mount_records():
            ok, error = unmount_pfs_record(record)
            if not ok:
                raise HelperError("PFS_UNMOUNT_FAILED",
                                  "The partition %s could not be unmounted; close any program using it." % record.get("partition"),
                                  stderr=error)
            unmounted.append(record.get("mount_id"))
            save_pfs_mount_records([r for r in pfs_mount_records() if r.get("mount_id") != record.get("mount_id")])

        os.sync()

        if is_mounted(NBD_DIR):
            ok, error = unmount_nbd()
            if not ok:
                raise HelperError("DISCONNECT_FAILED", "The NBD mount at %s is busy and was not unmounted." % NBD_DIR, stderr=error)

        if is_mounted(NBD_DIR):
            raise HelperError("DISCONNECT_FAILED", "The NBD mount at %s is still mounted." % NBD_DIR)
        remove_file(PID_FILE)
        remove_file(CONNECTION_FILE)
        save_pfs_mount_records([])
        return {"data": {"unmounted_pfs": unmounted}}


def verb_recover(request):
    """Forced recovery for stale or broken mounts. Never the normal disconnect route."""
    actions = []
    with DiskLock("recover"):
        records = pfs_mount_records()
        known = set(os.path.join(PFS_ROOT, r.get("mount_id", "")) for r in records if valid_mount_id(r.get("mount_id")))
        for point in mount_points():
            if point.startswith(PFS_ROOT + os.sep) and point not in known:
                records.append({"mount_id": os.path.basename(point)})
        for record in records:
            try:
                ok, _ = unmount_pfs_record(record)
                if not ok:
                    unmount_pfs_record(record, lazy=True)
                actions.append("unmounted pfs %s" % record.get("mount_id"))
            except HelperError as error:
                actions.append("pfs %s: %s" % (record.get("mount_id"), error.message))
        os.sync()
        pid = read_pid_file()
        if is_mounted(NBD_DIR):
            ok, _ = unmount_nbd()
            if not ok:
                unmount_nbd(lazy=True)
                actions.append("lazily unmounted nbd")
            else:
                actions.append("unmounted nbd")
        if pid and pid_alive(pid) and not wait_for_exit([pid], 5):
            argv = process_cmdline(pid)
            if argv and os.path.basename(argv[0]) == "nbdfuse":
                os.kill(pid, signal.SIGTERM)
                wait_for_exit([pid], 5)
                actions.append("sent SIGTERM to hung nbdfuse %d" % pid)
        remove_file(PID_FILE)
        remove_file(CONNECTION_FILE)
        remove_file(ACTIVE_OP_FILE)
        save_pfs_mount_records([])
        if is_mounted(NBD_DIR):
            raise HelperError("STALE_MOUNT_STATE", "The NBD mount could not be removed. Run 'wsl --shutdown' and try again.",
                              data={"actions": actions})
        return {"data": {"actions": actions}}


def verb_translate_path(request):
    linux_path = translate_windows_path(request.get("windows_path"), bool(request.get("must_exist", False)))
    return {"data": {"linux_path": linux_path}}


def stage_header_files(cwd):
    """hdl_dump modify_header opens ./system.cnf, ./icon.sys, ... in lower case. Windows-created folders are
    case-insensitive under /mnt, but a case-sensitive folder needs lower-case links in a staging directory."""
    try:
        entries = os.listdir(cwd)
    except OSError:
        return None
    lower = {}
    for entry in entries:
        lower.setdefault(entry.lower(), entry)
    needs_staging = any(name in lower and not os.path.exists(os.path.join(cwd, name)) for name in HEADER_FILES)
    if not needs_staging:
        return None
    os.makedirs(STATE_DIR, exist_ok=True)
    staging = tempfile.mkdtemp(prefix="header-", dir=STATE_DIR)
    for name in HEADER_FILES:
        if name in lower:
            os.symlink(os.path.join(cwd, lower[name]), os.path.join(staging, name))
    return staging


def verb_hdl(request):
    args = request.get("args")
    if not isinstance(args, list) or not args or not all(isinstance(a, str) for a in args):
        raise HelperError("INVALID_REQUEST", "'args' must be a non-empty list of strings.")
    verb = args[0]
    if verb not in ALLOWED_HDL_VERBS:
        raise HelperError("INVALID_REQUEST", "hdl_dump command '%s' is not allowed." % verb)
    if len(args) < 2 or args[1] != RAW_PATH:
        raise HelperError("INVALID_REQUEST", "hdl_dump must target the connected NBD device %s." % RAW_PATH)
    hdl_dump = require_tool("hdl_dump", "HDL_DUMP_MISSING")
    timeout = request.get("timeout", 0)
    timeout = timeout if isinstance(timeout, int) and timeout > 0 else 0
    write = verb not in READ_ONLY_HDL_VERBS

    with DiskLock("hdl " + verb):
        require_healthy_connection()
        if write and len(args) > 2 and args[2] in mounted_partition_names():
            raise HelperError("BACKEND_BUSY", "The partition '%s' is mounted with pfsfuse. Close it before changing it." % args[2])
        cwd = resolve_cwd(request.get("cwd", ""))
        staging = stage_header_files(cwd) if verb == "modify_header" else None
        try:
            run = run_tool([hdl_dump] + args, cwd=staging or cwd, timeout=timeout,
                           forward_output=verb in ("inject_cd", "inject_dvd"),
                           register_as="hdl " + verb, cancellable=verb in CANCELLABLE_HDL_VERBS, write=write)
        finally:
            if staging:
                shutil.rmtree(staging, ignore_errors=True)
        return tool_response(run, "HDL_DUMP_FAILED", "hdl_dump " + verb)


def verb_pfsshell(request):
    commands = request.get("commands")
    if not isinstance(commands, list) or not commands or not all(isinstance(c, str) for c in commands):
        raise HelperError("INVALID_REQUEST", "'commands' must be a non-empty list of strings.")
    if any(("\n" in c) or ("\r" in c) or ("\0" in c) for c in commands):
        raise HelperError("INVALID_REQUEST", "A pfsshell command contains a line break.")
    for command in commands:
        parts = command.strip().split(" ", 1)
        if parts[0] == "device" and (len(parts) < 2 or parts[1].strip() != RAW_PATH):
            raise HelperError("INVALID_REQUEST", "pfsshell must use the connected NBD device %s." % RAW_PATH)
    pfsshell = require_tool("pfsshell", "PFSSHELL_MISSING")
    timeout = request.get("timeout", 0)
    timeout = timeout if isinstance(timeout, int) and timeout > 0 else 0
    write = any(c.strip().split(" ", 1)[0] not in READ_ONLY_PFS_VERBS for c in commands if c.strip())

    with DiskLock("pfsshell"):
        require_healthy_connection()
        if write:
            mounted = mounted_partition_names()
            for command in commands:
                parts = command.strip().split(" ", 1)
                if parts[0] in ("mount", "rmpart") and len(parts) > 1 and parts[1].strip() in mounted:
                    raise HelperError("BACKEND_BUSY", "The partition '%s' is mounted with pfsfuse. Close it before changing it." % parts[1].strip())
        cwd = resolve_cwd(request.get("cwd", ""))
        script = ("\n".join(commands) + "\n").encode("utf-8")
        run = run_tool([pfsshell], cwd=cwd, stdin_bytes=script, timeout=timeout, register_as="pfsshell",
                       cancellable=False, write=write)
        return tool_response(run, "PFSSHELL_FAILED", "pfsshell")


def verb_mount_pfs(request):
    partition = request.get("partition")
    display_name = request.get("display_name") or partition
    if not valid_partition_name(partition):
        raise HelperError("INVALID_PARTITION_NAME", "'%s' is not a valid PS2 partition name." % partition)
    pfsfuse = require_tool("pfsfuse", "PFSFUSE_MISSING")
    if not which("fusermount"):
        raise HelperError("BACKEND_SETUP_REQUIRED", "fusermount (FUSE 2) is missing. Run Install / Repair WSL Backend.")

    with DiskLock("mount-pfs"):
        require_healthy_connection()
        mount_id = make_mount_id(partition)
        path = os.path.join(PFS_ROOT, mount_id)
        records = [r for r in pfs_mount_records() if r.get("mount_id") != mount_id]
        if is_mounted(path):
            record = {"mount_id": mount_id, "partition": partition, "display_name": display_name, "path": path}
            save_pfs_mount_records(records + [record])
            return {"data": dict(record, home=HOME, reused=True)}

        os.makedirs(path, exist_ok=True)
        if os.listdir(path):
            raise HelperError("PFS_MOUNT_FAILED", "The mount directory %s is not empty." % path)

        command = [pfsfuse, "--partition=" + partition, RAW_PATH, path]
        if user_allow_other():
            # The Windows \\wsl.localhost share must be allowed into the FUSE mount.
            command += ["-o", "allow_other"]
        # pfsfuse daemonizes once mounted; its output goes to a file so no pipe is held open by the daemon.
        with tempfile.TemporaryFile() as output:
            try:
                result = subprocess.run(command, cwd=resolve_cwd(""), stdin=subprocess.DEVNULL, stdout=output,
                                        stderr=output, timeout=PFS_MOUNT_TIMEOUT)
                exit_code = result.returncode
            except subprocess.TimeoutExpired:
                exit_code = -1
            output.seek(0)
            text = output.read().decode("utf-8", "replace")

        deadline = time.time() + PFS_MOUNT_TIMEOUT
        while not is_mounted(path) and time.time() < deadline:
            time.sleep(0.1)
        if not is_mounted(path):
            try:
                os.rmdir(path)
            except OSError:
                pass
            raise HelperError("PFS_MOUNT_FAILED", "pfsfuse could not mount the partition '%s' (exit code %d)." % (partition, exit_code),
                              stderr=text)

        record = {"mount_id": mount_id, "partition": partition, "display_name": display_name, "path": path,
                  "mounted_at": time.time()}
        save_pfs_mount_records(records + [record])
        return {"stderr": text, "data": dict(record, home=HOME, reused=False)}


def verb_unmount_pfs(request):
    mount_id = request.get("mount_id")
    if not valid_mount_id(mount_id):
        raise HelperError("INVALID_REQUEST", "Invalid mount id.")
    with DiskLock("unmount-pfs"):
        records = pfs_mount_records()
        record = next((r for r in records if r.get("mount_id") == mount_id), {"mount_id": mount_id})
        ok, error = unmount_pfs_record(record)
        if not ok:
            raise HelperError("PFS_UNMOUNT_FAILED",
                              "The partition %s is still in use and was not unmounted. Close any window or program using it." %
                              record.get("partition", mount_id), stderr=error)
        save_pfs_mount_records([r for r in records if r.get("mount_id") != mount_id])
        return {"data": {"mount_id": mount_id}}


def verb_raw_size(request):
    status = require_healthy_connection()
    return {"data": {"size": status["raw_size"], "raw_path": RAW_PATH}}


def check_block_size(request):
    block_size = request.get("block_size", "1M")
    if block_size not in ("1M", "4M"):
        raise HelperError("INVALID_REQUEST", "Block size must be 1M or 4M.")
    return block_size


def verb_backup(request):
    destination = request.get("destination")
    block_size = check_block_size(request)
    if not isinstance(destination, str) or not destination.startswith("/"):
        raise HelperError("INVALID_REQUEST", "The backup destination must be an absolute Linux path.")
    if os.path.realpath(destination).startswith(os.path.realpath(DATA_DIR) + os.sep):
        raise HelperError("INVALID_REQUEST", "The backup cannot be written inside the PSX XMB Manager mounts.")
    if not os.path.isdir(os.path.dirname(destination)):
        raise HelperError("PATH_TRANSLATION_FAILED", "The backup folder %s does not exist." % os.path.dirname(destination))
    dd = require_tool("dd", "BACKUP_FAILED")

    with DiskLock("backup"):
        status = require_healthy_connection()
        run = run_tool([dd, "if=" + RAW_PATH, "of=" + destination, "bs=" + block_size, "status=progress", "conv=fsync"],
                       cwd=resolve_cwd(""), forward_output=True, register_as="backup", cancellable=True, write=False)
        if run.cancelled:
            raise HelperError("OPERATION_CANCELLED", "The backup was cancelled; %s may be incomplete." % destination,
                              stderr=run.stderr, data={"exit_code": run.exit_code})
        written = os.stat(destination).st_size if os.path.exists(destination) else 0
        result = tool_response(run, "BACKUP_FAILED", "dd", {"bytes": written, "expected": status["raw_size"]})
        if written != status["raw_size"]:
            raise HelperError("BACKUP_FAILED", "The backup has %d bytes but the HDD has %d bytes." % (written, status["raw_size"]),
                              stderr=run.stderr, data=result["data"])
        return result


def verb_restore(request):
    source = request.get("source")
    block_size = check_block_size(request)
    if request.get("confirmed") is not True:
        raise HelperError("INVALID_REQUEST", "A restore must be confirmed by the user first.")
    if not isinstance(source, str) or not os.path.isfile(source):
        raise HelperError("INPUT_FILE_NOT_VISIBLE_IN_WSL", "The backup file %s does not exist." % source)
    dd = require_tool("dd", "RESTORE_FAILED")
    hdl_dump = require_tool("hdl_dump", "HDL_DUMP_MISSING")

    with DiskLock("restore"):
        status = require_healthy_connection()
        if status["pfs_mounts"]:
            raise HelperError("BACKEND_BUSY", "Close all mounted PSX partitions before restoring.")
        source_size = os.stat(source).st_size
        target_size = status["raw_size"]
        sizes = {"source_size": source_size, "target_size": target_size}
        if source_size != target_size:
            raise HelperError("SIZE_MISMATCH", "The backup has %d bytes but the PSX HDD has %d bytes. Nothing was written." %
                              (source_size, target_size), data=sizes)
        # notrunc: the target is nbdfuse's fixed-size file; fsync makes dd flush through to the PSX.
        run = run_tool([dd, "if=" + source, "of=" + RAW_PATH, "bs=" + block_size, "conv=notrunc,fsync", "status=progress"],
                       cwd=resolve_cwd(""), forward_output=True, register_as="restore", cancellable=False, write=True)
        os.sync()
        result = tool_response(run, "RESTORE_FAILED", "dd", sizes)
        # Revalidate the HDD table after writing.
        toc = run_tool([hdl_dump, "toc", RAW_PATH], cwd=resolve_cwd(""), timeout=HDL_TOC_TIMEOUT)
        result["data"]["toc_ok"] = toc.exit_code == 0 and any(l.startswith("Total") for l in toc.stdout.splitlines())
        result["data"]["toc"] = toc.stdout
        return result


def verb_cancel(request):
    active = active_operation()
    if not active:
        return {"data": {"cancelled": False}, "message": "No operation is running."}
    if not active.get("cancellable"):
        return {"data": {"cancelled": False, "verb": active.get("verb")},
                "message": "%s cannot be cancelled once it has started." % active.get("verb")}
    pid = int(active.get("pid", 0))
    if not pid_alive(pid):
        return {"data": {"cancelled": False, "verb": active.get("verb")}}
    with open(ACTIVE_OP_FILE + ".cancelled-%d" % pid, "w") as marker:
        marker.write("1")
    os.kill(pid, signal.SIGINT)
    return {"data": {"cancelled": True, "verb": active.get("verb")}}


def verb_keepalive(request):
    """Keeps this WSL distribution (and nbdfuse) running while Windows holds our stdin open."""
    try:
        while sys.stdin.buffer.read(1):
            pass
    except (OSError, ValueError):
        pass
    return {"data": {}}


VERBS = {
    "probe": verb_probe,
    "status": verb_status,
    "connect": verb_connect,
    "disconnect": verb_disconnect,
    "recover": verb_recover,
    "translate-path": verb_translate_path,
    "hdl": verb_hdl,
    "pfsshell": verb_pfsshell,
    "mount-pfs": verb_mount_pfs,
    "unmount-pfs": verb_unmount_pfs,
    "raw-size": verb_raw_size,
    "backup": verb_backup,
    "restore": verb_restore,
    "cancel": verb_cancel,
    "keepalive": verb_keepalive,
}


def main(argv):
    # Anything a library prints by accident must not corrupt the single JSON response.
    sys.stdout = sys.stderr
    if len(argv) == 2 and argv[1] in ("--version", "version"):
        _REAL_STDOUT.write("%d\n" % PROTOCOL_VERSION)
        return 0
    if len(argv) != 2 or argv[1] not in VERBS:
        respond(False, "INVALID_REQUEST", "Usage: psx-xmb-helper.py {%s}" % "|".join(sorted(VERBS)))
        return 2
    verb = argv[1]

    request = {}
    if verb != "keepalive":
        try:
            text = sys.stdin.read()
            request = json.loads(text) if text.strip() else {}
            if not isinstance(request, dict):
                raise ValueError("request is not an object")
        except ValueError as error:
            respond(False, "INVALID_REQUEST", "The request is not a JSON object: %s" % error)
            return 2

    started = time.time()
    try:
        result = VERBS[verb](request) or {}
        respond(True, "OK", result.get("message", ""), result.get("stdout", ""), result.get("stderr", ""), result.get("data", {}))
        if verb not in ("probe", "status", "keepalive", "translate-path"):
            log("%s ok %.1fs" % (verb, time.time() - started))
        return 0
    except HelperError as error:
        log("%s %s %s" % (verb, error.code, error.message))
        respond(False, error.code, error.message, error.stdout, error.stderr, error.data)
        return 1
    except Exception as error:  # Never leave Windows without a JSON answer.
        log("%s INTERNAL %r" % (verb, error))
        respond(False, "HELPER_PROTOCOL_ERROR", "Unexpected helper error in '%s': %s" % (verb, error))
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
