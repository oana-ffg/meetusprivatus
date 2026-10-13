[CmdletBinding()]
param([switch]$Publish, [switch]$SkipTests, [string]$PublishDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot '.artifacts/publish' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.artifacts/dotnet'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.artifacts/packages'
Push-Location $repoRoot
try {
    dotnet restore Meetu.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
    dotnet build Meetu.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        dotnet test tests/Meetu.Tests/Meetu.Tests.csproj -c Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    if ($Publish) {
        dotnet publish src/Meetu/Meetu.csproj -c Release --self-contained true -o $PublishDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath LICENSE, THIRD-PARTY-NOTICES.md, README.md, Start.cmd, Update.cmd, Update.ps1 -Destination $PublishDirectory
        Copy-Item -LiteralPath licenses -Destination $PublishDirectory -Recurse -Force
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $PublishDirectory 'meetus.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
        $runtimePackage = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
        Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $PublishDirectory 'licenses/dotnet-runtime.txt')
        Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $PublishDirectory 'licenses/dotnet-runtime-third-party.txt')
    }
} finally { Pop-Location }
