# Notes for agents working on RyGTS

## Switch names: SHADGT_, RYGT_ or SHARED_ (FEAT-GTS-275)

From FEAT-GTS-275 on, an environment switch is named after where it takes effect:

| Prefix | Read by | Example |
|---|---|---|
| `SHADGT_` | only the renderer (shadGT: `src/`, `ryty_gpu/`) | `SHADGT_PARALLEL_WORKERS` |
| `RYGT_` | only the runtime (RyGTS: `core/`, `app/`, `plugins/`) | `RYGT_LOG_DIRECTORY` |
| `SHARED_` | both the renderer and the runtime | `SHARED_DIAG` |

- **A new switch** gets the prefix of the code that reads it. Never add a new `RYGT_` switch to the
  renderer or a `SHADGT_` one to the runtime.
- **A switch that starts being read on the other side too** is renamed to `SHARED_` in every
  repository (renderer, runtime, launcher, docs), and added to `SHARED` in the renderer's
  `tools/switch_prefix.py`.
- **The rename is a hard one**: the old names are not read any more. At start the launchers list
  switches saved under an old name in their obsolete-settings question and rename them on Yes
  (`SwitchWalker.FindRenames`), so players keep their settings.
- **A branch from before the rename** brings old names back on merge. Run the tool there after
  merging; a second run changes nothing:

  ```
  python tools/switch_prefix.py --game gts --write-map switch-map.json .   # in shadGT
  python <shadGT>/tools/switch_prefix.py --apply-map switch-map.json <RyGTS>   # the launcher and runtime
  ```

  It renames exact switch names only (not macros such as `RYGT_SWITCH_EXPORT`), in code, comments
  and docs. `CHANGES.MD` and `CHANGELOG.md` keep the names of their time.

## Renderer switches on the launcher's Advanced page

The launcher (`loader/`) does not keep a list of the renderer's and runtime's `SHADGT_*`, `RYGT_*` and `SHARED_*` switches. `SwitchWalker`
in `loader/AdvancedPage.cs` reads `libs\libSceVideoOut.prx` (the copy the game loads) as a file (it never loads it) and lists every
`"SHADGT_..."`, `"RYGT_..."` and `"SHARED_..."` string the renderer passes to `getenv`. Descriptions come from the same DLL:
`"RYGTSW1|NAME|kind|group|default|values|description"` strings in shadGT's
`src/video_core/rygt_switches.cpp`. See `loader/ADVANCED-PAGE.md`.

- **Adding a renderer switch:** add its description line in shadGT's `src/video_core/rygt_switches.cpp`
  (format in shadGT's `AGENTS.md`). Do not add switch names, tooltips or defaults to the launcher.
- **Adding a switch read by the RyGTS runtime itself** (`core/`, `app/`, ...): the walker reads every
  `libs\*.prx`, the runtime modules included (ASCII and UTF-16 names, so `_wgetenv(L"RYGT_...")` is
  found), so it is listed. Describe it in shadGT's `src/video_core/rygt_switches.cpp` like a renderer
  switch (group `Runtime` or the one that fits); the walker takes descriptions from any of the files.
- **A variable the launcher sets itself** (`RYGT_LOG_DIRECTORY`, `RYGT_HDD_STREAMING`,
  `SHADGT_ANTI_LAG`, the render-scale ones in `loader/RenderResolution.cs`): add it to
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
