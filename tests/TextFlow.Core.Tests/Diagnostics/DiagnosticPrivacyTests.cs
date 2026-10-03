using System.Reflection;
using TextFlow.Core.Diagnostics;

namespace TextFlow.Core.Tests.Diagnostics;

/// <summary>
/// Spec §20/§28, ADR-0006: diagnostics must never carry user content. Enforced structurally: every event field
/// is a number, bool, enum or time, or a string explicitly marked <see cref="SafeToLogAttribute"/> whose name
/// is on a short allowlist of metadata (process names, strategy names), never text.
/// </summary>
public class DiagnosticPrivacyTests
{
    private static readonly HashSet<string> SafeStringProperties = ["TargetProcess", "Reason"];

    private static readonly Type[] EventTypes = typeof(DiagnosticEvent).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(DiagnosticEvent)) && !t.IsAbstract)
        .ToArray();

    public static TheoryData<string> EventNames()
    {
        var data = new TheoryData<string>();
        foreach (var type in EventTypes)
        {
            data.Add(type.Name);
        }

        return data;
    }

    [Fact]
    public void ThereAreDiagnosticEvents()
    {
        Assert.NotEmpty(EventTypes);
    }

    [Theory]
    [MemberData(nameof(EventNames))]
    public void EventFields_AreContentFreeByType(string eventName)
    {
        var type = EventTypes.Single(t => t.Name == eventName);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract"))
        {
            var fieldType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (fieldType == typeof(string))
            {
                Assert.True(property.GetCustomAttribute<SafeToLogAttribute>() is not null,
                    $"{type.Name}.{property.Name} is a string without [SafeToLog]");
                Assert.Contains(property.Name, SafeStringProperties);
                continue;
            }

            Assert.True(IsContentFree(fieldType), $"{type.Name}.{property.Name} has type {fieldType.Name}, which may carry content");
        }
    }

    private static bool IsContentFree(Type type) =>
        type.IsEnum || type.IsPrimitive || type == typeof(decimal) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan);
}
