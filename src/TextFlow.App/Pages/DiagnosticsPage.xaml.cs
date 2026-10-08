using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TextFlow.Contracts.Insertion;
using TextFlow.Core.Diagnostics;
using TextFlow.Infrastructure.Diagnostics;

namespace TextFlow.App.Pages;

/// <summary>Diagnóstico (H5.4): one day of content-free metrics from the diagnostic log, plus the memory in use now.</summary>
public sealed partial class DiagnosticsPage : Page, IRefreshable
{
    /// <summary>V0.1 "listo" criteria (plan §Criterios): idle RAM and time to "motor activo".</summary>
    private const double MemoryTargetMb = 150;
    private const double StartupTargetMs = 2000;
    private const double SuccessTarget = 0.99;

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private static readonly Dictionary<InsertionStatus, string> StatusNames = new()
    {
        [InsertionStatus.UnsupportedTarget] = "Destino no compatible",
        [InsertionStatus.TargetChanged] = "El foco cambió antes de insertar",
        [InsertionStatus.PermissionDenied] = "App con permisos de administrador",
        [InsertionStatus.ForegroundRequired] = "La ventana no estaba al frente",
        [InsertionStatus.ClipboardConflict] = "Portapapeles ocupado",
        [InsertionStatus.Failed] = "Error al insertar",
        [InsertionStatus.Cancelled] = "Cancelada",
    };

    private static readonly Dictionary<RejectionReason, string> RejectionNames = new()
    {
        [RejectionReason.NoTarget] = "Sin campo de texto",
        [RejectionReason.WindowChanged] = "Cambió la ventana",
        [RejectionReason.PolicyDenied] = "App excluida o contraseña",
    };

    private static readonly Dictionary<MenuCloseReason, string> MenuCloseNames = new()
    {
        [MenuCloseReason.Chosen] = "elegido",
        [MenuCloseReason.OtherKey] = "seguiste escribiendo",
        [MenuCloseReason.LongerTrigger] = "abreviatura más larga",
        [MenuCloseReason.Escape] = "Esc",
        [MenuCloseReason.ClickOutside] = "clic fuera",
        [MenuCloseReason.FocusChanged] = "cambio de ventana",
    };

    private static readonly Dictionary<FieldsCloseReason, string> FieldsCloseNames = new()
    {
        [FieldsCloseReason.Inserted] = "insertados",
        [FieldsCloseReason.Cancelled] = "cancelados",
        [FieldsCloseReason.TargetLost] = "sin volver al campo",
        [FieldsCloseReason.Replaced] = "reemplazados",
    };

    private readonly DiagnosticFileSink _log = App.Current.Services.GetRequiredService<DiagnosticFileSink>();
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private bool _filling;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Refresh();
    }

    public void Refresh()
    {
        FillDays();
        ShowDay();
    }

    /// <summary>Reloads the metrics of <see cref="_day"/> without touching the day list, so it is safe inside the ComboBox's own SelectionChanged.</summary>
    private void ShowDay()
    {
        ShowMemory();
        try
        {
            Show(DiagnosticSummary.From(_log.Read(_day)));
            ReadError.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReadError.Text = $"No se pudo leer el registro de ese día ({ex.GetType().Name}).";
            ReadError.Visibility = Visibility.Visible;
        }
    }

    private void FillDays()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var days = _log.Days().Append(today).Distinct().OrderDescending().ToArray();
        _filling = true;
        DayChoice.Items.Clear();
        foreach (var day in days)
        {
            DayChoice.Items.Add(new ComboBoxItem { Content = DayName(day, today), Tag = day });
        }

        DayChoice.SelectedIndex = Math.Max(0, Array.IndexOf(days, _day));
        _filling = false;
    }

    private static string DayName(DateOnly day, DateOnly today)
    {
        var date = day.ToString("ddd d MMM", Es);
        return day == today ? $"Hoy · {date}" : day == today.AddDays(-1) ? $"Ayer · {date}" : date;
    }

    private void ShowMemory()
    {
        using var process = Process.GetCurrentProcess();
        var mb = process.WorkingSet64 / (1024.0 * 1024.0);
        MemoryValue.Text = $"{mb:0} MB";
        SetValueBrush(MemoryValue, mb <= MemoryTargetMb ? null : "SystemFillColorCautionBrush");
        MemoryLabel.Text = $"Memoria ahora (objetivo < {MemoryTargetMb:0} MB)";
    }

    private void Show(DiagnosticSummary summary)
    {
        ExpansionsValue.Text = summary.Expansions.ToString("N0", Es);
        SuccessValue.Text = summary.SuccessRate is { } rate ? rate.ToString("P1", Es) : "—";
        SetValueBrush(SuccessValue, summary.SuccessRate is { } r && r < SuccessTarget ? "SystemFillColorCautionBrush" : null);
        LatencyValue.Text = Ms(summary.MedianMs);

        Fill(InsertionLines,
        [
            Line("Lentas (95 %)", Ms(summary.P95Ms)),
            Line("La más lenta", Ms(summary.MaxMs)),
            .. summary.Failures.Count == 0 && summary.Expansions > 0
                ? [Line("Fallos", "ninguno", "SystemFillColorSuccessBrush")]
                : summary.Failures.OrderByDescending(f => f.Value).Select(f => Line(StatusNames[f.Key], Count(f.Value), "SystemFillColorCautionBrush")),
            .. summary.Rejections.OrderByDescending(r => r.Value).Select(r => Line($"No insertada: {RejectionNames[r.Key]}", Count(r.Value))),
        ]);

        Fill(AppLines, summary.TopApps.Count == 0
            ? [Line("Sin expansiones este día", string.Empty)]
            : summary.TopApps.Select(a => Line(a.Process, Count(a.Count))));

        Fill(MenuLines,
        [
            Line("Menús abiertos", Count(summary.MenusShown)),
            Line("Junto al cursor de texto", summary.MenusShown == 0 ? "—" : (summary.MenusAnchoredToCaret / (double)summary.MenusShown).ToString("P0", Es)),
            .. summary.MenuCloses.OrderByDescending(c => c.Value).Select(c => Line($"  Cerrados: {MenuCloseNames[c.Key]}", Count(c.Value))),
            Line("Formularios de campos", Count(summary.FieldsShown)),
            .. summary.FieldsCloses.OrderByDescending(c => c.Value).Select(c => Line($"  {Capitalize(FieldsCloseNames[c.Key])}", Count(c.Value))),
        ]);

        Fill(StabilityLines,
        [
            Line("Teclado reconectado (hook)", Count(summary.HookReinstalls), summary.HookReinstalls == 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"),
            Line("Errores internos", Count(summary.Faults), summary.Faults == 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush"),
            Line("Arranques", Count(summary.Startups)),
            Line($"Último arranque (objetivo < {StartupTargetMs / 1000:0} s)", Ms(summary.LastStartupMs),
                summary.LastStartupMs is > StartupTargetMs ? "SystemFillColorCautionBrush" : null),
        ]);
    }

    private static void Fill(Panel panel, IEnumerable<UIElement> lines)
    {
        panel.Children.Clear();
        foreach (var line in lines)
        {
            panel.Children.Add(line);
        }
    }

    private static Grid Line(string label, string value, string? valueBrush = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
        var number = new TextBlock { Text = value, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (valueBrush is not null)
        {
            number.Foreground = Brush(valueBrush);
        }

        Grid.SetColumn(number, 1);
        grid.Children.Add(number);
        return grid;
    }

    /// <summary>Null goes back to the inherited color: assigning a null Foreground makes WinUI draw nothing.</summary>
    private static void SetValueBrush(TextBlock text, string? key)
    {
        if (key is null)
        {
            text.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            text.Foreground = Brush(key);
        }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static string Ms(double? ms) => ms switch
    {
        null => "—",
        >= 1000 => $"{ms.Value / 1000:0.0} s",
        _ => $"{ms.Value:0} ms",
    };

    private static string Count(int value) => value.ToString("N0", Es);

    private static string Capitalize(string text) => char.ToUpper(text[0], Es) + text[1..];

    private void OnDayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && DayChoice.SelectedItem is ComboBoxItem { Tag: DateOnly day })
        {
            // Not Refresh(): clearing DayChoice.Items while it raises SelectionChanged crashes WinUI natively.
            _day = day;
            ShowDay();
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Current.Services.GetRequiredService<AppPaths>().Logs}\"") { UseShellExecute = true })
            ?.Dispose();
}
