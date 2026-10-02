# TextFlow

Capa de entrada de texto local para Windows 11: expansión de snippets, dictado local e inserción segura en el destino correcto.

- Especificación: [`docs/textflow_especificacion_v2.md`](docs/textflow_especificacion_v2.md)
- Decisiones: [`docs/adr/`](docs/adr)
- Estado actual: [`docs/fase0-plan.md`](docs/fase0-plan.md)

## Requisitos
Windows 11 24H2+, .NET SDK 10.

## Build y tests
```powershell
dotnet build
dotnet test tests/TextFlow.Core.Tests                 # lógica pura
dotnet test tests/TextFlow.Infrastructure.Tests       # abre ventanas e inyecta teclado: no tocar el PC durante ~3 s
```

## Estructura
```
src/TextFlow.Contracts        tipos de dominio e interfaces (sin dependencias de Windows)
src/TextFlow.Core             triggers, plantillas, política de seguridad, coordinador de inserción
src/TextFlow.Infrastructure   Win32/UIA: target resolver, portapapeles, SendInput, hook de teclado
spikes/TextFlow.Spikes        CLI de pruebas manuales de la Fase 0
tests/                        xUnit
```
