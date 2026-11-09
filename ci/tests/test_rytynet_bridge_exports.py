"""Integration coverage for host bridge symbols in patched runtime modules."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
NID = ROOT / 'core/libs/nid'
SOURCES = ('Sha1.cpp', 'NidCompute.cpp', 'NidResolver.cpp', 'ExportExclusions.cpp',
           'ElfNidPatcher.cpp', 'PeNidPatcher.cpp', 'BinaryPatcherFactory.cpp', 'main.cpp')


@unittest.skipUnless(shutil.which('g++') and shutil.which('nm') and shutil.which('objdump'), 'requires GCC binutils')
class BridgeExportTests(unittest.TestCase):
    def test_prefix_preserves_bridge_but_not_guest_exports(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            patcher = root / ('patcher.exe' if os.name == 'nt' else 'patcher')
            subprocess.run(['g++', '-std=c++20', '-I' + str(NID / 'include'),
                            *(str(NID / 'src' / name) for name in SOURCES),
                            '-o', str(patcher)], check=True, capture_output=True)
            fixture = root / 'bridge.cpp'
            fixture.write_text('namespace RytyNet { int Bridge() { return 1; } }\n'
                               'extern "C" int RytyNetVersion() { return 2; }\n')
            original = root / ('bridge.dll' if os.name == 'nt' else 'bridge.so')
            subprocess.run(['g++', '-shared', '-fPIC', str(fixture), '-o', str(original)],
                           check=True, capture_output=True)
            regular, preserved = root / 'regular', root / 'preserved'
            shutil.copyfile(original, regular); shutil.copyfile(original, preserved)
            subprocess.run([str(patcher), 'libRytyNet', str(regular)], check=True, capture_output=True)
            subprocess.run([str(patcher), 'libRytyNet', '--preserve-prefix', '_ZN7RytyNet',
                            str(preserved)], check=True, capture_output=True)
            def names(path):
                if os.name == 'nt':
                    output = subprocess.run(['objdump', '-p', str(path)], check=True,
                                            capture_output=True, text=True).stdout
                    table = output.split('[Ordinal/Name Pointer] Table', 1)[1].split('The Function Table', 1)[0]
                    return set(re.findall(r'^\s*\[\s*\d+\].*?\s([^\s]+)\s*$', table, re.M))
                args = ['nm', '-g', '--defined-only', str(path)]
                args.insert(1, '-D')
                output = subprocess.run(args, check=True, capture_output=True, text=True).stdout
                return {line.split()[-1] for line in output.splitlines() if line.split()}
            before, after = names(regular), names(preserved)
            self.assertIn('_ZN7RytyNet6BridgeEv', after)
            self.assertNotIn('_ZN7RytyNet6BridgeEv', before)
            self.assertNotIn('RytyNetVersion', after)
            self.assertEqual(len(before - after), 1)
            self.assertEqual(len(after - before), 1)
            self.assertNotEqual(subprocess.run([str(patcher), 'libRytyNet', '--preserve-prefix'],
                                               capture_output=True).returncode, 0)


if __name__ == '__main__':
    unittest.main()

