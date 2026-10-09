Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static string Cls(IntPtr h) { var s = new StringBuilder(128); GetClassName(h, s, 128); return s.ToString(); }
  public static string Txt(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
  public static List<IntPtr> Top(uint pid) { var r = new List<IntPtr>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, IntPtr.Zero); return r; }
  public static List<IntPtr> Kids(IntPtr parent) { var r = new List<IntPtr>(); EnumChildWindows(parent, (h, l) => { r.Add(h); return true; }, IntPtr.Zero); return r; }
}
"@
$pid1 = (Get-Process OUTLOOK | Select-Object -First 1).Id
for ($i = 0; $i -lt 6; $i++) {
  $tops = [W]::Top($pid1)
  $dlg = $tops | ? { [W]::Cls($_) -eq '#32770' } | Select-Object -First 1
  if (-not $dlg) {
    $compose = $tops | ? { [W]::Txt($_) -match 'Mensaje' } | Select-Object -First 1
    if (-not $compose) { break }
    [W]::PostMessage($compose, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE
    Start-Sleep 2; continue
  }
  $kids = [W]::Kids($dlg)
  "dialog: " + [W]::Txt($dlg) + " | buttons: " + (($kids | ? { [W]::Cls($_) -eq 'Button' } | % { [W]::Txt($_) }) -join ", ")
  $no = $kids | ? { [W]::Cls($_) -eq 'Button' -and ([W]::Txt($_) -replace '&','') -eq 'No' } | Select-Object -First 1
  if (-not $no) { "no 'No' button; stopping"; break }
  [W]::SendMessage($no, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # BM_CLICK
  Start-Sleep 2
}
"left: " + (([W]::Top($pid1) | % { [W]::Cls($_) + ':' + [W]::Txt($_) }) -join " ; ")
