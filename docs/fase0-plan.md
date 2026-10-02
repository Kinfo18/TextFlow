# Fase 0 — Risk Spikes: plan y estado

Decisiones de la sesión 2026-10-01: ver `docs/adr/0001`–`0006`. Revisión 2026-10-02: ADR-0001 (clipboard primero para todo).

| # | Spike | Estado | Evidencia |
|---|---|---|---|
| S0 | Andamiaje .NET 10 + tests | ✅ | `TextFlow.sln`, 153 tests Core + 5 de escritorio |
| S1 | Target Resolver multi-monitor | ✅ base | `Win32TargetResolver` + `UiaFocusedControlInspector`; captura 54 ms frío / 2 ms caliente |
| S2 | Inserción clipboard + SendInput | ✅ base | `ClipboardStrategy`, `SendInputStrategy`, `InsertionCoordinator`; 5 tests de escritorio reales |
| S3 | Hook de teclado + triggers | 🟡 validado en Notepad/Firefox/Word; falta resto de matriz | expansión **instantánea** por defecto (`TriggerMode.Immediate`, estilo aText) + `IgnoreCase` por trigger; SendInput falló en Notepad → clipboard primero |
| S4 | Template Engine | ✅ | `TemplateParser`/`TemplateRenderer`, 22 tests |
| S5 | Benchmark ASR (whisper.cpp vs Parakeet) | ⏳ | requiere descargar modelos (~0,5–1,6 GB c/u) |
| S6 | Importador aText (Windows) | 🟡 lector listo; falta menú de grupo | `ATextBackupReader` lee el backup real: 51 grupos, 36 abreviaturas, 326 snippets. `spikes import <archivo>` |
| S7 | Menú de grupo (abreviatura → menú de snippets) | ✅ prototipo | ventana **no activable** (`WS_EX_NOACTIVATE`) + hook en `MenuMode`: el destino nunca pierde foco. Validado por el usuario en Notepad (ancla caret) y Firefox (ancla ratón: Firefox no expone caret Win32, pero el usuario lo vio junto al cursor). Sonido validado. `spikes menu <archivo>` |

## Cómo probar a mano

```powershell
dotnet run --project spikes/TextFlow.Spikes -- target 15        # mueve el foco entre apps/monitores
dotnet run --project spikes/TextFlow.Spikes -- insert auto       # 3 s para enfocar el destino
dotnet run --project spikes/TextFlow.Spikes -- insert clipboard
dotnet run --project spikes/TextFlow.Spikes -- expand            # escribe ;firma ;fecha ;cur ;mail CC (sin espacio)
dotnet run --project spikes/TextFlow.Spikes -- import BUXtendo.atext  # resumen, sin contenido
```

Ejecutar `spikes` desde una terminal: la terminal está excluida por política, así que el hook no captura lo que se escribe en ella.

## Matriz de compatibilidad a rellenar (S2/S3)

| App | target | insert auto | insert clipboard | expand ;firma | dead keys (´a) | notas |
|---|---|---|---|---|---|---|
| Notepad (Win11) | ✅ | ❌ SendInput destroza texto corto (`;fecha`+espacio → `6`) | ✅ | ✅ | | clipboard ~270 ms |
| Word | ✅ | ✅ | ✅ | ✅ | | |
| Outlook | | | | | | |
| Chrome / Edge | | | | | | |
| Firefox | ✅ | | ✅ | ✅ | | |
| VS Code | | | | | | autocompletado vs. retrocesos |
| Visual Studio | | | | | | |
| Teams / Slack / WhatsApp Web | | | | | | lectura async del portapapeles |
| Windows Terminal / CMD | ✅ DENY (captura OFF) | | | | | |
| App elevada | PermissionDenied esperado | | | | | |

## Requisitos UX para V0.1 (feedback del usuario 2026-10-02)

- **Menú de grupo final (WinUI 3):** más limpio y moderno que el prototipo WinForms; esquinas redondeadas, gradiente sutil, Fluent (Mica/Acrylic), modo claro/oscuro. Mantener: no activable, números 1-9, notas ⓘ no seleccionables, breadcrumb de submenús.
- **Sonido al expandir:** chime breve propio (`ChimeSynth`, ~95 ms, dos notas E6→A6, ≈ -13 dBFS), asíncrono vía `PlaySound` en memoria; en ajustes: activar/desactivar y **volumen 0-100 %** (`ExpansionSound.Volume`, independiente del mezclador de Windows). No se copia el sonido de aText.
- **Caret en navegadores:** Firefox/Chrome no exponen caret Win32 → buscar UIA `TextPattern` para anclar el menú al cursor de texto en vez del ratón.

## Pendientes técnicos detectados

1. `EVENT_OBJECT_FOCUS` para reevaluar la política al cambiar de control dentro de la misma ventana (campos de contraseña en navegador).
2. Watchdog del hook: detectar si Windows lo retiró (input reciente sin callbacks) y reinstalarlo.
3. `{{clipboard}}` y `{{selection}}` reales (lectura en el hilo del portapapeles; selección vía UIA TextPattern antes de recurrir a Ctrl+C).
4. Deshacer: decidir entre `Ctrl+Z` al destino o retroceso de N caracteres (medir por app).
5. Adaptación de mayúsculas (`;Firma` → `Saludos…`); `IgnoreCase` ya existe.
7. Formato aText inferido de un backup real (claves numéricas, LZ4 frame). "Ignorar mayúsculas" es ajuste global de aText (no por grupo); la clave `8` sigue sin identificar.
8. Comportamiento aText confirmado por el usuario (2026-10-02), requisitos de S7:
   - abreviatura duplicada (`OD`, `FP`) → un solo menú que lista ambos grupos; se entra con clic o flechas+Enter;
   - submenús multinivel (`CROSS` → Reasignación → No gestionable → …);
   - snippets vacíos = notas informativas visibles en el menú, no insertan nada;
   - teclado: flechas, Enter, Esc cierra; números para elegir rápido (mejora sobre aText).
6. Volcados WER sin heap (ADR-0006).

## Siguiente orden recomendado
Resto de matriz S3 → S7 menú de grupo → importador completo S6 → S5 benchmark → cerrar Fase 0 con informe de riesgos → V0.1.
