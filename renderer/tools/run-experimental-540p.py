"""Experimental fixed 540p scene / 1080p output for the verified GT Sport binary."""
import argparse, os, struct, subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('game_root', type=Path, help='Portable installation containing GTSport.exe')
parser.add_argument('--low-composition', action='store_true', help='Also shade final composition at 540p (quality/performance tradeoff)')
parser.add_argument('--verify-only', action='store_true')
args = parser.parse_args()
root = args.game_root.resolve()
exe = root/'GTSport.exe'
patches = [
 (0x1988AD3, '48b98007000038040000', '48b9c00300001c020000'),
 (0x1988AB0, '8b0d72ea2301', 'b9c003000090'),
 (0x1988AB9, '8b0d6dea2301', 'b91c02000090'),
]
data = exe.read_bytes()
pe = struct.unpack_from('<I', data, 0x3c)[0]
if data[:2] != b'MZ' or data[pe:pe+4] != b'PE\0\0':
 raise SystemExit('Not a supported PE executable')
count = struct.unpack_from('<H', data, pe+6)[0]
table = pe+24+struct.unpack_from('<H', data, pe+20)[0]
for rva, before, after in patches:
 before = bytes.fromhex(before)
 for i in range(count):
  at = table+i*40
  _, va, size, raw = struct.unpack_from('<4I', data, at+8)
  if va <= rva and rva+len(before) <= va+size:
   if data[raw+rva-va:raw+rva-va+len(before)] != before:
    raise SystemExit('Unsupported game executable: patch signature mismatch at '+hex(rva))
   break
 else:
  raise SystemExit('Unsupported game executable: missing patch site '+hex(rva))
if args.verify_only:
 print('PASS: all three startup allocation signatures match; no files changed')
 raise SystemExit(0)
# Cache cleanup must never race another process, including the normal launcher.
if os.name == 'nt' and 'gtsport.exe' in subprocess.check_output(
 ['tasklist.exe','/FI','IMAGENAME eq GTSport.exe','/FO','CSV','/NH'],text=True).lower():
 raise SystemExit('Close the running game before starting this comparison')
env = os.environ.copy()
if env.get('SHADGT_PATCH'):
 raise SystemExit('Unset SHADGT_PATCH before this controlled experiment')
env['SHADGT_PATCH'] = ','.join('%x:%s:%s'%p for p in patches)
env['SHADGT_NATIVE'] = '1'
env['SHADGT_EXPERIMENTAL_SCENE_VIEWPORT'] = '1'
# Preserve the measured validated performance setup; exclude rejected experiments.
env['SHADGT_COMPOSITION_BUFFER_DESCRIPTORS'] = '1'
env['SHADGT_POSTPROCESS_BUFFER_DESCRIPTORS'] = '1'
env['SHADGT_COMPUTE_PREFIX_DESCRIPTOR'] = '1'
for key in ('RYGT_COMPUTE_BUFFER_DESCRIPTORS','RYGT_MATERIAL_BUFFER_DESCRIPTORS','RYGT_COMPUTE_PREFIX_ALL'):
 env.pop(key, None)
if args.low_composition: env['SHADGT_LOW_RES_COMPOSITION'] = '1'
else: env.pop('SHADGT_LOW_RES_COMPOSITION', None)
process = subprocess.Popen([str(exe)],cwd=root,env=env)
print('Started experimental 540p scene / 1080p output; PID',process.pid)
