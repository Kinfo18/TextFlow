# ADR-0008 — Menú de grupo en WinUI 3 como ventana no activable

- Estado: Aceptado (2026-10-02)
- Resuelve: riesgo R1 del [informe de Fase 0](../fase0-informe.md) (tarea H0.1 de [V0.1](../v0.1-plan.md))

## Contexto
El menú de grupo debe mostrarse junto al caret sin quitar el foco a la aplicación destino (S7). El prototipo de
Fase 0 lo conseguía con WinForms. Faltaba comprobar que también se puede hacer con WinUI 3, que da el diseño
Fluent (Acrylic, esquinas redondeadas, tema claro/oscuro) que pidió el usuario.

## Decisión
El menú final es una `Microsoft.UI.Xaml.Window` (WinUI 3, unpackaged). Las cuatro técnicas siguientes son obligatorias:

1. **No activable:** `OverlappedPresenter.CreateForContextMenu()` (sin barra de título), `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`,
   subclase con `WM_MOUSEACTIVATE → MA_NOACTIVATE` (los clics no activan) y `AppWindow.Show(activateWindow: false)`.
   El teclado llega por el hook (`KeyboardHook.MenuMode`), nunca por el foco de XAML.
2. **Primera activación fuera de pantalla:** una ventana WinUI no renderiza su contenido hasta activarse una vez.
   Se activa una vez al iniciar la app en (-32000, -32000) y se oculta. Es el único momento en que TextFlow toma el foco.
3. **`IsAlwaysOnTop` vía presenter:** `WS_EX_TOPMOST` aplicado después de crear la ventana se ignora.
4. **Manifiesto PerMonitorV2:** sin él, el proceso no es consciente del DPI. Las coordenadas del caret (píxeles físicos)
   se interpretan como lógicas y el menú aparece fuera de la pantalla en monitores escalados (al 125 % era invisible).

Además, Acrylic se fuerza con `DesktopAcrylicController` + `SystemBackdropConfiguration.IsInputActive = true`.
Si no, una ventana que nunca se activa usa el color sólido de reserva. Las esquinas son las nativas de DWM (`DWMWCP_ROUND`).

## Evidencia
`spikes/TextFlow.WinUiSpike`. El usuario lo probó en Notepad, Firefox y Word: **53 comprobaciones PASS** de
"primer plano y control enfocado intactos" (mostrar, flechas, Enter, números, Esc y clic). El caret siguió
parpadeando. Hubo 2 avisos de cambio de ventana en primer plano con el menú abierto, sin clic en el menú,
atribuibles al usuario cambiando de app. Se verificará en H1 registrando el proceso que toma el foco.
`--selftest <carpeta>` muestra el popup en una posición fija y guarda una captura (verificación sin usuario).

## Consecuencias
- Se descarta el fallback Win32/WinForms para el menú final.
- La app WinUI debe ser x64 (Windows App SDK self-contained). El spike está fuera de `TextFlow.sln` (AnyCPU).
  En H0.2 se decide la plataforma de la solución.
- Sin App.xaml, la `Application` debe implementar `IXamlMetadataProvider` (si no, error `0xC000027B` al cargar
  `XamlControlsResources`). En la app real se usará App.xaml normal.
