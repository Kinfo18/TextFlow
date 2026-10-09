param([string]$Exe, [string]$Label)
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
  public static void Ctrl(byte vk) { keybd_event(0x11, 0, 0, UIntPtr.Zero); Key(vk); keybd_event(0x11, 0, 2, UIntPtr.Zero); }
  public static void Type(string s, int delay) {
    foreach (var c in s) {
      short r = VkKeyScan(c); byte vk = (byte)(r & 0xff); bool shift = (r & 0x100) != 0;
      if (shift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
      Key(vk);
      if (shift) keybd_event(0x10, 0, 2, UIntPtr.Zero);
      Thread.Sleep(delay);
    }
  }
}
"@
$log = Join-Path $env:LOCALAPPDATA ("TextFlow\logs\textflow-" + (Get-Date -Format yyyyMMdd) + ".jsonl")
function Count($pattern) { @(Get-Content $log | Select-Object -Skip $script:start | Select-String $pattern).Count }

Get-Process TextFlow -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$p = Start-Process $Exe -ArgumentList "--background" -PassThru; Start-Sleep 6
$script:start = @(Get-Content $log).Count
$np = Get-Process Notepad -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $np) { Start-Process notepad; Start-Sleep 2; $np = Get-Process Notepad | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1 }
[K]::Focus($np.MainWindowHandle); Start-Sleep -Milliseconds 500
if ([K]::FgPid() -ne $np.Id) { "ABORT: Notepad is not in front"; $p | Stop-Process -Force; exit 1 }
[K]::Ctrl(0x4E)
Start-Sleep 1
if ([K]::FgPid() -ne $np.Id) { "ABORT: Notepad is not in front"; $p | Stop-Process -Force; exit 1 }
$fg = [K]::GetForegroundWindow()

[K]::Type("la direccion es nueva, gracias por todo ", 60)   # ~normal typing speed
Start-Sleep 1
$flash = Count '"MenuShown"'
[K]::Type("lc", 60); Start-Sleep -Milliseconds 700
$shownAfterWait = Count '"MenuShown"'
[K]::Key(0x1B); Start-Sleep -Milliseconds 400                  # Esc
[K]::Type(" lc", 60); [K]::Type("1", 0); Start-Sleep 1          # choice typed from memory
$fromMenu = Count '"FromMenu":true'
if ([K]::FgPid() -ne $np.Id) { "WARNING: focus left Notepad during the run" }
$doc = $null
$root = [System.Windows.Automation.AutomationElement]::FromHandle($fg)
$edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
if ($edit) { $doc = $edit.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(500) }
"[$Label] menus while typing words: $flash | after 'lc'+pause: $($shownAfterWait - $flash) shown | 'lc1' from memory -> FromMenu expansions: $fromMenu"
"[$Label] document starts: " + ($doc -replace "`r", ' ').Substring(0, [Math]::Min(45, $doc.Length))
$p | Stop-Process -Force
