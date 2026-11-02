param([string]$VisualStudioRoot, [string]$WindowsSdkRoot, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$nativeRoot = $PSScriptRoot
if (-not $VisualStudioRoot) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools with Desktop development with C++, or pass -VisualStudioRoot.' }
    $VisualStudioRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $VisualStudioRoot) { throw 'No Visual Studio C++ toolchain found.' }
$toolset = Get-ChildItem -LiteralPath (Join-Path $VisualStudioRoot 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $toolset) { throw 'No MSVC toolset found.' }
if (-not $WindowsSdkRoot) {
    $registeredSdk = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue).KitsRoot10
    # A stale registry view can point at Program Files while the SDK is in (x86).
    $WindowsSdkRoot = @($registeredSdk, (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10')) |
        Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'Include')) } | Select-Object -First 1
    if (-not $WindowsSdkRoot) { throw 'No Windows SDK found. Install the Windows SDK or pass -WindowsSdkRoot.' }
}
$sdk = Get-ChildItem -LiteralPath (Join-Path $WindowsSdkRoot 'Include') -Directory |
    Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'ucrt\stdio.h')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'No complete Windows SDK found.' }
$compilerDirectory = Join-Path $toolset.FullName 'bin\Hostx64\x64'
$compiler = Join-Path $compilerDirectory 'cl.exe'
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
$ninjaCommand = Get-Command ninja -ErrorAction SilentlyContinue
$cmake = if ($cmakeCommand) { $cmakeCommand.Source } else { Join-Path $VisualStudioRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' }
$ninja = if ($ninjaCommand) { $ninjaCommand.Source } else { Join-Path $VisualStudioRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe' }
foreach ($executable in @($compiler, $cmake, $ninja)) { if (-not (Test-Path -LiteralPath $executable)) { throw "Required build tool missing: $executable" } }
$previousPath = $env:PATH; $previousInclude = $env:INCLUDE; $previousLib = $env:LIB
try {
    $sdkBinaries = Join-Path $WindowsSdkRoot "bin\$($sdk.Name)\x64"
    $env:PATH = "$compilerDirectory;$sdkBinaries;$(Split-Path $ninja);$previousPath"
    $env:INCLUDE = "$($toolset.FullName)\include;$($sdk.FullName)\ucrt;$($sdk.FullName)\shared;$($sdk.FullName)\um;$($sdk.FullName)\winrt"
    $env:LIB = "$($toolset.FullName)\lib\x64;$WindowsSdkRoot\Lib\$($sdk.Name)\ucrt\x64;$WindowsSdkRoot\Lib\$($sdk.Name)\um\x64"
    $cmakeCompiler = $compiler.Replace('\', '/')
    $cmakeSdkBinaries = $sdkBinaries.Replace('\', '/')
    & $cmake -S $nativeRoot -B (Join-Path $nativeRoot 'build') -G Ninja "-DCMAKE_MAKE_PROGRAM=$($ninja.Replace('\', '/'))" "-DCMAKE_C_COMPILER=$cmakeCompiler" "-DCMAKE_CXX_COMPILER=$cmakeCompiler" "-DCMAKE_RC_COMPILER=$cmakeSdkBinaries/rc.exe" "-DCMAKE_MT=$cmakeSdkBinaries/mt.exe" -DCMAKE_BUILD_TYPE=Release
    if ($LASTEXITCODE -ne 0) { throw 'Native configure failed.' }
    & $cmake --build (Join-Path $nativeRoot 'build')
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    if (-not $SkipTests) {
        & (Join-Path (Split-Path $cmake) 'ctest.exe') --test-dir (Join-Path $nativeRoot 'build') --output-on-failure
        if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
    }
} finally { $env:PATH = $previousPath; $env:INCLUDE = $previousInclude; $env:LIB = $previousLib }
