import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import urllib.error


spec = importlib.util.spec_from_file_location('registry_ci', Path(__file__).resolve().parents[1] / 'github-registry' / 'registry_ci.py')
registry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(registry)
SOURCE = 'a' * 40
RENDERER = 'b' * 40
OTHER = 'c' * 40
REPOSITORY = 'https://github.com/RyGT-Team/shadGT.git'


def response(value):
    return io.BytesIO(json.dumps(value).encode())


def policy(commit='HEAD', repository=REPOSITORY, branch='ryty-gpu'):
    return {'repository': repository, 'branch': branch, 'commit': commit}


class IsolatedWorkflowFiles(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.workflow_folder = Path(temporary.name)
        self.summary = self.workflow_folder / 'summary'
        self.workflow_output = self.workflow_folder / 'output'
        environment = patch.dict(os.environ, {
            'GITHUB_STEP_SUMMARY': str(self.summary), 'GITHUB_OUTPUT': str(self.workflow_output),
            'SOURCE_OVERRIDE_SHA': '', 'RENDERER_OVERRIDE_SHA': '', 'FORCE_BUILD': 'false',
        })
        environment.start(); self.addCleanup(environment.stop)
        os.environ.pop('CI_CHANNEL', None)


class RendererRevisionTests(IsolatedWorkflowFiles):
    def test_pinned_revision_uses_source_policy_only(self):
        with patch.object(registry.urllib.request, 'urlopen', return_value=response(policy(RENDERER))) as request:
            self.assertEqual(registry.renderer_revision(SOURCE), RENDERER)
        request.assert_called_once_with('https://raw.githubusercontent.com/RyGT-Team/RyGTS/' + SOURCE + '/renderer/source.json', timeout=60)

    def test_head_resolves_official_branch_once(self):
        with patch.object(registry.urllib.request, 'urlopen', side_effect=[response(policy()), response({'sha': RENDERER})]) as request:
            self.assertEqual(registry.renderer_revision(SOURCE), RENDERER)
        self.assertEqual(request.call_count, 2)
        self.assertEqual(request.call_args_list[1].args[0], 'https://api.github.com/repos/RyGT-Team/shadGT/commits/ryty-gpu')

    def test_repository_comparison_ignores_case(self):
        with patch.object(registry.urllib.request, 'urlopen', return_value=response(policy(RENDERER, REPOSITORY.upper()))):
            self.assertEqual(registry.renderer_revision(SOURCE), RENDERER)

    def test_invalid_policy_rejected_without_branch_request(self):
        for config in (policy(repository='https://example.com/foreign.git'), policy(branch='main'), policy(commit='abc'), {}, []):
            with self.subTest(config=config), patch.object(registry.urllib.request, 'urlopen', return_value=response(config)) as request:
                with self.assertRaises((ValueError, RuntimeError, TypeError)):
                    registry.renderer_revision(SOURCE)
                self.assertEqual(request.call_count, 1)

    def test_invalid_head_rejected(self):
        with patch.object(registry.urllib.request, 'urlopen', side_effect=[response(policy()), response({'sha': 'bad'})]):
            with self.assertRaises((ValueError, RuntimeError)):
                registry.renderer_revision(SOURCE)

    def test_version_includes_both_validated_revisions(self):
        self.assertEqual(registry.version(SOURCE, RENDERER), f'development-{SOURCE}-renderer-{RENDERER}')
        self.assertNotEqual(registry.version(SOURCE, RENDERER), registry.version(SOURCE, OTHER))
        for source, renderer in (('bad', RENDERER), (SOURCE, 'bad')):
            with self.subTest(source=source, renderer=renderer), self.assertRaises(ValueError):
                registry.version(source, renderer)


class ResolveTests(IsolatedWorkflowFiles):
    def test_github_only_runtime_and_renderer_overrides_need_no_gitlab_source(self):
        requests = []
        def open_url(url, timeout):
            requests.append(url)
            if url == registry.SOURCE_API + '/commits/' + SOURCE:
                return response({'sha': SOURCE})
            if url == 'https://raw.githubusercontent.com/RyGT-Team/RyGTS/' + SOURCE + '/renderer/source.json':
                return response(policy())
            if url == registry.RENDERER_API + '/commits/' + RENDERER:
                return response({'sha': RENDERER})
            if url.startswith(registry.API + '/packages/generic/'):
                raise urllib.error.HTTPError(url, 404, 'Not Found', {}, None)
            self.fail('Unexpected source request: ' + url)
        with patch.dict(os.environ, {'SOURCE_OVERRIDE_SHA': SOURCE, 'RENDERER_OVERRIDE_SHA': RENDERER}), \
                patch.object(registry.urllib.request, 'urlopen', side_effect=open_url):
            registry.resolve('ci')
        self.assertEqual(len(requests), 4)
        self.assertIn('renderer_commit=' + RENDERER, self.workflow_output.read_text())

    def test_missing_github_source_never_falls_back_to_gitlab(self):
        missing = urllib.error.HTTPError('url', 404, 'Not Found', {}, None)
        with patch.dict(os.environ, {'SOURCE_OVERRIDE_SHA': SOURCE}), \
                patch.object(registry.urllib.request, 'urlopen', side_effect=missing) as request, \
                patch('github_source.time.sleep'), \
                patch('github_source.wait_for_commit', side_effect=AssertionError('unexpected fallback')):
            with self.assertRaisesRegex(RuntimeError, 'GitHub did not provide'):
                registry.resolve('ci')
        self.assertTrue(all(call.args[0] == registry.SOURCE_API + '/commits/' + SOURCE
                            for call in request.call_args_list))

    def run_resolve(self, markers, channel=None):
        requests = []
        def open_url(url, timeout):
            requests.append(url)
            if url.endswith('/commits/development'):
                return response({'sha': SOURCE})
            channel = 'debug-ci' if '/debug-ci/' in url else 'ci'
            marker = markers[channel]
            if marker is None:
                raise urllib.error.HTTPError(url, 404, 'Not Found', {}, None)
            return response(marker)
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / 'output'
            env = {'GITHUB_OUTPUT': str(output), 'FORCE_BUILD': 'false'}
            if channel is not None:
                env['CI_CHANNEL'] = channel
            with patch.dict(os.environ, env), patch.object(registry.urllib.request, 'urlopen', side_effect=open_url), patch.object(registry, 'renderer_revision', return_value=RENDERER) as renderer:
                registry.resolve()
            renderer.assert_called_once_with(SOURCE)
            return dict(line.split('=', 1) for line in output.read_text().splitlines()), requests

    def test_existing_pair_skips_build(self):
        markers = {channel: {'commit': SOURCE, 'renderer_commit': RENDERER, 'channel': channel} for channel in registry.CHANNELS}
        output, requests = self.run_resolve(markers)
        self.assertEqual(output, {'commit': SOURCE, 'renderer_commit': RENDERER, 'version': f'development-{SOURCE}-renderer-{RENDERER}', 'needed': 'false'})
        self.assertEqual(sum('/commits/development' in url for url in requests), 1)
        self.assertTrue(all(output['version'] in url for url in requests[1:]))
        summary = self.summary.read_text()
        self.assertIn(f'[`{SOURCE}`](https://github.com/RyGT-Team/RyGTS/commit/{SOURCE})', summary)
        self.assertIn(f'[`{RENDERER}`](https://github.com/RyGT-Team/shadGT/commit/{RENDERER})', summary)
        self.assertNotIn('/packages/generic/', summary)
        self.assertNotIn('[ZIP]', summary)

    def test_missing_pair_requires_build(self):
        output, _ = self.run_resolve({'ci': None, 'debug-ci': None})
        self.assertEqual(output['needed'], 'true')

    def test_mismatching_marker_rejected(self):
        for field, value in (('commit', OTHER), ('renderer_commit', OTHER), ('channel', 'debug-ci')):
            markers = {channel: {'commit': SOURCE, 'renderer_commit': RENDERER, 'channel': channel} for channel in registry.CHANNELS}
            markers['ci'][field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                self.run_resolve(markers)

    def test_selected_channel_resolves_independently(self):
        markers = {'ci': None, 'debug-ci': {'commit': SOURCE, 'renderer_commit': RENDERER, 'channel': 'debug-ci'}}
        output, requests = self.run_resolve(markers, 'ci')
        self.assertEqual(output['needed'], 'true')
        self.assertEqual(sum('/packages/generic/' in url for url in requests), 1)
        self.assertIn('/packages/generic/ci/', requests[-1])

        markers = {'ci': None, 'debug-ci': {'commit': SOURCE, 'renderer_commit': RENDERER, 'channel': 'debug-ci'}}
        output, requests = self.run_resolve(markers, 'debug-ci')
        self.assertEqual(output['needed'], 'false')
        self.assertEqual(sum('/packages/generic/' in url for url in requests), 1)
        self.assertIn('/packages/generic/debug-ci/', requests[-1])

    def test_unknown_channel_rejected_before_network(self):
        with patch.dict(os.environ, {'GITHUB_OUTPUT': 'unused', 'CI_CHANNEL': 'wrong'}), patch.object(registry.urllib.request, 'urlopen') as request:
            with self.assertRaises(ValueError):
                registry.resolve()
            request.assert_not_called()


class PublishTests(IsolatedWorkflowFiles):
    def test_matching_pair_publishes_both_channels_with_markers_last(self):
        import hashlib
        import zipfile

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); folder = root / 'source' / 'dist'
            folder.mkdir(parents=True); variants = []
            for channel in registry.CHANNELS:
                archive = folder / f'RyGTS-{channel}-{SOURCE[:8]}.zip'
                info = {'renderer_source': f'{REPOSITORY}\n{RENDERER}\n'}
                with zipfile.ZipFile(archive, 'w') as package:
                    package.writestr(f'{archive.stem}/BUILD_INFO.txt', json.dumps(info))
                digest = hashlib.sha256(archive.read_bytes()).hexdigest()
                archive.with_suffix('.zip.sha256').write_text(f'{digest}  {archive.name}\n')
                variants.append({'channel': channel, 'archive': archive.name, 'sha256': digest})
            (folder / 'CI-PACKAGES.json').write_text(json.dumps({'schema': 2, 'commit': SOURCE, 'renderer_commit': RENDERER, 'variants': variants}))
            uploads = {}; order = []
            class Response(io.BytesIO):
                status = 201
            def open_url(request, timeout):
                if isinstance(request, str): return Response(uploads[request])
                uploads[request.full_url] = request.data.read(); order.append(request.full_url)
                return Response()
            original = Path.cwd()
            try:
                os.chdir(root)
                env = {'SOURCE_SHA': SOURCE, 'RENDERER_SHA': RENDERER, 'GITHUB_REPOSITORY': 'RyGT-Team/RyGT-ci',
                       'GITHUB_RUN_ID': '123', 'GITLAB_PACKAGE_TOKEN': 'test-token', 'GITHUB_STEP_SUMMARY': str(root / 'summary')}
                with patch.dict(os.environ, env), patch.object(registry.urllib.request, 'urlopen', side_effect=open_url):
                    registry.publish()
                self.assertEqual(len(order), 6)
                self.assertTrue(all(f'development-{SOURCE}-renderer-{RENDERER}/' in url for url in order))
                self.assertTrue(all(url.endswith('/CI-BUILD.json') for url in order[-2:]))
                self.assertTrue(all(json.loads(uploads[url])['renderer_commit'] == RENDERER for url in order[-2:]))
                summary = (root / 'summary').read_text()
                self.assertIn(f'/commit/{SOURCE}', summary)
                self.assertIn(f'/commit/{RENDERER}', summary)
                for variant in variants:
                    self.assertIn(f'SHA-256: `{variant["sha256"]}`', summary)
                    self.assertIn(variant['archive'], summary)
            finally:
                os.chdir(original)

    def test_wrong_renderer_in_archive_rejected_before_network_writes(self):
        import hashlib
        import zipfile

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder = root / 'source' / 'dist'
            folder.mkdir(parents=True)
            variants = []
            for channel in registry.CHANNELS:
                archive = folder / f'RyGTS-{channel}-{SOURCE[:8]}.zip'
                info = {'renderer_source': f'{REPOSITORY}\n{OTHER}\n'}
                with zipfile.ZipFile(archive, 'w') as package:
                    package.writestr(f'{archive.stem}/BUILD_INFO.txt', json.dumps(info))
                digest = hashlib.sha256(archive.read_bytes()).hexdigest()
                archive.with_suffix('.zip.sha256').write_text(f'{digest}  {archive.name}\n')
                variants.append({'channel': channel, 'archive': archive.name, 'sha256': digest})
            (folder / 'CI-PACKAGES.json').write_text(json.dumps({'schema': 2, 'commit': SOURCE, 'variants': variants}))
            original = Path.cwd()
            try:
                os.chdir(root)
                with patch.dict(os.environ, {'SOURCE_SHA': SOURCE, 'RENDERER_SHA': RENDERER}), patch.object(registry.urllib.request, 'urlopen') as request:
                    with self.assertRaises((ValueError, RuntimeError)):
                        registry.publish()
                    request.assert_not_called()
            finally:
                os.chdir(original)

    def test_selected_channel_publishes_only_its_package_and_marker(self):
        import hashlib
        import zipfile

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); folder = root / 'source' / 'dist'
            folder.mkdir(parents=True)
            channel = 'debug-ci'
            archive = folder / f'RyGTS-{channel}-{SOURCE[:8]}.zip'
            info = {'renderer_source': f'{REPOSITORY}\n{RENDERER}\n'}
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr(f'{archive.stem}/BUILD_INFO.txt', json.dumps(info))
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            archive.with_suffix('.zip.sha256').write_text(f'{digest}  {archive.name}\n')
            (folder / 'CI-PACKAGES.json').write_text(json.dumps({
                'schema': 2, 'commit': SOURCE, 'renderer_commit': RENDERER,
                'variants': [{'channel': channel, 'archive': archive.name, 'sha256': digest}],
            }))
            uploads = {}; order = []
            class Response(io.BytesIO):
                status = 201
            def open_url(request, timeout):
                if isinstance(request, str): return Response(uploads[request])
                uploads[request.full_url] = request.data.read(); order.append(request.full_url)
                return Response()
            original = Path.cwd()
            try:
                os.chdir(root)
                env = {'SOURCE_SHA': SOURCE, 'RENDERER_SHA': RENDERER, 'CI_CHANNEL': channel,
                       'GITHUB_REPOSITORY': 'RyGT-Team/RyGT-ci', 'GITHUB_RUN_ID': '123',
                       'GITLAB_PACKAGE_TOKEN': 'test-token', 'GITHUB_STEP_SUMMARY': str(root / 'summary')}
                with patch.dict(os.environ, env), patch.object(registry.urllib.request, 'urlopen', side_effect=open_url):
                    registry.publish()
                self.assertEqual(len(order), 3)
                self.assertTrue(all('/debug-ci/' in url for url in order))
                self.assertTrue(order[-1].endswith('/CI-BUILD.json'))
                self.assertIn(f'SHA-256: `{digest}`', (root / 'summary').read_text())
                with patch.dict(os.environ, {'SOURCE_SHA': SOURCE, 'RENDERER_SHA': RENDERER}), patch.object(registry.urllib.request, 'urlopen') as request:
                    with self.assertRaisesRegex(RuntimeError, 'channels do not match'):
                        registry.publish('ci')
                    request.assert_not_called()
            finally:
                os.chdir(original)


class WorkflowEnvironmentIsolationTests(unittest.TestCase):
    def test_fixtures_do_not_write_hosted_workflow_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            summary, output = root / 'host-summary', root / 'host-output'
            summary.write_text('real hosted summary\n')
            output.write_text('real_output=unchanged\n')
            environment = dict(os.environ, GITHUB_ACTIONS='true',
                               GITHUB_STEP_SUMMARY=str(summary), GITHUB_OUTPUT=str(output))
            completed = subprocess.run([sys.executable, str(Path(__file__).resolve()),
                                        'RendererRevisionTests', 'ResolveTests', 'PublishTests'],
                                       env=environment, capture_output=True, text=True)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            self.assertEqual(summary.read_text(), 'real hosted summary\n')
            self.assertEqual(output.read_text(), 'real_output=unchanged\n')


if __name__ == '__main__':
    unittest.main()
