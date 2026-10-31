#!/usr/bin/env python3
"""Run fixed synthetic checks against a newly created, disposable SQL Server."""
import argparse
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
PROBE = ROOT / "experiments/SqlClientProbe/bin/Release/net10.0/SqlClientProbe.dll"
# SQL Server 2022 CU27 GDR1, Ubuntu 22.04; resolved from MCR on 2026-10-31.
IMAGE = "mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718"
LABEL = "org.sqloryn.disposable-probe"


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


def execute():
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
            result = subprocess.run(
                ["dotnet", str(PROBE), "--confirm-disposable", "--port", str(port), "--expected-server", name,
                 "--certificate", str(certs / "server.crt"), "--wrong-certificate", str(certs / "wrong.crt")],
                cwd=ROOT, input=password + "\n", text=True, capture_output=True, timeout=110)
            # Only the probe's bounded evidence lines are eligible for public CI output.
            for line in result.stdout.splitlines():
                if line.startswith(("PASS PROBE-", "Client: ")):
                    print(line)
            if result.returncode:
                for line in result.stderr.splitlines():
                    if line.startswith(("FAIL PROBE-", "FAIL SQL request", "FAIL TLS", "FAIL interrupted")):
                        print(line, file=sys.stderr)
                raise ProbeError("SQL acceptance suite failed; no compatibility claim is valid.")
        finally:
            cleanup(name)
    print("PASS PROBE-10 disposable container/network and temporary files removed.")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--accept-sql-server-license", action="store_true",
                        help="Explicitly accept Microsoft's SQL Server Developer test-use terms for this run.")
    args = parser.parse_args(argv)
    if not args.accept_sql_server_license:
        parser.error("Explicit --accept-sql-server-license is required; no Docker resources were created.")
    try:
        execute()
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
