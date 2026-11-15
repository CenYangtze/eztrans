#Requires -Version 7.0
<#
.SYNOPSIS
Build Windows x64/ARM64 packages, optional Paddle OCR component, and Windows installers.
.EXAMPLE
pwsh ./tools/publish.ps1 -Runtime win-x64 -Mode all -CreateZip -IncludePaddle
.EXAMPLE
pwsh ./tools/publish.ps1 -Runtime win-arm64 -Mode all -CreateZip -IncludePaddle -BuildInstaller -BuildVelopack
#>
[CmdletBinding()]
param(
    [ValidateSet('fdd', 'scd', 'all')]
    [string] $Mode = 'all',
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $CreateZip,
    [switch] $IncludePaddle,
    [switch] $BuildInstaller,
    [switch] $BuildVelopack,
    [string] $IsccPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/SnapTrans.App/SnapTrans.App.csproj'
$pluginProject = Join-Path $repoRoot 'src/SnapTrans.Ocr.Paddle/SnapTrans.Ocr.Paddle.csproj'
$outputRoot = if ($Runtime -eq 'win-x64') { Join-Path $repoRoot 'out' } else { Join-Path $repoRoot "out/$Runtime" }
$artifactsRoot = Join-Path $repoRoot "artifacts/publish/$Runtime"
$architecture = $Runtime.Substring(4)
[xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = [string] $buildProperties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { throw 'Directory.Build.props must contain a semantic Version.' }
$numericVersion = ($version -split '[-+]')[0]
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET SDK version pinned by global.json first.' }
if (($BuildInstaller -or $BuildVelopack) -and -not $IsWindows) { throw 'Inno Setup and Windows Velopack packaging require Windows. Omit those switches for cross-publishing.' }
if ($BuildVelopack -and $Mode -eq 'fdd') { throw 'Velopack requires the self-contained output; select -Mode scd or all.' }
if ($BuildInstaller) {
    if (-not $IsccPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $IsccPath = $command.Source }
        else { $IsccPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
    }
    if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw 'Install Inno Setup 6 or specify -IsccPath.' }
}

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}
function Remove-GeneratedSymbols([string] $Folder) {
    Get-ChildItem -LiteralPath $Folder -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.xml', '.md' } | Remove-Item -Force
}
function Copy-LegalNotices([string] $Folder) {
    $notice = Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md'
    $licenses = Join-Path $repoRoot 'licenses'
    if (-not (Test-Path -LiteralPath $notice -PathType Leaf) -or -not (Test-Path -LiteralPath $licenses -PathType Container)) {
        throw 'Third-party license notices must accompany binary distributions.'
    }
    Copy-Item -LiteralPath $notice -Destination $Folder -Force
    Copy-Item -LiteralPath $licenses -Destination $Folder -Recurse -Force
}
function Write-PaddlePackage {
    $destination = Join-Path $outputRoot 'paddle'
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Invoke-Dotnet @(
        'publish', $pluginProject, '--configuration', 'Release', '--runtime', $Runtime,
        '--self-contained', 'false', '--output', $destination,
        '--artifacts-path', (Join-Path $repoRoot "artifacts/paddle/$Runtime"),
        "-p:RuntimeIdentifiers=$Runtime",
        '-p:RestoreLockedMode=true', "-p:NuGetLockFilePath=packages.paddle.$Runtime.lock.json",
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false'
    )
    # Only executable dependencies belong in the plugin ZIP. Models are fetched
    # separately using their pinned catalog, never bundled with the base application.
    Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {
        $_.Name -notmatch '(?i)(\.dll|\.deps\.json|\.runtimeconfig\.json)$'
    } | Remove-Item -Force
    Copy-LegalNotices $destination
    $entryAssembly = Join-Path $destination 'SnapTrans.Ocr.Paddle.dll'
    if (-not (Test-Path -LiteralPath $entryAssembly -PathType Leaf)) { throw 'The optional Paddle component assembly is missing.' }
    $files = [System.Collections.Generic.List[object]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($destination, $file.FullName).Replace('\', '/')
        $files.Add([ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    if ($files.Count -eq 0) { throw 'The optional Paddle package is empty.' }
    $componentManifest = [ordered]@{ runtime = $Runtime; version = $version; files = $files.ToArray() }
    ConvertTo-Json -InputObject $componentManifest -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'plugin-manifest.json') -Encoding utf8NoBOM
    $archive = Join-Path $outputRoot "SnapTrans-$version-$Runtime-paddle.zip"
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -Force
    Write-Host "Created optional OCR component: $archive ($($files.Count) payload files; no models)."
}

$targets = if ($Mode -eq 'all') { @('fdd', 'scd') } else { @($Mode) }
$manifest = [System.Collections.Generic.List[object]]::new()
Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $manifestPath = Join-Path $outputRoot "publish-manifest.$Runtime.json"
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
    foreach ($target in $targets) {
        $destination = Join-Path $outputRoot $target
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        $selfContained = if ($target -eq 'scd') { 'true' } else { 'false' }
        Invoke-Dotnet @(
            'publish', $project, '--configuration', 'Release', '--runtime', $Runtime,
            '--self-contained', $selfContained, '--output', $destination, '--artifacts-path', $artifactsRoot,
            "-p:RuntimeIdentifiers=$Runtime",
            '-p:RestoreLockedMode=true', "-p:NuGetLockFilePath=packages.publish.$Runtime.lock.json", '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
            "-p:EnableCompressionInSingleFile=$selfContained"
        )
        Remove-GeneratedSymbols $destination
        Copy-LegalNotices $destination
        $executable = Join-Path $destination 'SnapTrans.exe'
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Missing release executable: $executable" }
        $size = (Get-ChildItem -LiteralPath $destination -File -Recurse | Measure-Object -Property Length -Sum).Sum
        $manifest.Add([ordered]@{
            version = $version; runtime = $Runtime; mode = $target
            executable = "${target}/SnapTrans.exe"
            bytes = $size; sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
        })
        if ($CreateZip) {
            $archive = Join-Path $outputRoot "SnapTrans-$version-$Runtime-$target.zip"
            Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -Force
            Write-Host "Created $archive"
        }
        if ($BuildInstaller) {
            $selfContainedFlag = if ($target -eq 'scd') { '1' } else { '0' }
            & $IsccPath "/DAppVersion=$version" "/DAppVersionNumeric=$numericVersion" "/DSelfContained=$selfContainedFlag" "/DArchitecture=$architecture" "/DPublishRoot=$outputRoot" (Join-Path $PSScriptRoot 'installer.iss')
            if ($LASTEXITCODE -ne 0) { throw "Inno Setup ($Runtime/$target) failed with exit code $LASTEXITCODE." }
        }
        Write-Host "Published $Runtime/$target ($([math]::Round($size / 1MB, 2)) MiB) to $destination"
    }
    if ($IncludePaddle) { Write-PaddlePackage }
    if ($BuildVelopack) {
        Invoke-Dotnet @('tool', 'restore')
        $velopackDirectory = Join-Path $outputRoot 'velopack'
        if (Test-Path -LiteralPath $velopackDirectory) { Remove-Item -LiteralPath $velopackDirectory -Recurse -Force }
        $arguments = @(
            'tool', 'run', 'vpk', '--', '--yes', '--skip-updates', 'pack',
            '--packId', 'SnapTrans', '--packVersion', $version, '--packDir', (Join-Path $outputRoot 'scd'),
            '--mainExe', 'SnapTrans.exe', '--channel', $Runtime, '--runtime', $Runtime,
            '--packAuthors', 'SnapTrans contributors', '--packTitle', '屏译 SnapTrans',
            '--icon', (Join-Path $repoRoot 'src/SnapTrans.App/Resources/SnapTrans.ico'),
            '--outputDir', $velopackDirectory
        )
        $notes = Join-Path $repoRoot ".github/release-notes/v$version.md"
        if (Test-Path -LiteralPath $notes) { $arguments += @('--releaseNotes', $notes) }
        Invoke-Dotnet $arguments
        if (-not (Get-ChildItem -LiteralPath $velopackDirectory -File -Filter '*.nupkg')) { throw 'Velopack produced no update package.' }
    }
    ConvertTo-Json -InputObject $manifest.ToArray() -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
}
finally { Pop-Location }
