"""Audit tracked files and reachable Git history before public publication."""
from pathlib import Path
import argparse
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
APPROVED = {
    '.editorconfig', '.gitattributes', '.gitignore', 'README.md', 'LICENSE',
    'CONTRIBUTING.md', 'make_icon.py', 'assets/banner.svg',
    '.github/workflows/build.yml', 'scripts/check_public_repo.py',
    'src/Core.cs', 'src/Program.cs', 'src/SelfTest.cs',
    'src/VerseMirror.csproj', 'src/mirror.ico',
}
PATTERNS = {
    'absolute drive path': re.compile(r'\b[A-Za-z]:[\\/]'),
    'absolute user-home path': re.compile(r'/(?:Users|home)/[A-Za-z0-9_.-]+/', re.I),
    'GitHub access token': re.compile(r'(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{20,}'),
    'private key material': re.compile(r'-----BEGIN [A-Z ]*PRIVATE KEY-----'),
}

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--deny-value', action='append', default=[], help='Additional private string that must not appear')
    args = parser.parse_args()
    failures = []
    tracked = git('ls-files', '-z').decode().split('\0')
    for name in filter(None, tracked):
        if name not in APPROVED:
            failures.append(f'Unapproved tracked file: {name}')
    def check(data, label):
        try:
            text = data.decode('utf-8-sig')
        except UnicodeDecodeError:
            if label.endswith('src/mirror.ico'):
                return
            failures.append(f'Unexpected binary: {label}')
            return
        for description, pattern in PATTERNS.items():
            if pattern.search(text):
                failures.append(f'{description}: {label}')
        for private in args.deny_value:
            if private and private.casefold() in text.casefold():
                failures.append(f'Forbidden private string: {label}')
    for name in filter(None, tracked):
        check((ROOT / name).read_bytes(), name)
    try:
        commits = git('rev-list', '--all').decode().splitlines()
    except subprocess.CalledProcessError:
        commits = []
    seen = set()
    for commit in commits:
        for record in git('ls-tree', '-r', '-z', commit).split(b'\0'):
            if not record:
                continue
            meta, encoded_name = record.split(b'\t', 1)
            name = encoded_name.decode()
            if name not in APPROVED:
                failures.append(f'Unapproved historical file: {name}')
            sha = meta.split()[2].decode()
            if sha not in seen:
                seen.add(sha)
                check(git('cat-file', 'blob', sha), 'history:' + name)
        metadata = git('show', '-s', '--format=%an <%ae>%n%cn <%ce>%n%B', commit)
        check(metadata, 'commit metadata')
        for email in re.findall(rb'<([^>]+)>', metadata):
            if not email.endswith(b'@users.noreply.github.com'):
                failures.append('Commit metadata contains a non-noreply email')
    if failures:
        print('\n'.join(sorted(set(failures))), file=sys.stderr)
        return 1
    print(f'Privacy audit passed: {len(list(filter(None, tracked)))} tracked files, {len(commits)} commits, {len(seen)} historical blobs.')
    return 0

if __name__ == '__main__':
    sys.exit(main())
