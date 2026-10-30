#!/usr/bin/env python3
"""Minimal repository path checks. GitHub Secret Protection/CodeQL provide separate controls."""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
REQUIRED = [
    "README.md", "LICENSE", "SECURITY.md", "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md", "AGENTS.md", ".gitignore",
    ".github/CODEOWNERS", ".github/dependabot.yml",
    ".github/workflows/repository-safeguards.yml",
    "docs/SECURITY_BASELINE.md", "docs/FEATURE_MATRIX.md",
]
problems = [f"Missing: {f}" for f in REQUIRED if not (ROOT / f).is_file()]
if (ROOT / ".git").exists():
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
else:
    paths = [str(p.relative_to(ROOT)) for p in ROOT.rglob("*") if p.is_file()]
for path in filter(None, paths):
    p = Path(path)
    name = p.name.lower()
    suffix = p.suffix.lower()
    if (name in {".env", "id_rsa", "id_ed25519", ".npmrc", ".pypirc"} or
        (name.startswith(".env.") and name not in {".env.example", ".env.template"}) or
        suffix in {".pem", ".key", ".p8", ".p12", ".pfx", ".keystore", ".jks", ".kdbx", ".bak", ".mdf", ".ldf"}):
        problems.append(f"Forbidden tracked path: {path}")
    if path.startswith(("node_modules/", "target/", "dist/", "build/", "obj/")):
        problems.append(f"Generated build/dependency artifact: {path}")
if problems:
    print("\n".join(problems), file=sys.stderr)
    sys.exit(1)
print("Repository baseline: PASS (path checks only; run secret scanning separately)")
