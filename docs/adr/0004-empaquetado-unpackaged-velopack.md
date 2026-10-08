# ADR-0004 — App sin empaquetar + Velopack

- Estado: Aceptado (2026-10-01)

## Decisión
Distribución self-contained sin MSIX. Arranque con Windows vía `HKCU\...\Run`. Actualizaciones con Velopack. Bandeja con H.NotifyIcon (WinUI 3 no tiene tray nativo).

## Motivo
Compatible con `uiAccess=true` en V1 (inserción en apps elevadas: requiere firma + instalación en Program Files), sideload sencillo para la beta cerrada.

## Implementación (2026-10-08, V0.1)

- Velopack 1.2.158 (librería y `vpk` como herramienta local en `dotnet-tools.json`). `Program.Main` propio (`DISABLE_XAML_GENERATED_MAIN`) ejecuta `VelopackApp.Run()` antes de arrancar WinUI.
- `packId` = **`TextFlowApp`**: se instala en `%LOCALAPPDATA%\TextFlowApp`, separado de `%LOCALAPPDATA%\TextFlow` (biblioteca, ajustes, logs). Desinstalar borra la carpeta de instalación y nunca debe llevarse los datos del usuario. Al desinstalar se quita también la entrada de `HKCU\...\Run`.
- Origen de actualizaciones: GitHub Releases del repo público `Kinfo18/TextFlow`, sin token en la app. Busca 30 s después de arrancar y cada 6 h, descarga en segundo plano y espera al usuario ("Reiniciar y actualizar" en Inicio y en la bandeja). La copia portable (zip) y la de desarrollo no buscan actualizaciones.
- Publicar: `build/publish-release.ps1 -Version x.y.z [-Upload]` (sube con `$env:TEXTFLOW_GITHUB_TOKEN`). Sin firma de código por ahora: SmartScreen avisa la primera vez.
- La bandeja es nativa (`Shell_NotifyIcon`, H1.2), no H.NotifyIcon.
