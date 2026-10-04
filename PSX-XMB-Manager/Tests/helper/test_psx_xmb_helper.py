"""Unit tests for the WSL helper (PSX XMB Manager/WSL/psx-xmb-helper.py).

They run on any Linux with Python 3.8+ and need no PSX, NBD server, FUSE or root:

    python3 -m unittest discover -s Tests/helper -v

The helper keeps its state under $HOME, so every test points HOME at a temporary folder.
"""

import importlib.util
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest

HELPER = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                       "..", "..", "PSX XMB Manager", "WSL", "psx-xmb-helper.py"))

_HOME = tempfile.mkdtemp(prefix="psx-helper-home-")
_ORIGINAL_HOME = os.environ.get("HOME")


def setUpModule():
    global helper
    # HOME is read when the module is imported, so it has to be set first.
    os.environ["HOME"] = _HOME
    spec = importlib.util.spec_from_file_location("psx_xmb_helper", HELPER)
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)


def tearDownModule():
    if _ORIGINAL_HOME is None:
        os.environ.pop("HOME", None)
    else:
        os.environ["HOME"] = _ORIGINAL_HOME
    shutil.rmtree(_HOME, ignore_errors=True)


class ValidationTests(unittest.TestCase):

    def test_valid_ipv4(self):
        for text in ("192.168.1.50", "10.0.0.2", "0.0.0.0"):
            self.assertTrue(helper.valid_ipv4(text), text)
        for text in ("256.1.1.1", "1.2.3", "::1", "psx.local", "", " 1.2.3.4", "1.2.3.4\n", None, 1234):
            self.assertFalse(helper.valid_ipv4(text), repr(text))

    def test_valid_partition_name(self):
        self.assertTrue(helper.valid_partition_name("PP.SLUS-12345..GAME TITLE"))
        self.assertTrue(helper.valid_partition_name("A" * 32))
        for name in ("", "A" * 33, "PP.A\nrmpart __system", "PP.A\0", "PP.é", None, 5):
            self.assertFalse(helper.valid_partition_name(name), repr(name))

    def test_mount_ids_are_stable_safe_and_distinct(self):
        first = helper.make_mount_id("PP.SLUS-12345..MY GAME")
        self.assertEqual(first, helper.make_mount_id("PP.SLUS-12345..MY GAME"))
        self.assertTrue(first.startswith("PP.SLUS-12345..MY_GAME-"))
        self.assertTrue(helper.valid_mount_id(first))
        # Names that collapse to the same safe text still get different directories.
        self.assertNotEqual(helper.make_mount_id("PP.A B"), helper.make_mount_id("PP.A/B"))
        for hostile in ("../../etc", "...", "/", "a" * 32):
            mount_id = helper.make_mount_id(hostile)
            self.assertTrue(helper.valid_mount_id(mount_id), mount_id)
            self.assertNotIn("/", mount_id)
            self.assertFalse(mount_id.startswith("."), mount_id)
        self.assertTrue(helper.make_mount_id("...").startswith("partition-"))

    def test_valid_mount_id_rejects_traversal(self):
        for mount_id in (".", "..", "a/b", "a b", "", "x" * 65, None):
            self.assertFalse(helper.valid_mount_id(mount_id), repr(mount_id))

    def test_unescape_mountinfo(self):
        self.assertEqual("/home/me/My Games/x\ty", helper.unescape_mountinfo("/home/me/My\\040Games/x\\011y"))
        self.assertEqual("/plain", helper.unescape_mountinfo("/plain"))


class StageHeaderFilesTests(unittest.TestCase):

    def setUp(self):
        self.folder = tempfile.mkdtemp(prefix="psx-header-")

    def tearDown(self):
        shutil.rmtree(self.folder, ignore_errors=True)

    def touch(self, name):
        with open(os.path.join(self.folder, name), "w") as handle:
            handle.write(name)

    def test_lower_case_files_need_no_staging(self):
        self.touch("system.cnf")
        self.touch("icon.sys")
        self.assertIsNone(helper.stage_header_files(self.folder))

    def test_upper_case_files_are_linked_in_lower_case(self):
        self.touch("SYSTEM.CNF")
        self.touch("Icon.Sys")
        self.touch("BOOT.KELF")
        self.touch("unrelated.txt")
        staging = helper.stage_header_files(self.folder)
        try:
            self.assertIsNotNone(staging)
            self.assertTrue(staging.startswith(helper.STATE_DIR))
            self.assertEqual(["boot.kelf", "icon.sys", "system.cnf"], sorted(os.listdir(staging)))
            with open(os.path.join(staging, "icon.sys")) as handle:
                self.assertEqual("Icon.Sys", handle.read())
        finally:
            if staging:
                shutil.rmtree(staging, ignore_errors=True)

    def test_missing_folder_returns_none(self):
        self.assertIsNone(helper.stage_header_files(os.path.join(self.folder, "missing")))


class MainTests(unittest.TestCase):
    """Runs the helper as the Windows application does: one verb argument, the request on stdin."""

    def setUp(self):
        self.home = tempfile.mkdtemp(prefix="psx-helper-run-")
        # Stand-ins for the tools, so a well-formed request gets past require_tool on any machine.
        self.bin = os.path.join(self.home, "bin")
        os.makedirs(self.bin)
        for tool in ("hdl_dump", "pfsshell", "pfsfuse", "fusermount", "fusermount3"):
            path = os.path.join(self.bin, tool)
            with open(path, "w") as handle:
                handle.write("#!/bin/sh\nexit 0\n")
            os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)
        self.raw_path = os.path.join(self.home, ".local", "share", "psx-xmb-manager", "nbd", "nbd")
        self.state_dir = os.path.join(self.home, ".local", "state", "psx-xmb-manager")

    def tearDown(self):
        shutil.rmtree(self.home, ignore_errors=True)

    def run_helper(self, *argv, stdin=""):
        env = dict(os.environ, HOME=self.home, PATH=self.bin + os.pathsep + os.environ.get("PATH", ""))
        return subprocess.run([sys.executable, HELPER] + list(argv), input=stdin.encode("utf-8"),
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, timeout=30)

    def call(self, verb, request):
        run = self.run_helper(verb, stdin=request if isinstance(request, str) else json.dumps(request))
        lines = run.stdout.decode("utf-8").splitlines()
        self.assertEqual(1, len(lines), "exactly one JSON line expected, got %r" % run.stdout)
        response = json.loads(lines[0])
        self.assertEqual({"ok", "code", "message", "stdout", "stderr", "data"}, set(response))
        return run.returncode, response

    def test_version_prints_the_protocol_version(self):
        run = self.run_helper("--version")
        self.assertEqual(0, run.returncode)
        self.assertEqual(b"1\n", run.stdout)

    def test_unknown_verb_and_missing_verb(self):
        for argv in (("format-everything",), (), ("status", "extra")):
            run = self.run_helper(*argv)
            self.assertEqual(2, run.returncode, argv)
            self.assertEqual("INVALID_REQUEST", json.loads(run.stdout)["code"])

    def test_bad_json_is_rejected(self):
        for text in ("{not json", "[1, 2]", "\"text\""):
            code, response = self.call("status", text)
            self.assertEqual(2, code, text)
            self.assertEqual("INVALID_REQUEST", response["code"])

    def test_status_while_disconnected(self):
        code, response = self.call("status", "")
        self.assertEqual(0, code)
        self.assertTrue(response["ok"])
        self.assertEqual("disconnected", response["data"]["state"])
        self.assertEqual(self.raw_path, response["data"]["raw_path"])
        self.assertEqual([], response["data"]["pfs_mounts"])

    def test_hdl_allows_only_listed_verbs(self):
        for verb in ("rmpart", "delete", "dump", "init", "--help", ""):
            code, response = self.call("hdl", {"args": [verb, self.raw_path, "PP.X"]})
            self.assertEqual(1, code, verb)
            self.assertEqual("INVALID_REQUEST", response["code"], verb)

    def test_hdl_must_target_the_nbd_raw_file(self):
        for device in ("/dev/sda", "hdd1:", self.raw_path + "x", ""):
            code, response = self.call("hdl", {"args": ["hdl_toc", device]})
            self.assertEqual("INVALID_REQUEST", response["code"], device)
        for bad in ({}, {"args": "hdl_toc"}, {"args": []}, {"args": ["hdl_toc", 1]}):
            code, response = self.call("hdl", bad)
            self.assertEqual("INVALID_REQUEST", response["code"], bad)

    def test_hdl_refuses_to_run_without_a_connection(self):
        code, response = self.call("hdl", {"args": ["hdl_toc", self.raw_path]})
        self.assertEqual(1, code)
        self.assertEqual("NOT_CONNECTED", response["code"])

    def test_stale_state_is_reported_and_blocks_tools(self):
        os.makedirs(self.state_dir)
        with open(os.path.join(self.state_dir, "connection.json"), "w") as handle:
            json.dump({"ip": "192.168.1.50", "port": 10809}, handle)
        code, response = self.call("status", {})
        self.assertEqual("stale-state-file", response["data"]["state"])
        code, response = self.call("hdl", {"args": ["hdl_toc", self.raw_path]})
        self.assertEqual("STALE_MOUNT_STATE", response["code"])

    def test_pfsshell_rejects_line_breaks_and_other_devices(self):
        for commands in (["device " + self.raw_path, "mount PP.A\nrmpart __system"],
                         ["device " + self.raw_path, "ls\r"],
                         ["device " + self.raw_path, "ls\0"]):
            code, response = self.call("pfsshell", {"commands": commands})
            self.assertEqual("INVALID_REQUEST", response["code"], commands)
            self.assertIn("line break", response["message"])
        for device in ("device /dev/sda", "device", "device " + self.raw_path + "2"):
            code, response = self.call("pfsshell", {"commands": [device, "ls"]})
            self.assertEqual("INVALID_REQUEST", response["code"], device)
        for bad in ({}, {"commands": "ls"}, {"commands": []}, {"commands": ["ls", None]}):
            code, response = self.call("pfsshell", bad)
            self.assertEqual("INVALID_REQUEST", response["code"], bad)

    def test_pfsshell_refuses_to_run_without_a_connection(self):
        code, response = self.call("pfsshell", {"commands": ["device " + self.raw_path, "ls", "exit"]})
        self.assertEqual("NOT_CONNECTED", response["code"])

    def test_connect_validates_ip_and_port_first(self):
        code, response = self.call("connect", {"ip": "192.168.1.300"})
        self.assertEqual("INVALID_IP", response["code"])
        for port in (0, 65536, "10809", True):
            code, response = self.call("connect", {"ip": "192.168.1.50", "port": port})
            self.assertEqual("INVALID_PORT", response["code"], repr(port))

    def test_mount_pfs_validates_the_partition_name(self):
        for name in ("", "PP.A\nB", "A" * 33, None):
            code, response = self.call("mount-pfs", {"partition": name})
            self.assertEqual("INVALID_PARTITION_NAME", response["code"], repr(name))

    def test_errors_are_logged_under_the_state_folder(self):
        self.call("hdl", {"args": ["rmpart", self.raw_path]})
        with open(os.path.join(self.state_dir, "helper.log"), encoding="utf-8") as handle:
            self.assertIn("hdl INVALID_REQUEST", handle.read())


if __name__ == "__main__":
    unittest.main()
