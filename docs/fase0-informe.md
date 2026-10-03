# Fase 0 — Informe de cierre y riesgos

- Fecha: 2026-10-02
- Commits: `1c01b37` → `0a7d6b3` → `a84aa23` (github.com/Kinfo18/TextFlow, rama `main`)
- Estado: **Fase 0 cerrada.** Los 8 spikes (S0–S7) están validados en el equipo real del usuario.
- Detalle por spike y matriz de compatibilidad: [`fase0-plan.md`](fase0-plan.md). Decisiones: [`adr/0001`–`0007`](adr/).

## 1. Qué se validó

| Riesgo de la spec (§30) | Spike | Resultado | Evidencia |
|---|---|---|---|
| Inserción en destino equivocado (crítico) | S1, S2, S7 | ✅ Revalidación del destino antes de cada estrategia. En Outlook bloqueó correctamente 1 de 3 intentos (`TargetChanged`) sin insertar nada | `InsertionCoordinator`, log de matriz |
| Incompatibilidad entre aplicaciones (crítico) | S2, S3 | ✅ Notepad, Word, Firefox, Chrome, VS Code y Outlook, todas por portapapeles (≈265 ms). SendInput destrozó texto en Notepad Win11, así que ahora va **portapapeles primero** | ADR-0001 rev. 2026-10-02 |
| Trigger falso/accidental (alto) | S3 | 🟡 Funciona la expansión instantánea estilo aText con 293 comandos y 34 menús. Los prefijos ambiguos (17) se resuelven sin retraso para los menús. Riesgo abierto, ver §2 | `TriggerMatcher`, 180 tests |
| Latencia ASR (alto) | S5 | ✅ whisper turbo q5 CUDA: WER 0,9 %, ≈0,55 s por frase en caliente | ADR-0007 |
| Consumo RAM/VRAM (medio/alto) | S5 | 🟡 ≈950 MiB de VRAM con el modelo residente. El LLM de V0.3 no cabe a la vez | ADR-0005/0007 |
| Aplicaciones elevadas/UAC (alto) | S1 | ✅ Fail-closed: captura OFF en apps elevadas (rufus.exe en los logs). Insertar en ellas queda para V1 (`uiAccess`) | ADR-0004 |
| Privacidad accidental en logs (crítico) | todos | ✅ Ningún log contiene texto escrito ni contenido de snippets (solo nombres e IDs). Portapapeles fuera de Win+V y de la nube | ADR-0006 |
| Importación de aText | S6 | ✅ Lee el backup real (formato inferido: JSON + LZ4). Abreviaturas de grupo → menús; abreviaturas de snippet, incluidas las que llevan espacios → comandos | `ATextBackupReader`, `LibraryIndex` |
| Menú de grupo sin robar foco | S7 | ✅ Ventana no activable + teclas encaminadas por el hook: el destino nunca pierde el caret | `GroupMenuPopup`, `KeyboardHook.MenuMode` |
| Feedback sonoro | S7 | ✅ Chime propio con volumen. Validado 12/12 tras añadir 120 ms de lead-in inaudible | `ChimeSynth`, `ExpansionSound` |

## 2. Riesgos abiertos (entran en V0.1)

| # | Riesgo | Nivel | Qué sabemos | Mitigación prevista |
|---|---|---|---|---|
| R1 | **Popup no activable en WinUI 3.** El prototipo usa WinForms; no está probado que una ventana WinUI 3 pueda mostrarse sin activarse | **Alto** | Una ventana WinUI 3 es un HWND normal, así que `WS_EX_NOACTIVATE` vía interop *debería* funcionar | Mini-spike al inicio de V0.1 (H0). Fallback: mantener el popup en Win32/WinForms con estilo Fluent dibujado |
| R2 | **Disparos accidentales.** 293 comandos sin distinguir mayúsculas, algunos frases comunes ("no ingresa", "lleva solo") | Alto | Mismo comportamiento que aText; el usuario lo acepta | Métrica *Trigger False Positive* en diagnóstico, interruptor por snippet/grupo, pausa global por hotkey |
| R3 | **Windows retira el hook** si el callback supera `LowLevelHooksTimeout` | Alto | Callback máx. medido ≈1 ms; no ha ocurrido | Watchdog que detecta "input sin callbacks" y reinstala (pendiente técnico 2) |
| R4 | **Campos de contraseña dentro de la misma ventana** (navegador): la política solo se reevalúa al cambiar de ventana | Alto | `EVENT_OBJECT_FOCUS` no está implementado | Reevaluar la política en cada cambio de foco (pendiente técnico 1) |
| R5 | **Outlook mueve su foco interno** con el menú abierto → `TargetChanged` | Medio | Es seguro (no inserta), pero molesta | Investigar qué handle cambia; validar por ventana raíz + control UIA en lugar de HWND de foco |
| R6 | **Formato aText inferido**, no documentado | Medio | Clave `8` desconocida; rich text (`h`) se importa como texto plano (2 snippets) | Informe de incidencias del importador; claves desconocidas siempre reportadas |
| R7 | **Ancla del menú en navegadores/VS Code**: a veces cae en la posición del ratón (sin caret Win32) | Bajo | El usuario lo considera aceptable | UIA `TextPattern` para el caret real (pendiente técnico) |
| R8 | **Memoria del equipo** (16 GB): Claude Code mató un proceso de fondo por presión de memoria | Medio | No era culpa de TextFlow, pero el equipo va justo | Presupuesto de RAM de TextFlow en reposo < 150 MB, medido en diagnóstico |
| R9 | **Causa raíz de SendInput en Notepad** desconocida | Bajo | Mitigado al ir portapapeles primero | Solo si se reactiva SendInput como preferido |
| R10 | **Corpus ASR pequeño** (9 frases, 1 hablante) | Medio (V0.2) | Suficiente para elegir motor, no para garantizar calidad | Repetir con más frases y ruido antes de V0.2 |

## 3. Cambios de alcance respecto a la spec V2

Decididos con el usuario durante la Fase 0:

1. **Expansión instantánea por defecto** (sin espacio/Enter), como aText. El modo "tras delimitador" queda como opción por trigger.
2. **Menús de grupo** (abreviatura de grupo → menú con submenús, números 1-9, notas ⓘ). La spec §1 decía "sin menú intermedio"; ahora es una funcionalidad central para el usuario.
3. **Abreviaturas con espacios** permitidas en triggers instantáneos.
4. **Mayúsculas configurables por grupo** (heredadas por sus snippets).
5. **Sonido al expandir** con volumen ajustable.
6. **Portapapeles primero** para cualquier longitud de texto.

Estos cambios están en los ADR y en `fase0-plan.md`. La spec V2 se actualizará al empezar V0.1 (tarea H0.4).

## 4. Métricas de referencia (línea base para V0.1)

| Métrica (spec §27) | Fase 0 |
|---|---|
| Insertion Success Rate | 100 % de los intentos que pasaron la validación (≈30 en 6 apps) |
| Wrong Target Rate | 0 (1 bloqueo correcto en Outlook) |
| Latencia de inserción | ≈265 ms (portapapeles, incluye restauración) |
| Callback del hook | máx. ≈1 ms |
| ASR caliente p50 | ≈0,55 s/frase (turbo CUDA) |
| Tests automatizados | 180 Core + 5 de escritorio |

## 5. Conclusión

Los dos riesgos críticos, insertar en el destino equivocado y la compatibilidad entre apps, están mitigados con evidencia real. El resto de la arquitectura tiene base en código probado. **Se puede iniciar V0.1.** La primera tarea es cerrar R1 (popup no activable en WinUI 3), porque condiciona el diseño final del menú.
