[CmdletBinding()]
param(
    [string]$PublishDirectory,
    [string]$Models,
    [switch]$Isolated
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot '.artifacts/publish' }
if (-not $Models) { $Models = Join-Path $repoRoot 'models' }
if (-not $Isolated) {
    # The child may detach its console; the user's calling PowerShell must retain its own.
    & powershell.exe -NoProfile -NonInteractive -File $PSCommandPath -Isolated -PublishDirectory $PublishDirectory -Models $Models
    if ($LASTEXITCODE -ne 0) { throw 'Isolated console cancellation check failed; see .artifacts/console-check for diagnostics.' }
    Write-Output 'Console Ctrl+C, automatic defaults, graceful completion, and owned-audio cleanup passed. No microphone was opened.'
    return
}
$executable = Join-Path ([IO.Path]::GetFullPath($PublishDirectory)) 'meetus.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Publish the application before checking console cancellation.' }
$stateRoot = Join-Path $repoRoot ('.artifacts/console-check/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
# No output directory: exercise automatic first-run defaults. An invalid mic is a safety guard;
# if cancellation arrives late, startup fails before any microphone can be opened.
@{
    MicrophoneId = 'nonexistent-console-test-input'
    ModelPath = Join-Path ([IO.Path]::GetFullPath($Models)) 'ggml-base.en-q5_1.bin'
    VadPath = Join-Path ([IO.Path]::GetFullPath($Models)) 'ggml-silero-v5.1.2.bin'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stateRoot 'config.json') -Encoding utf8
$workspaceId = [Guid]::NewGuid().ToString('N')
$abandoned = Join-Path $stateRoot "audio/$workspaceId"
New-Item -ItemType Directory -Path $abandoned -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $abandoned 'meetus-owned-audio-v1'), $workspaceId)
[IO.File]::WriteAllBytes((Join-Path $abandoned ([Guid]::NewGuid().ToString('N') + '.pcm')), [byte[]]@(0, 0))

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class MeetusConsoleProbe {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, xSize, ySize, xChars, yChars, fill, flags;
        public short show, reservedSize;
        public IntPtr reservedPointer, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo { public IntPtr process, thread; public uint processId, threadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttrs,
        IntPtr threadAttrs, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint groupId);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
'@
$startup = New-Object MeetusConsoleProbe+StartupInfo
$startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
$startup.flags = 1 # STARTF_USESHOWWINDOW
$startup.show = 0  # SW_HIDE: the test console stays hidden
$processInfo = New-Object MeetusConsoleProbe+ProcessInfo
$command = New-Object Text.StringBuilder ('"' + $executable + '" run --state-dir "' + $stateRoot + '"')
if (-not [MeetusConsoleProbe]::CreateProcess($executable, $command, [IntPtr]::Zero, [IntPtr]::Zero,
    $false, 0x10, [IntPtr]::Zero, (Split-Path -Parent $executable), [ref]$startup, [ref]$processInfo)) {
    throw 'Could not launch the isolated console probe.'
}
$probeProcess = [Diagnostics.Process]::GetProcessById($processInfo.processId)
$attached = $false
try {
    # This script must be a separate PowerShell process: do not detach a user's interactive console.
    [MeetusConsoleProbe]::FreeConsole() | Out-Null
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (-not $attached -and $wait.Elapsed.TotalSeconds -lt 3) {
        $attached = [MeetusConsoleProbe]::AttachConsole($processInfo.processId)
        if (-not $attached) { Start-Sleep -Milliseconds 10 }
    }
    if (-not $attached) { throw 'Could not attach to the isolated probe console.' }
    if (-not [MeetusConsoleProbe]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) { throw 'Could not protect the signal sender.' }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($sid + $stateRoot.ToUpperInvariant())) }
    finally { $hash.Dispose() }
    $pipeName = 'meetus-' + ([BitConverter]::ToString($bytes).Replace('-', '').Substring(0, 24))
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000)
        $pipe.WriteByte(0)
        $pipe.Flush()
        $reader = New-Object IO.StreamReader($pipe)
        try { $starting = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($starting.State -ne 'Starting') { throw 'Probe left startup before cancellation; no microphone was opened.' }
    } finally { $pipe.Dispose() }
    if (-not [MeetusConsoleProbe]::GenerateConsoleCtrlEvent(0, 0)) { throw 'Ctrl+C delivery failed.' }
    if (-not $probeProcess.WaitForExit(20000)) { throw 'Ctrl+C did not finish the isolated probe within 20 seconds.' }
    $status = Get-Content -LiteralPath (Join-Path $stateRoot 'status.json') -Raw | ConvertFrom-Json
    [uint32]$exitCode = 0
    if (-not [MeetusConsoleProbe]::GetExitCodeProcess($processInfo.process, [ref]$exitCode)) { throw 'Could not read probe exit code.' }
    if ($exitCode -ne 0 -or $status.State -ne 'Complete') { throw "Ctrl+C did not reach graceful completion (exit $exitCode, state $($status.State))." }
    if (@(Get-ChildItem -LiteralPath (Join-Path $stateRoot 'audio') -Filter '*.pcm' -Recurse).Count -ne 0) { throw 'Owned audio remained after cancellation.' }
    $config = Get-Content -LiteralPath (Join-Path $stateRoot 'config.json') -Raw | ConvertFrom-Json
    if ($config.OutputDirectory -ne (Join-Path $stateRoot 'transcripts') -or $config.MaxSessionMinutes -ne 60) {
        throw 'First-run defaults were not saved correctly.'
    }
} catch {
    [IO.File]::WriteAllText((Join-Path $stateRoot 'probe-error.txt'), $_.ToString() + [Environment]::NewLine + $_.ScriptStackTrace)
    throw
} finally {
    if ($attached) {
        [MeetusConsoleProbe]::FreeConsole() | Out-Null
        [MeetusConsoleProbe]::SetConsoleCtrlHandler([IntPtr]::Zero, $false) | Out-Null
    }
    if (-not $probeProcess.HasExited) { [MeetusConsoleProbe]::TerminateProcess($processInfo.process, 1) | Out-Null }
    $probeProcess.Dispose()
    [MeetusConsoleProbe]::CloseHandle($processInfo.thread) | Out-Null
    [MeetusConsoleProbe]::CloseHandle($processInfo.process) | Out-Null
}
