Debug / Diagnostics

The renderer uses an optimized Debug configuration with full matching symbols,
capture instrumentation and the redistributable C++ runtime. The separate
debug-ci archive contains Diagnostics; the ci archive contains Release.
Extract them to different folders for independent settings, saves and caches;
setup points each at your own game dump and firmware modules.

Run this folder's RyGTS.exe. Close
the Release game first. PDBs are beside the matching launcher and renderer for
automatic debugger discovery. Symbols cover the rebuilt launcher and renderer;
other helpers/runtime libraries are retained from the pinned runtime archive.
BUILD_INFO.txt identifies the source, diagnostics overlay, binaries and symbols.

Optional capture tools require Python 3.12 or newer. Run
tools\diagnostics\Start-Capture.cmd, then press Play and focus the game:

F8: visible screenshot, native frame, CPU/GPU pass spans, shadow/depth state,
    shader identities and scalar inputs, image views, effective sampler LOD
    settings, indirect dispatch arguments and bounded mip-chain readbacks.
Ctrl+F8: start/stop continuous performance recording.
Shift+F8: bounded visible-frame burst for flicker comparisons.

Captures go to investigation-build\frame-captures in this Debug folder.
Do not use an F8 diagnostic frame as a normal-play performance benchmark.
Timestamp spans can overlap and include dependencies. Existing F8 capture bypasses
worker-prepared bindings. GPU timestamps do not give exclusive shader cost.
Additional mip readbacks are capped at 64 MiB, and skipped data is recorded.
Occlusion candidates are inferred from recorded resources; the trace does not
force culling, shadow, mip or model-LOD behavior and does not claim a graphics fix.

After capture, analyze offline with:
  python tools\diagnostics\analyze-trace.py CAPTURE_DIRECTORY

This creates native\scene-analysis.json without opening or pausing the game.
Keep the matching PDBs, BUILD_INFO.txt and capture logs when reporting a fault.
No game executable, game data, firmware, saves, logs, caches or NVIDIA SDK DLLs
are shipped. Those produced locally while playing stay in this Debug runtime.
