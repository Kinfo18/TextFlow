# Graba cada frase de corpus.tsv con el micrófono (16 kHz mono WAV) en models/recordings/.
# Las grabaciones son voz del usuario: models/ está en .gitignore y nunca se suben.
param(
    [string]$Device = 'Varios micrófonos (2- Intel® Smart Sound Technology for Digital Microphones)',
    [string]$Only
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Resolve-Path "$PSScriptRoot/../.."
$out = Join-Path $root 'models/recordings'
New-Item -ItemType Directory -Force $out | Out-Null

$rows = Import-Csv -Delimiter "`t" -Encoding UTF8 (Join-Path $PSScriptRoot 'corpus.tsv')
if ($Only) { $rows = $rows | Where-Object id -eq $Only }

foreach ($row in $rows) {
    $words = ($row.reference -split '\s+').Count
    $seconds = [math]::Ceiling($words / 2.0) + 2   # ~2 palabras/s + margen
    Write-Host ''
    Write-Host "[$($row.id)] ($seconds s)" -ForegroundColor Cyan
    Write-Host "    $($row.reference)" -ForegroundColor White
    Read-Host '    Enter para grabar'
    # The microphone takes ~0.5 s to open: start ffmpeg first and only then ask the user to speak,
    # otherwise the first words are cut off (seen in the first S5 run).
    $file = Join-Path $out "$($row.id).wav"
    # Start-Process joins arguments with spaces: quote the ones that contain spaces (device name, path).
    $ffmpeg = Start-Process ffmpeg -PassThru -NoNewWindow -ArgumentList (
        "-hide_banner -loglevel error -y -f dshow -i `"audio=$Device`" " +
        "-t $($seconds + 1) -ar 16000 -ac 1 -c:a pcm_s16le `"$file`"")
    Start-Sleep -Milliseconds 1000
    if ($ffmpeg.HasExited) {
        throw "ffmpeg no pudo abrir el micrófono '$Device' (código $($ffmpeg.ExitCode))."
    }

    Write-Host '    ● HABLA AHORA: lee la frase con naturalidad' -ForegroundColor Red
    $ffmpeg.WaitForExit()
    if ($ffmpeg.ExitCode -ne 0 -or -not (Test-Path $file)) {
        throw "La grabación $($row.id) falló (código $($ffmpeg.ExitCode))."
    }

    Write-Host '    ✓ guardado' -ForegroundColor Green
}

Write-Host ''
Write-Host "Listo: $($rows.Count) grabaciones en $out"
