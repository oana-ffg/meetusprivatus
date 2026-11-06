[CmdletBinding()]
param([string]$Models)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Models) { $Models = Join-Path $repoRoot 'models' }
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
# Optional large file, fetched by the app's download button. Without it, that one check is reported as skipped.
$summaryModel = Join-Path ([IO.Path]::GetFullPath($Models)) 'Qwen3.5-4B-Q4_K_M.gguf'
if (Test-Path -LiteralPath $summaryModel) { $env:MEETUS_SUMMARY_MODEL = $summaryModel }
# Also optional: without it, the Parakeet check is reported as skipped.
$parakeetModel = Join-Path ([IO.Path]::GetFullPath($Models)) 'parakeet-tdt-0.6b-v3-int8'
if (Test-Path -LiteralPath $parakeetModel) { $env:MEETUS_PARAKEET_MODEL = $parakeetModel }
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish
    if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }
    $env:MEETUS_BASELINE_TESTS = '1'
    dotnet test (Join-Path $repoRoot 'tests/Meetu.Tests/Meetu.Tests.csproj') -c Release --no-build --no-restore --filter 'FullyQualifiedName~BaselineRuntimeTests'
    if ($LASTEXITCODE -ne 0) { throw 'Baseline CPU check failed.' }
    # The GPU run needs a working Vulkan driver, so a failure here is reported but does not count against the CPU path.
    if ($env:MEETUS_SUMMARY_MODEL) {
        $env:MEETUS_SUMMARY_GPU = '1'
        dotnet test (Join-Path $repoRoot 'tests/Meetu.Tests/Meetu.Tests.csproj') -c Release --no-build --no-restore --filter 'FullyQualifiedName~NativeSummaryTests'
        if ($LASTEXITCODE -ne 0) { Write-Warning 'The GPU summary check failed on this machine.' }
    }
} finally {
    Remove-Item Env:MEETUS_NATIVE_TESTS, Env:MEETUS_BASELINE_TESTS, Env:MEETUS_MODELS, Env:MEETUS_FIXTURE, Env:MEETUS_SUMMARY_MODEL, Env:MEETUS_SUMMARY_GPU, Env:MEETUS_PARAKEET_MODEL -ErrorAction SilentlyContinue
}
