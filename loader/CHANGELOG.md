# RyGTS launcher changelog

The launcher (RyGTS.exe) is versioned on its own with semantic versioning
(MAJOR.MINOR.PATCH), separately from the game, emulator and release packages.
The version lives in `loader/Version.cs`. To release a change, bump
`LauncherVersion.Value` there and add a section here with the same number;
CI fails when the two disagree.

## 0.1.3 - 2026-11-13

* FEAT-GTS-270: Profiles on the Advanced page. A dropdown of saved profiles with Save and Load
  next to it: Save keeps the current switches and other variables under a name
  (`launcher\profiles\<name>.ini`, the same `env=` lines as `RyGT.ini`), Load replaces them with a
  profile's, to swap between configurations instead of tracking several similar switches by hand.

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
