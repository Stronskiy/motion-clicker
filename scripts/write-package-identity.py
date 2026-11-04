#!/usr/bin/env python3
"""Write exact-head identity for a local generated package, never acceptance media."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--platform', choices=['macOS', 'Windows'], required=True)
    parser.add_argument('--toolchain', required=True)
    parser.add_argument('--signing', required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    for options in ([], ['--cached']):
        subprocess.run(['git', 'diff', '--quiet', *options], cwd=repo, check=True)
    source = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
    locks = [repo / 'macos/Package.swift', repo / 'windows/global.json', *sorted((repo / 'windows').glob('*/packages.lock.json'))]
    files = {p.relative_to(args.root).as_posix(): sha256(p) for p in sorted(args.root.rglob('*'))
             if p.is_file() and p.name != 'artifact-identity.json'}
    identity = {'schemaVersion': 1, 'sourceRevision': source, 'trackedSourceClean': True,
                'platform': args.platform, 'architecture': 'arm64' if args.platform == 'macOS' else 'x64',
                'minimumOS': 'macOS 26.0' if args.platform == 'macOS' else 'Windows 11 build 22000',
                'toolchain': args.toolchain, 'signing': args.signing,
                'dependencyLocks': {p.relative_to(repo).as_posix(): sha256(p) for p in locks},
                'files': files, 'runtimeAcceptance': 'UNVERIFIED; source/build package only',
                'fixture': 'Separate process; own-window generated target only; no global hook, injection, capture or audio'}
    (args.root / 'artifact-identity.json').write_text(json.dumps(identity, indent=2) + '\n')


if __name__ == '__main__':
    main()
