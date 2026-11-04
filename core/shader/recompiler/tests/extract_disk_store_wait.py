import argparse
import re
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--source", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
source = Path(args.source).read_text(encoding="utf-8")
# Ignore literal/comment braces when locating the real production method bodies.
masked = re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'',
                lambda m: "".join("\n" if c == "\n" else " " for c in m.group()), source)
methods = []
for signature in ("    void Store(", "    void Flush()", "    void run()"):
    start = source.index(signature)
    brace = masked.index("{", start)
    end, depth = brace + 1, 1
    while depth:
        depth += (masked[end] == "{") - (masked[end] == "}")
        end += 1
    methods.append(source[start:end])
methods = "\n".join(methods)
# The retained Debug branch is the original timed-wait implementation. Selecting
# that actual branch supplies the comparison without duplicating Store/Flush/run.
def debug_branch(text):
    out, active, stack = [], True, []
    for line in text.splitlines(True):
        directive = line.strip()
        if directive == "#if !defined(NDEBUG)":
            stack.append(active)
        elif directive == "#else":
            active = stack[-1] and not active
        elif directive == "#endif":
            active = stack.pop()
        else:
            if directive.startswith("#"):
                raise SystemExit("Unexpected preprocessor directive in extracted methods: " + directive)
            if active:
                out.append(line)
    if stack:
        raise SystemExit("Unbalanced extracted preprocessor guards")
    return "".join(out)
result = "// Generated from actual ShaderDiskCache.cpp methods.\n"
for name, body in (("TimedBaseline", debug_branch(methods)), ("Candidate", methods)):
    result += "class " + name + " final : public StoreFixture {\npublic:\n" + body + "\n};\n"
Path(args.output).write_text(result, encoding="utf-8")
