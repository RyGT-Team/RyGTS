"""Resolve GitHub sources and publish CI packages to GitLab."""
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

API = 'https://gitlab.com/api/v4/projects/rygt-team%2FRyGTS'
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
    needed = 'false'
    for selected in channels:
        url = API + '/packages/generic/' + selected + '/' + package_version + '/CI-BUILD.json'
        try:
            with urllib.request.urlopen(url, timeout=60) as response:
                previous = json.load(response)
            if (previous.get('commit') != commit or previous.get('channel') != selected or
                    previous.get('renderer_commit') != renderer):
                raise RuntimeError('Existing package marker has unexpected identity')
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            needed = 'true'
    if os.environ.get('FORCE_BUILD') == 'true':
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


def publish(channel=None):
    channels = selected_channels(channel)
    commit = os.environ['SOURCE_SHA']
    renderer = os.environ['RENDERER_SHA']
    package_version = version(commit, renderer)
    folder = Path('source/dist')
    manifest = json.loads((folder / 'CI-PACKAGES.json').read_text())
    variants = manifest.get('variants', [])
    if (manifest.get('schema') != 2 or manifest.get('commit') != commit or
            [v.get('channel') for v in variants] != list(channels) or
            manifest.get('renderer_commit', renderer) != renderer):
        raise RuntimeError('Package manifest channels do not match selection')
    packages = []
    # Validate every package before making any network writes.
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
    run = f'https://github.com/{os.environ["GITHUB_REPOSITORY"]}/actions/runs/{os.environ["GITHUB_RUN_ID"]}'
    uploads = []
    markers = []
    for channel, archive, checksum, digest in packages:
        marker = folder / channel / 'CI-BUILD.json'
        marker.parent.mkdir(exist_ok=True)
        marker.write_text(json.dumps({'type': 'CI build', 'branch': 'development', 'commit': commit,
                                     'renderer_commit': renderer,
                                     'channel': channel, 'build_url': run, 'filename': archive.name,
                                     'sha256': digest}, indent=2) + '\n')
        uploads.extend(((channel, archive), (channel, checksum)))
        markers.append((channel, marker))
    token = os.environ['GITLAB_PACKAGE_TOKEN']
    # Marker is uploaded last: source resolution only skips fully published packages.
    for channel, file in uploads + markers:
        url = API + '/packages/generic/' + channel + '/' + package_version + '/' + file.name
        with file.open('rb') as stream:
            request = urllib.request.Request(url, data=stream, method='PUT', headers={
                'DEPLOY-TOKEN': token, 'Content-Length': str(file.stat().st_size),
                'Content-Type': 'application/octet-stream'})
            with urllib.request.urlopen(request, timeout=600) as response:
                if response.status != 201:
                    raise RuntimeError('Package upload did not return Created')
        # Verify server content before publishing the completion marker.
        with urllib.request.urlopen(url, timeout=600) as response:
            remote_digest = hashlib.file_digest(response, 'sha256').hexdigest()
        with file.open('rb') as stream:
            local_digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        if remote_digest != local_digest:
            raise RuntimeError('Uploaded package checksum mismatch')
        print('Published and verified:', url)
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as output:
            output.write('### Verified CI packages\n\n'
                         f'Source: [`{commit}`](https://github.com/RyGT-Team/RyGTS/commit/{commit})\n\n'
                         f'Renderer: [`{renderer}`](https://github.com/RyGT-Team/shadGT/commit/{renderer})\n\n')
            for channel, archive, checksum, digest in packages:
                output.write(f'{channel}: [ZIP]({API}/packages/generic/{channel}/{package_version}/{archive.name})'
                             f' | [SHA-256 file]({API}/packages/generic/{channel}/{package_version}/{checksum.name})\n\n'
                             f'SHA-256: `{digest}`\n\n')


if __name__ == '__main__':
    import sys
    command = {'resolve': resolve, 'publish': publish}[sys.argv[1]]
    command(sys.argv[2] if len(sys.argv) > 2 else None)
