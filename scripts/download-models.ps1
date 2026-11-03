[CmdletBinding()]
param(
    [string]$Destination,
    [ValidateSet('base.en-q5_1', 'base.en')][string]$Model = 'base.en-q5_1'
)

# Development setup for the speech models that releases include. Summary models come through the app's download button.
$ErrorActionPreference = 'Stop'
try {
    # Resolved here because Windows PowerShell leaves $PSScriptRoot empty in parameter defaults.
    if (-not $Destination) { $Destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'models' }
    $Destination = [IO.Path]::GetFullPath($Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'model-assets.psd1')
    $assets = @($manifest[$Model], $manifest['silero-v5.1.2'])
    foreach ($asset in $assets) {
        $targetPath = Join-Path $Destination $asset.Name
        if (Test-Path -LiteralPath $targetPath) {
            if ((Get-Item -LiteralPath $targetPath).Length -eq $asset.Size -and
                (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $asset.Hash) {
                Write-Output "Verified existing $($asset.Name)"
                continue
            }
            throw "Existing $($asset.Name) has an unexpected hash. Move it aside before retrying; it was not overwritten."
        }
        $temporaryPath = Join-Path $Destination ('.download-' + [Guid]::NewGuid().ToString('N'))
        try {
            Write-Output "Downloading $($asset.Name) ($([Math]::Round($asset.Size / 1MB)) MB)"
            # curl.exe ships with Windows 11, shows progress, and resumes after a dropped connection.
            & curl.exe --location --fail --proto '=https' --retry 5 --retry-all-errors --continue-at - --output $temporaryPath $asset.Url
            if ($LASTEXITCODE -ne 0) { throw "Download failed for $($asset.Name) (curl exit code $LASTEXITCODE)." }
            Write-Output 'Verifying the download...'
            if ((Get-Item -LiteralPath $temporaryPath).Length -ne $asset.Size -or
                (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.Hash) {
                throw "Integrity verification failed for $($asset.Name)."
            }
            [IO.File]::Move($temporaryPath, $targetPath)
            Write-Output "Verified $($asset.Name)"
        } finally {
            if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
        }
    }
    Write-Output 'Models are ready.'
} catch {
    Write-Output "Model download failed: $($_.Exception.Message)"
    exit 1
}
