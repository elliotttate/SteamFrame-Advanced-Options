param([switch]$SkipBuild, [ValidatePattern('^(SteamFrame-Advanced-Options|SteamLinkVRSettings)-[A-Za-z0-9-]+$')][string]$PortableFolder = 'SteamFrame-Advanced-Options-win-x64')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $taskMsbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $taskMsbuild) { throw 'Visual Studio MSBuild is required.' }
    $artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
    $codecSource = Join-Path $artifactRoot 'CodecLab'
    if (-not (Test-Path -LiteralPath $codecSource -PathType Container)) {
        throw 'The supervised CodecLab runtime must be staged in artifacts\CodecLab before creating a release package.'
    }
    function Assert-ArtifactPath([string]$TaskPath) {
        $taskResolvedPath = [System.IO.Path]::GetFullPath($TaskPath)
        if (-not $taskResolvedPath.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Package operation would leave the artifact directory: $taskResolvedPath"
        }
        if ((Test-Path -LiteralPath $taskResolvedPath) -and ((Get-Item -LiteralPath $taskResolvedPath -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Package directories must not be reparse points: $taskResolvedPath"
        }
    }
    $portableRoot = Join-Path $artifactRoot $PortableFolder
    function Assert-PortableIdle {
        $portablePrefix = $portableRoot + [IO.Path]::DirectorySeparatorChar
        $codecStatusPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SteamLinkVRSettings\CodecSession\status.json'
        if (Test-Path -LiteralPath $codecStatusPath -PathType Leaf) {
            try {
                $codecStatus = Get-Content -LiteralPath $codecStatusPath -Raw | ConvertFrom-Json
                $codecOwner = Get-Process -Id $codecStatus.owner_pid -ErrorAction SilentlyContinue
                if ($codecOwner) {
                    $codecOwnerEpoch = [DateTimeOffset]::new($codecOwner.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
                    if ([Math]::Abs($codecOwnerEpoch - [double]$codecStatus.owner_created_epoch) -le 2 -and $codecOwner.Path -and $codecOwner.Path.StartsWith($portablePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'End the supervised codec session before packaging; its recovery files must remain available until it exits.'
                    }
                }
            } catch [System.ArgumentException] { } # A process that has just exited holds no files.
        }
        $portablePrefix = $portableRoot + [IO.Path]::DirectorySeparatorChar
        foreach ($packageProcess in Get-Process -Name SteamLinkVRSettings,python,vrserver -ErrorAction SilentlyContinue) {
            if ($packageProcess.Path -and $packageProcess.Path.StartsWith($portablePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Close the portable app and codec runtime before packaging.'
            }
            if ($packageProcess.ProcessName -eq 'vrserver') {
                foreach ($loadedModule in $packageProcess.Modules) {
                    if ($loadedModule.FileName.StartsWith($portablePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'Restart SteamVR with stock HEVC before packaging; a portable native helper is still loaded.'
                    }
                }
            }
        }
    }
    Assert-PortableIdle
    $stageRoot = Join-Path $artifactRoot ('.publish-' + [guid]::NewGuid().ToString('N'))
    $packageBuildRoot = Join-Path $artifactRoot ('.package-build-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    & $taskMsbuild 'SteamLinkVRSettings\SteamLinkVRSettings.csproj' -restore -t:Publish -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false "-p:OutputPath=$packageBuildRoot\" "-p:PublishDir=$stageRoot\" -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Portable publish failed; output preserved at $stageRoot" }
    # WinUI's loose compiled XAML and app resource index are generated into the
    # build output, but this unpackaged Publish target does not copy them.
    $appResourceIndex = Join-Path $packageBuildRoot 'SteamLinkVRSettings.pri'
    if (-not (Test-Path -LiteralPath $appResourceIndex)) { throw 'WinUI app resource index was not built.' }
    Copy-Item -LiteralPath $appResourceIndex -Destination $stageRoot
    foreach ($compiledXaml in Get-ChildItem -LiteralPath $packageBuildRoot -Recurse -File -Filter '*.xbf') {
        $relativeXaml = [IO.Path]::GetRelativePath($packageBuildRoot, $compiledXaml.FullName)
        $publishedXaml = [IO.Path]::GetFullPath((Join-Path $stageRoot $relativeXaml))
        Assert-ArtifactPath $publishedXaml
        if (-not $publishedXaml.StartsWith($stageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Compiled XAML destination leaves the publish directory.'
        }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($publishedXaml)) -Force | Out-Null
        Copy-Item -LiteralPath $compiledXaml.FullName -Destination $publishedXaml
    }
    foreach ($requiredXaml in @('App.xbf', 'MainWindow.xbf', 'Views\FramePage.xbf')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageRoot $requiredXaml))) { throw "Missing compiled UI resource: $requiredXaml" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stageRoot 'Native\SteamFrameBenchmark.exe') -PathType Leaf)) {
        throw 'The wireless benchmark scene is missing. Build the native targets before packaging.'
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $stageRoot
    # Ship only the supervised codec runtime and exact guarded trial assets.
    # Development fixtures, local configs and backend build products are excluded.
    if (Test-Path -LiteralPath $codecSource) {
        $codecDestination = Join-Path $stageRoot 'CodecLab'
        Assert-ArtifactPath $codecSource
        Assert-ArtifactPath $codecDestination
        New-Item -ItemType Directory -Path $codecDestination -Force | Out-Null
        foreach ($codecScript in @('codec_supervisor.py', 'root_session.py', 'frame_rate_remote.py', 'frame_cef_stdio.py', 'start_stream.js', 'stop_stream.js', 'README.txt')) {
            $codecFile = Join-Path $codecSource $codecScript
            if (-not (Test-Path -LiteralPath $codecFile -PathType Leaf)) { throw "Missing codec runtime file: $codecScript" }
            Copy-Item -LiteralPath $codecFile -Destination $codecDestination
        }
        foreach ($codecFolder in @('runtime', 'Assets')) {
            $codecDirectory = Join-Path $codecSource $codecFolder
            if (-not (Test-Path -LiteralPath $codecDirectory -PathType Container)) { throw "Missing codec runtime directory: $codecFolder" }
            Assert-ArtifactPath $codecDirectory
            Copy-Item -LiteralPath $codecDirectory -Destination $codecDestination -Recurse
        }
        $codecBackend = Join-Path $codecDestination 'Backend'
        New-Item -ItemType Directory -Path $codecBackend -Force | Out-Null
        foreach ($codecBinary in @('SteamFrameAV1Backend.dll', 'trial_control.exe')) {
            $codecBinarySource = Join-Path $codecSource "Backend\$codecBinary"
            if (-not (Test-Path -LiteralPath $codecBinarySource -PathType Leaf)) { throw "Missing codec backend: $codecBinary" }
            Copy-Item -LiteralPath $codecBinarySource -Destination $codecBackend
        }
    }
    # Ship the release guides, keeping development investigations out of the package.
    $guideRoot = Join-Path $stageRoot 'docs'
    New-Item -ItemType Directory -Path $guideRoot -Force | Out-Null
    foreach ($guide in @('auto-tune.md', 'advanced-controls.md', 'building.md', 'codec-modes.md', 'connection-setup.md', 'driver-patcher.md', 'steamvr-25998080.md', 'hevc-high-quality.md', 'live-dashboard.md', 'settings-editor.md', 'stream-controls.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\$guide") -Destination $guideRoot
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\images') -Destination (Join-Path $guideRoot 'images') -Recurse
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination $stageRoot }
    $licenseRoot = Join-Path $stageRoot 'Licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\vendor\minhook\LICENSE.txt') -Destination (Join-Path $licenseRoot 'MinHook-HDE.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\vendor\NVIDIA-LICENSE.txt') -Destination $licenseRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\vendor\NVIDIA-LICENSE12.txt') -Destination $licenseRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\vendor\OpenVR-LICENSE.txt') -Destination $licenseRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $licenseRoot 'SteamLinkVRSettings-MIT.txt')
    if (Test-Path -LiteralPath $portableRoot) {
        $previousRoot = Join-Path $artifactRoot ('.previous-' + [guid]::NewGuid().ToString('N'))
        Assert-ArtifactPath $portableRoot
        Assert-ArtifactPath $previousRoot
        Assert-PortableIdle
        # Directory.Move performs one same-volume rename. PowerShell Move-Item
        # can move children before encountering a locked runtime subdirectory.
        [IO.Directory]::Move($portableRoot, $previousRoot)
    }
    Assert-ArtifactPath $stageRoot
    Assert-ArtifactPath $portableRoot
    [IO.Directory]::Move($stageRoot, $portableRoot)
    $zip = Join-Path $artifactRoot ($PortableFolder + '.zip')
    Compress-Archive -LiteralPath $portableRoot -DestinationPath $zip -Force
    Write-Output "Portable app: $portableRoot"
    Write-Output "ZIP: $zip"
} finally { Pop-Location }
