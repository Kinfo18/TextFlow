Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $S = [System.Windows.Automation.TreeScope]
$p = Get-Process TextFlow | Select-Object -First 1
$win = $A::FromHandle($p.MainWindowHandle)
$kinds = 'Button|ListItem|CheckBox|RadioButton|Edit|ComboBox|Slider|TreeItem|Hyperlink|SplitButton|MenuItem'
foreach ($page in "Inicio","Snippets","Configuración","Diagnóstico") {
  $e = $win.FindFirst($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $page)))
  $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
  if ($page -eq "Snippets") {
    $li = $win.FindAll($S::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Select-Object -Last 1
    $li.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
  }
  $all = $win.FindAll($S::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($x in $all) { $c = $x.Current
    if ($c.ControlType.ProgrammaticName -match $kinds -and [string]::IsNullOrWhiteSpace($c.Name) -and -not $c.IsOffscreen) { "[$page] {0} id={1} class={2}" -f $c.ControlType.ProgrammaticName, $c.AutomationId, $c.ClassName }
    if ($page -eq "Snippets" -and $c.ControlType.ProgrammaticName -eq 'ControlType.ListItem' -and $c.Name -and $c.Name -notmatch '^(Inicio|Snippets|Configuración|Diagnóstico)$') { "[$page] ListItem name: " + $c.Name }
  }
}
