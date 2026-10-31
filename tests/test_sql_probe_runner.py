"""No Docker/SSH operations: prove the runner's isolation and licence gates."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch

ROOT = Path(__file__).resolve().parents[1]


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


runner = load("run_sql_probe", "scripts/run_sql_probe.py")
helper = load("sql_probe_remote_helper", "scripts/sql_probe_remote_helper.py")


class ProbeRunnerTests(unittest.TestCase):
    def test_refuses_unaccepted_license_before_any_work(self):
        with patch.object(runner, "execute_local") as execute, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                runner.main([])
        self.assertEqual(error.exception.code, 2)
        execute.assert_not_called()

    def test_remote_flags_require_both_host_and_known_hosts(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                runner.main(["--accept-sql-server-license", "--ssh-host", "example.test"])
        self.assertEqual(error.exception.code, 2)

    def test_requires_native_linux_x64_and_local_socket(self):
        for info in ({"OSType": "linux", "Architecture": "aarch64"},
                     {"OSType": "windows", "Architecture": "amd64"}, {}):
            with self.subTest(info=info), self.assertRaises(runner.ProbeError):
                runner.validate_engine(info, "unix:///var/run/docker.sock")
        for endpoint in ("ssh://host", "tcp://host:2375", ""):
            with self.subTest(endpoint=endpoint), self.assertRaises(runner.ProbeError):
                runner.validate_engine({"OSType": "linux", "Architecture": "amd64"}, endpoint)
        for architecture in ("amd64", "x86_64"):
            runner.validate_engine({"OSType": "linux", "Architecture": architecture}, "unix:///var/run/docker.sock")

    def test_failed_command_does_not_expose_output_or_arguments(self):
        result = subprocess.CompletedProcess([], 1, stdout="sensitive output", stderr="sensitive diagnostic")
        with patch.object(runner.subprocess, "run", return_value=result):
            with self.assertRaises(runner.ProbeError) as error:
                runner.run(["docker", "sensitive argument"])
        self.assertNotIn("sensitive", str(error.exception))

    def test_cleanup_refuses_resources_without_matching_ownership(self):
        result = subprocess.CompletedProcess([], 0, stdout="different owner\n", stderr="")
        with patch.object(runner.subprocess, "run", return_value=result), patch.object(runner, "run") as remove:
            with self.assertRaises(runner.ProbeError):
                runner.cleanup("sqloryn-probe-0123456789abcdef")
        remove.assert_not_called()

    def test_cleanup_removes_only_owned_resources(self):
        name = "sqloryn-probe-0123456789abcdef"
        result = subprocess.CompletedProcess([], 0, stdout=name + "\n", stderr="")
        with patch.object(runner.subprocess, "run", return_value=result), patch.object(runner, "run") as remove:
            runner.cleanup(name)
        self.assertEqual([call.args[0] for call in remove.call_args_list], [
            ["docker", "container", "rm", "--force", "--volumes", name], ["docker", "network", "rm", name]])

    def test_published_port_requires_one_loopback_binding(self):
        for bindings in (None, [], [{"HostIp": "0.0.0.0", "HostPort": "14330"}],
                         [{"HostIp": "127.0.0.1", "HostPort": "1"}]):
            with self.subTest(bindings=bindings), self.assertRaises(runner.ProbeError):
                runner.published_port(bindings)
        self.assertEqual(runner.published_port([{"HostIp": "127.0.0.1", "HostPort": "14330"}]), 14330)

    def test_cleanup_failure_is_not_success(self):
        name = "sqloryn-probe-0123456789abcdef"
        result = subprocess.CompletedProcess([], 0, stdout=name + "\n", stderr="")
        with patch.object(runner.subprocess, "run", return_value=result), \
                patch.object(runner, "run", side_effect=runner.ProbeError("cleanup failed")):
            with self.assertRaises(runner.ProbeError):
                runner.cleanup(name)

    def test_image_digest_locked_between_runner_and_helper(self):
        self.assertEqual(runner.IMAGE, helper.IMAGE)
        self.assertEqual(runner.LABEL, helper.LABEL)

    def test_ssh_destination_validation(self):
        self.assertEqual(runner.parse_ssh_destination("sql-test.example"), (None, "sql-test.example"))
        self.assertEqual(runner.parse_ssh_destination("ci_user@10.0.0.8"), ("ci_user", "10.0.0.8"))
        for bad in ("", "user@host;rm", "root@host/../x", "-oProxyCommand=evil", "user@host open"):
            with self.subTest(bad=bad), self.assertRaises(runner.ProbeError):
                runner.parse_ssh_destination(bad)

    def test_ssh_options_are_isolated_and_noninteractive(self):
        with tempfile.NamedTemporaryFile() as known:
            options = runner.ssh_common_options(known.name)
        self.assertIn("-F", options)
        self.assertIn("/dev/null", options)
        joined = " ".join(options)
        self.assertIn("BatchMode=yes", joined)
        self.assertIn("StrictHostKeyChecking=yes", joined)
        self.assertIn("ExitOnForwardFailure=yes", joined)
        self.assertIn("ForwardAgent=no", joined)
        self.assertIn("ProxyCommand=none", joined)
        self.assertIn("ControlMaster=no", joined)
        self.assertIn("GlobalKnownHostsFile=/dev/null", joined)
        self.assertNotIn("StrictHostKeyChecking=no", joined)

    def test_ssh_run_rejects_metacharacter_tokens(self):
        with tempfile.NamedTemporaryFile() as known:
            with self.assertRaises(runner.ProbeError):
                runner.ssh_run(None, "example.test", known.name, ["echo", "safe;rm"])

    def test_ssh_run_does_not_leak_failure_diagnostics(self):
        with tempfile.NamedTemporaryFile() as known:
            result = subprocess.CompletedProcess([], 255, stdout="", stderr="Permission denied (publickey)")
            with patch.object(runner.subprocess, "run", return_value=result):
                with self.assertRaises(runner.ProbeError) as error:
                    runner.ssh_run(None, "example.test", known.name, ["/bin/true"])
        self.assertNotIn("publickey", str(error.exception))
        self.assertNotIn("Permission", str(error.exception))

    def test_scp_upload_path_must_be_owned_helper_location(self):
        with tempfile.NamedTemporaryFile() as known, tempfile.NamedTemporaryFile() as local:
            with self.assertRaises(runner.ProbeError):
                runner.scp_upload(None, "example.test", known.name, local.name, "/var/tmp/helper.py")

    def test_occupied_local_port_is_rejected(self):
        with socket_bound_port() as port:
            with self.assertRaises(runner.ProbeError):
                runner.assert_local_port_free(port)

    def test_helper_rejects_wrong_image_and_bad_name(self):
        with patch.object(helper.sys, "stdin", io.StringIO('{"password":"Aa1!deadbeef","image":"evil"}')), \
                patch.object(helper.sys, "stdout", io.StringIO()):
            self.assertEqual(helper.main(["provision", "sqloryn-probe-0123456789abcdef"]), 1)
        with patch.object(helper.sys, "stdout", io.StringIO()):
            self.assertEqual(helper.main(["cleanup", "../etc"]), 1)
            self.assertEqual(helper.main(["cleanup", "sqloryn-probe-ABCDEF0123456789"]), 1)

    def test_helper_cleanup_requires_exact_owner_marker(self):
        name = "sqloryn-probe-0123456789abcdef"
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary) / name
            work.mkdir()
            (work / "OWNER").write_text("someone-else\n")
            (work / "server.key").write_text("private")
            with patch.object(helper, "WORK_ROOT", Path(temporary)), \
                    patch.object(helper, "remove_owned", return_value=True), \
                    patch.object(helper.sys, "stdout", io.StringIO()):
                self.assertEqual(helper.main(["cleanup", name]), 1)
            self.assertTrue((work / "server.key").exists())

    def test_helper_cleanup_removes_prep_helper_only_dir(self):
        name = "sqloryn-probe-fedcba9876543210"
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary) / name
            work.mkdir()
            (work / "helper.py").write_text("# helper\n")
            stdout = io.StringIO()
            with patch.object(helper, "WORK_ROOT", Path(temporary)), \
                    patch.object(helper, "remove_owned", return_value=True), \
                    patch.object(helper.sys, "stdout", stdout):
                self.assertEqual(helper.main(["cleanup", name]), 0)
            self.assertFalse(work.exists())

    def test_remote_cleanup_failure_is_not_success(self):
        tunnel = MagicMock()
        tunnel.poll.return_value = 0
        with patch.object(runner, "stop_owned_tunnel", return_value=False), \
                patch.object(runner, "ssh_run", side_effect=runner.ProbeError("unreachable")), \
                patch.object(runner, "scp_upload", side_effect=runner.ProbeError("unreachable")):
            with self.assertRaises(runner.ProbeError) as error:
                runner.remote_cleanup(None, "example.test", "/tmp/known", "sqloryn-probe-0123456789abcdef", tunnel)
        self.assertIn("unverifiable", str(error.exception))

    def test_parse_helper_json_requires_ok(self):
        with self.assertRaises(runner.ProbeError):
            runner.parse_helper_json('{"ok":false,"error":"nope"}')
        self.assertEqual(runner.parse_helper_json('{"ok":true,"port":14330}')["port"], 14330)


def socket_bound_port():
    import socket

    class Holder:
        def __enter__(self):
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            self.sock.bind(("127.0.0.1", 0))
            self.sock.listen(1)
            return self.sock.getsockname()[1]

        def __exit__(self, *args):
            self.sock.close()

    return Holder()


if __name__ == "__main__":
    unittest.main()
