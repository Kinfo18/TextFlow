param([string]$Exe, [ValidateSet('notepad', 'word', 'chrome')][string]$Target = 'notepad', [int]$Count = 1000)
# H6.1 stress run: starts the TextFlow build under test in the tray, opens an EMPTY document in the target app,
# makes sure it is in front, then types $Count real abbreviations (spikes/TextFlow.Spikes stress).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint FgPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  public static void Focus(IntPtr h) { keybd_event(0x12, 0, 0, UIntPtr.Zero); SetForegroundWindow(h); keybd_event(0x12, 0, 2, UIntPtr.Zero); }
  public static void Ctrl(byte vk) { keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); }
}
"@
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
. (Join-Path $PSScriptRoot 'wait-idle.ps1'); Wait-UserIdle
Get-Process TextFlow -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$tf = Start-Process $Exe -ArgumentList "--background" -PassThru; Start-Sleep 6

switch ($Target) {
  'notepad' {
    $proc = Get-Process Notepad -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    if (-not $proc) { Start-Process notepad; Start-Sleep 2; $proc = Get-Process Notepad | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1 }
    for ($try = 0; $try -lt 5 -and [K]::FgPid() -ne $proc.Id; $try++) { [K]::Focus($proc.MainWindowHandle); Start-Sleep -Milliseconds 700 }
    if ([K]::FgPid() -eq $proc.Id) { [K]::Ctrl(0x4E); Start-Sleep 1 }   # Ctrl+N: new empty tab
  }
  'word' {
    Start-Process "C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE" -ArgumentList "/q /n /w"; Start-Sleep 8
    $proc = Get-Process WINWORD | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    [K]::Focus($proc.MainWindowHandle); Start-Sleep 1
  }
  'chrome' {
    Start-Process chrome -ArgumentList '--new-window', 'data:text/html,<title>TextFlow stress</title><textarea autofocus style=width:100%;height:95vh></textarea>'
    Start-Sleep 4
    $proc = Get-Process chrome | Where-Object { $_.MainWindowTitle -like 'TextFlow stress*' } | Select-Object -First 1
    [K]::Focus($proc.MainWindowHandle); Start-Sleep 1
  }
}
for ($try = 0; $proc -and $try -lt 5 -and [K]::FgPid() -ne $proc.Id; $try++) { [K]::Focus($proc.MainWindowHandle); Start-Sleep 1 }
if (-not $proc -or [K]::FgPid() -ne $proc.Id) { "ABORT: $Target is not in front"; $tf | Stop-Process -Force; exit 1 }

Push-Location $repo
dotnet run --project spikes/TextFlow.Spikes -c Release --no-build -- stress $Count 2>&1 | Where-Object { $_ -notmatch 'Enfoca el destino' }
Pop-Location
$tf | Stop-Process -Force
