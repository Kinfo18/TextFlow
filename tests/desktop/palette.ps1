param([string]$Exe)
# D11: Ctrl+Shift+Alt+Space in Notepad, type a query, Enter -> the snippet lands in Notepad; Esc closes and gives the focus back.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Threading;
public static class K {
  [DllImport("user32.dll")] static extern short VkKeyScan(char c);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static uint FgPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  public static void Focus(IntPtr h) { keybd_event(0x12, 0, 0, UIntPtr.Zero); SetForegroundWindow(h); keybd_event(0x12, 0, 2, UIntPtr.Zero); }
  public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void CtrlShiftAlt(byte vk) { keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x10, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0, UIntPtr.Zero); Key(vk); keybd_event(0x12, 0, 2, UIntPtr.Zero); keybd_event(0x10, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero); }
  public static void Type(string s) { foreach (var c in s) { short r = VkKeyScan(c); bool sh = (r & 0x100) != 0;
      if (sh) keybd_event(0x10, 0, 0, UIntPtr.Zero); Key((byte)(r & 0xff)); if (sh) keybd_event(0x10, 0, 2, UIntPtr.Zero); Thread.Sleep(40); } }
}
"@
. (Join-Path $PSScriptRoot 'wait-idle.ps1'); Wait-UserIdle
$log = Join-Path $env:LOCALAPPDATA ("TextFlow\logs\textflow-" + (Get-Date -Format yyyyMMdd) + ".jsonl")
Get-Process TextFlow -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$tf = Start-Process $Exe -ArgumentList "--background" -PassThru; Start-Sleep 6
$np = Get-Process Notepad -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $np) { Start-Process notepad; Start-Sleep 2; $np = Get-Process Notepad | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1 }
for ($i = 0; $i -lt 5 -and [K]::FgPid() -ne $np.Id; $i++) { [K]::Focus($np.MainWindowHandle); Start-Sleep -Milliseconds 700 }
if ([K]::FgPid() -ne $np.Id) { "ABORT: Notepad is not in front"; $tf | Stop-Process -Force; exit 1 }
$before = @(Get-Content $log -ErrorAction SilentlyContinue).Count

# 1. Esc: the palette closes and Notepad gets the focus back
[K]::CtrlShiftAlt(0x20); Start-Sleep 1
$paletteUp = [K]::FgPid() -eq $tf.Id
# Never type unless the palette has the focus: another app may own the shortcut (Claude desktop took Ctrl+Alt+Space).
if (-not $paletteUp) { "ABORT: the palette did not open (shortcut taken by another app?)"; $tf | Stop-Process -Force; exit 1 }
[K]::Key(0x1B); Start-Sleep 1
"Esc: palette opened=$paletteUp, focus back in Notepad=$([K]::FgPid() -eq $np.Id)"

# 2. Query + Enter: the snippet goes into Notepad
if ([K]::FgPid() -ne $np.Id) { "ABORT: Notepad lost the focus"; $tf | Stop-Process -Force; exit 1 }
[K]::Key(0x0D); Start-Sleep -Milliseconds 300
[K]::CtrlShiftAlt(0x20); Start-Sleep 1
if ([K]::FgPid() -ne $tf.Id) { "ABORT: the palette did not open the second time"; $tf | Stop-Process -Force; exit 1 }
[K]::Type("orden"); Start-Sleep -Milliseconds 500
if ([K]::FgPid() -ne $tf.Id) { "ABORT: the palette lost the focus while typing"; $tf | Stop-Process -Force; exit 1 }
[K]::Key(0x0D); Start-Sleep 2
$events = @(Get-Content $log | Select-Object -Skip $before | Select-String 'ExpansionCompleted')
"Enter: expansions logged=$($events.Count) | " + (($events | ForEach-Object { ($_.Line -replace '.*"TargetProcess":"([^"]+)".*"Status":"([^"]+)".*', '$1 $2') }) -join ', ')
"focus back in Notepad=$([K]::FgPid() -eq $np.Id)"
$root = [System.Windows.Automation.AutomationElement]::FromHandle($np.MainWindowHandle)
$doc = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
$text = $doc.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
"document ends with the snippet: $($text.TrimEnd().EndsWith('pedido'))"
"(keeping TextFlow running for the usage flush check)"
