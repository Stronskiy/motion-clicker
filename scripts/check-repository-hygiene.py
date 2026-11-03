#!/usr/bin/env python3
"""Fail if tracked paths include local/private state or generated outputs.

Uses the index, so adding a file forcibly despite .gitignore cannot bypass it.
Source, original assets, dependency locks and synthetic contracts/fixtures are allowed.
"""
from pathlib import PurePosixPath
import subprocess
import sys


def violation(name: str) -> bool:
    path = PurePosixPath(name)
    parts = set(path.parts)
    forbidden = {
        "docs", ".superpowers", ".codex", ".agents", ".worktrees", "recordings",
        "projects-local", "dist", "build", ".build", "DerivedData", "bin", "obj",
        "TestResults", "__pycache__", ".vs", ".idea", "xcuserdata", "node_modules",
    }
    if parts & forbidden:
        return True
    lower = name.lower()
    leaf = path.name.lower()
    if leaf in {".ds_store", "thumbs.db", "desktop.ini"}:
        return True
    if (leaf == ".env" or leaf.startswith(".env.")) and leaf != ".env.example":
        return True
    return lower.endswith((".p12", ".pfx", ".key", ".mobileprovision", ".xcuserstate",
                           ".suo", ".user", ".dll", ".exe", ".dylib", ".so", ".pdb",
                           ".mp4", ".mov", ".mkv", ".avi", ".webm", ".log", ".trx")) or any(
                               p.lower().endswith((".app", ".dsym")) for p in path.parts)


if __name__ == "__main__":
    tracked = subprocess.check_output(["git", "ls-files", "-z"]).decode("utf-8").split("\0")
    bad = sorted(name for name in tracked if name and violation(name))
    if bad:
        print("Forbidden tracked local/private/generated files:\n" + "\n".join(bad), file=sys.stderr)
        sys.exit(1)
    print(f"Repository hygiene PASS ({sum(bool(n) for n in tracked)} tracked paths)")
