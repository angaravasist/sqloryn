"""No Docker operations: prove the runner's isolation and licence gates."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("run_sql_probe", Path(__file__).resolve().parents[1] / "scripts/run_sql_probe.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class ProbeRunnerTests(unittest.TestCase):
    def test_refuses_unaccepted_license_before_any_work(self):
        with patch.object(runner, "execute") as execute, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                runner.main([])
        self.assertEqual(error.exception.code, 2)
        execute.assert_not_called()

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


if __name__ == "__main__":
    unittest.main()
