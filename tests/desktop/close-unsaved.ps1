Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $S = [System.Windows.Automation.TreeScope]
function ByName($root, $name) { $root.FindFirst($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) }
function Win {
  for ($i = 0; $i -lt 20; $i++) {
    $w = $A::RootElement.FindFirst($S::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)))
    if ($w) { return $w }; Start-Sleep -Milliseconds 300
  }
}
$p = Get-Process TextFlow | Select-Object -First 1
Start-Process $p.Path | Out-Null   # ask the running instance for its window
Start-Sleep 2
$win = Win
(ByName $win "Snippets").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
$item = $win.FindAll($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -eq "" } | Select-Object -Last 1
$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
$edits = $win.FindAll($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
$edit = $edits | Where-Object { $_.Current.Name -notlike "Buscar*" } | Select-Object -Last 1
"edit fields: " + (($edits | % { $_.Current.Name + "#" + $_.Current.AutomationId }) -join " , "); "editing field: '$($edit.Current.Name)'"
$vp = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue($vp.Current.Value + " x"); Start-Sleep 1

$wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
$wp.Close(); Start-Sleep 1
$dialog = ByName $win "Cambios sin guardar"
"dialog on close: $([bool]$dialog)"
(ByName $win "Seguir editando").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
"window alive after 'Seguir editando': $([bool](Win))"

$wp.Close(); Start-Sleep 1
(ByName $win "Descartar").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2
$w = $A::RootElement.FindFirst($S::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)))
$p.Refresh()
"after 'Descartar': window gone=$(-not $w) process alive=$(-not $p.HasExited)"
Start-Sleep 4; $p.Refresh(); "WS after close: {0:0} MB" -f ($p.WorkingSet64 / 1MB)
