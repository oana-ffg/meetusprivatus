[CmdletBinding()]
param([string]$Models)

# A real portable update from one version to the next: package both, extract the first, press the real buttons
# through UI Automation, and check that the new version runs and that meetings and the summary model survive.
# Uses a local feed and synthetic files only. Publishes nothing, opens no microphone, and needs no network.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.IO.Compression.FileSystem
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Models) { $Models = Join-Path $repoRoot 'models' }
$root = Join-Path $repoRoot (".artifacts/update-test/" + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $root 'feed'
$install = Join-Path $root 'installed app'
$state = Join-Path $root 'state'
New-Item -ItemType Directory -Path $root, $state | Out-Null

function Step($text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }
function Wait-For($what, [scriptblock]$check, $seconds = 60) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $seconds) {
        $value = & $check
        if ($value) { Step "OK  $what"; return $value }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out waiting for: $what"
}
function Get-AppWindow {
    $process = Get-Process MeetusPrivatus -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$install*" } | Select-Object -First 1
    if (-not $process) { return $null }
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))
}
function Get-Texts($window) {
    @($window.FindAll([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ })
}
function Press($window, $name) {
    $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))))
    if (-not $button) { throw "No button named $name." }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

& (Join-Path $PSScriptRoot 'package.ps1') -Version 0.0.1 -Models $Models -OutputDirectory $feed -SkipTests
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $feed 'MeetusPrivatus-win-x64-Portable.zip'), $install)
& (Join-Path $PSScriptRoot 'package.ps1') -Version 0.0.2 -Models $Models -OutputDirectory $feed -SkipTests

# What must survive an update lives beside the launcher, outside the folder being replaced.
$meeting = Join-Path $install 'transcripts/synthetic-session/notes.txt'
New-Item -ItemType Directory -Path (Split-Path -Parent $meeting) | Out-Null
[IO.File]::WriteAllText($meeting, 'synthetic preserved text')
$model = Join-Path $install 'models/synthetic-summary-model.gguf'
New-Item -ItemType Directory -Path (Split-Path -Parent $model) | Out-Null
[IO.File]::WriteAllText($model, 'synthetic preserved model')
# A nonexistent microphone: nothing here can ever record.
@{ MicrophoneId = 'nonexistent-update-test-input'; HideWindowFromScreenSharing = $true } | ConvertTo-Json | Set-Content (Join-Path $state 'config.json') -Encoding utf8

$launcher = Join-Path $install 'Meetus Privatus.exe'
if (-not (Test-Path -LiteralPath $launcher)) { throw 'The portable package has no launcher in its root.' }
$arguments = @('--state-dir', "`"$state`"", '--update-feed', "`"$feed`"")
try {
    $null = Start-Process -FilePath $launcher -ArgumentList $arguments -PassThru
    $window = Wait-For 'version 0.0.1 opens' { $w = Get-AppWindow; if ($w -and ((Get-Texts $w) -contains 'Version 0.0.1')) { $w } }
    Press $window 'Check for updates'
    Wait-For 'the update downloads and is offered' { (Get-Texts $window) -contains 'Version 0.0.2 is ready.' } 120 | Out-Null
    Press $window 'Restart to update'
    Wait-For 'the old version exits' { -not (Get-Process MeetusPrivatus -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$install*" }) -or -not (Get-AppWindow) } 60 | Out-Null
    $window = Wait-For 'version 0.0.2 reopens by itself' { $w = Get-AppWindow; if ($w -and ((Get-Texts $w) -contains 'Version 0.0.2')) { $w } } 120
    if ([IO.File]::ReadAllText($meeting) -ne 'synthetic preserved text') { throw 'The update changed a saved meeting.' }
    if ([IO.File]::ReadAllText($model) -ne 'synthetic preserved model') { throw 'The update changed the summary model.' }
    Step 'OK  saved meetings and the summary model survived'
    Wait-For 'the update asks for a transcription model' { (Get-Texts $window) -contains 'Choose a transcription model' } | Out-Null
    $record = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Start recording')))
    if (-not $record -or $record.Current.IsEnabled) { throw 'Recording must stay disabled until a model is chosen.' }
    Press $window ('Choose model' + [char]0x2026)
    $settings = Wait-For 'model setup opens' {
        $condition = New-Object Windows.Automation.AndCondition(
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window)),
            (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Choose a transcription model')))
        $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    }
    $radios = @($settings.FindAll([Windows.Automation.TreeScope]::Descendants,
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::RadioButton))))
    if ($radios.Count -ne 4) { throw "Expected all four transcription model choices; found $($radios.Count)." }
    foreach ($radio in $radios) {
        if ($radio.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
            throw 'A transcription model was selected automatically.'
        }
    }
    $base = $radios | Where-Object { $_.Current.Name -eq 'Whisper base.en' }
    $base.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Press $settings 'Save'
    Wait-For 'the chosen model is saved and recording is enabled' {
        $saved = Get-Content -LiteralPath (Join-Path $state 'config.json') -Raw | ConvertFrom-Json
        $saved.SpeechModelId -eq 'whisper-base.en' -and $record.Current.IsEnabled -and
            ((Get-Texts $window) -notcontains 'Choose a transcription model')
    } | Out-Null
    Press $window 'Check for updates'
    Wait-For 'the new version reports it is current' { (Get-Texts $window) -contains 'You have the latest version.' } | Out-Null
    $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-For 'the app closes' { -not (Get-AppWindow) } 15 | Out-Null
    $null = Start-Process -FilePath $launcher -ArgumentList $arguments -PassThru
    $window = Wait-For 'the saved model choice survives a restart' {
        $w = Get-AppWindow
        if ($w -and ((Get-Texts $w) -contains 'Version 0.0.2') -and ((Get-Texts $w) -notcontains 'Choose a transcription model')) { $w }
    }
    $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-For 'the restarted app closes' { -not (Get-AppWindow) } 15 | Out-Null
    Write-Output 'Updater checks passed.'
} finally {
    Get-Process MeetusPrivatus, meetus -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$install*" } | Stop-Process -Force
}
Remove-Item -LiteralPath $root -Recurse -Force
