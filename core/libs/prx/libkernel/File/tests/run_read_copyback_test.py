"""Compile the real ReadIntoGuest body with a Windows protection fixture.

Run on Windows: python run_read_copyback_test.py PATH_TO_GXX
"""
import pathlib
import subprocess
import sys
import tempfile

here = pathlib.Path(__file__).resolve().parent
source = (here.parent / 'src' / 'Open.cpp').read_text(encoding='utf-8')
start = source.index('std::int64_t ReadIntoGuest(')
end = source.index('\n}', start) + 2
fixture = (here / 'read_copyback_fixture.cpp').read_text(encoding='utf-8')
fixture = fixture.replace('// INSERT_READ_INTO_GUEST', source[start:end])
with tempfile.TemporaryDirectory() as directory:
    src = pathlib.Path(directory) / 'test.cpp'
    exe = pathlib.Path(directory) / 'test.exe'
    src.write_text(fixture, encoding='utf-8')
    subprocess.run([sys.argv[1], '-std=c++20', '-O2', '-static', str(src), '-o', str(exe)], check=True)
    subprocess.run([str(exe)], check=True)
