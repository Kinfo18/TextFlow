param([string]$Exe)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$C = [System.Windows.Automation.TreeScope]
function Find($root, $name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name)
  for ($i = 0; $i -lt 20; $i++) { $e = $root.FindFirst($C::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 300 }
  return $null
}

$p = Start-Process $Exe -PassThru
$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
  Start-Sleep -Milliseconds 500
  $p.Refresh()
  if ($p.MainWindowHandle -ne 0) { $win = $A::FromHandle($p.MainWindowHandle) }
}
if (-not $win) { "NO_WINDOW (app may start hidden in tray)"; $p | Stop-Process -Force; exit 2 }
"window: $($win.Current.Name)"

$nav = Find $win "Diagnóstico"
if (-not $nav) { "NO_NAV"; $win.FindAll($C::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Select-Object -First 60 | % { "  " + $_.Current.ControlType.ProgrammaticName + " | " + $_.Current.Name }; $p | Stop-Process -Force; exit 3 }
try { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 1

$comboCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
$combo = $win.FindFirst($C::Descendants, $comboCond)
if (-not $combo) { "NO_COMBO"; $p | Stop-Process -Force; exit 4 }

$ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
$ec.Expand(); Start-Sleep -Milliseconds 500
$items = $combo.FindAll($C::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$names = @($items | % { $_.Current.Name })
"days: $($names -join ' / ')"
$ec.Collapse()

$ok = 0
foreach ($round in 1..2) {
  for ($k = 0; $k -lt $names.Count; $k++) {
    $ec.Expand(); Start-Sleep -Milliseconds 300
    $it = $combo.FindFirst($C::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $names[$k])))
    $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
    $p.Refresh()
    if ($p.HasExited) { "CRASH after selecting '$($names[$k])' exit=$($p.ExitCode)"; exit 1 }
    $exp = $win.FindFirst($C::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, "Expansiones")))
    "  ok: $($names[$k])"
    $ok++
  }
}
"ALIVE after $ok day changes"
$p | Stop-Process -Force
exit 0
