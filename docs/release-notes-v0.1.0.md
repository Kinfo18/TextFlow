# TextFlow v0.1.0 (borrador)

> Borrador para H6.4. Se publica cuando la lista de [incidencias](v0.1-incidencias.md) esté cerrada o aceptada.

Primera versión para uso diario: sustituye a aText en la jornada de trabajo. Funciona sin red y nunca guarda lo que escribes; los registros de diagnóstico no tienen contenido.

## Qué incluye

- **Expansión instantánea** de abreviaturas, sin delimitador, como en aText. Las teclas muertas y AltGr se respetan, y las abreviaturas pueden tener espacios (hasta 255 caracteres).
- **Menús de grupo.** Escribir la abreviatura de un grupo (`LC` o `lc`) abre su menú junto al cursor, también en Chrome y Edge. Se elige con números, flechas o el ratón, sin robar el foco. El menú aparece tras una pausa de 250 ms: si sigues escribiendo una palabra que empieza igual («dirección»), no se abre. `lc1` tecleado de memoria funciona igual.
- **Biblioteca.** Incluye:
  - importación del backup de aText, que sigue el archivo si cambia;
  - editor de snippets y grupos con búsqueda;
  - avisos de abreviaturas en conflicto;
  - exportación e importación de copias de TextFlow;
  - copia automática antes de cada importación.
- **Plantillas con campos.** `{{cliente}}` y similares se rellenan copiando cada valor, y el texto se inserta solo tras la última copia. También hay `{{clipboard}}`, `{{date:…}}` y `{{cursor}}`, y un asistente que convierte los antiguos `XXX` en campos. Cada snippet puede pulsar Enter al terminar, para chats.
- **Inserción segura.** Valida el destino antes de escribir, excluye apps por nombre y nunca escribe en campos de contraseña. Restaura el portapapeles.
- **Bandeja.** Pausa (atajo configurable, por defecto `Ctrl+Shift+Alt+P`), inicio con Windows (activado por defecto), sonido opcional con volumen y tema claro, oscuro o del sistema.
- **Diagnóstico.** Métricas del día sin contenido: expansiones, % insertadas bien, tiempos, programas, menús, estabilidad y memoria. Se pueden consultar los 7 últimos días.
- **Ligera en la bandeja.** Al cerrar la ventana se libera su memoria: unos 10–40 MB en uso mientras está en la bandeja.
- **Robustez.** El hook de teclado se reinstala solo si Windows lo retira, y los fallos se registran sin detener el motor.
- **Accesible.** Los controles de la ventana principal tienen nombre para lectores de pantalla.

## Instalación

1. Descarga `TextFlowApp-win-Setup.exe` de la [última versión](https://github.com/Kinfo18/TextFlow/releases) y ábrelo. Se instala para tu usuario, sin permisos de administrador, en `%LOCALAPPDATA%\TextFlowApp`.
2. TextFlow se actualiza solo: busca versiones nuevas en segundo plano y avisa en la bandeja. También puedes buscarlas en Configuración → Acerca de TextFlow.
3. Si prefieres no instalar nada, `TextFlowApp-win-Portable.zip` funciona descomprimido en cualquier carpeta, pero no se actualiza solo.
4. Para llevar tu biblioteca de un PC a otro, usa Inicio → Más → Exportar en el PC de origen e Importar en el de destino. Los ajustes no viajan con la biblioteca.

Requisitos: Windows 11 de 64 bits. No necesita instalar .NET ni el Windows App SDK.

## Limitaciones conocidas

- No inserta en apps que se ejecutan como administrador (llegará en V1, con `uiAccess`).
- El texto enriquecido de aText se importa como texto plano.
- Para deshacer una expansión se usa el `Ctrl+Z` de cada app.
- El instalador no está firmado: Windows SmartScreen puede pedir confirmación la primera vez.
