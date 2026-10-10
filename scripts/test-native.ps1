[CmdletBinding()]
param([string]$Models = (Join-Path (Split-Path -Parent $PSScriptRoot) 'models'))

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixtureDir = Join-Path $repoRoot '.artifacts/fixtures'
New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null
$fixture = Join-Path $fixtureDir 'jfk.wav'
$fixtureHash = '59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e'
if (-not (Test-Path -LiteralPath $fixture)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/v1.8.5/samples/jfk.wav' -OutFile $fixture
}
if ((Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fixtureHash) {
    throw 'Public test fixture checksum mismatch. No recognition was run.'
}
$env:MEETUS_NATIVE_TESTS = '1'
$env:MEETUS_MODELS = [IO.Path]::GetFullPath($Models)
$env:MEETUS_FIXTURE = $fixture
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish
    if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }
    $env:MEETUS_BASELINE_TESTS = '1'
    dotnet test (Join-Path $repoRoot 'tests/Meetu.Tests/Meetu.Tests.csproj') -c Release --no-build --no-restore --filter 'FullyQualifiedName~BaselineRuntimeTests'
    if ($LASTEXITCODE -ne 0) { throw 'Baseline CPU check failed.' }
    & (Join-Path $PSScriptRoot 'test-console.ps1') -Models $Models
} finally {
    Remove-Item Env:MEETUS_NATIVE_TESTS, Env:MEETUS_BASELINE_TESTS, Env:MEETUS_MODELS, Env:MEETUS_FIXTURE -ErrorAction SilentlyContinue
}
