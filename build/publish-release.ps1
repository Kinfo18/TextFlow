<#
.SYNOPSIS
    Builds the TextFlow installer and its update packages with Velopack, and optionally publishes them to GitHub Releases.

.DESCRIPTION
    Publishes TextFlow.App (self-contained, win-x64) with the given version, downloads the previous release so Velopack
    can build a delta, and packs dist\releases\ (TextFlow-win-Setup.exe, full and delta .nupkg, releases.win.json).
    With -Upload the packages go to a new GitHub release v<Version>; installed copies find it within hours, or at
    their next start.

    The install folder is %LOCALAPPDATA%\TextFlowApp (packId), NOT %LOCALAPPDATA%\TextFlow where the library,
    settings and logs live: uninstalling removes the install folder, and it must never take the user's data with it.

    Close TextFlow first: a running instance locks the build output.

.EXAMPLE
    pwsh build\publish-release.ps1 -Version 0.1.1
    pwsh build\publish-release.ps1 -Version 0.1.1 -Upload    # needs $env:TEXTFLOW_GITHUB_TOKEN

    Normally GitHub Actions runs it (.github/workflows/release.yml) when a v* tag is pushed: the dev PC's network
    resets large uploads.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string]$Version,

    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
$repoUrl = 'https://github.com/Kinfo18/TextFlow'
$packId = 'TextFlowApp'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TextFlow.App\TextFlow.App.csproj'
$publishDir = Join-Path $root "dist\publish-v$Version"
$releasesDir = Join-Path $root 'dist\releases'
$icon = Join-Path $root 'src\TextFlow.App\Assets\TextFlow.ico'

if (Get-Process -Name TextFlow -ErrorAction SilentlyContinue) {
    throw 'TextFlow está abierto: ciérralo (bandeja → Salir) antes de publicar.'
}

$token = $env:TEXTFLOW_GITHUB_TOKEN
if ($Upload -and -not $token) {
    throw 'Falta $env:TEXTFLOW_GITHUB_TOKEN (token de GitHub con permiso Contents: Read and write sobre el repo).'
}

Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore falló ($LASTEXITCODE)." }

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)." }

    # Without TextFlow.pri (compiled XAML) the tray works but the main window cannot load (H6, first work-PC run).
    foreach ($required in 'TextFlow.exe', 'TextFlow.dll', 'TextFlow.pri', 'Microsoft.WindowsAppRuntime.dll', 'Assets\TextFlow.ico') {
        if (-not (Test-Path (Join-Path $publishDir $required))) {
            throw "Falta $required en la publicación: el paquete no funcionaría."
        }
    }

    # The previous release lets Velopack build a small delta package; the first release has none.
    dotnet vpk download github --repoUrl $repoUrl --outputDir $releasesDir
    if ($LASTEXITCODE -ne 0) { Write-Warning 'No se pudo bajar la versión anterior: solo habrá paquete completo.' }

    dotnet vpk pack --packId $packId --packVersion $Version --packDir $publishDir --mainExe TextFlow.exe `
        --packTitle TextFlow --packAuthors Kinfo18 --icon $icon --outputDir $releasesDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack falló ($LASTEXITCODE)." }

    if ($Upload) {
        dotnet vpk upload github --repoUrl $repoUrl --token $token --outputDir $releasesDir `
            --publish --releaseName "TextFlow $Version" --tag "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "vpk upload falló ($LASTEXITCODE)." }
        Write-Host "Publicado: $repoUrl/releases/tag/v$Version"
    }
    else {
        Write-Host "Listo en $releasesDir. Instalador: $packId-win-Setup.exe. Para publicarlo: -Upload."
    }
}
finally {
    Pop-Location
}
