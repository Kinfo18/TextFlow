# ADR-0002 — Campos de plantilla en popup

- Estado: Aceptado (2026-10-01)
- Precisa: spec V2 §11

## Contexto
Navegar campos con Tab *dentro* de la app destino no es viable de forma general: Tab inserta tabulador o mueve el foco según la app.

## Decisión
Si la plantilla tiene campos (`{{cliente}}`, `{{empresa=ACME}}`), TextFlow captura el destino (target lock), abre un popup junto al caret con los campos (Tab/Shift+Tab, Enter confirma, Esc cancela), renderiza y **inserta el texto final de una vez** en el destino bloqueado.

## Consecuencias
- El popup sí toma el foco (excepción explícita a "el ORB nunca roba foco"); al cerrarse se revalida el destino antes de insertar.
- Sintaxis: `{{nombre}}`, `{{nombre=valor por defecto}}`, `{{date:formato}}`, `{{cursor}}`, `\{{` escapa. Placeholders mal formados se conservan literalmente.
