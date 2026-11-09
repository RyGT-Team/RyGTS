from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text(encoding="utf-8")
output = Path(sys.argv[2])
output.mkdir(parents=True, exist_ok=True)
first = source.index("struct BatchMapTrace {")
last = source.index("constexpr size_t FLEXIBLE_MEMORY_SIZE", first)
(output / "batch-trace-under-test.inc").write_text(source[first:last], encoding="utf-8")
first = source.index("int APS5_VABI sceKernelBatchMap2(KernelBatchMapEntry* entries, int num_entries, int* num_entries_out, int flags) {")
last = source.index("\n}\n\n}", first) + 2
(output / "batch-map-under-test.inc").write_text(source[first:last], encoding="utf-8")
