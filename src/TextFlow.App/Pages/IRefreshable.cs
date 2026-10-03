namespace TextFlow.App.Pages;

/// <summary>A page that re-reads engine or library state when it changes (pause, import, hook reinstall).</summary>
public interface IRefreshable
{
    void Refresh();
}
