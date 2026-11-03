[CmdletBinding()]
param([switch]$Publish, [switch]$SkipTests, [string]$PublishDirectory, [string]$Version)

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
        # Two executables share one folder: the desktop app, and the helper it starts for recording, summaries and downloads.
        foreach ($project in 'src/Meetu/Meetu.csproj', 'src/Meetu.App/Meetu.App.csproj') {
            $publishArguments = @('publish', $project, '-c', 'Release', '--self-contained', 'true', '-o', $PublishDirectory)
            if ($Version) { $publishArguments += "/p:Version=$Version" }
            & dotnet @publishArguments
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
        }
        Copy-Item -LiteralPath LICENSE, THIRD-PARTY-NOTICES.md, README.md -Destination $PublishDirectory
        Copy-Item -LiteralPath licenses -Destination $PublishDirectory -Recurse -Force
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $PublishDirectory 'meetus.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
        $runtimePackage = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
        Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $PublishDirectory 'licenses/dotnet-runtime.txt')
        Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $PublishDirectory 'licenses/dotnet-runtime-third-party.txt')
        $desktopConfig = Get-Content -LiteralPath (Join-Path $PublishDirectory 'MeetusPrivatus.runtimeconfig.json') -Raw | ConvertFrom-Json
        $desktopVersion = @($desktopConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App')[0].version
        $desktopPackage = Join-Path $env:NUGET_PACKAGES "microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion"
        Copy-Item -LiteralPath (Join-Path $desktopPackage 'LICENSE') -Destination (Join-Path $PublishDirectory 'licenses/windowsdesktop-runtime.txt')
    }
} finally { Pop-Location }
