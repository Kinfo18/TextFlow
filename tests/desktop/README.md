# Desktop checks

Scripts that drive the real app through keyboard input and UI Automation. They **steal the focus** and type into
Notepad or Outlook, so run them only when nobody is using the PC. Each one prints its own verdict.

Run them with Windows PowerShell 5.1 (`powershell.exe -ExecutionPolicy Bypass -File <script>`). The files are saved
as UTF-8 with BOM: without it, PowerShell 5.1 misreads names such as "Diagnóstico". Close any running TextFlow
first, because these scripts start the build they test (`-Exe <path to TextFlow.exe>`), or they drive the copy
that is already running.

| Script | Checks |
|---|---|
| `menu-delay.ps1 -Exe … -Label …` | Typing «direccion», «gracias» opens no menu; `lc` plus a pause opens it; `lc1` typed from memory inserts option 1. It aborts if Notepad is not in front. |
| `outlook-menu.ps1 -Exe … [-Runs 10]` | Classic Outlook (H5.5/R5): `lc` + `1` in a new mail body, N times. It reports the focused control and the expansions, then discards the drafts. |
| `idle-memory.ps1 -Exe …` | Working set and private memory with the window open, closed, and reopened. |
| `diagnostics-day-switch.ps1 -Exe …` | Changes the day in Diagnóstico 10 times; the app must stay alive. |
| `close-unsaved.ps1` | Closing with an edited command asks first. «Seguir editando» keeps the window; «Descartar» closes it and TextFlow stays in the tray. |
| `a11y-names.ps1` | Lists every visible interactive control without an accessible name, on the four pages. Empty output means it passes. |

For the 1,000-expansion stress run (H6.1), use `dotnet run --project spikes/TextFlow.Spikes -c Release -- stress 1000`.
