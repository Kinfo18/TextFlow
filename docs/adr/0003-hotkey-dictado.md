# ADR-0003 — Hotkey de dictado configurable

- Estado: Aceptado (2026-10-16, H0.4 de [`v0.2-plan.md`](../v0.2-plan.md)); propuesto originalmente para el spike S5
- Reemplaza: spec V2 default `Ctrl+Shift+D`

## Contexto
`Ctrl+Shift+D` colisiona con VS Code (Debug), Chrome/Edge (marcar todas las pestañas) y Word (doble subrayado). `RegisterHotKey` no notifica la liberación, así que hold-to-talk exige el hook LL igualmente.

La propuesta inicial: mantener Ctrl derecho ≥ 150 ms sin otra tecla y suprimir la tecla. Quedaba por validar: teclados
de portátil sin Ctrl derecho e interacción con AltGr (en layout ES genera LCtrl+RAlt).

## Spike (2026-10-16)
`TextFlow.Core.Input.HoldToTalk` (máquina de estados pura, 15 tests) y la sonda `spikes/TextFlow.Spikes keys
[segundos] [rctrl|lctrl|f1..f24|0xVK] [ms]`: hook LL de teclado y ratón que imprime lo que ve el hook (letras
anónimas) y lo que decidiría el detector. Teclado del usuario, layout español.

| Prueba | Hook | Decisión |
|---|---|---|
| Ctrl derecho | VK `0xA3`, scan `0x01D` extendido | — |
| Ctrl izquierdo mantenido | VK `0xA2`, scan `0x01D` | ignorado ✔ |
| AltGr + 2 / AltGr + Q | Ctrl izq **falso** (scan `0x21D`) + Alt der (`0x038` ext.) a 1–2 ms | nunca dicta ✔ |
| Mantener 1,3 / 4,6 / 5 s | auto-repetición a los 500 ms, cada ~32 ms | empieza a los 150 ms, termina al soltar ✔ |
| Toque de 86 ms | — | no empieza ✔ |
| «Toque corto» de 415–418 ms | — | empieza y se **descarta** ✔ |
| Ctrl der + C: 16 / 107 ms entre teclas | — | atajo, no empieza ✔ |
| Ctrl der + C: 194 / 195 / 346 / 822 ms | — | empieza y se **cancela**, no inserta ✔ |
| Mantener y pulsar otra tecla | — | cancelado ✔ |

Hallazgos:
1. **AltGr no es Ctrl derecho.** El Ctrl que fabrica AltGr es el izquierdo (VK `0xA2`, scan `0x21D`); escuchar
   VK `0xA3` basta y no hace falta mirar el scan.
2. **Los toques reales duran ~400 ms**, no < 150 ms: el umbral de inicio por sí solo no los filtra.
3. **Un atajo con Ctrl derecho a ritmo normal tarda 100–350 ms** entre Ctrl y la letra (Ctrl izq + C: 183–494 ms):
   4 de 6 llegaron a empezar el dictado antes de cancelarse. Nada se inserta, pero la grabación arranca un instante.
4. **Un Ctrl suelto no hace nada en las apps probadas**, así que no hace falta suprimirlo; suprimirlo rompería
   Ctrl der + C, que llega al hook antes de saber si es atajo.

## Decisión
1. **Por defecto: mantener Ctrl derecho.** La grabación empieza a los **150 ms** sin otra tecla (no se pierde la
   primera sílaba) y termina al soltar.
2. **Se descarta** lo soltado antes de **600 ms** desde la pulsación: es un toque, no una frase.
3. **Otra tecla antes de 150 ms** = atajo, el dictado no empieza. **Otra tecla con el dictado en marcha** (Esc
   incluida) = cancelar sin insertar. Clic de ratón antes de 150 ms (Ctrl+clic) = atajo; después, se ignora.
4. **La tecla no se suprime** (revisa la propuesta inicial): los atajos con Ctrl derecho siguen funcionando.
5. Teclas inyectadas (el Ctrl+V de TextFlow al pegar, con `LLKHF_INJECTED`) se ignoran.
6. **Alternativas configurables** (H2.2): otra tecla para mantener (F1–F24, Ctrl izq, cualquier VK; la sonda ya las
   acepta) y los dos umbrales. Para portátiles sin Ctrl derecho, una F-key o la tecla Copilot/Menú si existe.

## Consecuencias
- El indicador (ORB, H3) no debe aparecer en cuanto empieza la grabación: con 150 ms parpadearía en la mayoría de
  atajos con Ctrl derecho. Mostrarlo al cumplirse los ~400 ms (ajustable en H3) oculta 3 de los 4 casos medidos; el
  audio ya se está grabando desde los 150 ms.
- El icono de micrófono de Windows sí aparecerá un instante en esos atajos: coste aceptado. Quien use mucho Ctrl
  derecho para atajos puede cambiar la tecla.
- Una app que reaccione a un Ctrl suelto (p. ej. «buscar el ratón» de PowerToys con doble Ctrl) lo verá al dictar.
- Sin probar: teclado del portátil por separado y la tecla Copilot. El detector solo depende del VK, así que basta con
  repetir la sonda en cada equipo nuevo (`keys 60`).
