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

## Revisión 2026-10-04 (H5.2)
El usuario copia cada dato de otra página. El popup se adapta a ese flujo:
- Mientras está abierto escucha el portapapeles: cada copia llena el campo marcado y pasa al siguiente. Si dos avisos de copia llegan con el mismo texto en menos de 1 s, se toman como una sola copia.
- Si el último campo se llena con una copia, inserta sin Enter. Escribir a mano sigue usando Enter.
- La abreviatura se borra al abrir el popup. Al confirmar, TextFlow trae al frente la ventana original y comprueba que el foco está en el mismo campo: misma ventana, mismo control y mismo id de elemento UIA, porque las pestañas del navegador comparten HWND.
- Si el campo original no tiene el foco (por ejemplo, los datos estaban en otra pestaña), espera hasta 2 min a que el usuario vuelva: el popup lo indica sin robar el foco. Si el usuario no vuelve, el texto va al portapapeles con un aviso. Nunca se escribe en otro sitio.
- Los valores solo viven en memoria. El diagnóstico registra únicamente el número de campos.
