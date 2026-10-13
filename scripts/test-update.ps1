[CmdletBinding()]
param([string]$Models)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Models) { $Models = Join-Path $repoRoot 'models' }
$checkRoot = Join-Path $repoRoot ('.artifacts/update-check/' + [Guid]::NewGuid().ToString('N'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src/Meetu/Meetu.csproj') -Raw)).Project.PropertyGroup.Version
$name = "meetus-privatus-$version-win-x64"
$staging = Join-Path $checkRoot 'package'
$publication = Join-Path $staging $name
$install = Join-Path $checkRoot 'installed app'
$zipPath = Join-Path $checkRoot "$name.zip"
$checksumPath = "$zipPath.sha256"
& (Join-Path $PSScriptRoot 'build.ps1') -Publish -SkipTests -PublishDirectory $publication
New-Item -ItemType Directory -Path (Join-Path $publication 'models') | Out-Null
$assets = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'model-assets.psd1')
foreach ($key in @('base.en-q5_1', 'silero-v5.1.2')) {
    $asset = $assets[$key]
    $source = Join-Path $Models $asset.Name
    if ((Get-Item -LiteralPath $source).Length -ne $asset.Size -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Hash) { throw 'Test model integrity failed.' }
    Copy-Item -LiteralPath $source -Destination (Join-Path $publication 'models')
}

function Write-Manifest([string]$Directory, [string]$Version) {
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Where-Object { $_.Name -notin @('release.json', '.update.lock') } |
        ForEach-Object { @{ Path = $_.FullName.Substring($Directory.Length + 1).Replace('\', '/'); Size = $_.Length;
            Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    @{ Version = $Version; Commit = (git -C $repoRoot rev-parse HEAD); Runtime = 'win-x64'; Files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'release.json') -Encoding utf8
}

function Invoke-Update([int]$ExpectedExit, [string]$ArchivePath = $zipPath, [string]$ChecksumFile = $checksumPath) {
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $install 'Update.ps1') -Archive $ArchivePath -Checksum $ChecksumFile
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Updater exit was $LASTEXITCODE; expected $ExpectedExit." }
}

function Assert-Preserved {
    if ((Get-Content -LiteralPath (Join-Path $install 'transcripts/synthetic-session/notes.txt') -Raw) -ne 'synthetic preserved text' -or
        (Get-Content -LiteralPath (Join-Path $install 'custom-user-file.txt') -Raw) -ne 'synthetic user file') { throw 'The updater changed user files.' }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Copy-Item -LiteralPath $publication -Destination $install -Recurse
[IO.File]::WriteAllText((Join-Path $install 'README.md'), 'synthetic previous release')
[IO.File]::WriteAllText((Join-Path $install 'obsolete-package-file.txt'), 'synthetic obsolete program asset')
Write-Manifest $install '0.0.0-updater-test'
New-Item -ItemType Directory -Path (Join-Path $install 'transcripts/synthetic-session') | Out-Null
[IO.File]::WriteAllText((Join-Path $install 'transcripts/synthetic-session/notes.txt'), 'synthetic preserved text')
[IO.File]::WriteAllText((Join-Path $install 'custom-user-file.txt'), 'synthetic user file')
[IO.File]::WriteAllText((Join-Path $publication 'new-component.txt'), 'synthetic new program asset')
Write-Manifest $publication $version
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath)
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksumPath, "$hash  $name.zip`n")

# No update or startup can proceed while recording/update holds the installation lease.
$lease = [IO.File]::Open((Join-Path $install '.update.lock'), 'OpenOrCreate', 'ReadWrite', 'ReadWrite')
try { Invoke-Update 1 } finally { $lease.Dispose() }
Assert-Preserved

$badChecksum = Join-Path $checkRoot 'bad.sha256'
[IO.File]::WriteAllText($badChecksum, (('0' * 64) + "  $name.zip`n"))
Invoke-Update 1 -ChecksumFile $badChecksum
Assert-Preserved

# A checksummed ZIP still cannot escape its expected top-level directory.
$unsafeRoot = Join-Path $checkRoot 'unsafe'
New-Item -ItemType Directory -Path $unsafeRoot | Out-Null
$unsafeZip = Join-Path $unsafeRoot "$name.zip"
$zip = [IO.Compression.ZipFile]::Open($unsafeZip, 'Create')
try { $null = $zip.CreateEntry("$name/../escaped.txt") } finally { $zip.Dispose() }
$unsafeChecksum = "$unsafeZip.sha256"
[IO.File]::WriteAllText($unsafeChecksum, ((Get-FileHash -LiteralPath $unsafeZip -Algorithm SHA256).Hash + "  $name.zip`n"))
Invoke-Update 1 -ArchivePath $unsafeZip -ChecksumFile $unsafeChecksum
if (Test-Path -LiteralPath (Join-Path $install 'escaped.txt')) { throw 'Archive traversal created a file.' }

# A newly shipped filename must not replace an unrelated user file.
$collision = Join-Path $install 'new-component.txt'
[IO.File]::WriteAllText($collision, 'synthetic user collision')
Invoke-Update 1
if ([IO.File]::ReadAllText($collision) -ne 'synthetic user collision') { throw 'A colliding user file was replaced.' }
[IO.File]::Delete($collision)

# Permit checksum reads but deny replacement: exercise a real failed replacement and journal recovery.
$lockedReadme = [IO.File]::Open((Join-Path $install 'README.md'), 'Open', 'Read', 'Read')
try {
    Invoke-Update 1
    if (-not (Test-Path -LiteralPath (Join-Path $install '.update-pending/transaction.json'))) { throw 'Failed rollback did not preserve its recovery journal.' }
    $stateRoot = Join-Path $checkRoot 'blocked-start'
    & (Join-Path $install 'meetus.exe') start --state-dir $stateRoot
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath (Join-Path $stateRoot 'audio'))) { throw 'Interrupted update did not block capture startup.' }
} finally { $lockedReadme.Dispose() }
Invoke-Update 0
Assert-Preserved
if (Test-Path -LiteralPath (Join-Path $install '.update-pending')) { throw 'Completed update left a transaction.' }
if (Test-Path -LiteralPath (Join-Path $install 'obsolete-package-file.txt')) { throw 'An obsolete owned program file survived the update.' }
$manifest = Get-Content -LiteralPath (Join-Path $install 'release.json') -Raw | ConvertFrom-Json
if ($manifest.Version -ne $version) { throw 'Updated release version mismatch.' }
foreach ($file in $manifest.Files) {
    if ((Get-FileHash -LiteralPath (Join-Path $install $file.Path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.Hash) { throw 'Updated program file mismatch.' }
}
# Successful replacement followed by interrupted cleanup has no usable rollback backup.
# The committed marker must validate the installed version and finish cleanup instead.
$pending = Join-Path $install '.update-pending'
New-Item -ItemType Directory -Path $pending | Out-Null
[IO.File]::WriteAllText((Join-Path $pending 'meetus-update-v1'), '1')
[IO.File]::WriteAllText((Join-Path $pending 'committed.json'), '{}')
Invoke-Update 0
Assert-Preserved
if (Test-Path -LiteralPath $pending) { throw 'Committed transaction cleanup did not finish.' }
Write-Output 'Updater checks passed: offline installation, locks, checksum, archive traversal, user-file collision, failed replacement/recovery, committed cleanup, obsolete assets, and transcript preservation. No audio was captured.'
Write-Output "Check directory: $checkRoot"
