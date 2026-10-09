param([string]$Exe, [int]$Runs = 10)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Threading;
public static class K {
  [DllImport("user32.dll")] static extern short VkKeyScan(char c);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUI { public int cb; public int flags; public IntPtr active, focus, capture, menuOwner, moveSize, caret; public RECT rc; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUI g);
  public static uint FgPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  public static string Focus() {
    uint pid; var tid = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    var g = new GUI(); g.cb = Marshal.SizeOf(g); GetGUIThreadInfo(tid, ref g);
    var sb = new StringBuilder(128); GetClassName(g.focus, sb, 128);
    return sb.ToString() + "#" + g.focus.ToInt64().ToString("X");
  }
  public static void Activate(IntPtr h) { keybd_event(0x12, 0, 0, UIntPtr.Zero); SetForegroundWindow(h); keybd_event(0x12, 0, 2, UIntPtr.Zero); }
  public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void Type(string s, int delay) {
    foreach (var c in s) { short r = VkKeyScan(c); bool sh = (r & 0x100) != 0;
      if (sh) keybd_event(0x10, 0, 0, UIntPtr.Zero); Key((byte)(r & 0xff)); if (sh) keybd_event(0x10, 0, 2, UIntPtr.Zero); Thread.Sleep(delay); }
  }
}
"@
$A = [System.Windows.Automation.AutomationElement]; $S = [System.Windows.Automation.TreeScope]
$log = Join-Path $env:LOCALAPPDATA ("TextFlow\logs\textflow-" + (Get-Date).ToUniversalTime().ToString("yyyyMMdd") + ".jsonl")
$logLocal = Join-Path $env:LOCALAPPDATA ("TextFlow\logs\textflow-" + (Get-Date -Format yyyyMMdd) + ".jsonl")

Get-Process TextFlow -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$tf = Start-Process $Exe -ArgumentList "--background" -PassThru; Start-Sleep 6
$before = @{}; foreach ($f in $log, $logLocal) { if (Test-Path $f) { $before[$f] = @(Get-Content $f).Count } }

Start-Process "C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE" -ArgumentList "/c ipm.note"
$compose = $null
for ($i = 0; $i -lt 60 -and -not $compose; $i++) {
  Start-Sleep 1
  $ol = Get-Process OUTLOOK -ErrorAction SilentlyContinue
  if ($ol) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $ol[0].Id)
    $compose = $A::RootElement.FindAll($S::Children, $cond) | Where-Object { $_.Current.ClassName -eq 'rctrl_renwnd32' -and $_.Current.Name -match 'Mensaje|Message' } | Select-Object -First 1
  }
}
if (-not $compose) { "NO_COMPOSE_WINDOW (Outlook profile/first run?)"; $A::RootElement.FindAll($S::Children, [System.Windows.Automation.Condition]::TrueCondition) | ? { $_.Current.ProcessId -eq (Get-Process OUTLOOK -ErrorAction SilentlyContinue | Select -First 1).Id } | % { "  win: " + $_.Current.ClassName + " | " + $_.Current.Name }; $tf | Stop-Process -Force; exit 2 }
"compose: " + $compose.Current.Name
$h = [IntPtr]$compose.Current.NativeWindowHandle
for ($t = 0; $t -lt 10 -and [K]::FgPid() -ne $compose.Current.ProcessId; $t++) { [K]::Activate($h); Start-Sleep 1 }
$body = $compose.FindAll($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document))) | Select-Object -Last 1
if ($body) { $body.SetFocus(); Start-Sleep 1 }
$olPid = (Get-Process OUTLOOK)[0].Id
for ($r = 1; $r -le $Runs; $r++) {
  if ([K]::FgPid() -ne $olPid) { "ABORT run ${r}: Outlook not in front"; break }
  $f0 = [K]::Focus()
  [K]::Type(" lc", 60); Start-Sleep -Milliseconds 700
  $f1 = [K]::Focus()
  [K]::Type("1", 0); Start-Sleep -Milliseconds 1500
  $f2 = [K]::Focus()
  $n = @(Get-Content $logLocal | Select-Object -Skip $before[$logLocal] | Select-String '"FromMenu":true').Count; "run ${r}: expansions so far=$n focus=$f0/$f1/$f2"
  [K]::Key(0x0D); Start-Sleep -Milliseconds 300   # new line between attempts
}
foreach ($f in $before.Keys) {
  Get-Content $f | Select-Object -Skip $before[$f] | Select-String 'OUTLOOK' | ForEach-Object { $_.Line -replace '"At".*', '' }
}
$tf | Stop-Process -Force

# Leave no drafts behind: answer "No" to "save changes?" for every compose window.
& (Join-Path $PSScriptRoot "outlook-discard-drafts.ps1")
