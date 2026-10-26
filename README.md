# RyGT

<h1 align="center">
  <img src="assets/GranTurismoSportBanner.png" width="800" alt="Gran Turismo Sport banner">
  <br>
  <b>RyGT</b>
  <br>
  <sub>Gran Turismo Sport on PC</sub>
</h1>


**Gran Turismo Sport running as a native Windows program.**

RyGT ports Gran Turismo Sport (CUSA03220, update 1.69) from PlayStation 4 to Windows. The game is
not emulated: RyGT's converter turns the game's executable into `GTSport.exe`, the game code runs
directly on your CPU, and RyGT's libraries stand in for the PS4 system. Graphics go through
[shadGT](https://gitlab.com/rygt-team/shadGT)'s Vulkan renderer, built as the
game's video output library.

RyGT is a fork of [Ryty](https://github.com/aarvsn/Ryty), the general PS4/PS5 porting tool, with
everything GT Sport needs added on top.

## Status

| Area | State |
| --- | --- |
| Boot, menus, car and track selection | Working |
| Races | Working, about 42-60 fps depending on how many cars are on screen |
| Race summary and results | Working |
| Audio | Working |
| Controllers | Working through SDL |
| Online features | Not supported |

Known issues:
- The first 30 seconds of a race can stutter while graphics pipelines compile.
- A few draws in the race summary can occasionally read garbage vertex descriptors; those draws
  are skipped.

Tested on Windows 11 with an Intel i7-13700KF and an RTX 4070Ti.
Tested on Windows 11 with an AMD Ryzen 5 9600X and an RTX 3060 12GB

## Download

Get the latest build from [Releases](https://gitlab.com/rygt-team/RyGT/-/releases). A release
contains the runtime libraries, the renderer, the converter, a launcher and the GT Sport settings.
It contains **no game files**: you need your own decrypted dump of GT Sport CUSA03220 with update
1.69, and the system modules and fonts from your own PS4 firmware.

Unzip it on the same drive as your dump and run `RyGT.exe`: pick your game, update, firmware
module and font folders, press **Set up**, then **Play**. The loader's source is in
[loader](loader). Manual setup steps are in [docs/gt-sport/README.md](docs/gt-sport/README.md).

## How it fits together

```
your dump: eboot.bin (1.69) + sce_module
        |
        |  unwrap the SELF, then convert with ryty.exe --windows --ps4
        v
GTSport.exe + app0\sce_module\*.guest.prx
        |
        |  loads at run time
        v
libs\  RyGT runtime libraries (kernel, libc, save data, audio, input, ...)
       + shadGT renderer (libSceVideoOut.prx: Gnm driver, PM4 processing, Vulkan)
```

## What this repository contains

- **Converter** ([core/relinker](core/relinker)): turns the PS4 executable and its modules into
  Windows binaries. RyGT adds fixes for GT Sport's executable and modules.
- **Runtime libraries** ([core/libs/prx](core/libs/prx)): the PS4 system libraries the game links
  against. RyGT's changes for GT Sport include:
  - kernel: an event queue bridge for the renderer, mapping and protection change notices so the
    renderer's memory tracking stays correct, `MAP_FIXED` replacing mappings, direct memory and
    semaphore fixes;
  - libc: C++ exceptions through the SEH runtime, guest allocation fixes, crash reports with
    debugging aids;
  - save data, AppContent, audio output and network control fixes, including holding the first
    save data mount until the boot screen is drawn (GT Sport's boot script otherwise crashes).
- **Loader** ([loader](loader)): `RyGT.exe`, which converts the game from your own dump and
  starts it, with options for the splash screen, FPS counter, Discord and AMD FSR 3.1 or FSR 1 upscaling.
- **DLSS plugin** ([plugins/dlss](plugins/dlss)): optional NVIDIA DLSS upscaling, built and
  installed separately (not in the releases).
- **GT Sport guide** ([docs/gt-sport](docs/gt-sport)): setup from your own dump.

The renderer lives in [shadGT, branch `ryty-gpu`](https://gitlab.com/rygt-team/shadGT)
(`ryty_gpu/`). It builds shadGT's video output, Gnm driver and Vulkan renderer as
`libSceVideoOut.prx`, plus shadGT's audio, NP and other libraries for RyGT. It carries the GT
Sport rendering fixes and the performance work (Vulkan calls on their own thread, cheaper buffer
and image tracking).

## Building

- **Runtime libraries:** the Windows MinGW build in an MSYS2 shell (link-time optimization off).
  See [docs/user/BUILDING.md](docs/user/BUILDING.md).
- **Converter:** the `ryty` target in [core/relinker](core/relinker).
- **Renderer:** in shadGT's `ryty-gpu` branch,
  `cmake --preset x64-Clang-Release -DSHADGT_RYTY_GPU=ON`, then build the `ryty_gpu` target.

## Branches

- `main`: stable builds; releases come from here.
- `development`: ongoing work. Changes reach `main` once they are tested.
- 'shader_fixes': Fixing shader bugs.'

# Discord Server
Join our server for more updates: https://discord.gg/8dz4U9Nd8d

## Documentation

- [GT Sport setup](docs/gt-sport/README.md)
- Ryty's general documentation: [index](docs/README.md), [building](docs/user/BUILDING.md),
  [usage](docs/user/USAGE.md), [input mapping](docs/user/INPUT_MAPPING.md),
  [porting pipeline](docs/dev/PORTING_PIPELINE.md)

## Credits

- [Ryty](https://github.com/aarvsn/Ryty) by aarvsn, itself a fork of
  [AnyPS5](https://github.com/boykopovar/AnyPS5) by Andrii Semkiv (boykopovar) and its
  contributors: the converter, the system library implementations and the test infrastructure.
- [shadPS4](https://github.com/shadps4-emu/shadPS4) and its contributors, through shadGT: the
  renderer, shader recompiler and HLE libraries.
- [Nenkai](https://github.com/Nenkai): [GTToolsSharp](https://github.com/Nenkai/GTToolsSharp)
  (reading the game's volumes) and [GTAdhocToolchain](https://github.com/Nenkai/GTAdhocToolchain)
  (the AdHoc script compiler and disassembler), both MIT licensed. The launcher uses them to build
  the offline livery and decal scripts from your own game files, and Nenkai's research into
  Gran Turismo's formats and scripts made that work possible.
- See [AUTHORS.md](AUTHORS.md).

## Disclaimer

This project is for interoperability, research and preservation. It does not include or
distribute copyrighted games, firmware, keys or proprietary libraries. Use your own legally
obtained copy of the game and your own console's firmware files, in line with the laws where
you live.

Please do NOT ask shadps4 or Ryty servers for help from this project.

## License

GNU General Public License version 2 only, as Ryty. The renderer (shadGT) is also GPL-2.0.

<p align="center">
  <img src="assets/GranTurismoSportBanner2.webp" width="800" alt="Gran Turismo Sport banner">
</p>
