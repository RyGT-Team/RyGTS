# Advanced page: renderer switches and environment variables (FEAT-GTS-433, FEAT-GTS-437)

The Advanced tab lists the experimental `RYGT_*` switches of the installed renderer and sets them
for `GTSport.exe` when the launcher starts it.

## Where the list comes from

Nothing is listed in the launcher. When the launcher starts it reads `libs\libSceVideoOut.prx` (the copy the game loads) as a file
(it never loads it) and collects:

- every string that is exactly `RYGT_` plus capitals, digits and `_`: the names the renderer passes to
  `getenv`. A switch added to the renderer appears with the next renderer build.
- every `RYGTSW1|NAME|kind|group|default|values|description` string: the renderer's description of a
  switch, kept in shadGT's `src/video_core/rygt_switches.cpp` (exported as `RyGT_SwitchDescriptions`
  so the linker keeps it). The description is the hover tooltip; kind picks the editor (Default / On /
  Off for flags, a checked value for int, hex and float); group is the heading.

A switch the renderer reads but does not describe is listed under "Not described in this renderer
build". A description without a matching name (for example a switch only a diagnostics build reads)
is not listed. Switches the launcher sets from its own settings (`RYGT_LOG_DIRECTORY`,
`RYGT_HDD_STREAMING`, `RYGT_ANTI_LAG`, the render-scale ones) are left out.

"Diagnostics & tracing" switches are hidden until "Show diagnostics and tracing" is ticked (changed
ones always show). Search matches names and descriptions; "Only changed" shows what is set; Reset all
returns every switch to its default. Delete on a setting returns it to the default.

## Other environment variables

Any variable the list does not have goes in the table under it (On / Name / Value). Names are
letters, digits and `_`; other names are flagged and not saved. An empty value removes the variable.

## Storage and launch

Everything is saved as you change it, as `env=<1|0>|NAME=VALUE` lines in `RyGT.ini` (the game's
`user\custom_configs\CUSA03220.json` is not touched). When the launcher starts `GTSport.exe` (and on
the device-loss relaunch) the entries are applied after the launcher's own variables, so they win.
Only a count is written to the setup log, never values.

To confirm a switch reaches the game, set one the renderer logs at startup (for example
`RYGT_SO_HASH_MASK`, which logs `DIAG-GTS-432`) and check `logs\stdout.log`.
