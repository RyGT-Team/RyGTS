# Notes for agents working on RyGTS

## Renderer switches on the launcher's Advanced page

The launcher (`loader/`) does not keep a list of the renderer's `RYGT_*` and `SHADGT_*` switches. `SwitchWalker`
in `loader/AdvancedPage.cs` reads `libs\libSceVideoOut.prx` (the copy the game loads) as a file (it never loads it) and lists every
`"RYGT_..."` and `"SHADGT_..."` string the renderer passes to `getenv`. Descriptions come from the same DLL:
`"RYGTSW1|NAME|kind|group|default|values|description"` strings in shadGT's
`src/video_core/rygt_switches.cpp`. See `loader/ADVANCED-PAGE.md`.

- **Adding a renderer switch:** add its description line in shadGT's `src/video_core/rygt_switches.cpp`
  (format in shadGT's `AGENTS.md`). Do not add switch names, tooltips or defaults to the launcher.
- **Adding a switch read by the RyGTS runtime itself** (`core/`, `app/`, ...): the launcher only walks
  the renderer DLL, so a runtime-only switch is not listed. Users can still set it under "Other
  environment variables". To list it, describe it in the renderer's table too, and extend the walker
  to the module that reads it.
- **A variable the launcher sets itself** (`RYGT_LOG_DIRECTORY`, `RYGT_HDD_STREAMING`,
  `RYGT_ANTI_LAG`, the render-scale ones in `loader/RenderResolution.cs`): add it to
  `SwitchWalker.LauncherManaged` so the page does not offer it.
- **Changing the format:** bump the `RYGTSW1` tag on both sides together. The walker ignores strings
  with a tag it does not know.

## Launcher build

`loader\build.cmd [output folder]` builds `RyGTS.exe` with the .NET Framework 4 `csc`, which only
accepts **C# 5**: no local functions, `out var`, tuples or string interpolation. Add new `.cs`
files to the list in `build.cmd`.

## Launcher version

The launcher is versioned on its own: `LauncherVersion.Value` in `loader/Version.cs`, with a
matching section in `loader/CHANGELOG.md`.

- **Bump only the patch number:** 0.1.0, 0.1.1, 0.1.2, and so on. Launcher changes count as minor
  updates, new features and new looks included. Do not raise the minor or major number unless the
  maintainer says so.
- Change `Version.cs` and the changelog's newest `## x.y.z - date` heading together:
  `ci/tests/test_launcher_version.py` fails when they disagree.
