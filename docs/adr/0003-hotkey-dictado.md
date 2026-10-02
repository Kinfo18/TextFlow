# ADR-0003 — Hotkey de dictado configurable

- Estado: Propuesto (validar en spike S5)
- Reemplaza: spec V2 default `Ctrl+Shift+D`

## Contexto
`Ctrl+Shift+D` colisiona con VS Code (Debug), Chrome/Edge (marcar todas las pestañas) y Word (doble subrayado). `RegisterHotKey` no notifica la liberación, así que hold-to-talk exige el hook LL igualmente.

## Decisión
Hotkey configurable, implementado sobre el hook LL. Default candidato: **mantener Ctrl derecho** (≥150 ms sin otra tecla). Alternativas configurables: F-key, combinación arbitraria. La tecla de activación se suprime para que el destino no la reciba.

## A validar
Teclados de portátil sin Ctrl derecho; interacción con AltGr (que en layout ES genera LCtrl+RAlt).
