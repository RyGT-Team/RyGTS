param([Parameter(Mandatory=$true)][ValidateSet('Launcher','Renderer','Runtime','Tools')][string]$Target, [ValidateSet('Release','Diagnostics')][string]$Flavor='Release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot -Parent)
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
$suffix = if ($Flavor -eq 'Diagnostics') { '-diagnostics' } else { '' }
$out = Join-Path (Get-Location) ("artifacts/" + $Target.ToLowerInvariant() + $suffix)
New-Item -ItemType Directory -Force $out | Out-Null
if ($Target -eq 'Launcher') {
    Invoke-Checked 'cmd.exe' @('/d','/c', 'loader\build.cmd', $out, $Flavor)
    if (!(Test-Path "$out/RyGTS.exe")) { throw 'Launcher output missing' }
    if ($env:RYGT_CODESIGN -eq '1') { & (Join-Path $PSScriptRoot 'sign-windows.ps1') -Path "$out/RyGTS.exe" }
} elseif ($Target -eq 'Runtime') {
    # FEAT-GTS-260: the runtime libraries (libs/*.prx) and the MinGW runtime DLLs beside GTSport.exe,
    # built with the pinned MSYS2 UCRT64 GCC and FFmpeg package in ci/runtime-toolchain.json. Only the
    # renderer's libSceVideoOut.prx is left out: the Renderer target builds the real one.
    $tools = if ($env:RYGT_RUNTIME_TOOLS) { $env:RYGT_RUNTIME_TOOLS } else { 'C:/ci-tools/runtime' }
    $toolchain = & python ci/runtime_toolchain.py install $tools | Select-Object -Last 1
    if ($LASTEXITCODE -or !$toolchain -or !(Test-Path "$toolchain/g++.exe")) { throw 'Pinned runtime toolchain unavailable' }
    $ffmpeg = & python ci/runtime_toolchain.py ffmpeg $tools | Select-Object -Last 1
    if ($LASTEXITCODE -or !$ffmpeg) { throw 'Pinned FFmpeg package unavailable' }
    $env:PATH = "$toolchain;$env:PATH"
    $compiler = & "$toolchain/g++.exe" --version | Select-Object -First 1
    if ("$compiler" -notmatch '\b16\.2\.0\b') { throw "Unexpected runtime compiler: $compiler" }
    $submodules = @('LibAtrac9','SDL2','SPIRV-Headers','Vulkan-Headers','ffmpeg-core','freetype','glslang','stb') | ForEach-Object { "3rdparty/$_" }
    Invoke-Checked 'git' (@('submodule','update','--init','--depth','1','--') + $submodules)
    $build = Join-Path (Get-Location) 'build/ci-runtime'
    Invoke-Checked 'cmake' @('-S','.','-B',$build,'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DBUILD_TESTING=OFF',
        '-DRTY_BUILD_GUI=OFF','-DRTY_ENABLE_LTO=OFF',"-DCMAKE_C_COMPILER=$toolchain/gcc.exe",
        "-DCMAKE_CXX_COMPILER=$toolchain/g++.exe","-DFFMPEG_PREBUILT_DIR=$ffmpeg")
    # FEAT-GTS-263: the converter (ryty.exe) and the wheel probe too; they came from the archive before.
    Invoke-Checked 'cmake' @('--build',$build,'--target','libs','ryty','ryty-wheel-probe')
    $libraries = @(Get-ChildItem "$build/core/libs/libs" -Filter *.prx -File | Where-Object Name -ne 'libSceVideoOut.prx')
    foreach ($required in 'libc.prx','libkernel.prx','libScePad.prx') {
        if (!($libraries | Where-Object Name -eq $required)) { throw "Runtime build is missing $required" }
    }
    $libraries | Copy-Item -Destination $out
    foreach ($program in 'ryty.exe','ryty-wheel-probe.exe') {
        $built = @(Get-ChildItem $build -Recurse -Filter $program -File)
        if ($built.Count -ne 1) { throw "Expected exactly one built $program (found $($built.Count))" }
        Copy-Item $built[0].FullName $out
    }
    foreach ($dll in 'libgcc_s_seh-1.dll','libstdc++-6.dll','libwinpthread-1.dll') { Copy-Item "$toolchain/$dll" $out }
    Copy-Item ci/runtime-toolchain.json "$out/RUNTIME-TOOLCHAIN.json"
    Write-Host "Runtime: $($libraries.Count) libraries, the converter and the wheel probe built with $compiler"
} elseif ($Target -eq 'Tools') {
    # FEAT-GTS-263: what the October 25 archive used to supply besides the runtime: adcpatch, built
    # from its pinned upstream sources and this repository's patches (tools/adcpatch/README.txt), and
    # AMD's FidelityFX runtime, downloaded from AMD's FidelityFX SDK release. Both pinned in
    # ci/runtime-toolchain.json.
    $pins = Get-Content ci/runtime-toolchain.json -Raw | ConvertFrom-Json
    foreach ($source in $pins.adcpatch.sources) {
        $dir = "tools/$($source.name)"
        if (Test-Path $dir) { throw "$dir already exists; the Tools target needs a clean checkout" }
        Invoke-Checked 'git' @('init','-q',$dir)
        Invoke-Checked 'git' @('-C',$dir,'remote','add','origin',$source.repository)
        Invoke-Checked 'git' @('-C',$dir,'fetch','-q','--depth','1','origin',$source.commit)
        Invoke-Checked 'git' @('-C',$dir,'checkout','-q','--detach','FETCH_HEAD')
        if ((& git -C $dir rev-parse HEAD) -cne $source.commit) { throw "$($source.name) commit mismatch" }
        Invoke-Checked 'git' @('-C',$dir,'submodule','update','--init','--recursive','--depth','1')
        Invoke-Checked 'git' @('-C',$dir,'apply','--whitespace=nowarn',(Resolve-Path "tools/adcpatch/$($source.patch)").Path)
        Copy-Item "$dir/LICENSE" "$out/$($source.name)-LICENSE.txt"
        if ($source.name -eq 'GTToolsSharp') { Copy-Item "$dir/PDTools/LICENSE" "$out/PDTools-LICENSE.txt" }
    }
    $publish = Join-Path (Get-Location) 'build/ci-adcpatch'
    Invoke-Checked 'dotnet' @('publish','tools/adcpatch/adcpatch.csproj','-c','Release','-r','win-x64','--self-contained',
        '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:DebugType=none','-o',$publish,'--nologo')
    foreach ($file in 'adcpatch.exe','grim.ad','openGrim.ad') { Copy-Item "$publish/$file" $out }
    Copy-Item tools/adcpatch/README.txt "$out/adcpatch-README.txt"
    $amd = $pins.fidelityfx
    foreach ($file in @(@($amd.dll_url, $amd.dll_sha256, 'amd_fidelityfx_vk.dll'), @($amd.license_url, $amd.license_sha256, 'amd_fidelityfx_vk-LICENSE.txt'))) {
        Invoke-WebRequest -UseBasicParsing $file[0] -OutFile "$out/$($file[2])"
        if ((Get-FileHash "$out/$($file[2])" -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file[1]) { throw "SHA-256 mismatch for $($file[2])" }
    }
    Write-Host "Tools: adcpatch and AMD FidelityFX $($amd.version)"
} else {
    if (!$env:LLVM_ROOT) { throw 'Set LLVM_ROOT to the LLVM 23.1.3 distribution (see docs/dev/CI.md)' }
    $llvm = (Resolve-Path $env:LLVM_ROOT).Path
    $env:PATH = "$llvm/bin;$env:PATH"
    $version = & clang-cl --version
    if ($LASTEXITCODE -ne 0 -or "$version" -notmatch '23\.1\.3') { throw 'LLVM 23.1.3 required' }
    $builtins = Get-ChildItem "$llvm/lib" -Recurse -Filter clang_rt.builtins-x86_64.lib | Select-Object -First 1
    if (!$builtins) { throw 'Full LLVM archive with compiler-rt builtins is required' }
    $env:LIB = "$($builtins.DirectoryName);$env:LIB"
    $source = Join-Path (Get-Location) ('.ci-renderer' + $suffix)
    Invoke-Checked 'python' @('ci/prepare-renderer.py', $source)
    $rendererIdentity = @(Get-Content -LiteralPath "$source/CI-SOURCE.txt")
    if ($rendererIdentity.Count -ne 2 -or $rendererIdentity[1] -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid prepared renderer identity' }
    if ($env:RENDERER_SHA -and $env:RENDERER_SHA -cne $rendererIdentity[1]) { throw 'Prepared renderer revision mismatch' }
    $env:RENDERER_SHA = $rendererIdentity[1]
    $configuration = if ($Flavor -eq 'Diagnostics') { 'RelWithDebInfo' } else { 'Release' }
    $cmakeArgs = @('-S',$source,'-B',"$source/Build/ci",'-G','Ninja',"-DCMAKE_BUILD_TYPE=$configuration",'-DCMAKE_C_COMPILER=clang-cl','-DCMAKE_CXX_COMPILER=clang-cl','-DCMAKE_LINKER=lld-link','-DHAVE_ENDIAN_H=OFF','-DSHADGT_RYTY_GPU=ON','-DENABLE_DISCORD_RPC=ON','-DCMAKE_EXPORT_COMPILE_COMMANDS=ON')
    if ($Flavor -eq 'Diagnostics') {
        # RelWithDebInfo normally defines NDEBUG. Diagnostics need the optimized,
        # symbolized capture instrumentation guarded by #ifndef NDEBUG.
        $cmakeArgs += @('-DCMAKE_C_FLAGS_RELWITHDEBINFO=/O2 /Ob2 /Zi /UNDEBUG','-DCMAKE_CXX_FLAGS_RELWITHDEBINFO=/O2 /Ob2 /Zi /UNDEBUG')
    }
    if (!$env:VCINSTALLDIR) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -ne 0 -or !$vs) { throw 'Visual Studio x64 C++ tools not found' }
        Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
        Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
        # Developer-shell initialization can replace LIB/PATH; restore our pinned LLVM first.
        $env:PATH = "$llvm/bin;$env:PATH"
        $env:LIB = "$($builtins.DirectoryName);$env:LIB"
    }
    Invoke-Checked 'cmake' $cmakeArgs
    # CMake also exports the emulator's compilation of these same sources.
    # Verify only the object compiled for the packaged renderer target.
    function Test-RendererCompileTarget($Entry) {
        $output = $Entry.PSObject.Properties['output']
        if ($output) {
            return $output.Value -match '(?:^|[/\\])CMakeFiles[/\\]ryty_gpu\.dir[/\\]'
        }
        return $Entry.command -match '(?:^|\s)/Fo"?CMakeFiles[/\\]ryty_gpu\.dir[/\\]'
    }
    if ($Flavor -eq 'Diagnostics') {
        $commands = @(Get-Content -Raw "$source/Build/ci/compile_commands.json" | ConvertFrom-Json)
        foreach ($pattern in @('[/\\]vk_rasterizer\.cpp$', '[/\\]native_xxh512\.c$')) {
            $compile = @($commands | Where-Object { $_.file -match $pattern -and (Test-RendererCompileTarget $_) })
            if ($compile.Count -ne 1) { throw "Expected one diagnostics compile command for $pattern (ryty_gpu target; found $($compile.Count))" }
            $command = $compile[0].command
            if ($command -notmatch '(?:^|\s)/O2(?:\s|$)' -or
                $command -notmatch '(?:^|\s)/Ob2(?:\s|$)' -or
                $command -notmatch '(?:^|\s)/Zi(?:\s|$)' -or
                $command -notmatch '(?:^|\s)/UNDEBUG(?:\s|$)' -or
                $command -match '(?:^|\s)(?:/D|-D)NDEBUG(?:\s|$)') {
                throw "Diagnostics compile command for $pattern must be optimized, symbolized, and compiled without NDEBUG"
            }
        }
    } else {
        $commands = @(Get-Content -Raw "$source/Build/ci/compile_commands.json" | ConvertFrom-Json)
        $rasterizer = @($commands | Where-Object { $_.file -match '[/\\]vk_rasterizer\.cpp$' -and (Test-RendererCompileTarget $_) })
        if ($rasterizer.Count -ne 1) { throw "Expected one Release rasterizer compile command for NDEBUG verification (ryty_gpu target; found $($rasterizer.Count))" }
        $command = $rasterizer[0].command
        if ($command -notmatch '(?:^|\s)(?:/D|-D)NDEBUG(?:\s|$)' -or $command -match '(?:^|\s)/UNDEBUG(?:\s|$)') {
            throw 'Release rasterizer compile command must retain NDEBUG'
        }
    }
    $probeSource = @'
#if defined(RYGT_EXPECT_NDEBUG) != defined(NDEBUG)
#error NDEBUG state does not match the build flavor
#endif
int rygt_ndebug_probe(void) { return 0; }
'@
    $probeCpp = "$source/Build/ci/ndebug-probe.cpp"
    $probeC = "$source/Build/ci/ndebug-probe.c"
    Set-Content -LiteralPath $probeCpp -Value $probeSource -Encoding utf8
    Set-Content -LiteralPath $probeC -Value $probeSource -Encoding utf8
    $probeFlags = if ($Flavor -eq 'Diagnostics') { @('/UNDEBUG') } else { @('/DNDEBUG','/DRYGT_EXPECT_NDEBUG=1') }
    foreach ($probe in @($probeC, $probeCpp)) {
        $object = "$source/Build/ci/ndebug-probe-$([IO.Path]::GetExtension($probe).TrimStart('.')).obj"
        Invoke-Checked 'clang-cl' (@('/nologo','/c','/O2','/Ob2','/Zi') + $probeFlags + @($probe,"/Fo$object"))
    }
    if (!(Select-String -LiteralPath "$source/Build/ci/CMakeCache.txt" -Pattern '^ENABLE_DISCORD_RPC:BOOL=ON$' -Quiet)) { throw 'Discord RPC was not enabled in renderer configuration' }
    # FIX-GTS-262: the renderer repository also implements these runtime libraries (as ryGT7's CI
    # builds them); they replace both the archive's and this source's own versions in the package.
    $rendererLibraries = [ordered]@{ ryty_ajm='libSceAjm.prx'; ryty_ngs2='libSceNgs2.prx'; ryty_audio3d='libSceAudio3d.prx';
        ryty_zlib='libSceZlib.prx'; ryty_discmap='libSceDiscMap.prx'; ryty_screenshot='libSceScreenShot.prx'; ryty_np='libSceNpAuth.prx' }
    # FEAT-GTS-280: ryty-feel-test, the launcher's HD rumble Test buttons, packaged in tools\.
    $rendererTools = [ordered]@{ ryty_feel_test='ryty-feel-test.exe' }
    Invoke-Checked 'cmake' (@('--build',"$source/Build/ci",'--target','ryty_gpu') + @($rendererLibraries.Keys) + @($rendererTools.Keys) + @('--parallel','2'))
    foreach ($library in @($rendererLibraries.Values) + @($rendererTools.Values)) {
        $built = @(Get-ChildItem "$source/Build/ci" -Recurse -Filter $library)
        if ($built.Count -ne 1) { throw "Expected exactly one renderer-built $library (found $($built.Count))" }
        Copy-Item $built[0].FullName $out
    }
    $binary = Get-ChildItem "$source/Build/ci" -Recurse -Filter libSceVideoOut.prx
    if (@($binary).Count -ne 1) { throw 'Expected exactly one renderer output' }
    Copy-Item $binary.FullName $out
    $symbols = @(Get-ChildItem "$source/Build/ci" -Recurse -Filter 'libSceVideoOut.pdb')
    if ($Flavor -eq 'Diagnostics' -and $symbols.Count -ne 1) { throw 'Exactly one matching renderer PDB required' }
    $symbols | Copy-Item -Destination $out
    if ($Flavor -eq 'Diagnostics') {
        Invoke-Checked 'python' @('ci/diagnostics/write-capture-layout.py', $out, "$out/frame-capture-layouts.json", "$llvm/bin")
    }
    Copy-Item "$source/CI-SOURCE.txt" $out
    Invoke-Checked 'git' @('-C',$source,'submodule','status','--recursive')
}
if ($Flavor -eq 'Diagnostics' -and $Target -ne 'Runtime') {
    if (!$env:LLVM_ROOT) { throw 'LLVM_ROOT required to verify diagnostics symbols' }
    $name = if ($Target -eq 'Launcher') { 'RyGTS' } else { 'libSceVideoOut' }
    $extension = if ($Target -eq 'Launcher') { 'exe' } else { 'prx' }
    Invoke-Checked 'python' @('ci/diagnostics/verify-symbols.py', "$out/$name.$extension", "$out/$name.pdb", "$env:LLVM_ROOT/bin", "$out/SYMBOLS.json")
}
$commit = & git rev-parse HEAD
if ($LASTEXITCODE) { throw 'Cannot record source revision' }
$commit | Set-Content "$out/RYGT-COMMIT.txt"
@{schema=1;flavor=$Flavor;commit=$commit;optimized=$true;full_symbols=($Flavor -eq 'Diagnostics')} | ConvertTo-Json | Set-Content "$out/BUILD-PROFILE.json"
Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    $hash = Get-FileHash $_.FullName -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content "$out/SHA256SUMS.txt"
