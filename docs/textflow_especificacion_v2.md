# TextFlow — Especificación de producto y arquitectura V2

> Aplicación Windows 11 para expansión de texto, plantillas con campos, dictado de voz local y transformación de texto asistida por IA. Diseñada para uso personal y pruebas controladas con terceros, con privacidad local por defecto y especial atención a la inserción correcta en configuraciones multi-monitor.

## 1. Estado del proyecto

### Decisiones cerradas

- Alcance de plataforma: únicamente Windows.
- Sistema mínimo: Windows 11 24H2 (build 26100).
- Público inicial: uso personal + beta cerrada con otros usuarios.
- Expansión principal: trigger reconocido sin menú intermedio.
- Ejemplo: `;firma` → expansión de la plantilla al confirmar el delimitador configurado.
- Campos de plantilla: editables y navegables con `Tab`.
- Dictado: `Ctrl+Shift+D` pulsado y mantenido para hablar; al soltar se finaliza la sesión.
- Objetivo de inserción: siempre la ventana/control capturado al iniciar el dictado.
- Texto parcial: únicamente en el ORB; no se escribe progresivamente en la aplicación destino.
- Corrección predeterminada: modo Profesional.
- El modo Profesional puede modificar estilo y redacción, pero nunca debe alterar hechos, entidades protegidas o datos.
- Exclusiones automáticas: campos de contraseña, aplicaciones bancarias/sensibles, terminales, escritorio seguro/UAC y otras superficies de alto riesgo.
- Exclusiones personalizadas: configurables por proceso, aplicación y reglas adicionales que se incorporen más adelante.
- No se almacena contenido del usuario de forma persistente.
- El audio tampoco se almacena por defecto.
- Deshacer utiliza estado únicamente en memoria y se pierde al cerrar la aplicación.
- Historial persistente de transcripciones/expansiones: eliminado del producto base.
- Scripts: previstos como extensión futura; fuera del MVP.
- Importación desde aText: prioridad temprana, antes de la beta externa.

## 2. Propuesta de producto

### Visión

Crear una capa de entrada de texto local para Windows 11 que permita convertir dos acciones habituales en texto útil sin abandonar la aplicación actual:

```text
ESCRIBIR  →  EXPANDIR
HABLAR    →  REDACTAR
```

### Propuesta de valor

1. Expansión de texto rápida mediante triggers, plantillas y variables.
2. Dictado local en español con baja latencia.
3. Transformación profesional opcional sin enviar texto a la nube.
4. Inserción segura en la ventana y control correctos, incluso con múltiples monitores.
5. Interfaz moderna de Windows 11 que permanece invisible hasta que aporta información.
6. Sin historial persistente de contenido: lo escrito y dictado no se convierte en una base de datos local.

### Diferenciador real

TextFlow no debe posicionarse únicamente como otro text expander. El eje técnico y de experiencia debe ser:

**entrada de texto local + dictado local + inserción contextual segura.**

## 3. Principios de diseño

### 3.1 Invisible por defecto

El motor permanece residente. La ventana principal no es necesaria para utilizar las funciones cotidianas.

Superficies, en orden de importancia:

```text
Tray → ORB → Command Palette → Ventana principal
```

### 3.2 Seguridad antes que automatismo

Si TextFlow no está seguro del destino, debe cancelar o pedir intervención. Una inserción incorrecta es peor que una inserción fallida.

### 3.3 Cero contenido persistente

El producto puede mantener configuración y snippets, pero no debe persistir transcripciones, texto dictado, texto seleccionado, texto de campos o contenido escrito por el usuario.

### 3.4 El foco es una entidad, no una coordenada

Todas las operaciones deben trabajar con un objetivo capturado:

```text
HWND
ProcessId
ProcessName
Control
Monitor
DPI
Timestamp
```

Nunca con una coordenada de pantalla como identidad principal.

### 3.5 IA como capa, no como dependencia

Sin internet y sin LLM, TextFlow debe seguir siendo útil para expansión y dictado.

## 4. Alcance por versiones

### V0.1 — Expansión de texto

Incluye:

- Tray.
- Arranque con Windows.
- Hotkey global configurable.
- Motor de triggers.
- Snippets.
- Grupos y etiquetas.
- Variables básicas.
- Plantillas con campos editables.
- `Tab` / `Shift+Tab` entre campos.
- Perfiles por aplicación.
- Exclusiones de seguridad.
- Inserción multi-estrategia.
- Deshacer de última operación en memoria.
- SQLite para configuración/snippets.
- Importación/exportación propia.
- Importador de aText compatible con el formato realmente disponible.

No incluye:

- LLM.
- Scripts arbitrarios.
- Historial de contenido.
- Texto enriquecido avanzado.
- Sincronización.

### V0.2 — Dictado local

- Captura WASAPI.
- VAD.
- `Ctrl+Shift+D` hold-to-talk.
- ASR local.
- Texto parcial en ORB.
- Captura y bloqueo del destino al iniciar.
- Inserción final en el destino original.
- Métricas de latencia.
- Recuperación automática del worker.

### V0.3 — Redacción profesional

- Normalización determinista.
- Modo Literal.
- Modo Profesional como predeterminado.
- Protección de entidades.
- Corrección/reescritura mediante LLM local opcional.
- Validador de cambios.
- Fallback a texto normalizado si el LLM falla o excede el tiempo.

### V0.4 — Automatización segura

- Abrir URL.
- Abrir carpeta.
- Abrir aplicación.
- Acciones integradas.
- Sistema de permisos.

Scripts y comandos arbitrarios quedan fuera de esta etapa.

### V1.0 — Beta estable / distribución personal

- Instalador firmado.
- Actualización.
- Recuperación de fallos.
- Matriz amplia de compatibilidad.
- Accesibilidad.
- Diagnóstico exportable sin contenido.
- Documentación.
- Pruebas con usuarios externos.

## 5. Arquitectura general

Se evita separar UI y Core en procesos independientes. La aplicación principal y el motor ligero viven en el mismo proceso; el procesamiento pesado de voz vive en un worker aislado.

```text
                         TextFlow.exe
┌────────────────────────────────────────────────────────┐
│ UI / WinUI 3                                           │
│ Tray · ORB · Palette · Settings                        │
│                                                        │
│ Core                                                   │
│ Hotkeys · Profiles · Trigger Engine                    │
│ Template Engine · Target Resolver · Operation Manager  │
│ Security Policy · Diagnostics · Settings               │
│                                                        │
│ Insertion Engine                                       │
│ UIA · Clipboard · SendInput                            │
│ (TSF como spike/adaptador experimental)                │
└───────────────────────┬────────────────────────────────┘
                        │ Named Pipes / local IPC
                        ▼
                 TextFlow.VoiceWorker
┌────────────────────────────────────────────────────────┐
│ WASAPI → VAD → ASR → normalization → optional LLM      │
└────────────────────────────────────────────────────────┘
                        │
                        ▼
               Model files outside SQLite

SQLite:
configuration + snippets + profiles + metadata

NO persistent user-generated text.
```

## 6. Tecnología objetivo

- C#.
- .NET 10 LTS.
- WinUI 3.
- Windows App SDK 2.5.1 estable al iniciar esta versión de la especificación.
- Windows SDK compatible con el target Windows 11 24H2+.
- SQLite.
- WASAPI para captura.
- whisper.cpp como candidato principal de producción.
- faster-whisper para benchmarking y prototipos.
- Named Pipes para IPC local del worker.
- DPAPI para secretos/tokens, cuando existan.

Las interfaces de dominio deben impedir dependencia directa del runtime de ASR o del método concreto de inserción.

## 7. Componentes del sistema

### TextFlow.App

Responsabilidades:

- Shell WinUI 3.
- Tray.
- ORB.
- Command Palette.
- Ventana de configuración.
- Edición de snippets.
- Estados de operación.

No contiene la lógica crítica del hotkey, expansión o inserción.

### TextFlow.Core

Responsabilidades:

- Estado global de la aplicación.
- Hotkeys.
- Trigger Engine.
- Template Engine.
- Profiles.
- Target Resolver.
- Operation Manager.
- Security Policy.
- Coordinación del VoiceWorker.

### TextFlow.Infrastructure

Responsabilidades:

- Win32.
- UI Automation.
- Clipboard.
- SendInput.
- Monitor/DPI.
- Active window/control.
- Persistencia SQLite.
- Arranque con Windows.

### TextFlow.VoiceWorker

Responsabilidades:

- WASAPI.
- Buffer de audio.
- VAD.
- ASR.
- Normalización.
- LLM opcional.
- Carga y liberación del modelo.

El worker puede reiniciarse sin matar el Core.

## 8. Target Resolver

Este módulo captura el destino de una operación y le asigna una identidad estable.

```text
CaptureTarget()
  → Foreground HWND
  → Focused control
  → Process
  → Monitor
  → DPI
  → Application context
```

### Política de dictado

Al presionar `Ctrl+Shift+D`:

1. Capturar destino.
2. Validar exclusiones.
3. Inicializar audio.
4. Comenzar escucha.

Mientras el usuario habla puede mover el ratón o cambiar de ventana.

Al soltar `Ctrl+Shift+D`:

1. Finalizar audio.
2. Obtener texto final.
3. Normalizar/corregir según el modo.
4. Validar el destino original.
5. Insertar exclusivamente en el destino capturado.
6. Registrar solo metadatos de diagnóstico en memoria o almacenamiento sin contenido.

Si el destino original ya no es válido:

```text
NO INSERTAR

El destino original ya no está disponible.

[Reintentar en destino actual]
[Cancelar]
```

La acción elegida debe ser explícita; nunca cambiar silenciosamente al foco actual.

## 9. Inserción de texto

La interfaz será:

```csharp
public interface ITextInsertionService
{
    Task<InsertionResult> InsertAsync(
        InsertionRequest request,
        CancellationToken cancellationToken);
}
```

### Estrategias

El motor selecciona una estrategia mediante capacidades del objetivo:

1. UI Automation.
2. Clipboard transaccional.
3. SendInput.
4. TSF como adaptación experimental, no como requisito del primer MVP.

TSF sigue siendo relevante porque Windows lo define como infraestructura para servicios de texto que pueden obtener y escribir texto en aplicaciones habilitadas para TSF, pero su implementación implica componentes COM y aumenta la complejidad del producto. Por ello debe investigarse como un adaptador especializado y no bloquear el MVP. 

### Resultado de inserción

```text
Success
UnsupportedTarget
TargetChanged
PermissionDenied
ForegroundRequired
ClipboardConflict
Failed
Cancelled
```

### Regla de oro

Nunca asumir que porque una inserción funcionó una vez funcionará en todas las aplicaciones.

## 10. Motor de expansión

### Trigger

Formato inicial recomendado:

```text
;firma
;correo
;saludo
;cliente
```

La expansión no abre una paleta ni requiere confirmación cuando existe una coincidencia única.

El comportamiento por defecto será:

```text
;firma + delimitador
→ borrar trigger
→ insertar plantilla
→ colocar cursor/campos
```

Los delimitadores serán configurables:

- espacio.
- Enter.
- Tab.
- puntuación seleccionada.
- regla personalizada futura.

### Colisiones

El sistema debe detectar antes de guardar:

- triggers duplicados.
- triggers idénticos en el mismo perfil.
- triggers ambiguos entre perfiles.
- triggers demasiado cortos o peligrosos.

## 11. Template Engine

Variables base:

```text
{{date}}
{{time}}
{{clipboard}}
{{selection}}
{{cursor}}
```

Campos:

```text
{{cliente}}
{{pedido}}
{{empresa}}
```

El usuario podrá completar un formulario mínimo y desplazarse entre campos con:

```text
Tab       → siguiente campo
Shift+Tab → campo anterior
Enter     → confirmar cuando corresponda
Esc       → cancelar
```

El sistema debe preservar el texto literal y evitar dejar marcadores residuales.

## 12. Dictado

### Interacción

```text
Presionar y mantener Ctrl+Shift+D
           ↓
         ORB ON
           ↓
        Hablar
           ↓
 texto parcial en ORB
           ↓
Soltar Ctrl+Shift+D
           ↓
  ASR final + procesamiento
           ↓
       inserción
```

### El ORB nunca

- roba foco.
- intercepta clics del usuario.
- escribe texto parcial en la aplicación destino.
- ocupa una posición fija dependiente del monitor físico.

### Estado del ORB

```text
Idle
Listening
Processing
Inserting
Success
Error
```

Estados no dependientes exclusivamente del color.

## 13. Audio y ASR

Pipeline:

```text
WASAPI 16 kHz mono
→ ring buffer
→ VAD
→ chunks
→ ASR parcial
→ ASR final
```

Objetivos iniciales de experimentación:

- feedback visual <100 ms.
- primer parcial ideal <1 s.
- resultado final normalmente <2 s después de terminar de hablar, sujeto al hardware.

Métricas separadas:

```text
Cold start
Warm start
VAD latency
Partial ASR latency
Final ASR latency
Correction latency
Insertion latency
End-to-end latency
RAM
VRAM
CPU
GPU
```

### Gestión del modelo

```text
App inicia
→ modelo descargado de RAM

Primer dictado
→ cargar modelo
→ mantener caliente durante ventana configurable

Inactividad prolongada
→ liberar modelo
```

Debe existir una política de memoria para evitar que la aplicación mantenga el modelo pesado indefinidamente.

## 14. Corrección y transformación

### Modos

#### Literal

Mínima intervención.

Adecuado para:

- código.
- terminal.
- comandos.
- identificadores.
- rutas.
- nombres sensibles.

#### Profesional — predeterminado

Puede:

- corregir ortografía.
- mejorar puntuación.
- corregir gramática.
- reorganizar frases.
- mejorar claridad.
- ajustar tono profesional.
- dividir párrafos.

No puede:

- inventar hechos.
- cambiar números.
- cambiar correos.
- cambiar URLs.
- cambiar nombres propios protegidos.
- cambiar identificadores.
- resumir salvo que exista una función explícita de resumen en una versión futura.

### Pipeline

```text
RAW TEXT
  ↓
Protect entities
  ↓
Deterministic normalization
  ↓
Professional rewrite (optional LLM)
  ↓
Validate protected entities
  ↓
Validate output
  ↓
Insert or fallback
```

### Entidades protegidas

El sistema debe detectar y blindar, como mínimo:

- correos.
- URLs.
- números.
- fechas.
- teléfonos.
- códigos.
- rutas.
- fragmentos de código.
- identificadores.
- menciones y hashtags.

Si una entidad protegida cambia, la respuesta del LLM se rechaza o se restaura el valor original antes de insertar.

## 15. Privacidad

### Persistente

Se puede almacenar:

- snippets.
- grupos.
- etiquetas.
- perfiles.
- configuración.
- preferencias de exclusión.
- referencias técnicas a modelos.
- métricas sin contenido.
- logs técnicos sin texto.

### No persistente

No se almacena:

- audio.
- transcripciones.
- texto corregido.
- texto seleccionado.
- portapapeles capturado para insertar.
- contenido escrito en campos de texto.
- historial de operaciones con contenido.

### Undo

La última operación puede conservar en memoria solo la información técnica indispensable para revertirla durante la sesión. Al cerrar TextFlow se elimina.

## 16. Exclusiones

### Automáticas

TextFlow debe intentar detectar y excluir:

- campos de contraseña.
- gestores de contraseñas.
- superficies bancarias/sensibles conocidas.
- consola/terminal.
- escritorio seguro.
- UAC.
- aplicaciones configuradas como incompatibles.

### Personalizadas

El usuario podrá excluir por:

```text
Proceso (.exe)
Aplicación
Título de ventana
Clase de ventana
Regla futura por dominio/contexto
```

Una exclusión debe poder aplicarse a:

```text
Expansion
Dictation
Correction
All
```

## 17. Perfiles por aplicación

Ejemplo:

```text
Chrome
  Expansion: ON
  Dictation: ON
  Mode: Professional

VS Code
  Expansion: ON
  Dictation: ON
  Mode: Literal

Windows Terminal
  Expansion: OFF
  Dictation: OFF
  Correction: OFF

Outlook
  Expansion: ON
  Dictation: ON
  Mode: Professional
```

El perfil debe resolverse de forma local a partir de la aplicación objetivo.

## 18. Automatización segura

V0.4:

- OpenURL.
- OpenFolder.
- OpenApplication.
- BuiltinAction.

El usuario debe autorizar explícitamente acciones potencialmente sensibles.

Los scripts arbitrarios quedan fuera de las primeras versiones. Cuando se incorporen deberán ejecutarse fuera del proceso principal, con permisos, allowlist, timeout y registro técnico.

## 19. SQLite

La base contiene solo configuración y contenido creado deliberadamente como recurso reutilizable por el usuario.

Esquema conceptual:

```sql
Snippet(
  id TEXT PRIMARY KEY,
  trigger TEXT NOT NULL,
  content TEXT NOT NULL,
  content_type TEXT NOT NULL,
  group_id TEXT,
  enabled INTEGER NOT NULL,
  delimiters_json TEXT,
  app_rules_json TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

Profile(
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  process_rules_json TEXT,
  hotkeys_json TEXT,
  dictation_settings_json TEXT
);

ExclusionRule(
  id TEXT PRIMARY KEY,
  type TEXT NOT NULL,
  pattern TEXT NOT NULL,
  scope TEXT NOT NULL,
  enabled INTEGER NOT NULL
);

AppSetting(
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL
);
```

No crear una tabla `DictationHistory` en esta versión.

## 20. Diagnóstico

El diagnóstico debe servir para responder:

- ¿Qué falló?
- ¿Dónde falló?
- ¿Cuánto tardó?
- ¿Qué estrategia de inserción se intentó?
- ¿Qué aplicación era el destino?
- ¿El modelo estaba frío o caliente?

Ejemplo de evento permitido:

```json
{
  "operation": "dictation",
  "targetProcess": "chrome.exe",
  "insertionStrategy": "clipboard",
  "asrMs": 742,
  "correctionMs": 181,
  "insertionMs": 37,
  "status": "success"
}
```

No debe aparecer el texto dictado dentro del evento.

## 21. UX de error

TextFlow debe preferir errores claros a comportamientos silenciosos.

Casos:

```text
Destino cambió
→ no insertar automáticamente

Aplicación elevada
→ informar limitación

Clipboard cambió durante transacción
→ abortar/restaurar si es seguro

VoiceWorker falló
→ reiniciar worker

Modelo no disponible
→ ofrecer instalación/cambio de modelo
```

### Safe Mode

Ante una condición que comprometa la confianza en el destino:

```text
SAFE MODE

No se insertó texto porque el destino no pudo validarse.
```

## 22. Command Palette

No debe convertirse en launcher general.

Su propósito es operar TextFlow:

```text
> firma
> correo cliente
> iniciar dictado
> pausar expansiones
> activar perfil Literal
> crear snippet
> buscar snippet
> excluir aplicación
```

Atajo inicial:

```text
Ctrl+Alt+Space
```

Configurable.

## 23. Ventana principal

Secciones:

```text
Inicio
Snippets
Dictado
Perfiles
Automatización
Configuración
Diagnóstico
```

No existe una sección de Historial de contenido.

Inicio muestra:

- estado del motor.
- estado del micrófono.
- modelo cargado/no cargado.
- últimas métricas técnicas agregadas.
- snippets recientes.
- accesos rápidos.

## 24. Sistema visual

Referencia:

- Fluent 2.
- WinUI 3.
- Segoe UI Variable.
- Mica en ventana principal.
- Acrylic reservado para overlays pequeños cuando no perjudique rendimiento.
- Tema claro, oscuro y sistema.
- Soporte DPI por monitor.
- Animaciones cortas y opción reducir movimiento.

El ORB debe ser visualmente reconocible sin ser decorativo en exceso.

## 25. Repositorio

```text
textflow/
├─ src/
│  ├─ TextFlow.App/
│  │  ├─ UI/
│  │  ├─ Tray/
│  │  ├─ Orb/
│  │  └─ Palette/
│  ├─ TextFlow.Core/
│  │  ├─ Expansion/
│  │  ├─ Dictation/
│  │  ├─ Targeting/
│  │  ├─ Operations/
│  │  ├─ Profiles/
│  │  └─ Security/
│  ├─ TextFlow.Infrastructure/
│  │  ├─ Storage/
│  │  ├─ Windows/
│  │  ├─ UIAutomation/
│  │  ├─ Clipboard/
│  │  ├─ Input/
│  │  └─ TSF/
│  ├─ TextFlow.VoiceWorker/
│  │  ├─ Audio/
│  │  ├─ VAD/
│  │  ├─ ASR/
│  │  └─ Correction/
│  └─ TextFlow.Contracts/
├─ native/
│  └─ whisper/
├─ tests/
│  ├─ Unit/
│  ├─ Integration/
│  ├─ Compatibility/
│  └─ Stress/
├─ docs/
│  ├─ architecture.md
│  ├─ ux-spec.md
│  ├─ privacy.md
│  ├─ compatibility-matrix.md
│  └─ adr/
├─ assets/
├─ scripts/
├─ TextFlow.sln
├─ README.md
└─ LICENSE
```

## 26. Interfaces principales

```csharp
public interface ITextInsertionService
{
    Task<InsertionResult> InsertAsync(
        InsertionRequest request,
        CancellationToken ct);
}

public interface ITargetResolver
{
    ActiveTarget CaptureTarget();
    TargetValidation ValidateTarget(ActiveTarget target);
}

public interface ITranscriptionEngine
{
    IAsyncEnumerable<TranscriptPartial> TranscribeAsync(
        AudioSession session,
        CancellationToken ct);

    Task<TranscriptFinal> FinalizeAsync(
        AudioSession session,
        CancellationToken ct);
}

public interface ICorrectionEngine
{
    Task<CorrectionResult> ProcessAsync(
        TextPayload payload,
        CorrectionProfile profile,
        CancellationToken ct);
}
```

## 27. Pruebas críticas

### Aplicaciones

- Notepad.
- Word.
- Outlook.
- Chrome.
- Edge.
- Firefox.
- VS Code.
- Visual Studio.
- Windows Terminal.
- PowerShell.
- CMD.
- Git Bash.
- Teams/Slack/WhatsApp Web/Discord cuando estén disponibles para pruebas.

### Situaciones

- Un monitor.
- Dos monitores.
- Monitores con DPI 100/125/150/200.
- Cambio de foco durante dictado.
- Desconexión/reconexión de monitor.
- Suspensión/reactivación.
- Cambio de teclado/idioma.
- Teclas muertas y caracteres acentuados.
- AltGr.
- Texto largo.
- Escritura rápida.
- Aplicación elevada.
- RDP.
- Campos de contraseña.
- Clipboard modificado por otra aplicación.

### Métricas de aceptación

Más importantes que una cifra única de “éxito”:

```text
Insertion Success Rate
Wrong Target Rate
Rollback Success Rate
Trigger False Positive Rate
Trigger Miss Rate
Cold ASR latency p50/p95
Warm ASR latency p50/p95
Correction latency p50/p95
Memory idle
Memory while dictating
```

El objetivo crítico es minimizar `Wrong Target Rate` y hacer que un fallo sea detectable y reversible.

## 28. Criterios de privacidad

La beta no debe salir a pruebas externas hasta poder demostrar:

1. No se guardan transcripciones.
2. No se guarda audio.
3. No se registra contenido en logs.
4. El clipboard temporal se restaura cuando la operación lo permite.
5. Las exclusiones funcionan antes de activar captura o inserción.
6. El diagnóstico exportado no contiene texto del usuario.

## 29. Roadmap revisado

### Fase 0 — Risk Spikes

Antes del desarrollo amplio:

1. Motor de inserción experimental.
2. Target Resolver multi-monitor.
3. PoC WASAPI + VAD + Whisper.
4. Pruebas de cold/warm start.
5. PoC de templates y campos con Tab.
6. Prueba real de importación de aText.

### Fase 1 — V0.1

Construir expansión estable y utilizable diariamente.

### Fase 2 — V0.2

Añadir dictado local con destino bloqueado al inicio.

### Fase 3 — V0.3

Añadir Profesional + protección de entidades + LLM local opcional.

### Fase 4 — Beta cerrada

5–10 usuarios de prueba, hardware y aplicaciones diferentes.

Registrar únicamente métricas técnicas y errores sin contenido.

### Fase 5 — V1

Instalador, actualización, compatibilidad y documentación.

## 30. Riesgos principales

| Riesgo | Nivel | Mitigación |
|---|---|---|
| Inserción en destino equivocado | Crítico | Target lock + validación + Safe Mode |
| Incompatibilidad entre aplicaciones | Crítico | Insertion adapters + matriz de compatibilidad |
| Trigger falso/accidental | Alto | Buffer pequeño + reglas de contexto + exclusiones |
| Latencia ASR | Alto | VAD + worker + modelos configurables + warm cache |
| LLM cambia datos | Alto | entidades protegidas + validación + fallback |
| Consumo RAM/VRAM | Medio/alto | carga diferida + descarga por inactividad |
| Aplicaciones elevadas/UAC | Alto | detección + comportamiento seguro |
| Privacidad accidental en logs | Crítico | logger sin payload + tests automatizados |
| Scripts inseguros | Alto | fuera del MVP + sandbox/allowlist futura |

## 31. Definición de listo para uso diario

TextFlow estará listo para sustituir parcialmente una herramienta como aText cuando:

- pueda importar al menos los snippets relevantes del usuario.
- pueda mantener una sesión de trabajo de jornada completa sin perder objetivos.
- no produzca inserciones silenciosamente en un destino incorrecto.
- permita deshacer la última inserción dentro de la sesión.
- soporte los principales editores, navegadores y herramientas de desarrollo objetivo.
- el dictado en español tenga latencia aceptable en hardware de referencia.
- pueda ejecutar sin red las funciones centrales.
- ninguna transcripción ni audio quede persistido.
- un fallo del VoiceWorker no derribe expansión ni UI.

## 32. Hardware de referencia para benchmarking

El desarrollo debe probarse primero en el equipo real del desarrollador y, posteriormente, en al menos otro equipo con GPU diferente o sin GPU NVIDIA.

El benchmark no debe fijar una única expectativa de latencia sin medir CPU, GPU, RAM, modelo y estado cold/warm.

## 33. Decisiones de arquitectura que no deben romperse sin ADR

Antes de cambiar estas decisiones debe existir un Architecture Decision Record (ADR):

- Target lock al iniciar dictado.
- No persistencia de contenido.
- Worker de voz aislado.
- Insertion Engine por capacidades.
- LLM opcional.
- Scripts fuera del MVP.
- Windows 11 24H2 como mínimo.
- Interfaces para ASR, corrección e inserción.

## 34. Primer sprint ejecutable V2

```text
Día 1–2
→ solución .NET 10
→ WinUI 3
→ tray
→ hotkey

Día 3–4
→ CaptureTarget()
→ HWND + proceso + control + monitor + DPI

Día 5–6
→ Clipboard insertion
→ Undo en memoria
→ prueba Notepad/Chrome/VS Code/Terminal

Día 7
→ UIA adapter
→ matriz inicial

Día 8–9
→ WASAPI
→ VAD

Día 10–12
→ whisper.cpp / benchmark
→ cold/warm

Día 13
→ ORB

Día 14
→ dictado hold-to-talk
→ target lock
→ inserción final

Resultado:
un prototipo que permite medir los dos riesgos fundamentales antes de construir toda la aplicación.
```

## 35. Resultado esperado de la V2

El proyecto deja de ser una aplicación de utilidades grande desde el inicio y pasa a ser un núcleo de entrada de texto con cuatro capas claras:

```text
1. Expandir
2. Dictar
3. Transformar
4. Automatizar
```

Con una regla transversal:

```text
NO INSERTAR SI EL DESTINO NO ES CONFIABLE.
```

Y otra regla de privacidad:

```text
LA CONFIGURACIÓN PERSISTE.
EL CONTENIDO DEL USUARIO NO.
```
