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


def require_name(name):
    if not NAME_RE.fullmatch(name):
        raise HelperError("Invalid disposable resource name.")
    return name


def workdir_for(name):
    path = (WORK_ROOT / name).resolve()
    if path.parent != WORK_ROOT.resolve() or path.name != name:
        raise HelperError("Remote work directory path rejected.")
    return path


def validate_engine(info):
    if info.get("OSType") != "linux" or info.get("Architecture") not in {"x86_64", "amd64"}:
        raise HelperError("Requires a Linux x64 Docker engine; emulation is not accepted as evidence.")
    # shortcut: reported arch is not hardware proof; maintainer host approval required.


def published_port(bindings):
    if not isinstance(bindings, list) or len(bindings) != 1 or bindings[0].get("HostIp") != "127.0.0.1":
        raise HelperError("Expected one loopback-only published SQL port.")
    port = int(bindings[0]["HostPort"])
    if not 1024 <= port <= 65535:
        raise HelperError("Invalid published SQL port.")
    return port


def engine_check():
    info = json.loads(run(["docker", "info", "--format", "{{json .}}"]))
    validate_engine(info)
    emit({
        "ok": True,
        "ostype": info.get("OSType"),
        "architecture": info.get("Architecture"),
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

    directory = workdir_for(name)
    if directory.exists():
        raise HelperError("Remote work directory already exists.")
    directory.mkdir(mode=0o700)
    marker = directory / "OWNER"
    marker.write_text(name + "\n")
    marker.chmod(0o600)

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

    run(["docker", "pull", IMAGE], timeout=300)
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
        raise HelperError("SQL Server startup timed out; raw logs suppressed.")
    bindings = json.loads(run(
        ["docker", "inspect", "--format", '{{json (index .NetworkSettings.Ports "1433/tcp")}}', name]))
    port = published_port(bindings)
    emit({"ok": True, "name": name, "port": port, "certificate_pem": certificate_pem, "image": IMAGE})
    return 0


def owned_label(kind, name):
    fmt = '{{index .Labels "' + LABEL + '"}}' if kind == "network" else '{{index .Config.Labels "' + LABEL + '"}}'
    inspected = subprocess.run(
        ["docker", kind, "inspect", "--format", fmt, name],
        text=True, capture_output=True, timeout=30)
    if inspected.returncode:
        return None
    return inspected.stdout.strip()


def remove_owned(kind, name):
    label = owned_label(kind, name)
    if label is None:
        listed = run(["docker", kind, "ls", "--filter", f"label={LABEL}={name}", "--format", "{{.ID}}"]
                     + (["--all"] if kind == "container" else []))
        return not listed
    if label != name:
        return False
    try:
        run(["docker", kind, "rm"] + (["--force", "--volumes"] if kind == "container" else []) + [name])
    except HelperError:
        return False
    return True


def remove_owned_workdir(name, directory):
    if not directory.exists():
        return True
    if not directory.is_dir():
        return False
    marker = directory / "OWNER"
    try:
        names = {child.name for child in directory.iterdir()}
        if marker.is_file() and marker.read_text().strip() == name:
            for child in sorted(directory.rglob("*"), reverse=True):
                if child.is_file() or child.is_symlink():
                    child.unlink()
                elif child.is_dir():
                    child.rmdir()
            directory.rmdir()
            return True
        # Prep state before provision: only the uploaded helper may be present.
        if names <= {"helper.py"}:
            helper = directory / "helper.py"
            if helper.exists():
                helper.unlink()
            directory.rmdir()
            return True
    except OSError:
        return False
    return False


def cleanup(name):
    require_name(name)
    failed = False
    for kind in ("container", "network"):
        if not remove_owned(kind, name):
            failed = True
    if not remove_owned_workdir(name, workdir_for(name)):
        failed = True
    if failed:
        raise HelperError(
            "Cleanup incomplete; inspect resources labelled org.sqloryn.disposable-probe "
            "with the exact run name before retrying.")
    emit({"ok": True, "name": name})
    return 0


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    try:
        if argv == ["engine-check"]:
            return engine_check()
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
