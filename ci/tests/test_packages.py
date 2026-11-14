"""Exercise both archives and redistribution boundaries without network/builds."""
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import struct
import tempfile
import unittest
from unittest.mock import patch
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('package', ROOT / 'ci/package.py')
pkg = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pkg)
prepare_spec = importlib.util.spec_from_file_location('prepare_renderer', ROOT / 'ci/prepare-renderer.py')
prepare_renderer = importlib.util.module_from_spec(prepare_spec)
prepare_spec.loader.exec_module(prepare_renderer)
SHA = 'a' * 40
RENDERER_SHA = 'b' * 40
GUID = uuid.UUID('01234567-89ab-cdef-0123-456789abcdef')


def pe():
    data = bytearray(1024)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 60, 128)
    data[128:132] = b'PE\0\0'
    struct.pack_into('<H', data, 134, 1)
    struct.pack_into('<H', data, 148, 240)
    struct.pack_into('<H', data, 152, 0x20b)
    struct.pack_into('<II', data, 152 + 112 + 48, 0x1000, 28)
    struct.pack_into('<IIII', data, 392 + 8, 512, 0x1000, 512, 512)
    struct.pack_into('<IIII', data, 512 + 12, 2, 24, 0x1040, 576)
    data[576:600] = b'RSDS' + GUID.bytes_le + struct.pack('<I', 1)
    data[650:650+len(b'DISCORD-RPC: compiled in; 1440140585595047977')] = b'DISCORD-RPC: compiled in; 1440140585595047977'
    return bytes(data)


def linked_pe(exports=(), imports=None):
    """A PE32+ image with one section holding an export table and an import table (FEAT-GTS-260)."""
    imports = imports or {}
    rva, raw = 0x1000, 0x200
    body = bytearray(0x1000)
    strings = {}

    def put(text, at):
        encoded = text.encode() + b'\0'
        body[at:at + len(encoded)] = encoded
        return at + len(encoded)

    cursor = 0x800
    for text in list(exports) + list(imports):  # plain names
        strings[text] = cursor
        cursor = put(text, cursor)
    hints = {}
    for text in {n for names in imports.values() for n in names}:  # 2-byte hint, then the name
        hints[text] = cursor
        cursor = put(text, cursor + 2)
    # Export directory at 0x000: names table at 0x040.
    struct.pack_into('<IIIIIIIIII', body, 0, 0, 0, 0, 0, 1, len(exports), len(exports), 0, rva + 0x40, 0)
    for i, name in enumerate(exports):
        struct.pack_into('<I', body, 0x40 + 4 * i, rva + strings[name])
    # Import descriptors at 0x200, thunks from 0x400.
    thunk = 0x400
    for i, (dll, names) in enumerate(imports.items()):
        struct.pack_into('<IIIII', body, 0x200 + 20 * i, rva + thunk, 0, 0, rva + strings[dll], rva + thunk)
        for name in names:
            struct.pack_into('<Q', body, thunk, rva + hints[name])
            thunk += 8
        thunk += 8
    data = bytearray(pe()[:raw]) + body
    struct.pack_into('<II', data, 152 + 112, rva if exports else 0, 0x100 if exports else 0)
    struct.pack_into('<II', data, 152 + 112 + 8, rva + 0x200 if imports else 0, 0x100 if imports else 0)
    struct.pack_into('<II', data, 152 + 112 + 48, 0, 0)
    struct.pack_into('<IIII', data, 392 + 8, len(body), rva, len(body), raw)
    return bytes(data)


class PackageTests(unittest.TestCase):
    def fixture(self):
        pin = json.loads((ROOT / 'renderer/source.json').read_text())
        for flavor, _, suffix in pkg.VARIANTS:
            for target, binary in (('launcher', 'RyGTS.exe'), ('renderer', 'libSceVideoOut.prx')):
                folder = Path('artifacts/' + target + suffix)
                folder.mkdir(parents=True)
                (folder / binary).write_bytes(pe())
                (folder / 'RYGT-COMMIT.txt').write_text(SHA)
                (folder / 'BUILD-PROFILE.json').write_text(json.dumps(dict(
                    schema=1, flavor=flavor, commit=SHA, optimized=True,
                    full_symbols=bool(suffix))))
                if target == 'renderer':
                    (folder / 'CI-SOURCE.txt').write_text(pin['repository'] + '\n' + RENDERER_SHA + '\n')
                    for library in pkg.RENDERER_LIBRARIES:  # FIX-GTS-262
                        (folder / library).write_bytes(linked_pe(exports=('renderer_' + library.split('.')[0],)))
                    for tool in pkg.RENDERER_TOOLS:  # FEAT-GTS-280
                        (folder / tool).write_bytes(linked_pe(exports=('renderer_' + tool.split('.')[0],)))
                if suffix:
                    pdb = folder / (Path(binary).stem + '.pdb')
                    pdb.write_bytes(b'Microsoft C/C++ MSF 7.00 fixture')
                    (folder / 'SYMBOLS.json').write_text(json.dumps(dict(
                        binary=binary, pdb=pdb.name, guid=str(GUID), age=1,
                        binary_sha256=pkg.sha(folder / binary), pdb_sha256=pkg.sha(pdb))))
                    if target == 'renderer':
                        (folder / 'frame-capture-layouts.json').write_text(json.dumps({pkg.sha(folder / binary).upper(): {}}))
                self.rehash(folder)
        runtime = Path('artifacts/runtime')
        runtime.mkdir(parents=True)
        (runtime / 'libc.prx').write_bytes(linked_pe(exports=('__cxa_throw', 'malloc')))
        (runtime / 'libkernel.prx').write_bytes(linked_pe(exports=('sceKernelOpen',), imports={'libc.prx': ['malloc']}))
        (runtime / 'libScePad.prx').write_bytes(linked_pe(imports={'libc.prx': ['__cxa_throw'], 'KERNEL32.dll': ['Sleep']}))
        # This source's own (smaller) libSceAudio3d: the renderer's must replace it (FIX-GTS-262).
        (runtime / 'libSceAudio3d.prx').write_bytes(linked_pe(exports=('runtime_audio3d',)))
        (runtime / 'libSceBluetoothHid.prx').write_bytes(linked_pe(exports=('ryty_stub_none',)))
        for program in ('ryty.exe', 'ryty-wheel-probe.exe'):  # FEAT-GTS-263
            (runtime / program).write_bytes(linked_pe(exports=(program,)))
        for dll in pkg.RUNTIME_DLLS:
            (runtime / dll).write_bytes(pe())
        (runtime / 'RUNTIME-TOOLCHAIN.json').write_text(json.dumps({'packages': [], 'ffmpeg': {}}))
        (runtime / 'RYGT-COMMIT.txt').write_text(SHA)
        (runtime / 'BUILD-PROFILE.json').write_text(json.dumps(dict(schema=1, flavor='Release', commit=SHA, optimized=True, full_symbols=False)))
        self.rehash(runtime)
        tools = Path('artifacts/tools')  # FEAT-GTS-263: ci/build-windows.ps1 -Target Tools
        tools.mkdir(parents=True)
        for file in pkg.TOOLS_FILES:
            (tools / file).write_bytes(linked_pe(exports=(file,)) if file.endswith(('.exe', '.dll')) else file.encode())
        (tools / 'RYGT-COMMIT.txt').write_text(SHA)
        self.rehash(tools)

    def rehash(self, folder):
        (folder / 'SHA256SUMS.txt').write_text(''.join(
            pkg.sha(f) + '  ' + f.name + '\n' for f in sorted(folder.iterdir())
            if f.name != 'SHA256SUMS.txt'))

    def test_separate_archives_symbols_defaults_and_provenance(self):
        with tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
            self.fixture()
            with patch.dict(os.environ, {'CI_COMMIT_SHA': SHA, 'RENDERER_SHA': RENDERER_SHA}):
                pkg.package()
            manifest = json.loads(Path('dist/CI-PACKAGES.json').read_text())
            self.assertEqual(manifest['renderer_commit'], RENDERER_SHA)
            self.assertEqual([v['channel'] for v in manifest['variants']], ['ci', 'debug-ci'])
            for variant in manifest['variants']:
                archive = Path('dist') / variant['archive']
                self.assertEqual(pkg.sha(archive), variant['sha256'])
                prefix = archive.stem + '/'
                with zipfile.ZipFile(archive) as z:
                    names = {n.removeprefix(prefix) for n in z.namelist()}
                    pdbs = {n for n in names if n.endswith('.pdb')}
                    self.assertEqual(pdbs, {'RyGTS.pdb', 'libs/libSceVideoOut.pdb'} if variant['channel'] == 'debug-ci' else set())
                    self.assertEqual('tools/diagnostics/Start-Capture.cmd' in names, bool(pdbs))
                    config = json.loads(z.read(prefix + 'user/custom_configs/CUSA03220.json'))
                    self.assertTrue(config['GPU']['precompile_shaders'])
                    self.assertTrue(config['Vulkan']['pipeline_cache_enabled'])
                    info = json.loads(z.read(prefix + 'BUILD_INFO.txt'))
                    self.assertEqual(info['commit'], SHA)
                    self.assertEqual(info['channel'], variant['channel'])
                    sources = info['sources']
                    self.assertIn('converter/ryty.exe', sources['built'])
                    self.assertIn('tools/adcpatch/adcpatch.exe', sources['built'])
                    self.assertTrue({'libs/libc.prx', 'libs/libkernel.prx', 'libs/libScePad.prx'} <= set(sources['built']))
                    self.assertTrue({'libs/' + name for name in pkg.RENDERER_LIBRARIES} <= set(sources['built']))
                    self.assertEqual(sources['downloaded'], ['amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-LICENSE.txt'])
                    self.assertNotIn('runtime_base_url', info)
                    self.assertNotIn('retained', info)
                    # The renderer's libraries win over this source's own versions.
                    self.assertEqual(pkg.pe_tables(z.read(prefix + 'libs/libSceAudio3d.prx'))[0], {'renderer_libSceAudio3d'})
                    self.assertEqual(pkg.pe_tables(z.read(prefix + 'libs/libSceAjm.prx'))[0], {'renderer_libSceAjm'})
                    # FEAT-GTS-263: every file comes from an artifact of this pipeline or the repository.
                    for packaged, source in (('libs/libc.prx', 'artifacts/runtime/libc.prx'),
                                             ('libstdc++-6.dll', 'artifacts/runtime/libstdc++-6.dll'),
                                             ('converter/ryty.exe', 'artifacts/runtime/ryty.exe'),
                                             ('converter/libstdc++-6.dll', 'artifacts/runtime/libstdc++-6.dll'),
                                             ('tools/ryty-wheel-probe.exe', 'artifacts/runtime/ryty-wheel-probe.exe'),
                                             ('tools/ryty-feel-test.exe', 'artifacts/renderer' + ('' if variant['channel'] == 'ci' else '-diagnostics') + '/ryty-feel-test.exe'),
                                             ('tools/adcpatch/adcpatch.exe', 'artifacts/tools/adcpatch.exe'),
                                             ('tools/adcpatch/README.txt', 'artifacts/tools/adcpatch-README.txt'),
                                             ('amd_fidelityfx_vk.dll', 'artifacts/tools/amd_fidelityfx_vk.dll'),
                                             ('launcher/background.png', str(ROOT / 'loader/launcher/background.png')),
                                             ('LINUX.txt', str(ROOT / 'docs/user/LINUX.md'))):
                        self.assertEqual(z.read(prefix + packaged), Path(source).read_bytes(), packaged)
                    config = json.loads(z.read(prefix + 'user/custom_configs/CUSA03220.json'))
                    self.assertTrue(config['GPU']['native_renderer'])  # the repository's defaults, then the packaged ones
                    self.assertEqual(info['renderer_source'].splitlines(), [
                        'https://github.com/RyGT-Team/shadGT.git', RENDERER_SHA])
                    self.assertNotIn('runtime_overlay', info)
                    for line in z.read(prefix + 'SHA256SUMS.txt').decode().splitlines():
                        digest, name = line.split('  ', 1)
                        import hashlib
                        self.assertEqual(hashlib.sha256(z.read(prefix + name)).hexdigest(), digest)

    def test_both_flavors_require_expected_renderer(self):
        with tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
            self.fixture()
            folder = Path('artifacts/renderer-diagnostics')
            source = folder / 'CI-SOURCE.txt'
            lines = source.read_text().splitlines()
            source.write_text(lines[0] + '\n' + 'c' * 40 + '\n')
            self.rehash(folder)
            with patch.dict(os.environ, {'CI_COMMIT_SHA': SHA, 'RENDERER_SHA': RENDERER_SHA}):
                with self.assertRaisesRegex(RuntimeError, 'Renderer source revision mismatch'):
                    pkg.package()

    def test_local_head_packages_matching_variants_without_env(self):
        with tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
            self.fixture()
            env = {'CI_COMMIT_SHA': SHA}
            with patch.dict(os.environ, env, clear=True):
                pkg.package()
            manifest = json.loads(Path('dist/CI-PACKAGES.json').read_text())
            self.assertEqual(manifest['renderer_commit'], RENDERER_SHA)

    def test_local_head_rejects_mixed_flavors(self):
        with tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
            self.fixture()
            folder = Path('artifacts/renderer-diagnostics')
            source = folder / 'CI-SOURCE.txt'; lines = source.read_text().splitlines()
            source.write_text(lines[0] + '\n' + 'c' * 40 + '\n')
            self.rehash(folder)
            env = {'CI_COMMIT_SHA': SHA}
            with patch.dict(os.environ, env, clear=True):
                with self.assertRaisesRegex(RuntimeError, 'Renderer revisions differ'):
                    pkg.package()

    def test_package_builds_only_selected_flavor(self):
        for selected, removed_suffix in (('ci', '-diagnostics'), ('debug-ci', '')):
            with self.subTest(selected=selected), tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
                self.fixture()
                if removed_suffix:
                    shutil.rmtree(Path('artifacts/launcher' + removed_suffix))
                    shutil.rmtree(Path('artifacts/renderer' + removed_suffix))
                else:
                    shutil.rmtree(Path('artifacts/launcher'))
                    shutil.rmtree(Path('artifacts/renderer'))
                env = {'CI_COMMIT_SHA': SHA, 'CI_CHANNEL': selected}
                with patch.dict(os.environ, env, clear=True):
                    pkg.package()
                manifest = json.loads(Path('dist/CI-PACKAGES.json').read_text())
                self.assertEqual([variant['channel'] for variant in manifest['variants']], [selected])
                self.assertTrue((Path('dist') / f'RyGTS-{selected}-{SHA[:8]}.zip').exists())
                other = 'debug-ci' if selected == 'ci' else 'ci'
                self.assertFalse((Path('dist') / f'RyGTS-{other}-{SHA[:8]}.zip').exists())

    def test_missing_or_wrong_symbols_and_source_revision_rejected(self):
        for mutation in ('identity', 'missing', 'commit'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
                self.fixture()
                folder = Path('artifacts/renderer-diagnostics')
                if mutation == 'identity':
                    symbols = folder / 'SYMBOLS.json'
                    value = json.loads(symbols.read_text()); value['age'] = 2
                    symbols.write_text(json.dumps(value))
                elif mutation == 'missing':
                    (folder / 'libSceVideoOut.pdb').unlink()
                else:
                    (folder / 'RYGT-COMMIT.txt').write_text('b' * 40)
                if mutation != 'missing': self.rehash(folder)
                with self.assertRaises((RuntimeError, FileNotFoundError)):
                    pkg.verify_bundle(folder, 'libSceVideoOut.prx', SHA, 'Diagnostics')

    def test_build_uses_resolved_renderer_head_without_source_overlays(self):
        build = (ROOT / 'ci/build-windows.ps1').read_text(encoding='utf-8')
        self.assertIn("ci/prepare-renderer.py", build)
        self.assertIn("CI-SOURCE.txt", build)
        self.assertNotIn("ci/runtime/apply.py", build)
        self.assertNotIn("ci/diagnostics/apply.py", build)
        self.assertNotIn("RUNTIME-SOURCE.json", build)
        self.assertNotIn("DIAGNOSTICS-SOURCE.json", build)

    def test_diagnostics_configuration_keeps_optimized_capture_code(self):
        build = (ROOT / 'ci/build-windows.ps1').read_text(encoding='utf-8')
        self.assertIn("$configuration = if ($Flavor -eq 'Diagnostics') { 'RelWithDebInfo' } else { 'Release' }", build)
        self.assertIn('-DCMAKE_C_FLAGS_RELWITHDEBINFO=/O2 /Ob2 /Zi /UNDEBUG', build)
        self.assertIn('-DCMAKE_CXX_FLAGS_RELWITHDEBINFO=/O2 /Ob2 /Zi /UNDEBUG', build)
        self.assertIn('compile_commands.json', build)
        self.assertIn('without NDEBUG', build)

    def test_split_workflow_templates_resolve_and_verify_one_channel_each(self):
        release = (ROOT / 'ci/github-registry/ci-package-release.yml').read_text(encoding='utf-8')
        debug = (ROOT / 'ci/github-registry/ci-package-debug.yml').read_text(encoding='utf-8')
        for workflow, channel in ((release, 'ci'), (debug, 'debug-ci')):
            self.assertIn(f'CI_CHANNEL: {channel}', workflow)
            self.assertIn('python3 ci/github-registry/registry_ci.py resolve', workflow)
            self.assertIn('python3 ci/package.py', workflow)
            self.assertIn('python3 ci/github-registry/registry_ci.py verify', workflow)
            self.assertIn('workflow_dispatch:', workflow)
            self.assertNotIn('schedule:', workflow)
            self.assertIn('push:', workflow)
            self.assertIn('workflow_call:', workflow)
            self.assertIn('actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02', workflow)
            self.assertIn('actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093', workflow)
            self.assertNotIn('actions/upload-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093', workflow)
        self.assertNotIn('debug-ci', release)
        self.assertNotIn('ci and debug-ci', debug)

    def test_prepared_head_identity_flows_into_both_packages(self):
        with tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
            root = Path(temp) / 'checkout'
            (root / 'renderer').mkdir(parents=True)
            (root / 'ci').mkdir()
            (root / 'renderer/source.json').write_text(json.dumps({
                'repository': 'https://github.com/RyGT-Team/shadGT.git',
                'branch': 'ryty-gpu', 'commit': 'HEAD'}))
            prepared = Path(temp) / 'prepared'

            def run(*args):
                if args[1] == 'init':
                    prepared.mkdir()

            with patch.object(prepare_renderer, '__file__', str(root / 'ci/prepare-renderer.py')), \
                    patch.object(prepare_renderer, 'run', side_effect=run), \
                    patch.object(prepare_renderer.subprocess, 'check_output', return_value=RENDERER_SHA + '\n'), \
                    patch.dict(os.environ, {'RENDERER_SHA': RENDERER_SHA}, clear=True):
                prepare_renderer.prepare(prepared)

            identity = (prepared / 'CI-SOURCE.txt').read_text()
            self.fixture()
            for suffix in ('', '-diagnostics'):
                folder = Path('artifacts/renderer' + suffix)
                (folder / 'CI-SOURCE.txt').write_text(identity)
                self.rehash(folder)
            with patch.dict(os.environ, {'CI_COMMIT_SHA': SHA, 'RENDERER_SHA': RENDERER_SHA}):
                pkg.package()
            manifest = json.loads(Path('dist/CI-PACKAGES.json').read_text())
            self.assertEqual(manifest['renderer_commit'], RENDERER_SHA)


    def test_unresolved_imports_between_packaged_modules(self):
        # FEAT-GTS-260: an older library next to a newer libc stopped the game at start (error 127).
        with tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
            stage = Path('stage'); (stage / 'libs').mkdir(parents=True)
            (stage / 'libs/libc.prx').write_bytes(linked_pe(exports=('malloc',)))
            (stage / 'libs/libScePad.prx').write_bytes(linked_pe(imports={'libc.prx': ['malloc'], 'KERNEL32.dll': ['Sleep']}))
            (stage / 'amd_fidelityfx_vk.dll').write_bytes(pe())
            pkg.check_package_imports(stage)  # resolved; Windows' own DLLs are not checked
            exports, imports = pkg.pe_tables((stage / 'libs/libScePad.prx').read_bytes())
            self.assertEqual(imports, {'libc.prx': ['malloc'], 'kernel32.dll': ['Sleep']})
            (stage / 'libs/libSceAjm.prx').write_bytes(linked_pe(imports={'LIBC.PRX': ['__cxa_begin_catch', 'malloc']}))
            with self.assertRaisesRegex(RuntimeError, r'libSceAjm\.prx -> libc\.prx: __cxa_begin_catch'):
                pkg.check_package_imports(stage)



    def test_missing_tools_artifact_rejected(self):
        # FEAT-GTS-263: nothing falls back to an archive any more.
        with tempfile.TemporaryDirectory() as temp, contextlib.chdir(temp):
            self.fixture()
            (Path('artifacts/tools') / 'adcpatch.exe').unlink()
            self.rehash(Path('artifacts/tools'))
            with patch.dict(os.environ, {'CI_COMMIT_SHA': SHA, 'RENDERER_SHA': RENDERER_SHA}):
                with self.assertRaisesRegex(RuntimeError, 'adcpatch.exe'):
                    pkg.package(channels='ci')


if __name__ == '__main__':
    unittest.main()
