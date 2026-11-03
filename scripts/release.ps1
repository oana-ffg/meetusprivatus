[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseNotes,
    [string]$Models
)

# Publishes the version in Directory.Build.props from the commit on origin/main. A version must never be reused for different binaries.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$repository = 'oana-ffg/meetusprivatus'
Set-Location -LiteralPath $repoRoot
$notesPath = (Resolve-Path -LiteralPath $ReleaseNotes).Path
$version = [string]([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
if (git status --porcelain) { throw 'Commit the release changes before publishing.' }
$commit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the release commit.' }
$remoteCommit = gh api "repos/$repository/commits/main" --jq .sha
if ($LASTEXITCODE -ne 0 -or $remoteCommit -ne $commit) { throw 'Push this commit to origin/main before publishing.' }
$repo = gh repo view $repository --json isPrivate | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $repo.isPrivate) { throw 'The updater requires publicly downloadable release assets.' }

# Each release gets a fresh output directory, so old test packages cannot be uploaded.
$output = Join-Path $repoRoot (".artifacts/releases/$version-" + [Guid]::NewGuid().ToString('N'))
$packageArguments = @{ OutputDirectory = $output; ReleaseNotes = $notesPath }
if ($Models) { $packageArguments.Models = $Models }
& (Join-Path $PSScriptRoot 'package.ps1') @packageArguments
$assets = @(Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in '.zip', '.nupkg', '.json' | ForEach-Object FullName)
if ($assets.Count -lt 3) { throw 'The portable ZIP, full package, and release metadata are required.' }
# Previews stay marked as pre-releases. The updater includes them; live acceptance gates remain open until a stable version.
$prerelease = if ($version -match '-') { @('--prerelease') } else { @() }
gh release create "v$version" @assets --repo $repository --target $commit --title "Meetus Privatus $version" --notes-file $notesPath --draft @prerelease
if ($LASTEXITCODE -ne 0) { throw 'Creating the draft release failed. Inspect GitHub before retrying.' }
gh release edit "v$version" --repo $repository --draft=false
if ($LASTEXITCODE -ne 0) { throw 'The release remains a draft. Inspect its assets before publishing.' }
Write-Output "Published v$version."
