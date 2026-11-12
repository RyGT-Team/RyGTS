# Placeholder libraries

GT Sport links against PS4 libraries that RyGTS does not implement: NP and PSN dialogs, USB, VR,
camera, trophies, FreeType helpers and others. The converted `GTSport.exe` loads
`libs/<library>.prx` for each of them when it starts, and stops with "Failed to load module" if one is
missing. These placeholders provide them.

Each `<library>.nids` lists the functions the game imports from that library, one `<library> <NID>` per
line (a few libraries, such as `libSceMbus`, also answer for functions of other libraries).
`generate.py` turns a list into C source and a module definition. Every function prints
`[stub] <library> <NID>` to stderr the first time it is called and returns 0. An empty list makes a
library that exports only `ryty_stub_none`: the game loads it but calls nothing in it.

The lists were recovered from the placeholder binaries in the October 25 runtime archive, whose source
had not been kept (only `libSceMbus` existed as source, in ryGT7's `runtime/gt7`). To implement one of
these libraries for real, add it under `core/libs/prx/` and delete its list here.

`libSceSsl2.prx` is not a placeholder: it is the PS4's second module name for the libSceSsl API, so the build
copies the NID-patched `libSceSsl.prx` to it (`core/libs/CMakeLists.txt`).
