# Fase 0 — Risk Spikes: plan y estado

Decisiones de la sesión 2026-10-01: ver `docs/adr/0001`–`0006`. Revisión 2026-10-02: ADR-0001 (clipboard primero para todo).

| # | Spike | Estado | Evidencia |
|---|---|---|---|
| S0 | Andamiaje .NET 10 + tests | ✅ | `TextFlow.sln`, 180 tests Core + 5 de escritorio |
| S1 | Target Resolver multi-monitor | ✅ base | `Win32TargetResolver` + `UiaFocusedControlInspector`; captura 54 ms frío / 2 ms caliente |
| S2 | Inserción clipboard + SendInput | ✅ base | `ClipboardStrategy`, `SendInputStrategy`, `InsertionCoordinator`; 5 tests de escritorio reales |
| S3 | Hook de teclado + triggers | ✅ matriz completa (Notepad, Word, Firefox, Chrome, VS Code, Outlook; mensajería/elevada N/A) | expansión **instantánea** por defecto (`TriggerMode.Immediate`, estilo aText) + `IgnoreCase` por trigger; SendInput falló en Notepad → clipboard primero |
| S4 | Template Engine | ✅ | `TemplateParser`/`TemplateRenderer`, 22 tests |
| S5 | Benchmark ASR (whisper.cpp vs Parakeet) | ✅ | **ADR-0007**: whisper.cpp large-v3-turbo q5_0 CUDA (WER 0,9 %, ≈0,55 s/frase caliente, ≈950 MiB VRAM); fallback Parakeet v3 CPU (6,4 %). `spikes/asr/bench.py` |
| S6 | Importador aText (Windows) | ✅ | `ATextBackupReader` + `LibraryIndex` sobre el backup real: 51 grupos, 34 menús, 293 comandos directos (abreviaturas con espacios incluidas; 14 > 64 car. quedan solo como etiqueta). `spikes import <archivo>` |
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
| Outlook | ✅ | | ✅ | 🟡 | | 2/3: un intento bloqueado (TargetChanged: Outlook movió su foco interno con el menú abierto); no insertó nada (correcto) |
| Chrome / Edge | ✅ | | ✅ | ✅ | | Chrome: menú LC OK; ancla ratón |
| Firefox | ✅ | | ✅ | ✅ | | 2026-10-02: 12/12 expansiones (cc, s1, rr, cp1/cp2 desde menú CP abierto, "foto valida", menús LC/HU); chime 12/12 |
| VS Code | ✅ | | ✅ | ✅ | | 7/7 menú LC; ancla ratón; sin interferencia de autocompletado |
| Visual Studio | | | | | | |
| Teams / Slack / WhatsApp Web | N/A | N/A | N/A | N/A | N/A | el usuario no las usa (2026-10-02) |
| Windows Terminal / CMD | ✅ DENY (captura OFF) | | | | | |
| App elevada | N/A | N/A | N/A | N/A | N/A | no aplicable para el usuario; política fail-closed cubierta por tests (rufus.exe elevado → captura OFF en logs) |

## Requisitos UX para V0.1 (feedback del usuario 2026-10-02)

- **Menú de grupo final (WinUI 3):** más limpio y moderno que el prototipo WinForms; esquinas redondeadas, gradiente sutil, Fluent (Mica/Acrylic), modo claro/oscuro. Mantener: no activable, números 1-9, notas ⓘ no seleccionables, breadcrumb de submenús.
- **Sonido al expandir:** chime breve propio (`ChimeSynth`, ~95 ms, dos notas E6→A6, ≈ -13 dBFS), asíncrono vía `PlaySound` en memoria; en ajustes: activar/desactivar y **volumen 0-100 %** (`ExpansionSound.Volume`, independiente del mezclador de Windows). No se copia el sonido de aText.
- **Caret en navegadores:** Firefox/Chrome no exponen caret Win32 → buscar UIA `TextPattern` para anclar el menú al cursor de texto en vez del ratón.

## Reglas de triggers confirmadas con el usuario (2026-10-02)

- **Abreviaturas de snippet** (campo `1` de aText) son triggers directos, **incluidas las que tienen espacios** ("Foto valida", "No redelivery"), como en aText. Las de grupo abren menú.
- **Prefijos ambiguos** (17 en la biblioteca del usuario: `cp`/`cp1`, `dir`/`dir1`, `g`/`gracias1`…):
  - si el corto es un **menú**, se abre **al instante**; una tecla que continúa el trigger largo (`1` → `cp1`) cierra el menú y expande el largo (tiene prioridad sobre el número 1-9 del menú);
  - si el corto es un **snippet**, espera 600 ms o la siguiente tecla;
  - si tras el corto ya se escribió algo más ("foto " esperando "foto valida"), se descarta: nunca se borra texto que no sea el trigger.
- **Sonido**: el chime lleva 120 ms de ruido inaudible delante (≈ -72 dBFS) para despertar el audio del portátil, que se dormía y se comía el sonido de 95 ms.

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

## Cierre
Fase 0 cerrada el 2026-10-02: ver [`fase0-informe.md`](fase0-informe.md). Siguiente: [`v0.1-plan.md`](v0.1-plan.md), empezando por H0.1 (popup WinUI 3 no activable).
