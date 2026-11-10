# Custom environment variables (FEAT-GTS-433)

Settings > General > Custom environment variables, under Shaders.
A table of On / Name / Value rows with Add and Remove (Delete also removes selected rows).
Saved as you change it, as `env=<1|0>|NAME=VALUE` lines in `RyGT.ini` (the launcher's own settings
file; the game's `user\custom_configs\CUSA03220.json` is not touched). Restart the game to apply.

- Names: letters, digits and `_` only. A row with any other name shows an error and is not saved.
- Enabled rows are applied to `GTSport.exe` when the launcher starts it (and on the device-loss
  relaunch), after the launcher's own variables, so they override both the launcher's inherited
  environment and `RYGT_LOG_DIRECTORY` / `RYGT_HDD_STREAMING` / `RYGT_ANTI_LAG`.
- An empty value removes the variable from the game's environment.
- Off rows stay in the list but are not set.
- Only a count is written to the setup log ("N custom environment variable(s) applied."), never values.

Typical use: renderer diagnosis switches such as `RYGT_SO_CACHE=0`, `RYGT_DRIVER_CACHE=0`,
`RYGT_SHADER_STORE=0`, `RYGT_SO_HASH_MASK` / `RYGT_SO_HASH_VALUE`, `RYGT_NATIVE_PREPARE`.
To confirm they reach the game, set one the renderer logs at startup (for example
`RYGT_SO_HASH_MASK`, which logs `DIAG-GTS-432`) and check `logs\stdout.log`.
