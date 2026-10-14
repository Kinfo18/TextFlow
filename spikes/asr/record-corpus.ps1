# Graba cada frase de corpus.tsv con el micrófono (16 kHz mono WAV) en models/recordings-v2/.
# Las grabaciones son voz del usuario: models/ está en .gitignore y nunca se suben.
#
# Uso (en una terminal propia, no con "!" desde Claude Code):
#   powershell -ExecutionPolicy Bypass -File spikes\asr\record-corpus.ps1
#   ... -Only 36            # solo una frase
#   ... -Redo               # vuelve a grabar las que ya existen
#
# Cada frase: Enter empieza a grabar, Enter la termina. Después: Enter sigue, R repite, P la escucha.
# Ya grabadas se saltan, así que se puede parar (Ctrl+C) y seguir otro día.
param(
    [string]$Device = 'Varios micrófonos (2- Intel® Smart Sound Technology for Digital Microphones)',
    [string]$Only,
    [switch]$Redo
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Resolve-Path "$PSScriptRoot/../.."
$out = Join-Path $root 'models/recordings-v2'
New-Item -ItemType Directory -Force $out | Out-Null

$rows = Import-Csv -Delimiter "`t" -Encoding UTF8 (Join-Path $PSScriptRoot 'corpus.tsv')
if ($Only) { $rows = $rows | Where-Object id -eq $Only }

function Record-Phrase([string]$file) {
    # ffmpeg stops cleanly (valid WAV header) when it reads "q" on stdin.
    $info = New-Object System.Diagnostics.ProcessStartInfo 'ffmpeg'
    $info.Arguments = "-hide_banner -loglevel error -y -f dshow -i `"audio=$Device`" -ar 16000 -ac 1 -c:a pcm_s16le `"$file`""
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $ffmpeg = [System.Diagnostics.Process]::Start($info)
    # The microphone takes ~0.5 s to open: only then ask the user to speak, or the first words are cut off.
    Start-Sleep -Milliseconds 800
    if ($ffmpeg.HasExited) {
        throw "ffmpeg no pudo abrir el micrófono '$Device' (código $($ffmpeg.ExitCode))."
    }

    Write-Host '    ● GRABANDO: lee la frase y pulsa Enter al terminar' -ForegroundColor Red
    [void][Console]::ReadLine()
    Start-Sleep -Milliseconds 400   # keep the tail of the last word
    $ffmpeg.StandardInput.Write('q')
    $ffmpeg.StandardInput.Close()
    $ffmpeg.WaitForExit()
    if (-not (Test-Path $file)) { throw 'La grabación falló.' }
    $seconds = ((Get-Item $file).Length - 44) / 32000
    Write-Host ("    ✓ {0:N1} s" -f $seconds) -ForegroundColor Green
}

function Play([string]$file) {
    $player = New-Object System.Media.SoundPlayer $file
    $player.PlaySync()
}

$i = 0
foreach ($row in $rows) {
    $i++
    $file = Join-Path $out "$($row.id).wav"
    if ((Test-Path $file) -and -not $Redo -and -not $Only) { continue }

    $noise = $row.cond -eq 'ruido'
    Write-Host ''
    Write-Host "[$($row.id)] $i/$($rows.Count)" -NoNewline -ForegroundColor Cyan
    if ($noise) { Write-Host '  CON RUIDO DE FONDO (oficina, TV o vídeo de ambiente)' -ForegroundColor Yellow } else { Write-Host '  en silencio' -ForegroundColor DarkGray }
    Write-Host "    $($row.reference)" -ForegroundColor White

    while ($true) {
        Read-Host '    Enter para empezar'
        Record-Phrase $file
        $choice = Read-Host '    Enter = siguiente · R = repetir · P = escuchar'
        while ($choice -match '^[pP]$') {
            Play $file
            $choice = Read-Host '    Enter = siguiente · R = repetir · P = escuchar'
        }
        if ($choice -notmatch '^[rR]$') { break }
    }
}

$done = (Get-ChildItem $out -Filter *.wav).Count
Write-Host ''
Write-Host "Listo: $done de $((Import-Csv -Delimiter "`t" -Encoding UTF8 (Join-Path $PSScriptRoot 'corpus.tsv')).Count) grabaciones en $out"
