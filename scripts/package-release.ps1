[CmdletBinding()]
param([string]$Models)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Models) { $Models = Join-Path $repoRoot 'models' }
$Models = [IO.Path]::GetFullPath($Models)
Push-Location $repoRoot
try {
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Commit the source before packaging a release.' }
    $changes = git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Release packaging requires a clean Git working tree.' }
    $project = [xml](Get-Content -LiteralPath src/Meetu/Meetu.csproj -Raw)
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Set a valid project release version first.' }
    $name = "meetus-privatus-$version-win-x64"
    $releaseRoot = Join-Path $repoRoot '.artifacts/releases'
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    $zipPath = Join-Path $releaseRoot "$name.zip"
    $checksumPath = Join-Path $releaseRoot "$name.zip.sha256"
    if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $checksumPath)) {
        throw 'Release assets already exist. Move them aside before repackaging; they will not be overwritten.'
    }
    # Fresh build directory: never package prior recordings/configuration or stale publication files.
    $staging = Join-Path $repoRoot ('.artifacts/release-build/' + [Guid]::NewGuid().ToString('N'))
    $publication = Join-Path $staging $name
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish -PublishDirectory $publication
    $manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'model-assets.psd1')
    $modelAssets = @($manifest['base.en-q5_1'], $manifest['silero-v5.1.2'])
    New-Item -ItemType Directory -Path (Join-Path $publication 'models') | Out-Null
    foreach ($asset in $modelAssets) {
        $source = Join-Path $Models $asset.Name
        $copied = Join-Path (Join-Path $publication 'models') $asset.Name
        Copy-Item -LiteralPath $source -Destination $copied
        if ((Get-Item -LiteralPath $copied).Length -ne $asset.Size -or
            (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Hash) {
            throw "Release model integrity failed: $($asset.Name)."
        }
    }
    & (Join-Path $publication 'meetus.exe') help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Published executable smoke check failed.' }
    & (Join-Path $PSScriptRoot 'test-console.ps1') -PublishDirectory $publication -Models (Join-Path $publication 'models')
    # Startup checking can create the adjacent output directory. Ship no output, even an empty folder.
    # Nonrecursive deletion deliberately fails if the check unexpectedly left any files.
    $transcripts = Join-Path $publication 'transcripts'
    if ([IO.Directory]::Exists($transcripts)) {
        if (([IO.File]::GetAttributes($transcripts) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unexpected linked release output directory.' }
        [IO.Directory]::Delete($transcripts)
    }
    # A running app creates this lease, but it is installation state rather than a release asset.
    $lease = Join-Path $publication '.update.lock'
    if (Test-Path -LiteralPath $lease) { Remove-Item -LiteralPath $lease }
    $files = @(Get-ChildItem -LiteralPath $publication -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ Path = $_.FullName.Substring($publication.Length + 1).Replace('\', '/'); Size = $_.Length;
           Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    @{
        Version = $version
        Commit = $commit
        Runtime = 'win-x64'
        Models = $modelAssets
        Files = $files
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $publication 'release.json') -Encoding utf8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksumPath, "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Release archive: $zipPath"
    Write-Output "SHA-256: $hash"
} finally { Pop-Location }
