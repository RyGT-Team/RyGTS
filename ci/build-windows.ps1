param([Parameter(Mandatory=$true)][ValidateSet('Launcher','Renderer')][string]$Target, [ValidateSet('Release','Diagnostics')][string]$Flavor='Release')
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
    Invoke-Checked 'python' @('ci/runtime/apply.py', $source)
    if ($Flavor -eq 'Diagnostics') { Invoke-Checked 'python' @('ci/diagnostics/apply.py', $source) }
    $configuration = if ($Flavor -eq 'Diagnostics') { 'Debug' } else { 'Release' }
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
    # Keep Debug diagnostics and symbols with optimization and the redistributable CRT.
    Invoke-Checked 'cmake' @('-S',$source,'-B',"$source/Build/ci",'-G','Ninja',"-DCMAKE_BUILD_TYPE=$configuration",'-DCMAKE_C_COMPILER=clang-cl','-DCMAKE_CXX_COMPILER=clang-cl','-DCMAKE_LINKER=lld-link','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL','-DCMAKE_C_FLAGS_DEBUG=/O2 /Ob2 /Zi','-DCMAKE_CXX_FLAGS_DEBUG=/O2 /Ob2 /Zi','-DHAVE_ENDIAN_H=OFF','-DSHADGT_RYTY_GPU=ON','-DENABLE_DISCORD_RPC=ON')
    if (!(Select-String -LiteralPath "$source/Build/ci/CMakeCache.txt" -Pattern '^ENABLE_DISCORD_RPC:BOOL=ON$' -Quiet)) { throw 'Discord RPC was not enabled in renderer configuration' }
    Invoke-Checked 'cmake' @('--build',"$source/Build/ci",'--target','ryty_gpu','--parallel','2')
    $binary = Get-ChildItem "$source/Build/ci" -Recurse -Filter libSceVideoOut.prx
    if (@($binary).Count -ne 1) { throw 'Expected exactly one renderer output' }
    Copy-Item $binary.FullName $out
    Copy-Item "$source/RUNTIME-SOURCE.json" "$out/RUNTIME-SOURCE.json"
    $symbols = @(Get-ChildItem "$source/Build/ci" -Recurse -Filter 'libSceVideoOut.pdb')
    if ($Flavor -eq 'Diagnostics' -and $symbols.Count -ne 1) { throw 'Exactly one matching renderer PDB required' }
    $symbols | Copy-Item -Destination $out
    if ($Flavor -eq 'Diagnostics') {
        Copy-Item "$source/DIAGNOSTICS-SOURCE.json" "$out/DIAGNOSTICS-SOURCE.json"
        Invoke-Checked 'python' @('ci/diagnostics/write-capture-layout.py', $out, "$out/frame-capture-layouts.json", "$llvm/bin")
    }
    Copy-Item "$source/CI-SOURCE.txt" $out
    Invoke-Checked 'git' @('-C',$source,'submodule','status','--recursive')
}
if ($Flavor -eq 'Diagnostics') {
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
