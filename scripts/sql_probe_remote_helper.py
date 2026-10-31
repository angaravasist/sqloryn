#!/usr/bin/env python3
"""Remote-only disposable SQL Server helper. Invoked over SSH with fixed verbs.

Local orchestration uploads this file and speaks JSON on stdin/stdout. Never pass
passwords or certificates on the process argument list. Server private keys stay
on this host; only the public certificate PEM is returned.
"""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

# Keep in lockstep with scripts/run_sql_probe.py (asserted by unit tests).
IMAGE = "mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718"
LABEL = "org.sqloryn.disposable-probe"
NAME_RE = re.compile(r"\Asqloryn-probe-[a-f0-9]{16}\Z")
WORK_ROOT = Path("/tmp")
# Force the local engine; inherited DOCKER_HOST/CONTEXT must not redirect operations.
DOCKER_HOST = "unix:///var/run/docker.sock"
PREP_NAMES = frozenset({"OWNER", "helper.py"})


class HelperError(Exception):
    """Fixed, non-sensitive diagnostic text only."""


def emit(payload):
    sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def fail(message):
    emit({"ok": False, "error": message})
    return 1


def run(args, *, timeout=30, env=None, input_text=None):
    result = subprocess.run(
        args, env=env, input=input_text, text=True, capture_output=True, timeout=timeout)
    if result.returncode:
        raise HelperError(f"{args[0]} operation failed; raw diagnostics suppressed.")
    return result.stdout.strip()


def docker_environ(base=None):
    env = dict(os.environ if base is None else base)
    for key in list(env):
        if key.startswith("DOCKER_"):
            del env[key]
    env["DOCKER_HOST"] = DOCKER_HOST
    return env


def run_docker(args, *, timeout=30, env=None, input_text=None):
    merged = docker_environ(env)
    return run(["docker", "-H", DOCKER_HOST, *args], timeout=timeout, env=merged, input_text=input_text)


def require_name(name):
    if not NAME_RE.fullmatch(name):
        raise HelperError("Invalid disposable resource name.")
    return name


def workdir_for(name):
    path = WORK_ROOT / name
    if path.parent != WORK_ROOT or path.name != name:
        raise HelperError("Remote work directory path rejected.")
    if path.is_symlink():
        raise HelperError("Remote work directory symlink rejected.")
    return path


def validate_host_platform():
    system = run(["uname", "-s"])
    machine = run(["uname", "-m"])
    if system != "Linux":
        raise HelperError("Requires a Linux SSH host.")
    if machine not in {"x86_64", "amd64"}:
        # shortcut: reported Docker arch is not hardware proof; host uname must also be x64.
        raise HelperError("Requires an x64 SSH host; Docker architecture alone is not accepted.")


def validate_engine(info, endpoint):
    if info.get("OSType") != "linux" or info.get("Architecture") not in {"x86_64", "amd64"}:
        raise HelperError("Requires a Linux x64 Docker engine; emulation is not accepted as evidence.")
    if not isinstance(endpoint, str) or not endpoint.startswith("unix://"):
        raise HelperError("Requires the local Docker Unix socket; remote Docker contexts are not supported.")


def published_port(bindings):
    if not isinstance(bindings, list) or len(bindings) != 1 or bindings[0].get("HostIp") != "127.0.0.1":
        raise HelperError("Expected one loopback-only published SQL port.")
    port = int(bindings[0]["HostPort"])
    if not 1024 <= port <= 65535:
        raise HelperError("Invalid published SQL port.")
    return port


def assert_local_docker_configuration():
    """Fail closed on remote Docker redirects before any provision/cleanup work."""
    host = os.environ.get("DOCKER_HOST")
    if host and not host.startswith("unix://"):
        raise HelperError("Refusing non-local DOCKER_HOST override.")
    context = os.environ.get("DOCKER_CONTEXT")
    if context:
        endpoint = run(
            ["docker", "context", "inspect", context, "--format", "{{.Endpoints.docker.Host}}"],
            env=os.environ)
        if not endpoint.startswith("unix://"):
            raise HelperError("Refusing non-local Docker context endpoint.")


def resolve_docker_endpoint():
    assert_local_docker_configuration()
    # Subsequent docker calls force DOCKER_HOST to the local Unix socket.
    return DOCKER_HOST


def list_regular_names(directory):
    names = set()
    for child in directory.iterdir():
        if child.is_symlink():
            raise HelperError("Remote work directory contains a symlink.")
        names.add(child.name)
    return names


def read_owner(directory):
    marker = directory / "OWNER"
    if marker.is_symlink() or not marker.is_file():
        return None
    return marker.read_text().strip()


def write_owner(directory, name):
    marker = directory / "OWNER"
    marker.write_text(name + "\n")
    marker.chmod(0o600)


def require_prepared(directory, name, *, require_helper=True):
    if not directory.exists() or not directory.is_dir() or directory.is_symlink():
        raise HelperError("Prepared remote work directory missing.")
    owner = read_owner(directory)
    if owner != name:
        raise HelperError("Remote work directory ownership mismatch.")
    names = list_regular_names(directory)
    if not names <= PREP_NAMES or "OWNER" not in names:
        raise HelperError("Remote work directory is not in the prepared probe state.")
    if require_helper and "helper.py" not in names:
        raise HelperError("Prepared remote helper is missing.")
    helper = directory / "helper.py"
    if helper.exists() and helper.is_symlink():
        raise HelperError("Remote helper path is a symlink.")


def prepare(name):
    """Create or accept the legitimate prep state; never adopt unrelated directories."""
    require_name(name)
    directory = workdir_for(name)
    if directory.exists():
        if not directory.is_dir() or directory.is_symlink():
            raise HelperError("Remote work directory is not a real directory.")
        names = list_regular_names(directory)
        owner = read_owner(directory)
        if owner is None:
            if not names <= {"helper.py"}:
                raise HelperError("Refusing to claim an unrelated remote directory.")
            write_owner(directory, name)
        elif owner != name:
            raise HelperError("Remote work directory ownership mismatch.")
        elif not names <= PREP_NAMES:
            raise HelperError("Remote work directory is not in the prepared probe state.")
        else:
            # Idempotent prepare of an already owned prep directory.
            pass
    else:
        directory.mkdir(mode=0o700)
        write_owner(directory, name)
    require_prepared(directory, name, require_helper=False)
    emit({"ok": True, "name": name, "path": str(directory)})
    return 0


def engine_check():
    validate_host_platform()
    endpoint = resolve_docker_endpoint()
    info = json.loads(run_docker(["info", "--format", "{{json .}}"]))
    validate_engine(info, endpoint)
    emit({
        "ok": True,
        "ostype": info.get("OSType"),
        "architecture": info.get("Architecture"),
        "docker_host": DOCKER_HOST,
        "note": "Reported architecture is not proof of underlying hardware.",
    })
    return 0


def provision(name):
    require_name(name)
    request = json.loads(sys.stdin.read() or "{}")
    password = request.get("password")
    image = request.get("image")
    if not isinstance(password, str) or not (8 <= len(password) <= 128) or any(ch in password for ch in "\n\r\0"):
        raise HelperError("Invalid password payload.")
    if image != IMAGE:
        raise HelperError("Refusing image digest other than the pinned disposable probe image.")

    validate_host_platform()
    endpoint = resolve_docker_endpoint()
    info = json.loads(run_docker(["info", "--format", "{{json .}}"]))
    validate_engine(info, endpoint)

    directory = workdir_for(name)
    require_prepared(directory, name, require_helper=True)

    certs = directory / "certs"
    certs.mkdir(mode=0o755)
    run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
         "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
         "-keyout", str(certs / "server.key"), "-out", str(certs / "server.crt")])
    (certs / "server.key").chmod(0o644)
    certificate_pem = (certs / "server.crt").read_text()
    config = directory / "mssql.conf"
    config.write_text(
        "[network]\ntlscert = /probe-certs/server.crt\ntlskey = /probe-certs/server.key\nforceencryption = 1\n")
    config.chmod(0o644)

    run_docker(["pull", IMAGE], timeout=300)
    run_docker(["network", "create", "--label", f"{LABEL}={name}", name])
    run_docker(["create", "--name", name, "--hostname", name,
                "--label", f"{LABEL}={name}", "--network", name,
                "--publish", "127.0.0.1::1433", "--memory", "3g", "--cpus", "2",
                "--security-opt", "no-new-privileges=true", "--ulimit", "core=0",
                "--env", "ACCEPT_EULA=Y", "--env", "MSSQL_PID=Developer", "--env", "MSSQL_SA_PASSWORD",
                "--mount", f"type=bind,src={certs},dst=/probe-certs,readonly",
                "--mount", f"type=bind,src={config},dst=/var/opt/mssql/mssql.conf,readonly", IMAGE],
               env={**docker_environ(), "MSSQL_SA_PASSWORD": password})
    run_docker(["start", name])
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        if "SQL Server is now ready for client connections" in run_docker(["logs", name]):
            break
        time.sleep(2)
    else:
        raise HelperError("SQL Server startup timed out; raw logs suppressed.")
    bindings = json.loads(run_docker(
        ["inspect", "--format", '{{json (index .NetworkSettings.Ports "1433/tcp")}}', name]))
    port = published_port(bindings)
    emit({"ok": True, "name": name, "port": port, "certificate_pem": certificate_pem, "image": IMAGE})
    return 0


def owned_label(kind, name):
    fmt = '{{index .Labels "' + LABEL + '"}}' if kind == "network" else '{{index .Config.Labels "' + LABEL + '"}}'
    try:
        inspected = subprocess.run(
            ["docker", "-H", DOCKER_HOST, kind, "inspect", "--format", fmt, name],
            text=True, capture_output=True, timeout=30, env=docker_environ())
    except (OSError, subprocess.TimeoutExpired):
        raise HelperError("Docker inspect timed out or failed; raw diagnostics suppressed.")
    if inspected.returncode:
        return None
    return inspected.stdout.strip()


def remove_owned(kind, name):
    try:
        label = owned_label(kind, name)
        if label is None:
            listed_args = [kind, "ls", "--filter", f"label={LABEL}={name}", "--format", "{{.ID}}"]
            if kind == "container":
                listed_args.append("--all")
            return not run_docker(listed_args)
        if label != name:
            return False
        run_docker([kind, "rm"] + (["--force", "--volumes"] if kind == "container" else []) + [name])
        return True
    except (HelperError, OSError, subprocess.TimeoutExpired):
        return False


def remove_owned_workdir(name, directory):
    try:
        if directory.is_symlink():
            return False
        if not directory.exists():
            return True
        if not directory.is_dir():
            return False
        marker = directory / "OWNER"
        names = list_regular_names(directory)
        if marker.is_file() and not marker.is_symlink() and marker.read_text().strip() == name:
            for child in sorted(directory.rglob("*"), reverse=True):
                if child.is_symlink() or child.is_file():
                    child.unlink()
                elif child.is_dir():
                    child.rmdir()
            directory.rmdir()
            return True
        # Prep state: owned marker optional only when solely helper.py was uploaded pre-prepare.
        if names <= {"helper.py", "OWNER"}:
            owner = read_owner(directory)
            if owner not in {None, name}:
                return False
            if owner == name or names <= {"helper.py"}:
                for child_name in list(names):
                    (directory / child_name).unlink()
                directory.rmdir()
                return True
        return False
    except (HelperError, OSError):
        return False


def cleanup(name):
    require_name(name)
    failed = False
    for kind in ("container", "network"):
        try:
            if not remove_owned(kind, name):
                failed = True
        except (HelperError, OSError, subprocess.TimeoutExpired):
            failed = True
    try:
        if not remove_owned_workdir(name, workdir_for(name)):
            failed = True
    except (HelperError, OSError):
        failed = True
    if failed:
        raise HelperError(
            f"Cleanup incomplete for {name}; inspect resources labelled {LABEL} "
            "with that exact run name before retrying.")
    emit({"ok": True, "name": name})
    return 0


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    try:
        if argv == ["engine-check"]:
            return engine_check()
        if len(argv) == 2 and argv[0] == "prepare":
            return prepare(argv[1])
        if len(argv) == 2 and argv[0] == "provision":
            return provision(argv[1])
        if len(argv) == 2 and argv[0] == "cleanup":
            return cleanup(argv[1])
        return fail("Unsupported remote helper invocation.")
    except HelperError as error:
        return fail(str(error))
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError, json.JSONDecodeError):
        return fail(f"Remote helper operation ({sys.exc_info()[0].__name__}); raw diagnostics suppressed.")


if __name__ == "__main__":
    sys.exit(main())
