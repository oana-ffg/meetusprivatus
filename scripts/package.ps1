[CmdletBinding()]
param(
    # The default comes from Directory.Build.props. The update test overrides it to build two versions.
    [string]$Version,
    [string]$Models,
    [string]$OutputDirectory,
    [string]$ReleaseNotes,
    [switch]$SkipTests
)

# Builds the portable ZIP and update feed locally. Uploads nothing; see release.ps1.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Models) { $Models = Join-Path $repoRoot 'models' }
$props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
if (-not $Version) { $Version = [string]$props.Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'A valid release version is required.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ".artifacts/releases/$Version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

# A fresh directory: never package prior meetings, settings, or stale publication files.
$publication = Join-Path $repoRoot (".artifacts/release-build/" + [Guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'build.ps1') -Publish -SkipTests:$SkipTests -PublishDirectory $publication -Version $Version

$manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'model-assets.psd1')
New-Item -ItemType Directory -Path (Join-Path $publication 'models') | Out-Null
foreach ($asset in @($manifest['base.en-q5_1'], $manifest['silero-v5.1.2'])) {
    $copied = Join-Path $publication "models/$($asset.Name)"
    Copy-Item -LiteralPath (Join-Path $Models $asset.Name) -Destination $copied
    if ((Get-Item -LiteralPath $copied).Length -ne $asset.Size -or
        (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Hash) {
        throw "Release model integrity failed: $($asset.Name)."
    }
}

$velopackVersion = [string]$props.Project.PropertyGroup.VelopackVersion
$toolPath = Join-Path $repoRoot ".artifacts/tools/velopack-$velopackVersion"
if (-not (Test-Path -LiteralPath (Join-Path $toolPath 'vpk.exe'))) {
    dotnet tool install vpk --version $velopackVersion --tool-path $toolPath --configfile (Join-Path $repoRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Velopack tool installation failed.' }
}
$packArguments = @('pack', '--packId', 'MeetusPrivatus', '--packTitle', 'Meetus Privatus', '--packVersion', $Version,
    '--packDir', $publication, '--mainExe', 'MeetusPrivatus.exe', '--runtime', 'win-x64', '--channel', 'win-x64',
    '--icon', (Join-Path $repoRoot 'src/Meetu.App/Assets/app.ico'), '--noInst', '--outputDir', $OutputDirectory)
if ($ReleaseNotes) { $packArguments += @('--releaseNotes', [IO.Path]::GetFullPath($ReleaseNotes)) }
& (Join-Path $toolPath 'vpk.exe') @packArguments
if ($LASTEXITCODE -ne 0) { throw 'Update packaging failed.' }
Remove-Item -LiteralPath $publication -Recurse -Force
Write-Output "Portable ZIP and update feed: $OutputDirectory"
