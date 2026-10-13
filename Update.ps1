[CmdletBinding()]
param(
    # Offline updates use the release ZIP and its separately supplied checksum file.
    [string]$Archive,
    [string]$Checksum,
    [switch]$Pause
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$installRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$pending = Join-Path $installRoot '.update-pending'
$download = $null
$updateLock = $null
$exitCode = 0

function Assert-LocalPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\')) { throw 'Use a local drive path without links or junctions.' }
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    if ([IO.DriveInfo]::new($root).DriveType -notin @('Fixed', 'Removable')) { throw 'Updates require a local disk.' }
    $current = $root
    foreach ($part in $full.Substring($root.Length).Split([IO.Path]::DirectorySeparatorChar)) {
        if (-not $part) { continue }
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Updates cannot follow links, junctions, or cloud placeholders.' }
        } else { break }
    }
}

function Get-PackagePath([string]$Root, [string]$Relative) {
    if ($Relative -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or
        @($Relative.Split('/') | Where-Object { $_.EndsWith('.') -or $_ -match '^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -or
        $Relative.Split('/')[0] -match '^(?:transcripts|recordings|audio|config\.json|status\.json|\.update.*)$') {
        throw "Unsafe package path: $Relative"
    }
    $path = Join-Path $Root $Relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    Assert-LocalPath $path
    return $path
}

function New-PrivateDirectory([string]$Path) {
    Assert-LocalPath $Path
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    [IO.DirectoryInfo]::new($Path).Create($security)
}

function Read-Manifest([string]$Path) {
    Assert-LocalPath $Path
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($manifest.Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or $manifest.Runtime -ne 'win-x64' -or
        $manifest.Commit -notmatch '^[0-9a-f]{40}$' -or @($manifest.Files).Count -eq 0 -or @($manifest.Files).Count -gt 10000) {
        throw 'The release manifest is invalid or predates the supported updater format.'
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$total = 0
    foreach ($file in $manifest.Files) {
        Get-PackagePath $installRoot $file.Path | Out-Null
        if (-not $names.Add($file.Path) -or $file.Path -eq 'release.json' -or $file.Hash -notmatch '^[0-9a-f]{64}$' -or
            $file.Size -lt 0 -or $file.Size -gt 1GB) { throw 'The release file manifest is invalid.' }
        $total += $file.Size
    }
    if ($total -gt 1GB) { throw 'The release exceeds the updater size limit.' }
    foreach ($required in @('meetus.exe', 'Start.cmd', 'Update.cmd', 'Update.ps1', 'models/ggml-base.en-q5_1.bin', 'models/ggml-silero-v5.1.2.bin')) {
        if (-not $names.Contains($required)) { throw "Release is missing $required." }
    }
    return $manifest
}

function Assert-FileIntegrity([string]$Root, $File) {
    $path = Get-PackagePath $Root $File.Path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $File.Size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $File.Hash) {
        throw "File integrity check failed: $($File.Path). Restore the original release before updating."
    }
}

function Install-File([string]$Source, [string]$Target) {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Target) { [IO.File]::Replace($Source, $Target, [NullString]::Value) }
            else { [IO.File]::Move($Source, $Target) }
            return
        } catch {
            $cause = $_.Exception.GetBaseException()
            # Windows guarantees both names survive ERROR_UNABLE_TO_REMOVE_REPLACED (1175).
            # Brief file contention can clear after process exit; persistent locks still fail/roll back.
            if (($cause.HResult -band 0xFFFF) -in @(32, 33, 1175) -and $attempt -lt 9) {
                Start-Sleep -Milliseconds 200
                continue
            }
            throw ("Could not replace {0} (HRESULT 0x{1:X8}): {2}" -f [IO.Path]::GetFileName($Target), $cause.HResult, $cause.Message)
        }
    }
}

function Remove-Workspace([string]$Path) {
    # Only our direct-child staging/transaction directories are ever removed.
    $resolved = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $installRoot -or
        [IO.Path]::GetFileName($resolved) -notmatch '^\.update-(?:pending|download-[0-9a-f]{32})$') { throw 'Invalid updater workspace.' }
    Assert-LocalPath $resolved
    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'meetus-update-v1') -PathType Leaf)) { throw 'Unrecognized updater workspace; preserved for inspection.' }
    # Inspect recursively without following a reparse point.
    $directories = [Collections.Generic.Queue[string]]::new()
    $directories.Enqueue($resolved)
    while ($directories.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $directories.Dequeue() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked updater workspace preserved for inspection.' }
            if ($item.PSIsContainer) { $directories.Enqueue($item.FullName) }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Restore-Transaction {
    Assert-LocalPath $pending
    if (Test-Path -LiteralPath (Join-Path $pending 'committed.json')) {
        $completed = Read-Manifest (Join-Path $installRoot 'release.json')
        foreach ($file in $completed.Files) { Assert-FileIntegrity $installRoot $file }
        Remove-Workspace $pending
        Write-Host 'Finished cleanup of the completed update.'
        return
    }
    $journalPath = Join-Path $pending 'transaction.json'
    Assert-LocalPath $journalPath
    $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    if ($journal.Format -ne 1 -or @($journal.Files).Count -gt 10001) { throw 'Unrecognized update transaction; files preserved.' }
    foreach ($file in $journal.Files) {
        $target = Get-PackagePath $installRoot $file.Path
        if ($file.HadOriginal) {
            $backupRoot = Join-Path $pending 'backup'
            Assert-FileIntegrity $backupRoot $file
            $backup = Get-PackagePath $backupRoot $file.Path
            $replacement = Get-PackagePath (Join-Path $pending 'restore') $file.Path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($replacement)) | Out-Null
            [IO.File]::Copy($backup, $replacement, $true)
            Install-File $replacement $target
        } elseif (Test-Path -LiteralPath $target) { [IO.File]::Delete($target) }
    }
    Remove-Workspace $pending
    Write-Host 'Restored the previous version after an interrupted or failed update.'
}

try {
    Assert-LocalPath $installRoot
    Assert-LocalPath (Join-Path $installRoot '.update.lock')
    try { $updateLock = [IO.File]::Open((Join-Path $installRoot '.update.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'A recording or another update is running. Stop recording, let it finish, then double-click Update.cmd again.' }
    # Earlier versions did not take the installation lease; never replace a running executable.
    if (@(Get-Process -Name meetus -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $installRoot 'meetus.exe') }).Count) {
        throw 'Stop recording and let transcription finish before updating.'
    }
    if (Test-Path -LiteralPath $pending) { Restore-Transaction }
    $installed = Read-Manifest (Join-Path $installRoot 'release.json')
    if (($Archive -and -not $Checksum) -or ($Checksum -and -not $Archive)) { throw 'Offline updates require both -Archive and -Checksum.' }
    $download = Join-Path $installRoot ('.update-download-' + [Guid]::NewGuid().ToString('N'))
    New-PrivateDirectory $download
    [IO.File]::WriteAllText((Join-Path $download 'meetus-update-v1'), '1')
    if (-not $Archive) {
        Write-Host "Installed: $($installed.Version). Checking GitHub releases..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $headers = @{ 'User-Agent' = 'MeetusPrivatus-ManualUpdater'; Accept = 'application/vnd.github+json' }
        # /latest excludes previews. This project publishes preview releases until live gates pass.
        $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/oana-ffg/meetusprivatus/releases?per_page=100' -Headers $headers -TimeoutSec 30
        $release = @($releases | Where-Object { -not $_.draft -and $_.tag_name -match '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' } |
            Sort-Object published_at -Descending | Select-Object -First 1)
        if (-not $release.Count) { throw 'No published Windows release was found.' }
        $version = $release[0].tag_name.Substring(1)
        if ($version -eq $installed.Version) { Write-Host "You already have the latest release ($version)."; return }
        $assetName = "meetus-privatus-$version-win-x64.zip"
        $archiveAsset = @($release[0].assets | Where-Object name -eq $assetName)
        $checksumAsset = @($release[0].assets | Where-Object name -eq "$assetName.sha256")
        if ($archiveAsset.Count -ne 1 -or $checksumAsset.Count -ne 1 -or $archiveAsset[0].size -gt 500MB -or $checksumAsset[0].size -gt 1024) { throw 'Release assets are incomplete or exceed the download limit.' }
        $Archive = Join-Path $download $assetName
        $Checksum = Join-Path $download "$assetName.sha256"
        foreach ($asset in @($archiveAsset[0], $checksumAsset[0])) {
            $expectedUrl = "https://github.com/oana-ffg/meetusprivatus/releases/download/$($release[0].tag_name)/$($asset.name)"
            if ($asset.browser_download_url -cne $expectedUrl) { throw 'Unexpected release download URL.' }
            Write-Host "Downloading $($asset.name)..."
            Invoke-WebRequest -UseBasicParsing -Uri $expectedUrl -OutFile (Join-Path $download $asset.name) -TimeoutSec 300
            if ((Get-Item -LiteralPath (Join-Path $download $asset.name)).Length -ne $asset.size) { throw 'Release download size mismatch.' }
        }
    } else {
        $Archive = [IO.Path]::GetFullPath($Archive); $Checksum = [IO.Path]::GetFullPath($Checksum)
        Assert-LocalPath $Archive; Assert-LocalPath $Checksum
    }
    if ((Get-Item -LiteralPath $Archive).Length -gt 500MB -or (Get-Item -LiteralPath $Checksum).Length -gt 1024) { throw 'Release assets exceed the size limit.' }
    $checksumText = (Get-Content -LiteralPath $Checksum -Raw).Trim()
    if ($checksumText -notmatch '^([0-9a-fA-F]{64})\s+([^\s/\\]+\.zip)$' -or $Matches[2] -cne [IO.Path]::GetFileName($Archive)) { throw 'Invalid release checksum file.' }
    $expectedHash = $Matches[1]
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Release archive checksum mismatch. Nothing was installed.' }
    $archiveName = [IO.Path]::GetFileNameWithoutExtension($Archive)
    if ($archiveName -notmatch '^meetus-privatus-(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)-win-x64$') { throw 'Unexpected release archive name.' }
    $archiveVersion = $Matches[1]
    if (-not $PSBoundParameters.ContainsKey('Archive') -and $archiveVersion -ne $version) { throw 'Release tag/archive version mismatch.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$expandedBytes = 0
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.StartsWith($archiveName + '/', [StringComparison]::Ordinal) -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400)) { throw 'Unsafe archive entry.' }
            $relative = $entry.FullName.Substring($archiveName.Length + 1).TrimEnd('/')
            if ($relative) { Get-PackagePath $installRoot $relative | Out-Null }
            if (-not $names.Add($entry.FullName)) { throw 'Duplicate archive entry.' }
            $expandedBytes += $entry.Length
        }
        if ($expandedBytes -gt 1GB -or $zip.Entries.Count -gt 10000) { throw 'Expanded archive exceeds the updater limits.' }
    } finally { $zip.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, (Join-Path $download 'incoming'))
    $incoming = Join-Path (Join-Path $download 'incoming') $archiveName
    $next = Read-Manifest (Join-Path $incoming 'release.json')
    if ($next.Version -ne $archiveVersion) { throw 'Release manifest/archive version mismatch.' }
    foreach ($file in $next.Files) { Assert-FileIntegrity $incoming $file }
    $expectedFiles = @($next.Files.Path) + 'release.json'
    $actualFiles = @(Get-ChildItem -LiteralPath $incoming -File -Recurse | ForEach-Object { $_.FullName.Substring($incoming.Length + 1).Replace('\', '/') })
    if (@(Compare-Object $expectedFiles $actualFiles).Count) { throw 'Archive files do not match the release manifest.' }
    foreach ($file in $installed.Files) { Assert-FileIntegrity $installRoot $file }
    # Validate the new executable without opening audio or loading any user settings.
    & (Join-Path $incoming 'meetus.exe') help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The new executable failed its startup check.' }
    $oldNames = @($installed.Files.Path) + 'release.json'
    $allNames = @($oldNames + $expectedFiles | Sort-Object -Unique)
    $transactionFiles = foreach ($name in $allNames) {
        $target = Get-PackagePath $installRoot $name
        $exists = Test-Path -LiteralPath $target
        if ($exists -and $name -notin $oldNames) { throw "A user file conflicts with the update: $name. It was preserved." }
        if ($exists) {
            $backup = Get-PackagePath (Join-Path $download 'backup') $name
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            [IO.File]::Copy($target, $backup)
        }
        @{ Path = $name; HadOriginal = [bool]$exists; Size = $(if ($exists) { (Get-Item -LiteralPath $target).Length } else { 0 });
           Hash = $(if ($exists) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }) }
    }
    $journalBytes = [Text.UTF8Encoding]::new($false).GetBytes((@{ Format = 1; Files = @($transactionFiles) } | ConvertTo-Json -Depth 5))
    $journalStream = [IO.File]::Open((Join-Path $download 'transaction.json'), 'CreateNew', 'Write', 'None')
    try { $journalStream.Write($journalBytes, 0, $journalBytes.Length); $journalStream.Flush($true) }
    finally { $journalStream.Dispose() }
    # Backups and the journal are complete before publishing the recoverable transaction.
    [IO.Directory]::Move($download, $pending)
    $download = $null
    $incoming = Join-Path (Join-Path $pending 'incoming') $archiveName
    foreach ($name in $allNames | Where-Object { $_ -ne 'release.json' }) {
        $target = Get-PackagePath $installRoot $name
        if ($name -in $expectedFiles) {
            $source = Get-PackagePath $incoming $name
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Install-File $source $target
        } elseif (Test-Path -LiteralPath $target) { [IO.File]::Delete($target) }
    }
    Install-File (Join-Path $incoming 'release.json') (Join-Path $installRoot 'release.json')
    # Atomic commit marker: cleanup interruption must not roll back from partially removed backups.
    [IO.File]::Move((Join-Path $pending 'transaction.json'), (Join-Path $pending 'committed.json'))
    Remove-Workspace $pending
    Write-Host "Updated to $($next.Version). Start.cmd is ready. Your transcripts and settings were preserved."
} catch {
    $failure = $_.Exception.Message
    if ($updateLock -and (Test-Path -LiteralPath $pending)) {
        try { Restore-Transaction } catch { Write-Warning 'Automatic rollback could not finish. Run Update.cmd again before recording; the backup is preserved.' }
    }
    Write-Host "Update failed: $failure" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($download -and (Test-Path -LiteralPath $download)) { try { Remove-Workspace $download } catch { Write-Warning 'Update staging files were preserved for inspection.' } }
    if ($updateLock) { $updateLock.Dispose() }
    if ($Pause) { Write-Host 'Press any key to close.'; $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') }
}
exit $exitCode
