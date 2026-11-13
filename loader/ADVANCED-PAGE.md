# Advanced page: renderer switches and environment variables (FEAT-GTS-433, FEAT-GTS-437)

The Advanced tab lists the experimental `RYGT_*` and `SHADGT_*` switches of the installed renderer and sets them
for `GTSport.exe` when the launcher starts it.

## Where the list comes from

Nothing is listed in the launcher. When the launcher starts it reads `libs\libSceVideoOut.prx` (the copy the game loads) as a file
(it never loads it) and collects:

- every string that is exactly `RYGT_` or `SHADGT_` plus capitals, digits and `_`: the names the renderer passes to
  `getenv`. A switch added to the renderer appears with the next renderer build.
- every `RYGTSW1|NAME|kind|group|default|values|description` string: the renderer's description of a
  switch, kept in shadGT's `src/video_core/rygt_switches.cpp` (exported as `RyGT_SwitchDescriptions`
  so the linker keeps it). The description is the hover tooltip; kind picks the editor (a toggle for on/off switches, which flips and returns to its default; Default / On / Off when the
  default depends on the GPU or another setting, e.g. "on with AMD's older shader compiler"; a checked
  value for int, hex and float); group is the heading.

A switch the renderer reads but does not describe is listed under "Not described in this renderer
build". A description without a matching name (for example a switch only a diagnostics build reads)
is not listed. Switches the launcher sets from its own settings (`RYGT_LOG_DIRECTORY`,
`RYGT_HDD_STREAMING`, `RYGT_ANTI_LAG`, the render-scale ones) are left out.

"Diagnostics & tracing" switches are hidden until "Show diagnostics and tracing" is ticked (changed
ones always show). Search matches names and descriptions; "Only changed" shows what is set; Reset all
returns every switch to its default. Delete on a setting returns it to the default.

## Profiles (FEAT-GTS-270)

The Profile row above the table keeps named sets of the page's settings, to swap between
configurations. **Save...** asks for a name (the selected profile's, to overwrite it) and writes the
current switches and other variables to `launcher\profiles\<name>.ini`: a comment line and the same
`env=<1|0>|NAME=VALUE` lines as `RyGT.ini`, so a profile can be copied or shared as a file. **Load**
replaces the current settings with the selected profile's (after asking) and builds the page again,
keeping the search and filters. A profile holds everything the page sets; switches it does not name
are at their defaults after loading it.

## Other environment variables

Any variable the list does not have goes in the table under it (On / Name / Value). Names are
letters, digits and `_`; other names are flagged and not saved. An empty value removes the variable.

## Storage and launch

Everything is saved as you change it, as `env=<1|0>|NAME=VALUE` lines in `RyGT.ini` (the game's
`user\custom_configs\CUSA03220.json` is not touched). When the launcher starts `GTSport.exe` (and on
the device-loss relaunch) the entries are applied after the launcher's own variables, so they win.
Only a count is written to the setup log, never values.

To confirm the switches reach the game: `setup.log` gets "N custom environment variable(s) applied." when
the launcher starts `GTSport.exe`; then set one whose effect you can see (for example a rendering switch
from its tooltip) and compare.
