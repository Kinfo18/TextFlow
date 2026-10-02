# ADR-0006 — Contenido fuera del historial del portapapeles, logs y volcados

- Estado: Aceptado (2026-10-01)
- Refuerza: spec V2 §3.3, §15, §28

## Decisión
- Todo texto que TextFlow pone en el portapapeles (y la restauración del contenido original) lleva `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory=0` y `CanUploadToCloudClipboard=0`: no aparece en Win+V ni en el portapapeles en la nube.
- Snapshots del portapapeles se borran (`Array.Clear`) tras usarse.
- El buffer de triggers está acotado (64 caracteres), se vacía al cambiar foco/clic/navegación y **no se llena** en apps excluidas (fail closed hasta evaluar la política).
- Pendiente (V1): desactivar volcados completos de WER para TextFlow (`LocalDumps` → `DumpType=1` minidump sin heap) y test automatizado que verifique que logs/diagnóstico no contienen payload.
