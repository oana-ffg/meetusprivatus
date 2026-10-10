[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'models'),
    [ValidateSet('base.en-q5_1', 'base.en')][string]$Model = 'base.en-q5_1'
)

# This explicitly invoked setup script is the only model-download path.
$ErrorActionPreference = 'Stop'
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
        Write-Output "Downloading $($asset.Name)"
        Invoke-WebRequest -Uri $asset.Url -OutFile $temporaryPath -UseBasicParsing
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
Write-Output 'Models are ready. The meeting application itself performs no downloads.'
