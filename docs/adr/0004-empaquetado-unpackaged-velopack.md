# ADR-0004 — App sin empaquetar + Velopack

- Estado: Aceptado (2026-10-01)

## Decisión
Distribución self-contained sin MSIX. Arranque con Windows vía `HKCU\...\Run`. Actualizaciones con Velopack. Bandeja con H.NotifyIcon (WinUI 3 no tiene tray nativo).

## Motivo
Compatible con `uiAccess=true` en V1 (inserción en apps elevadas: requiere firma + instalación en Program Files), sideload sencillo para la beta cerrada.
