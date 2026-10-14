"""Resolve only numeric PackageReference version conflicts in merged projects."""

import re
import sys
from pathlib import Path


PACKAGE = re.compile(
    r'^(?P<prefix>[ \t]*<PackageReference Include="(?P<name>[^"]+)" Version=")'
    r'(?P<version>[^"]+)(?P<suffix>"[ \t]*/>[ \t]*)(?P<cr>\r?)$',
    re.MULTILINE,
)
CONFLICT = re.compile(
    r'^<<<<<<< [^\r\n]*\r?\n(?P<ours>.*?)^=======\r?\n'
    r'(?P<theirs>.*?)^>>>>>>> [^\r\n]*(?:\r?\n|$)',
    re.MULTILINE | re.DOTALL,
)
MARKER = re.compile(r'^(?:<<<<<<<|=======|>>>>>>>|\|\|\|\|\|\|\|)', re.MULTILINE)
NUMERIC_VERSION = re.compile(r'[0-9]+(?:\.[0-9]+)*')


def normalized(text):
    text = text.replace('\r\n', '\n').replace('\r', '\n')
    return '\n'.join(line.rstrip(' \t') for line in text.split('\n'))


def masked(text):
    return PACKAGE.sub(lambda match: match['prefix'] + '<VERSION>' + match['suffix'], normalized(text))


def resolve_block(ours, theirs):
    if masked(ours) != masked(theirs):
        raise ValueError('conflict contains changes other than package versions')

    ours_refs = list(PACKAGE.finditer(ours))
    theirs_refs = list(PACKAGE.finditer(theirs))
    if not ours_refs or len(ours_refs) != len(theirs_refs):
        raise ValueError('conflict does not contain matching simple PackageReferences')

    replacements = []
    for ours_ref, theirs_ref in zip(ours_refs, theirs_refs):
        if ours_ref['name'] != theirs_ref['name']:
            raise ValueError('conflict changes package identities')
        ours_version, theirs_version = ours_ref['version'], theirs_ref['version']
        if ours_version == theirs_version:
            continue
        if not all(NUMERIC_VERSION.fullmatch(version) for version in (ours_version, theirs_version)):
            raise ValueError('conflict contains a non-numeric package version')

        # Treat trailing zero components equally and retain the fork on a tie.
        ours_key = tuple(map(int, ours_version.split('.')))
        theirs_key = tuple(map(int, theirs_version.split('.')))
        width = max(len(ours_key), len(theirs_key))
        if theirs_key + (0,) * (width - len(theirs_key)) > ours_key + (0,) * (width - len(ours_key)):
            replacements.append((ours_ref.start('version'), ours_ref.end('version'), theirs_version))

    for start, end, version in reversed(replacements):
        ours = ours[:start] + version + ours[end:]
    return ours


def resolve_text(text):
    resolved, count = CONFLICT.subn(lambda match: resolve_block(match['ours'], match['theirs']), text)
    if not count or MARKER.search(resolved):
        raise ValueError('missing, malformed, or unsupported conflict markers')
    return resolved, count


def resolve_file(path):
    if path.suffix != '.csproj':
        raise ValueError('only project files may be resolved')
    with path.open('r', encoding='utf-8', newline='') as handle:
        original = handle.read()
    # Validate every conflict before writing, so a real conflict leaves the file intact.
    resolved, count = resolve_text(original)
    with path.open('w', encoding='utf-8', newline='') as handle:
        handle.write(resolved)
    return count


if __name__ == '__main__':
    try:
        project = Path(sys.argv[1])
        count = resolve_file(project)
    except (IndexError, OSError, ValueError) as error:
        print(f'Package conflict remains manual: {error}', file=sys.stderr)
        sys.exit(1)
    print(f'Resolved {count} package-version conflict(s): {project}')
