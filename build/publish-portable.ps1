<#
.SYNOPSIS
    Builds the self-contained portable zip of TextFlow (V0.1 H6.3) into dist\.

.DESCRIPTION
    Publishes TextFlow.App for win-x64 (self-contained, unpackaged), checks that the files the main window needs are
    there, and zips the result. Close TextFlow first: a running instance locks the build output.

.EXAMPLE
    pwsh build\publish-portable.ps1
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\TextFlow.App\TextFlow.App.csproj'
$output = Join-Path $repo "dist\TextFlow-v$Version"
$zip = Join-Path $repo "dist\TextFlow-v$Version-portable.zip"

if (Get-Process -Name TextFlow -ErrorAction SilentlyContinue) {
    throw 'TextFlow está abierto: ciérralo (bandeja → Salir) antes de publicar.'
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)." }

# Without TextFlow.pri (compiled XAML) the tray works but the main window cannot load: the first work-PC run (H6).
foreach ($required in 'TextFlow.exe', 'TextFlow.dll', 'TextFlow.pri', 'Microsoft.WindowsAppRuntime.dll') {
    if (-not (Test-Path (Join-Path $output $required))) {
        throw "Falta $required en la publicación: el paquete no funcionaría."
    }
}

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Listo: $zip ($size MB). Descomprímelo en cualquier carpeta y abre TextFlow.exe."
