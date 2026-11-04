# CI packages

GitLab is the authoritative source and download host. GitHub Actions in
[`RyGT-Team/RyGTS-ci`](https://github.com/RyGT-Team/RyGTS-ci/actions/workflows/ci-package.yml)
builds, tests, packages and publishes. No GitLab runner is used.

GitLab project CI/CD is disabled (`builds_access_level=disabled`). The repository
keeps a `workflow: when: never` guard as well. GitHub Actions remains enabled,
and GitLab Package Registry remains enabled for CI ZIP uploads. Build status
and logs are available in GitHub Actions rather than GitLab pipelines.

The workflow checks the public GitLab `development` branch every 15 minutes and
builds its exact commit when no completed CI package exists for that revision.
GitHub schedules can be delayed. To start sooner or retry, use **Run workflow**;
enable **force** to rebuild an already published revision. Changes arriving
during a build are picked up by a later check. Builds are serialized to avoid
duplicate uploads. Only development is automatically packaged; main, tags and
merge-request branches are not publication inputs.

## Downloads

Open GitLab **Deploy → Package Registry**. Two separate packages are produced:

- **ci**: Release, `RyGTS-ci-<short commit>.zip`.
- **debug-ci**: optimized Debug/Diagnostics, `RyGTS-debug-ci-<short commit>.zip`.

Each channel uses version `development-<full source commit>`, with its own
`.zip.sha256` and `CI-BUILD.json` recording channel, source commit, checksum and
GitHub run URL. Both channels must have valid completion markers to skip a build.
Both archives and checksums are verified before either completion marker is
published. GitHub also retains the two packages as separate artifacts, alongside
the `CI-PACKAGES.json` manifest.

These are CI builds. Publishing does not create a tag, release, announcement or
changelog. The completion marker is uploaded last, after reading back and
verifying all package files. Package versions persist until removed from the
registry. GitHub intermediate artifacts expire after seven days.

## Build and validation

The Windows job builds the launcher and pinned renderer using
`ci/setup-hosted-windows.ps1` and `ci/build-windows.ps1`. A separate GitHub Linux
job builds and tests the converter with CTest. Packaging requires both jobs to
succeed and verifies source revisions, artifact checksums, runtime archive
identity, required files, safe ZIP paths and packaged-file checksums.

The Windows converter and other helper/runtime binaries still come from the
immutable October 25 redistributable archive pinned by SHA-256 in `ci/package.py`.
They are not rebuilt by this workflow; the Linux test binary is not substituted
for a Windows executable. BUILD_INFO.txt records exactly what was rebuilt.
Game executable/data, firmware, saves, logs, shader caches and NVIDIA SDK files
are excluded from both packages. Base-archive PDBs remain forbidden; only
verified matching launcher/renderer symbols are explicitly added to debug-ci.
Compilation and package validation do not replace live GPU tests.

## Debug diagnostics

The renderer uses CMake `Debug` with `/O2 /Ob2 /Zi` and the redistributable
`MultiThreadedDLL` runtime. Debug-only instrumentation stays compiled in;
normal Debug/Trace log levels remain available in both configurations. The
launcher is optimized, with matching PDBs beside each rebuilt module. Debug
includes a versioned
diagnostics-only renderer overlay and optional capture tools. Release uses the
pinned renderer with production fixes. The overlay refuses another renderer pin until it
has been rebased and validated. Package metadata identifies both the pin and
overlay hashes. Other runtime helpers retain the same pinned base provenance;
this does not provide symbols for those helpers.

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

`renderer/source.json` pins the ShadGT `ryty-gpu` commit. Push a renderer change
there before publishing a RyGTS pin that references it. Local investigation
branches and unpushed renderer commits are not included automatically.

## Workflow maintenance and credentials

The reviewed workflow and control-script sources are retained under
`ci/github-registry/`. The deployed copies live in the GitHub workflow repository
as `.github/workflows/ci-package.yml` and `ci/registry_ci.py`. Update both together.
Official actions are pinned to full commit IDs. The old GitLab bridge is retired;
`.gitlab-ci.yml` explicitly disables GitLab pipelines.

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
