"""Resolve GitHub sources and verify CI packages."""
import sys
sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
from github_source import wait_for_commit
import hashlib
import json
import os
from pathlib import Path
import re
import urllib.error
import urllib.request
import zipfile

SOURCE_API = 'https://api.github.com/repos/RyGT-Team/RyGTS'
RENDERER_API = 'https://api.github.com/repos/RyGT-Team/shadGT'
CHANNELS = ('ci', 'debug-ci')


def selected_channels(channel=None):
    channel = channel or os.environ.get('CI_CHANNEL')
    if channel is None:
        return CHANNELS
    if channel not in CHANNELS:
        raise ValueError('Invalid CI package channel')
    return (channel,)


def version(commit, renderer):
    if not re.fullmatch('[0-9a-f]{40}', commit) or not re.fullmatch('[0-9a-f]{40}', renderer):
        raise ValueError('Invalid source or renderer commit')
    return 'development-' + commit + '-renderer-' + renderer


def renderer_revision(commit):
    with urllib.request.urlopen('https://raw.githubusercontent.com/RyGT-Team/RyGTS/' + commit + '/renderer/source.json', timeout=60) as response:
        policy = json.load(response)
    if (not isinstance(policy, dict) or not isinstance(policy.get('repository'), str) or
            policy['repository'].lower() != 'https://github.com/rygt-team/shadgt.git' or
            policy.get('branch') != 'ryty-gpu'):
        raise ValueError('Unexpected renderer repository or branch')
    revision = policy.get('commit', '')
    override = os.environ.get('RENDERER_OVERRIDE_SHA', '')
    if override:
        if revision != 'HEAD' and revision != override:
            raise ValueError('Requested renderer conflicts with the runtime renderer pin')
        return wait_for_commit(RENDERER_API, override)
    if revision == 'HEAD':
        url = RENDERER_API + '/commits/ryty-gpu'
        with urllib.request.urlopen(url, timeout=60) as response:
            revision = json.load(response)['sha']
    if not isinstance(revision, str) or not re.fullmatch('[0-9a-f]{40}', revision):
        raise ValueError('Invalid renderer revision')
    return revision


def resolve(channel=None):
    channels = selected_channels(channel)
    commit = os.environ.get('SOURCE_OVERRIDE_SHA', '')
    if commit:
        commit = wait_for_commit(SOURCE_API, commit)
    else:
        with urllib.request.urlopen(SOURCE_API + '/commits/development', timeout=60) as response:
            commit = json.load(response)['sha']
    if not isinstance(commit, str) or not re.fullmatch('[0-9a-f]{40}', commit):
        raise ValueError('Invalid source commit')
    renderer = renderer_revision(commit)
    package_version = version(commit, renderer)
    needed = 'true'
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write(f'commit={commit}\nrenderer_commit={renderer}\nversion={package_version}\nneeded={needed}\n')
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as output:
            output.write('### Resolved build sources\n\n'
                         f'- Source: [`{commit}`](https://github.com/RyGT-Team/RyGTS/commit/{commit})\n'
                         f'- Renderer: [`{renderer}`](https://github.com/RyGT-Team/shadGT/commit/{renderer})\n'
                         f'- Channels: {", ".join(channels)}\n'
                         f'- Build needed: `{needed}`\n\n')
    print(f'GitHub development: {commit}; renderer: {renderer}; channel: {", ".join(channels)}; build needed: {needed}')


def verify(channel=None):
    channels = selected_channels(channel)
    commit = os.environ['SOURCE_SHA']
    renderer = os.environ['RENDERER_SHA']
    package_version = version(commit, renderer)
    folder = Path('source/dist')
    manifest = json.loads((folder / 'CI-PACKAGES.json').read_text())
    variants = manifest.get('variants', [])
    if (manifest.get('schema') != 2 or manifest.get('commit') != commit or
            [v.get('channel') for v in variants] != list(channels) or
            manifest.get('renderer_commit') != renderer):
        raise RuntimeError('Package manifest channels do not match selection')
    packages = []
    # Validate all artifacts before upload.
    for variant in variants:
        channel = variant['channel']
        archive = folder / f'RyGTS-{channel}-{commit[:8]}.zip'
        checksum = archive.with_suffix('.zip.sha256')
        digest, name = checksum.read_text().strip().split('  ', 1)
        with archive.open('rb') as stream:
            actual = hashlib.file_digest(stream, 'sha256').hexdigest()
        if (name != archive.name or actual != digest or variant.get('archive') != name or
                variant.get('sha256') != digest):
            raise RuntimeError('Final package checksum mismatch')
        with zipfile.ZipFile(archive) as package:
            build = json.loads(package.read(archive.stem + '/BUILD_INFO.txt'))
        source = build.get('renderer_source', '').splitlines()
        if (len(source) != 2 or source[0].lower() != 'https://github.com/rygt-team/shadgt.git' or
                source[1] != renderer):
            raise RuntimeError('Package renderer differs from resolved CI revision')
        packages.append((channel, archive, checksum, digest))
    for channel, archive, checksum, digest in packages:
        print(f'Verified {channel}: {archive.name} SHA-256 {digest}')


if __name__ == '__main__':
    import sys
    command = {'resolve': resolve, 'verify': verify}[sys.argv[1]]
    command(sys.argv[2] if len(sys.argv) > 2 else None)
