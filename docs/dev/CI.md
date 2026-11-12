# CI packages

GitLab is the authoritative source and download host. GitHub Actions in
[`RyGT-Team/RyGTS-ci`](https://github.com/RyGT-Team/RyGTS-ci/actions)
builds, tests, packages and publishes Release (`ci`) and Diagnostics
(`debug-ci`) independently. No GitLab runner is used.

GitLab project CI/CD is disabled (`builds_access_level=disabled`). The repository
keeps a `workflow: when: never` guard as well. GitHub Actions remains enabled,
and GitLab Package Registry remains enabled for CI ZIP uploads. Build status
and logs are available in GitHub Actions rather than GitLab pipelines.

The Release workflow checks the public GitLab `development` branch at minutes
07 and 37; Diagnostics checks it at 22 and 52. Each resolves the same exact
source and renderer commits, then checks only its own completion marker. A
missing or failed Diagnostics package does not block Release, and vice versa.
GitHub schedules can be delayed. To start sooner or retry, use **Run workflow**
on the channel you need; enable **force** to rebuild that channel's published
revision. Changes arriving during a build are picked up by a later check. Each
channel is serialized separately. Only development is automatically packaged;
main, tags and merge-request branches are not publication inputs.

## Downloads

Open GitLab **Deploy → Package Registry**. Two separate packages are produced:

- **ci**: Release, `RyGTS-ci-<short commit>.zip`.
- **debug-ci**: optimized Debug/Diagnostics, `RyGTS-debug-ci-<short commit>.zip`.

Each channel uses version `development-<full source commit>-renderer-<full
renderer commit>`, with its own `.zip.sha256` and `CI-BUILD.json` recording
channel, source commit, checksum and GitHub run URL. A valid marker skips only
its own channel. The selected archive and checksum are verified before that
channel's completion marker is published. GitHub retains the packages as
separate workflow artifacts, alongside a channel-specific `CI-PACKAGES.json`
manifest.

These are CI builds. Publishing does not create a tag, release, announcement or
changelog. The completion marker is uploaded last, after reading back and
verifying all package files. Package versions persist until removed from the
registry. GitHub intermediate artifacts expire after seven days.

## Build and validation

Each channel's Windows job builds its launcher, its pinned renderer and the runtime
libraries using `ci/setup-hosted-windows.ps1` and `ci/build-windows.ps1`. Its own GitHub Linux
job builds and tests the converter with CTest. Packaging requires that
channel's jobs to succeed and verifies source revisions, artifact checksums,
runtime archive identity, required files, safe ZIP paths and packaged-file
checksums.

The runtime libraries (`libs/*.prx`, except the renderer's) are built by
`ci/build-windows.ps1 -Target Runtime` with the MSYS2 UCRT64 GCC 16.2.0 pinned in
`ci/runtime-toolchain.json`: every MSYS2 package and the FFmpeg prebuilt by file name
and SHA-256, unpacked by `ci/runtime_toolchain.py`. They replace the base archive's
copies, together with that toolchain's `libgcc_s_seh-1.dll`, `libstdc++-6.dll` and
`libwinpthread-1.dll`. That includes the placeholder libraries for PS4 libraries the
game loads but RyGTS does not implement (NP dialogs, USB, VR, `libSceGnmDriver.prx`,
...), generated from the export lists in `core/libs/placeholders`. The renderer step
also builds the runtime libraries the renderer repository implements (`libSceAjm`,
`libSceNgs2`, `libSceAudio3d`, `libSceZlib`, `libSceDiscMap`, `libSceScreenShot`,
`libSceNpAuth`); they are packaged last, over this source's own smaller versions.
The Windows converter and other helpers still come from the immutable October 25
redistributable archive pinned by SHA-256 in `ci/package.py`. Packaging fails when a
packaged module imports a function another packaged module does not export, so a
library that no longer matches the rebuilt `libc`/`libkernel` cannot ship.
BUILD_INFO.txt records exactly what was rebuilt and what was retained.

To move the toolchain to newer MSYS2 packages, run
`python ci/runtime_toolchain.py pin <date>` (each package at its newest version
published on or before that date) and build and test the runtime with the result.
Game executable/data, firmware, saves, logs, shader caches and NVIDIA SDK files
are excluded from both packages. Base-archive PDBs remain forbidden; only
verified matching launcher/renderer symbols are explicitly added to debug-ci.
Compilation and package validation do not replace live GPU tests.

## Debug diagnostics

Diagnostics uses optimized `RelWithDebInfo` native code and an optimized
launcher, with full matching PDBs beside each rebuilt module. Its native
configuration leaves `NDEBUG` undefined so debug-only capture instrumentation
and its frame-layout checks are present. Release keeps its normal `Release`
configuration and `NDEBUG` behavior. Both flavors build directly from the
resolved ShadGT renderer commit; no renderer source overlay is applied. Other
runtime helpers retain the same pinned base provenance; this does not provide
symbols for those helpers.

Extract ci and debug-ci into separate folders. Debug's README explains setup,
F8 graphics capture, continuous frame timing, visible-frame bursts and offline
analysis. Extra captures cover shader identities, CPU/GPU pass spans, shadow and
depth state, effective mip/LOD sampler settings, indirect dispatch arguments and
bounded mip readbacks. The optional collector requires Python 3.12 or newer.
No debugger or Python is required for ordinary gameplay. Diagnostic frames can
perturb timing, and timestamp spans include dependencies; use normal-play
recording for performance comparisons. The tracing overlay does not fix graphics
or change culling/LOD policy. Both packages preserve Discord RPC and enable shader
precompilation and pipeline caching in the factory configuration, including 16 GB.

`renderer/source.json` selects the ShadGT `ryty-gpu` branch with `commit: HEAD`.
Each workflow resolves its commit once and passes it as `RENDERER_SHA` to its
build and packager. A full commit SHA remains supported as an explicit pin; an
overriding SHA must match it. For manual builds set `RENDERER_SHA` to the same
full commit for both flavors and packaging. Without it, renderer preparation
resolves the branch itself.

## Code signing

CI Authenticode-signs the launcher with a self-signed certificate
(`CN=RyGT-Team, O=RyGT-Team`) before checksums and packaging, via
`ci/sign-windows.ps1`. Set repository secrets `CODESIGN_PFX_BASE64` (base64 PFX) and
`CODESIGN_PFX_PASSWORD` to use one stable certificate for every build; without
them each run signs with a throwaway certificate. The signature is not trusted
by Windows unless the certificate is installed as trusted, so SmartScreen and
"Unknown publisher" warnings can still appear. Local builds sign only when
`RYGT_CODESIGN=1`.

## Workflow maintenance and credentials

The reviewed workflow and control-script sources are retained under
`ci/github-registry/`. The deployed copies live in the GitHub workflow repository
as `.github/workflows/ci-package-release.yml`,
`.github/workflows/ci-package-debug.yml` and `ci/registry_ci.py`. Update the
canonical templates and deployed copies together. Official actions are pinned
to full commit IDs. The old GitLab bridge is retired; `.gitlab-ci.yml` explicitly
disables GitLab pipelines.

GitHub secret `GITLAB_PACKAGE_TOKEN` is a project deploy token restricted to
RyGTS package read/write access. It is exposed only to the final publish step,
not to Windows builds or Linux converter tests. The initial token expires
January 25, 2027; rotate it before then. It cannot create releases, modify source
or report commit statuses. Build status and logs are in GitHub Actions; GitLab
holds packages rather than runner jobs. Never put credentials in source files.

To build manually in a Windows developer PowerShell, install LLVM 23.1.3 with
compiler-rt, set `LLVM_ROOT`, and run:

```powershell
./ci/build-windows.ps1 -Target Launcher
./ci/build-windows.ps1 -Target Renderer
./ci/build-windows.ps1 -Target Launcher -Flavor Diagnostics
./ci/build-windows.ps1 -Target Renderer -Flavor Diagnostics
```
