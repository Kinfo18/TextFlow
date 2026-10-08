# TextFlow v0.1.0 (borrador)

> Borrador para H6.4. Se publica cuando la lista de [incidencias](v0.1-incidencias.md) esté cerrada o aceptada.

Primera versión para uso diario: sustituye a aText en la jornada de trabajo. Funciona sin red y nunca guarda lo que escribes; los logs no tienen contenido.

## Qué incluye

- **Expansión instantánea** de abreviaturas, sin delimitador, como en aText. Las teclas muertas y AltGr se respetan.
- **Menús de grupo:** escribir la abreviatura de un grupo (`LC` o `lc`) abre su menú junto al cursor, también en Chrome y Edge. Se elige con números, flechas o el ratón, sin robar el foco.
- **Biblioteca:** importación del backup de aText (sigue el archivo si cambia), editor de snippets y grupos con búsqueda, avisos de abreviaturas en conflicto, y exportación e importación de copias de TextFlow.
- **Plantillas con campos:** `{{cliente}}` y similares se rellenan copiando cada valor; el texto se inserta solo tras la última copia. Hay un asistente que convierte los antiguos `XXX` en campos.
- **Inserción segura:** valida el destino antes de escribir, excluye apps por nombre y nunca escribe en campos de contraseña. Restaura el portapapeles.
- **Bandeja:** pausa (atajo configurable, por defecto `Ctrl+Shift+Alt+P`), inicio con Windows, sonido opcional con volumen, y tema claro, oscuro o del sistema.
- **Robustez:** el hook de teclado se reinstala solo si Windows lo retira. Los fallos se registran en el log sin detener el motor.

## Instalación

1. Descarga `TextFlow-v0.1.0-portable.zip` y descomprímelo en una carpeta tuya, por ejemplo `C:\Users\<tú>\Apps\TextFlow`.
2. Abre `TextFlow.exe`. No necesita instalar .NET ni el Windows App SDK.
3. Para llevar tu biblioteca de un PC a otro, usa Inicio → Más → Exportar en el PC de origen e Importar en el de destino. Los ajustes no viajan con la biblioteca.

Requisitos: Windows 11 de 64 bits.

## Limitaciones conocidas

- No inserta en apps que se ejecutan como administrador (llegará en V1, con `uiAccess`).
- El texto enriquecido de aText se importa como texto plano.
- Para deshacer una expansión se usa el `Ctrl+Z` de cada app.
