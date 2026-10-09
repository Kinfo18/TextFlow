param([string]$Exe)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $S = [System.Windows.Automation.TreeScope]
function Mem($label) {
  $p.Refresh()
  if ($p.HasExited) { "$label : PROCESS EXITED"; exit 1 }
  "{0,-34} WS {1,4:0} MB   private {2,4:0} MB" -f $label, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB)
}
function OpenWindow {
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)
    $w = $A::RootElement.FindFirst($S::Children, $cond)
    if ($w) { return $w }
  }
}
function Pages($win) {
  foreach ($page in "Snippets", "Configuración", "Diagnóstico", "Inicio") {
    $e = $win.FindFirst($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $page)))
    $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800
  }
}
function CloseWindow($win) { $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }

$p = Start-Process $Exe -PassThru
$win = OpenWindow
Start-Sleep 3
Mem "1. window open (Inicio)"
Pages $win
Mem "2. after visiting all pages"
CloseWindow $win
Start-Sleep 6
Mem "3. window closed, 6 s later"
Start-Process $Exe | Out-Null   # second instance asks the first to show the window
$win = OpenWindow
Start-Sleep 2
Pages $win
Mem "4. reopened + all pages again"
CloseWindow $win
Start-Sleep 6
Mem "5. closed again, 6 s later"
$p | Stop-Process -Force
