"""No live Docker/SSH: prove runner isolation, lifecycle and licence gates."""
import contextlib
import importlib.util
import io
import os
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
        with patch.object(runner, "execute_local") as local, patch.object(runner, "execute_ssh") as remote, \
                contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(runner.main(["--accept-sql-server-license", "--ssh-host", "example.test"]), 1)
        local.assert_not_called()
        remote.assert_not_called()

    def test_empty_ssh_host_or_known_hosts_fails_before_work(self):
        with tempfile.NamedTemporaryFile() as known:
            for argv in (
                ["--accept-sql-server-license", "--ssh-host", "", "--ssh-known-hosts", known.name],
                ["--accept-sql-server-license", "--ssh-host", "example.test", "--ssh-known-hosts", ""],
                ["--accept-sql-server-license", "--ssh-host", "   ", "--ssh-known-hosts", known.name],
            ):
                with self.subTest(argv=argv), patch.object(runner, "execute_local") as local, \
                        patch.object(runner, "execute_ssh") as remote, contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(runner.main(argv), 1)
                local.assert_not_called()
                remote.assert_not_called()

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
            options = runner.ssh_command_options(known.name)
        joined = " ".join(options)
        self.assertIn("BatchMode=yes", joined)
        self.assertIn("StrictHostKeyChecking=yes", joined)
        self.assertIn("ExitOnForwardFailure=yes", joined)
        self.assertIn("ForwardAgent=no", joined)
        self.assertIn("ProxyCommand=none", joined)
        self.assertIn("ControlMaster=no", joined)
        self.assertIn("ClearAllForwardings=yes", joined)
        self.assertNotIn("StrictHostKeyChecking=no", joined)

    def test_tunnel_effective_config_keeps_loopback_forward(self):
        with tempfile.NamedTemporaryFile() as known:
            argv = runner.tunnel_ssh_command(None, "example.test", known.name, 23456, 14330)
            config = runner.effective_ssh_config(argv)
        self.assertEqual(config.get("clearallforwardings"), "no")
        self.assertEqual(config.get("exitonforwardfailure"), "yes")
        self.assertEqual(config.get("batchmode"), "yes")
        self.assertEqual(config.get("controlmaster"), "false")
        self.assertTrue(any("127.0.0.1" in item and "23456" in item and "14330" in item
                            for item in config["localforward"]))

    def test_command_session_effective_config_clears_forwardings(self):
        with tempfile.NamedTemporaryFile() as known:
            argv = ["ssh", *runner.ssh_command_options(known.name), "--", "example.test", "/bin/true"]
            config = runner.effective_ssh_config(argv)
        self.assertEqual(config.get("clearallforwardings"), "yes")
        self.assertEqual(config.get("localforward"), [])

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
            self.assertEqual((work / "OWNER").read_text(), "someone-else\n")

    def test_helper_cleanup_refuses_symlink_workdir_without_modification(self):
        name = "sqloryn-probe-aaaabbbbccccdddd"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            real = root / "real-dir"
            real.mkdir()
            (real / "OWNER").write_text(name + "\n")
            (real / "secret").write_text("keep")
            link = root / name
            link.symlink_to(real)
            with patch.object(helper, "WORK_ROOT", root), \
                    patch.object(helper, "remove_owned", return_value=True), \
                    patch.object(helper.sys, "stdout", io.StringIO()):
                self.assertEqual(helper.main(["cleanup", name]), 1)
            self.assertTrue(link.is_symlink())
            self.assertTrue((real / "secret").exists())

    def test_helper_cleanup_removes_owned_prep_dir(self):
        name = "sqloryn-probe-fedcba9876543210"
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary) / name
            work.mkdir()
            (work / "OWNER").write_text(name + "\n")
            (work / "helper.py").write_text("# helper\n")
            stdout = io.StringIO()
            with patch.object(helper, "WORK_ROOT", Path(temporary)), \
                    patch.object(helper, "remove_owned", return_value=True), \
                    patch.object(helper.sys, "stdout", stdout):
                self.assertEqual(helper.main(["cleanup", name]), 0)
            self.assertFalse(work.exists())

    def test_bootstrap_prepare_then_provision_accepts_legitimate_state(self):
        name = "sqloryn-probe-0123456789abcdef"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            work = root / name
            work.mkdir()
            (work / "helper.py").write_text("# helper\n")
            stdout = io.StringIO()
            with patch.object(helper, "WORK_ROOT", root), patch.object(helper.sys, "stdout", stdout):
                self.assertEqual(helper.main(["prepare", name]), 0)
            self.assertEqual((work / "OWNER").read_text().strip(), name)

            docker_calls = []

            def fake_run_docker(args, **kwargs):
                docker_calls.append(list(args))
                if args[:1] == ["info"]:
                    return '{"OSType":"linux","Architecture":"amd64"}'
                if args[:2] == ["pull", helper.IMAGE]:
                    return ""
                if args[:2] == ["network", "create"]:
                    return ""
                if args[:1] == ["create"]:
                    return ""
                if args[:1] == ["start"]:
                    return ""
                if args[:1] == ["logs"]:
                    return "SQL Server is now ready for client connections"
                if args[:1] == ["inspect"]:
                    return '[{"HostIp":"127.0.0.1","HostPort":"14330"}]'
                raise AssertionError(args)

            def fake_run(args, **kwargs):
                if args[:1] == ["uname"]:
                    return {"-s": "Linux", "-m": "x86_64"}[args[1]]
                if args[:1] == ["openssl"]:
                    certs = work / "certs"
                    certs.mkdir(exist_ok=True)
                    (certs / "server.key").write_text("key")
                    (certs / "server.crt").write_text(
                        "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n")
                    return ""
                raise AssertionError(args)

            stdout = io.StringIO()
            with patch.object(helper, "WORK_ROOT", root), \
                    patch.object(helper, "validate_host_platform"), \
                    patch.object(helper, "resolve_docker_endpoint", return_value=helper.DOCKER_HOST), \
                    patch.object(helper, "run_docker", side_effect=fake_run_docker), \
                    patch.object(helper, "run", side_effect=fake_run), \
                    patch.object(helper.sys, "stdin", io.StringIO(
                        '{"password":"Aa1!deadbeefdeadbeef","image":"%s"}' % helper.IMAGE)), \
                    patch.object(helper.sys, "stdout", stdout):
                # Re-call validate path inside provision; stub host/docker gates above.
                with patch.object(helper, "validate_host_platform"), \
                        patch.object(helper, "resolve_docker_endpoint", return_value=helper.DOCKER_HOST), \
                        patch.object(helper, "validate_engine"):
                    self.assertEqual(helper.main(["provision", name]), 0)
            self.assertTrue(any(call[:1] == ["create"] for call in docker_calls))
            self.assertIn("certificate_pem", stdout.getvalue())

    def test_prepare_refuses_unrelated_existing_directory(self):
        name = "sqloryn-probe-0123456789abcdef"
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary) / name
            work.mkdir()
            (work / "stray").write_text("nope")
            with patch.object(helper, "WORK_ROOT", Path(temporary)), \
                    patch.object(helper.sys, "stdout", io.StringIO()):
                self.assertEqual(helper.main(["prepare", name]), 1)
            self.assertFalse((work / "OWNER").exists())

    def test_prepare_refuses_symlink_directory(self):
        name = "sqloryn-probe-0123456789abcdef"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            real = root / "other"
            real.mkdir()
            (root / name).symlink_to(real)
            with patch.object(helper, "WORK_ROOT", root), patch.object(helper.sys, "stdout", io.StringIO()):
                self.assertEqual(helper.main(["prepare", name]), 1)
            self.assertFalse((real / "OWNER").exists())

    def test_provision_refuses_without_prepare(self):
        name = "sqloryn-probe-0123456789abcdef"
        with tempfile.TemporaryDirectory() as temporary:
            with patch.object(helper, "WORK_ROOT", Path(temporary)), \
                    patch.object(helper, "validate_host_platform"), \
                    patch.object(helper, "resolve_docker_endpoint", return_value=helper.DOCKER_HOST), \
                    patch.object(helper, "run_docker", return_value='{"OSType":"linux","Architecture":"amd64"}'), \
                    patch.object(helper, "validate_engine"), \
                    patch.object(helper.sys, "stdin", io.StringIO(
                        '{"password":"Aa1!deadbeefdeadbeef","image":"%s"}' % helper.IMAGE)), \
                    patch.object(helper.sys, "stdout", io.StringIO()):
                self.assertEqual(helper.main(["provision", name]), 1)

    def test_engine_check_rejects_remote_docker_host_override(self):
        with patch.dict(os.environ, {"DOCKER_HOST": "ssh://evil.example"}, clear=False), \
                patch.object(helper, "validate_host_platform"), \
                patch.object(helper.sys, "stdout", io.StringIO()):
            self.assertEqual(helper.main(["engine-check"]), 1)

    def test_engine_check_rejects_arm_host_even_if_daemon_reports_x64(self):
        def fake_run(args, **kwargs):
            if args == ["uname", "-s"]:
                return "Linux"
            if args == ["uname", "-m"]:
                return "aarch64"
            raise AssertionError(args)

        with patch.object(helper, "run", side_effect=fake_run), \
                patch.object(helper, "run_docker") as docker, \
                patch.object(helper.sys, "stdout", io.StringIO()):
            self.assertEqual(helper.main(["engine-check"]), 1)
        docker.assert_not_called()

    def test_remote_cleanup_continues_after_tunnel_stop_failure(self):
        name = "sqloryn-probe-0123456789abcdef"
        tunnel = MagicMock()
        with patch.object(runner, "stop_owned_tunnel", side_effect=RuntimeError("boom")), \
                patch.object(runner, "ssh_run", return_value='{"ok":true,"name":"%s"}' % name) as ssh_run, \
                patch.object(runner, "scp_upload") as scp:
            with self.assertRaises(runner.ProbeError) as error:
                runner.remote_cleanup(None, "example.test", "/tmp/known", name, tunnel)
        ssh_run.assert_called_once()
        scp.assert_not_called()
        self.assertIn("tunnel", str(error.exception))
        self.assertIn(name, str(error.exception))

    def test_remote_cleanup_does_not_upload_helper_on_failure(self):
        tunnel = MagicMock()
        tunnel.poll.return_value = 0
        with patch.object(runner, "stop_owned_tunnel", return_value=False), \
                patch.object(runner, "ssh_run", side_effect=runner.ProbeError("unreachable")), \
                patch.object(runner, "scp_upload") as scp:
            with self.assertRaises(runner.ProbeError) as error:
                runner.remote_cleanup(None, "example.test", "/tmp/known",
                                      "sqloryn-probe-0123456789abcdef", tunnel)
        scp.assert_not_called()
        self.assertIn("run-id sqloryn-probe-0123456789abcdef", str(error.exception))

    def test_helper_cleanup_continues_after_container_timeout(self):
        name = "sqloryn-probe-0123456789abcdef"
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary) / name
            work.mkdir()
            (work / "OWNER").write_text(name + "\n")
            (work / "helper.py").write_text("x")

            def remove_owned(kind, _name):
                if kind == "container":
                    raise subprocess.TimeoutExpired(cmd=["docker"], timeout=1)
                return True

            stdout = io.StringIO()
            with patch.object(helper, "WORK_ROOT", Path(temporary)), \
                    patch.object(helper, "remove_owned", side_effect=remove_owned), \
                    patch.object(helper.sys, "stdout", stdout):
                # cleanup catches and continues; overall still fails because container failed
                self.assertEqual(helper.main(["cleanup", name]), 1)
            self.assertFalse(work.exists())

    def test_start_owned_tunnel_reaps_process_on_early_exit(self):
        process = MagicMock()
        process.poll.return_value = 1
        with tempfile.NamedTemporaryFile() as known, \
                patch.object(runner, "assert_local_port_free"), \
                patch.object(runner.subprocess, "Popen", return_value=process), \
                patch.object(runner, "stop_owned_tunnel") as stop:
            with self.assertRaises(runner.ProbeError):
                runner.start_owned_tunnel(None, "example.test", known.name, 23456, 14330)
        stop.assert_called_once_with(process)

    def test_start_owned_tunnel_reaps_process_on_interrupt(self):
        process = MagicMock()
        process.poll.return_value = None

        def raise_interrupt(_timeout=0.2):
            raise KeyboardInterrupt

        with tempfile.NamedTemporaryFile() as known, \
                patch.object(runner, "assert_local_port_free"), \
                patch.object(runner.subprocess, "Popen", return_value=process), \
                patch.object(runner.socket, "socket") as sock_cls, \
                patch.object(runner, "stop_owned_tunnel") as stop:
            sock = MagicMock()
            sock_cls.return_value.__enter__.return_value = sock
            sock.connect.side_effect = raise_interrupt
            with self.assertRaises(KeyboardInterrupt):
                runner.start_owned_tunnel(None, "example.test", known.name, 23456, 14330)
        stop.assert_called_once_with(process)

    def test_parse_helper_json_requires_ok(self):
        with self.assertRaises(runner.ProbeError):
            runner.parse_helper_json('{"ok":false,"error":"nope"}')
        self.assertEqual(runner.parse_helper_json('{"ok":true,"port":14330}')["port"], 14330)

    def test_failed_mkdir_never_executes_helper(self):
        with tempfile.NamedTemporaryFile() as known:
            probe = Path(known.name)
            with patch.object(runner, "PROBE", probe), \
                    patch.object(runner, "REMOTE_HELPER", probe), \
                    patch.object(runner, "parse_ssh_destination", return_value=(None, "example.test")), \
                    patch.object(runner, "ssh_run", side_effect=runner.ProbeError("mkdir failed")) as ssh_run, \
                    patch.object(runner, "scp_upload") as scp, \
                    patch.object(runner, "remote_cleanup") as cleanup:
                with self.assertRaises(runner.ProbeError):
                    runner.execute_ssh("example.test", known.name)
        scp.assert_not_called()
        cleanup.assert_not_called()
        self.assertTrue(any(call.args[3][:1] == ["/bin/mkdir"] for call in ssh_run.call_args_list))
        self.assertFalse(any(call.args[3][:1] == ["/usr/bin/python3"] for call in ssh_run.call_args_list))

    def test_incomplete_helper_upload_never_executes_helper(self):
        calls = []

        def ssh_run(user, host, known_hosts, remote_argv, **kwargs):
            calls.append(list(remote_argv))
            if remote_argv[:1] == ["/bin/mkdir"]:
                return ""
            if remote_argv[:1] == ["/usr/bin/test"]:
                raise runner.ProbeError("helper missing")
            raise AssertionError(remote_argv)

        with tempfile.NamedTemporaryFile() as known:
            probe = Path(known.name)
            with patch.object(runner, "PROBE", probe), \
                    patch.object(runner, "REMOTE_HELPER", probe), \
                    patch.object(runner, "parse_ssh_destination", return_value=(None, "example.test")), \
                    patch.object(runner, "ssh_run", side_effect=ssh_run), \
                    patch.object(runner, "scp_upload"), \
                    patch.object(runner, "remote_cleanup") as cleanup:
                with self.assertRaises(runner.ProbeError):
                    runner.execute_ssh("example.test", known.name)
        cleanup.assert_not_called()
        self.assertFalse(any(argv[:1] == ["/usr/bin/python3"] for argv in calls))


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
