# Dot-source it: . (Join-Path $PSScriptRoot 'wait-idle.ps1'); Wait-UserIdle
# Desktop checks type into real apps: start only once nobody has touched the keyboard or mouse for a while
# (someone using the PC, even remotely through AnyDesk, would get keys typed into their own window).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Idle {
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
  public static uint Milliseconds() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return (uint)Environment.TickCount - i.dwTime; }
}
"@
function Wait-UserIdle([int]$Seconds = 60, [int]$TimeoutMinutes = 20) {
  $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
  while ([Idle]::Milliseconds() -lt $Seconds * 1000) {
    if ((Get-Date) -gt $deadline) { "ABORT: the PC was in use for $TimeoutMinutes min"; exit 3 }
    Start-Sleep 5
  }
}
