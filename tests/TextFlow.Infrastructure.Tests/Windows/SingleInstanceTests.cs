using TextFlow.Infrastructure.Windows;

namespace TextFlow.Infrastructure.Tests.Windows;

public sealed class SingleInstanceTests
{
    private static string UniqueName() => $"TextFlow.Tests.{Guid.NewGuid():N}";

    [Fact]
    public void FirstInstance_OwnsTheName_SecondDoesNot()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);

        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
    }

    [Fact]
    public async Task SecondInstance_AsksTheFirstToShowItself()
    {
        var name = UniqueName();
        using var first = new SingleInstance(name);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Activated += () => activated.TrySetResult();

        using (var second = new SingleInstance(name))
        {
            second.ActivateFirst();
        }

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void NameIsFree_AfterTheFirstInstanceIsDisposed()
    {
        var name = UniqueName();
        new SingleInstance(name).Dispose();

        using var next = new SingleInstance(name);

        Assert.True(next.IsFirst);
    }
}
