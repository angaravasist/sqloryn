#!/usr/bin/env python3
"""Run fixed synthetic checks against a newly created, disposable SQL Server."""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
PROBE = ROOT / "experiments/SqlClientProbe/bin/Release/net10.0/SqlClientProbe.dll"
REMOTE_HELPER = Path(__file__).resolve().parent / "sql_probe_remote_helper.py"
# SQL Server 2022 CU27 GDR1, Ubuntu 22.04; resolved from MCR on 2026-10-31.
IMAGE = "mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718"
LABEL = "org.sqloryn.disposable-probe"
NAME_RE = re.compile(r"\Asqloryn-probe-[a-f0-9]{16}\Z")
USER_RE = re.compile(r"\A[A-Za-z_][A-Za-z0-9_-]{0,31}\Z")
HOST_RE = re.compile(
    r"\A(?:(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,}|"
    r"localhost|(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?))\Z")


class ProbeError(Exception):
    """Only fixed, non-sensitive diagnostic text may be supplied."""


def run(args, *, timeout=30, env=None):
    result = subprocess.run(args, cwd=ROOT, env=env,
                            text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        raise ProbeError(f"{args[0]} operation failed; raw diagnostics suppressed.")
    return result.stdout.strip()


def validate_engine(info, endpoint):
    if info.get("OSType") != "linux" or info.get("Architecture") not in {"x86_64", "amd64"}:
        raise ProbeError("Requires a Linux x64 Docker engine; emulation is not accepted as evidence.")
    if not endpoint.startswith("unix://"):
        raise ProbeError("Requires a local Docker socket; remote Docker contexts are not supported.")


def published_port(bindings):
    if not isinstance(bindings, list) or len(bindings) != 1 or bindings[0].get("HostIp") != "127.0.0.1":
        raise ProbeError("Expected one loopback-only published SQL port.")
    port = int(bindings[0]["HostPort"])
    if not 1024 <= port <= 65535:
        raise ProbeError("Invalid published SQL port.")
    return port


def cleanup(name):
    failed = False
    for kind in ("container", "network"):
        inspected = subprocess.run(
            ["docker", kind, "inspect", "--format", '{{index .Labels "' + LABEL + '"}}'
             if kind == "network" else '{{index .Config.Labels "' + LABEL + '"}}', name],
            text=True, capture_output=True, timeout=30)
        if inspected.returncode:
            # A missing resource is normal when setup failed before its creation.
            listed = run(["docker", kind, "ls", "--filter", f"label={LABEL}={name}", "--format", "{{.ID}}"]
                         + (["--all"] if kind == "container" else []))
            if listed:
                failed = True
            continue
        if inspected.stdout.strip() != name:
            failed = True
            continue
        try:
            run(["docker", kind, "rm"] + (["--force", "--volumes"] if kind == "container" else []) + [name])
        except ProbeError:
            failed = True
    if failed:
        raise ProbeError("Cleanup incomplete; inspect resources labelled org.sqloryn.disposable-probe before retrying.")


def parse_ssh_destination(value):
    if not isinstance(value, str) or not value or len(value) > 253:
        raise ProbeError("Invalid SSH destination.")
    if "@" in value:
        user, host = value.rsplit("@", 1)
        if not USER_RE.fullmatch(user):
            raise ProbeError("Invalid SSH user.")
    else:
        user, host = None, value
    if not HOST_RE.fullmatch(host):
        raise ProbeError("Invalid SSH host.")
    return user, host


def ssh_isolation_options(known_hosts):
    path = Path(known_hosts)
    if not path.is_file():
        raise ProbeError("SSH known-hosts file is required and must already exist.")
    # Isolate from user/global ssh_config: no proxy, multiplex, agent or extra trust stores.
    return [
        "-F", "/dev/null",
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=yes",
        "-o", f"UserKnownHostsFile={path.resolve()}",
        "-o", "GlobalKnownHostsFile=/dev/null",
        "-o", "IdentitiesOnly=yes",
        "-o", "PasswordAuthentication=no",
        "-o", "KbdInteractiveAuthentication=no",
        "-o", "PreferredAuthentications=publickey",
        "-o", "PubkeyAuthentication=yes",
        "-o", "ForwardAgent=no",
        "-o", "ForwardX11=no",
        "-o", "ExitOnForwardFailure=yes",
        "-o", "RequestTTY=no",
        "-o", "UpdateHostKeys=no",
        "-o", "CanonicalizeHostname=no",
        "-o", "ProxyCommand=none",
        "-o", "ProxyUseFdpass=no",
        "-o", "ControlMaster=no",
        "-o", "ControlPath=none",
        "-o", "ControlPersist=no",
        "-o", "ConnectTimeout=20",
        "-o", "ServerAliveInterval=10",
        "-o", "ServerAliveCountMax=3",
        "-o", "ConnectionAttempts=1",
    ]


def ssh_command_options(known_hosts):
    # ClearAllForwardings must stay on command/scp sessions only. On tunnel sessions it
    # also clears explicit -L forwards (verified via ssh -G).
    return [*ssh_isolation_options(known_hosts), "-o", "ClearAllForwardings=yes"]


def ssh_tunnel_options(known_hosts):
    return ssh_isolation_options(known_hosts)


def ssh_common_options(known_hosts):
    """Backward-compatible alias for non-tunnel sessions."""
    return ssh_command_options(known_hosts)


def ssh_target(user, host):
    return f"{user}@{host}" if user else host


def tunnel_ssh_command(user, host, known_hosts, local_port, remote_port):
    if not 1024 <= local_port <= 65535 or not 1024 <= remote_port <= 65535:
        raise ProbeError("Invalid tunnel port.")
    forward = f"127.0.0.1:{local_port}:127.0.0.1:{remote_port}"
    return [
        "ssh", *ssh_tunnel_options(known_hosts),
        "-N", "-L", forward,
        "--", ssh_target(user, host),
    ]


def effective_ssh_config(argv):
    """Connection-free effective configuration via ssh -G."""
    if not argv or argv[0] != "ssh":
        raise ProbeError("Invalid SSH argv for configuration dump.")
    # Replace destination with a dummy host; -G does not connect.
    command = ["ssh", "-G", *argv[1:]]
    try:
        result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True, timeout=10)
    except subprocess.TimeoutExpired as error:
        raise ProbeError("SSH configuration dump timed out.") from error
    if result.returncode:
        raise ProbeError("SSH configuration dump failed; raw diagnostics suppressed.")
    parsed = {}
    localforward = []
    for line in result.stdout.splitlines():
        if not line or " " not in line:
            continue
        key, value = line.split(" ", 1)
        key = key.lower()
        if key == "localforward":
            localforward.append(value)
        else:
            parsed[key] = value
    parsed["localforward"] = localforward
    return parsed


def ssh_run(user, host, known_hosts, remote_argv, *, timeout=60, input_text=None):
    """Run a fixed remote argv list. Joined for sshd's remote shell; tokens must be safe."""
    for part in remote_argv:
        if not isinstance(part, str) or not part or re.search(r"[\s\"'\\;$`|&<>]", part):
            raise ProbeError("Refusing unsafe remote command token.")
    command = [
        "ssh", *ssh_command_options(known_hosts),
        "--", ssh_target(user, host),
        *remote_argv,
    ]
    try:
        result = subprocess.run(
            command, cwd=ROOT, input=input_text, text=True, capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired as error:
        raise ProbeError("SSH operation timed out; raw diagnostics suppressed.") from error
    if result.returncode:
        raise ProbeError("SSH authentication or remote command failed; raw diagnostics suppressed.")
    return result.stdout


def validate_remote_helper_path(remote_path):
    remote = Path(remote_path)
    if remote.parent.parent != Path("/tmp") or not NAME_RE.fullmatch(remote.parent.name) or remote.name != "helper.py":
        raise ProbeError("Refusing unexpected remote upload path.")


def scp_upload(user, host, known_hosts, local_path, remote_path):
    validate_remote_helper_path(remote_path)
    command = [
        "scp", *ssh_command_options(known_hosts),
        "--", str(local_path), f"{ssh_target(user, host)}:{remote_path}",
    ]
    try:
        result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True, timeout=60)
    except subprocess.TimeoutExpired as error:
        raise ProbeError("SCP upload timed out; raw diagnostics suppressed.") from error
    if result.returncode:
        raise ProbeError("SCP upload failed; raw diagnostics suppressed.")


def parse_helper_json(stdout):
    lines = [line for line in stdout.splitlines() if line.strip()]
    if not lines:
        raise ProbeError("Remote helper returned no result.")
    try:
        payload = json.loads(lines[-1])
    except json.JSONDecodeError as error:
        raise ProbeError("Remote helper returned unusable status.") from error
    if not isinstance(payload, dict) or not payload.get("ok"):
        raise ProbeError("Remote helper reported failure; raw diagnostics suppressed.")
    return payload


def allocate_loopback_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("127.0.0.1", 0))
        except OSError as error:
            raise ProbeError("Unable to allocate a free loopback port.") from error
        port = sock.getsockname()[1]
    if not 1024 <= port <= 65535:
        raise ProbeError("Invalid allocated loopback port.")
    return port


def assert_local_port_free(port):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("127.0.0.1", port))
        except OSError as error:
            raise ProbeError("Refusing to reuse an occupied local loopback port.") from error


def start_owned_tunnel(user, host, known_hosts, local_port, remote_port):
    assert_local_port_free(local_port)
    command = tunnel_ssh_command(user, host, known_hosts, local_port, remote_port)
    process = None
    try:
        process = subprocess.Popen(command, cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            code = process.poll()
            if code is not None:
                raise ProbeError("SSH tunnel exited before forward was established.")
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
                sock.settimeout(0.2)
                try:
                    sock.connect(("127.0.0.1", local_port))
                    break
                except OSError:
                    time.sleep(0.2)
        else:
            raise ProbeError("SSH tunnel forward did not become ready.")
        return process
    except BaseException:
        stop_owned_tunnel(process)
        raise


def stop_owned_tunnel(process):
    if process is None:
        return False
    failed = False
    try:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    failed = True
    except (OSError, subprocess.SubprocessError):
        failed = True
    return failed


def invoke_probe(port, expected_server, certificate, wrong_certificate, password):
    result = subprocess.run(
        ["dotnet", str(PROBE), "--confirm-disposable", "--port", str(port), "--expected-server", expected_server,
         "--certificate", str(certificate), "--wrong-certificate", str(wrong_certificate)],
        cwd=ROOT, input=password + "\n", text=True, capture_output=True, timeout=110)
    for line in result.stdout.splitlines():
        if line.startswith(("PASS PROBE-", "Client: ")):
            print(line)
    if result.returncode:
        for line in result.stderr.splitlines():
            if line.startswith(("FAIL PROBE-", "FAIL SQL request", "FAIL TLS", "FAIL interrupted", "FAIL probe (")):
                print(line, file=sys.stderr)
        raise ProbeError("SQL acceptance suite failed; no compatibility claim is valid.")


def execute_local():
    if not PROBE.is_file():
        raise ProbeError("Build the Release configuration first; see docs/TESTING.md.")
    endpoint = os.environ.get("DOCKER_HOST") or run(
        ["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"])
    info = json.loads(run(["docker", "info", "--format", "{{json .}}"]))
    validate_engine(info, endpoint)
    print("PROBE-10 supported Linux x64 engine; preparing a disposable instance.", flush=True)
    run(["docker", "pull", IMAGE], timeout=300)
    name = "sqloryn-probe-" + secrets.token_hex(8)
    password = "Aa1!" + secrets.token_hex(24)
    with tempfile.TemporaryDirectory(prefix="sqloryn-probe-") as temporary:
        directory = Path(temporary)
        certs = directory / "certs"
        certs.mkdir(mode=0o755)
        for stem in ("server", "wrong"):
            run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                 "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
                 "-keyout", str(certs / f"{stem}.key"), "-out", str(certs / f"{stem}.crt")])
        # The parent temp directory stays 0700; SQL Server's non-root UID needs read access to the mounted key.
        (certs / "server.key").chmod(0o644)
        config = directory / "mssql.conf"
        config.write_text("[network]\ntlscert = /probe-certs/server.crt\ntlskey = /probe-certs/server.key\nforceencryption = 1\n")
        config.chmod(0o644)
        try:
            run(["docker", "network", "create", "--label", f"{LABEL}={name}", name])
            run(["docker", "create", "--name", name, "--hostname", name,
                 "--label", f"{LABEL}={name}", "--network", name,
                 "--publish", "127.0.0.1::1433", "--memory", "3g", "--cpus", "2",
                 "--security-opt", "no-new-privileges=true", "--ulimit", "core=0",
                 "--env", "ACCEPT_EULA=Y", "--env", "MSSQL_PID=Developer", "--env", "MSSQL_SA_PASSWORD",
                 "--mount", f"type=bind,src={certs},dst=/probe-certs,readonly",
                 "--mount", f"type=bind,src={config},dst=/var/opt/mssql/mssql.conf,readonly", IMAGE],
                env={**os.environ, "MSSQL_SA_PASSWORD": password})
            run(["docker", "start", name])
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                if "SQL Server is now ready for client connections" in run(["docker", "logs", name]):
                    break
                time.sleep(2)
            else:
                raise ProbeError("SQL Server startup timed out; raw logs suppressed.")
            bindings = json.loads(run(["docker", "inspect", "--format", '{{json (index .NetworkSettings.Ports "1433/tcp")}}', name]))
            port = published_port(bindings)
            print(f"Image: {IMAGE}", flush=True)
            invoke_probe(port, name, certs / "server.crt", certs / "wrong.crt", password)
        finally:
            cleanup(name)
    print("PASS PROBE-10 disposable container/network and temporary files removed.")


def recovery_message(name, failures):
    detail = ",".join(failures) if failures else "unspecified"
    return (
        f"Cleanup incomplete or unverifiable ({detail}) for run-id {name}; "
        f"inspect remote resources labelled {LABEL}={name} and /tmp/{name} after verifying OWNER. "
        "Never use broad Docker prune."
    )


def remote_cleanup(user, host, known_hosts, name, tunnel):
    if not NAME_RE.fullmatch(name):
        raise ProbeError("Invalid disposable resource name.")
    failures = []
    try:
        if stop_owned_tunnel(tunnel):
            failures.append("tunnel")
    except Exception:
        failures.append("tunnel")
    try:
        parse_helper_json(ssh_run(
            user, host, known_hosts,
            ["/usr/bin/python3", f"/tmp/{name}/helper.py", "cleanup", name],
            timeout=90))
    except ProbeError:
        # No upload/overwrite recovery: ownership must already be established.
        failures.append("remote")
    if failures:
        raise ProbeError(recovery_message(name, failures))


def execute_ssh(destination, known_hosts):
    if not PROBE.is_file():
        raise ProbeError("Build the Release configuration first; see docs/TESTING.md.")
    if not REMOTE_HELPER.is_file():
        raise ProbeError("Missing remote helper script.")
    user, host = parse_ssh_destination(destination)
    known_hosts_path = str(Path(known_hosts).resolve())
    name = "sqloryn-probe-" + secrets.token_hex(8)
    password = "Aa1!" + secrets.token_hex(24)
    tunnel = None
    prepared = False
    print(f"PROBE-11 run-id={name}; preparing disposable remote instance over isolated SSH.", flush=True)
    try:
        ssh_run(user, host, known_hosts_path,
                ["/bin/mkdir", "-m", "700", "--", f"/tmp/{name}"], timeout=30)
        scp_upload(user, host, known_hosts_path, REMOTE_HELPER, f"/tmp/{name}/helper.py")
        parse_helper_json(ssh_run(
            user, host, known_hosts_path,
            ["/usr/bin/python3", f"/tmp/{name}/helper.py", "prepare", name], timeout=30))
        prepared = True
        engine = parse_helper_json(ssh_run(
            user, host, known_hosts_path,
            ["/usr/bin/python3", f"/tmp/{name}/helper.py", "engine-check"], timeout=60))
        print(
            f"PROBE-11 remote engine ostype={engine.get('ostype')} arch={engine.get('architecture')} "
            f"docker_host={engine.get('docker_host')} "
            "(reported arch is not hardware proof).",
            flush=True)
        provision = parse_helper_json(ssh_run(
            user, host, known_hosts_path,
            ["/usr/bin/python3", f"/tmp/{name}/helper.py", "provision", name],
            timeout=420,
            input_text=json.dumps({"password": password, "image": IMAGE})))
        remote_port = provision.get("port")
        certificate_pem = provision.get("certificate_pem")
        if provision.get("name") != name or not isinstance(remote_port, int) or not isinstance(certificate_pem, str):
            raise ProbeError("Remote provision returned unexpected identity.")
        if "-----BEGIN CERTIFICATE-----" not in certificate_pem or "PRIVATE KEY" in certificate_pem:
            raise ProbeError("Remote provision did not return a public certificate only.")
        with tempfile.TemporaryDirectory(prefix="sqloryn-probe-") as temporary:
            directory = Path(temporary)
            server_cert = directory / "server.crt"
            wrong_cert = directory / "wrong.crt"
            wrong_key = directory / "wrong.key"
            server_cert.write_text(certificate_pem)
            server_cert.chmod(0o600)
            run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                 "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
                 "-keyout", str(wrong_key), "-out", str(wrong_cert)])
            local_port = allocate_loopback_port()
            tunnel = start_owned_tunnel(user, host, known_hosts_path, local_port, remote_port)
            print(f"Image: {IMAGE}", flush=True)
            print(f"Transport: ssh-loopback local={local_port} remote={remote_port}", flush=True)
            invoke_probe(local_port, name, server_cert, wrong_cert, password)
    finally:
        if prepared or tunnel is not None:
            remote_cleanup(user, host, known_hosts_path, name, tunnel)
        else:
            try:
                parse_helper_json(ssh_run(
                    user, host, known_hosts_path,
                    ["/usr/bin/python3", f"/tmp/{name}/helper.py", "cleanup", name], timeout=60))
            except ProbeError:
                raise ProbeError(recovery_message(name, ["remote"]))
    print(f"PASS PROBE-11 disposable remote resources and owned tunnel removed for run-id {name}.")



def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--accept-sql-server-license", action="store_true",
                        help="Explicitly accept Microsoft's SQL Server Developer test-use terms for this run.")
    parser.add_argument("--ssh-host",
                        help="Optional approved SSH destination (host or user@host) for Mac-to-Linux qualification.")
    parser.add_argument("--ssh-known-hosts",
                        help="Existing known_hosts file used exclusively for host-key verification.")
    args = parser.parse_args(argv)
    if not args.accept_sql_server_license:
        parser.error("Explicit --accept-sql-server-license is required; no Docker resources were created.")
    if (args.ssh_host is None) ^ (args.ssh_known_hosts is None):
        parser.error("Remote mode requires both --ssh-host and --ssh-known-hosts.")
    try:
        if args.ssh_host:
            execute_ssh(args.ssh_host, args.ssh_known_hosts)
        else:
            execute_local()
    except KeyboardInterrupt:
        print("FAIL interrupted; cleanup attempted.", file=sys.stderr)
        return 130
    except ProbeError as error:
        print(f"FAIL {error}", file=sys.stderr)
        return 1
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError) as error:
        print(f"FAIL runner operation ({type(error).__name__}); raw diagnostics suppressed.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
