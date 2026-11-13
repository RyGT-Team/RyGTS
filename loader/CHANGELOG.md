# RyGTS launcher changelog

The launcher (RyGTS.exe) is versioned on its own with semantic versioning
(MAJOR.MINOR.PATCH), separately from the game, emulator and release packages.
The version lives in `loader/Version.cs`. To release a change, bump
`LauncherVersion.Value` there and add a section here with the same number;
CI fails when the two disagree.

## 0.1.3 - 2026-11-13

* FEAT-GTS-267: The Renderer list on the Graphics page has a third entry, "Native renderer async
  (experimental)": the native renderer started with the switches of shadGT's PERF-GTS-368 to 403
  (work moved off the recorder thread or made cheaper on it) at the values tested in races:
  `RYGT_DIRECT_NESTED_RESUME=1`, `RYGT_DYNAMIC_BUFFER_OFFSETS=1`, `RYGT_LONG_LIVED_SETS=1`,
  `RYGT_AHEAD_UPLOAD_MB=128`, `RYGT_NATIVE_CHUNK_MB=64`, `RYGT_NATIVE_UPLOAD_VRAM=1`,
  `RYGT_DRIVER_CACHE_MAX_MB=4096`, `RYGT_UPLOAD_ONCE_A_FRAME=1`, `RYGT_EVICT_SCAN_SHARDS=8`,
  `RYGT_MIRROR_DROP_BACKOFF=1`, `RYGT_MIRROR_ROUND_MB=4` and `RYGT_PREPARE_MISSING_PICTURES=1`. A
  switch set on the Advanced page (or already in the environment) keeps its own value. Saved as
  `native_async=1` in `RyGT.ini`.

## 0.1.2 - 2026-11-12

* FEAT-GTS-266: The Advanced page (the renderer's experimental switches) is hidden by default. It
  appears once "Show the Advanced page" is turned on under Logging (`show_advanced=1` in `RyGT.ini`);
  switches set there keep applying when it is hidden again.

## 0.1.1 - 2026-11-12

* New launcher look, ported from ryGT7's launcher: a full-window home view with the
  background pictures (slowly drifting, and fading between `launcherackground*.png`
  when there are several), the title or `launcher\logo.png`, Play and a settings button.
* The settings open on their own view with a sidebar of pages: Game (files, Set up and
  its log), Display, Graphics, Shaders, Controls, Advanced and About (folders, logging,
  and showing the Advanced page). The top tabs and the quick-access panel are gone.
* Settings are full-width rows with switches, drop-downs and sliders under spaced
  capital headings; buttons are outlined, and views change with a short fade.
* Every setting is kept with the same keys and files; only where it is shown changed.
* The native renderer is no longer experimental and is now the default renderer. Installs
  that never picked a renderer switch to it on the next Play; a saved choice of the old
  renderer is kept.
* When the game's config has entries nothing reads any more (so far `GPU.quality_preset`,
  replaced by System memory), the launcher asks on opening whether to remove them, with
  "Don't show again" (`prompt_obsolete` in `RyGT.ini`).
* Set up no longer links the game's files into `app0` (Explorer counted the links as a second
  copy of the game): the game reads them from the game and update folders. Links left by earlier
  versions are removed; copies are kept. The RyGTS folder can be on any drive.

## 0.1.0 - 2026-11-11

* First tracked launcher version.
* The version is in the exe's file properties (File version, Product version),
  in the launcher window, and on the first line of `setup.log`.
* CI packages record it as `launcher_version` in their build information.
