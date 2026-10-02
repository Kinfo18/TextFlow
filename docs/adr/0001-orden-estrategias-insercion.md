# ADR-0001 — Orden de estrategias de inserción

- Estado: Aceptado (2026-10-01)
- Reemplaza: spec V2 §9 "Estrategias" (orden UIA → Clipboard → SendInput)

## Contexto
UIA `ValuePattern.SetValue` reemplaza el valor completo del control (destruye el contenido existente y el undo nativo). UIA casi nunca permite insertar en el caret.

## Decisión
1. **SendInput Unicode** para textos de una línea ≤ 32 caracteres (no toca el portapapeles).
2. **Clipboard transaccional** para el resto (y fallback del anterior).
3. **UIA solo lectura**: metadatos del control (tipo, password, read-only), validación.
4. TSF sigue como spike experimental.

Fallback a otra estrategia **solo si ninguna tecla llegó al destino** (`InsertionResult.InputSent == false`); si no, se duplicaría texto. El destino se revalida antes de cada intento.

## Consecuencias
- Clipboard: snapshot de todos los formatos HGLOBAL, restauración salvo que otra app haya cambiado el portapapeles (gana la copia más reciente del usuario → `ClipboardConflict`). Snapshots > 64 MB no se toman → fallback a SendInput.
- `PasteSettleDelay` (250 ms por defecto) debe ajustarse por app en la matriz de compatibilidad.
- Validado por `tests/TextFlow.Infrastructure.Tests/Desktop`.

## Revisión 2026-10-02 (validación manual S3)
- En Notepad de Windows 11, `;fecha` + espacio insertado por SendInput dejó solo `6` (texto `2/10/2026` destrozado); con Enter funcionó, y en Word ambos bien. Clipboard funcionó en Notepad, Firefox y Word en todos los intentos.
- **Cambio:** `InsertionOptions.SendInputMaxLength` pasa a `0` por defecto → **Clipboard primero para todo** (como aText). SendInput queda como fallback (portapapeles bloqueado o snapshot > 64 MB) y como opción por app en la matriz.
- Pendiente: causa raíz de SendInput en Notepad (¿ráfaga Unicode demasiado rápida?) antes de volver a usarlo como preferido.
