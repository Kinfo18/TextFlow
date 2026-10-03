using TextFlow.Core.Expansion;

namespace TextFlow.Core.Tests.Expansion;

public class TriggerValidatorTests
{
    private static IReadOnlyList<TriggerIssue> Validate(string candidate, params string[] existing) =>
        TriggerValidator.Validate(
            new TriggerDefinition("candidate", candidate),
            existing.Select((t, i) => new TriggerDefinition($"e{i}", t)).ToArray(),
            TriggerOptions.Default);

    [Fact]
    public void ValidPrefixedTrigger_HasNoIssues()
    {
        Assert.Empty(Validate(";firma", ";correo"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTrigger_IsError(string trigger)
    {
        Assert.Contains(Validate(trigger), i => i.Code == TriggerIssueCode.Empty && i.Severity == TriggerIssueSeverity.Error);
    }

    [Fact]
    public void DuplicateTrigger_IsError()
    {
        Assert.Contains(Validate(";firma", ";firma"), i => i.Code == TriggerIssueCode.Duplicate && i.Severity == TriggerIssueSeverity.Error);
    }

    [Fact]
    public void SameSnippetId_IsNotADuplicateOfItself()
    {
        var issues = TriggerValidator.Validate(
            new TriggerDefinition("same", ";firma"),
            [new TriggerDefinition("same", ";firma")],
            TriggerOptions.Default);

        Assert.Empty(issues);
    }

    [Fact]
    public void AfterDelimiterTriggerContainingDelimiter_IsError()
    {
        var issues = TriggerValidator.Validate(
            new TriggerDefinition("candidate", ";mi firma", TriggerMode.AfterDelimiter), [], TriggerOptions.Default);

        Assert.Contains(issues, i => i.Code == TriggerIssueCode.ContainsDelimiter);
    }

    [Fact]
    public void ImmediateTriggerWithSpaces_IsAllowed_AsInAText()
    {
        Assert.DoesNotContain(Validate("Foto valida"), i => i.Code == TriggerIssueCode.ContainsDelimiter);
    }

    [Fact]
    public void SingleCharacterTrigger_IsError()
    {
        Assert.Contains(Validate(";"), i => i.Code == TriggerIssueCode.TooShort && i.Severity == TriggerIssueSeverity.Error);
    }

    [Fact]
    public void PlainWordWithoutSymbol_IsWarning()
    {
        Assert.Contains(Validate("hola"), i => i.Code == TriggerIssueCode.PlainWord && i.Severity == TriggerIssueSeverity.Warning);
    }

    [Fact]
    public void SuffixOverlapWithExistingTrigger_IsWarning()
    {
        Assert.Contains(Validate("rma", ";firma"), i => i.Code == TriggerIssueCode.Overlap && i.Severity == TriggerIssueSeverity.Warning);
        Assert.Contains(Validate(";firma", "rma"), i => i.Code == TriggerIssueCode.Overlap);
    }

    [Fact]
    public void ImmediateTriggerThatPrefixesAnother_IsOverlapWarning()
    {
        var issues = TriggerValidator.Validate(
            new TriggerDefinition("candidate", "ODX"),
            [new TriggerDefinition("short", "OD")],
            TriggerOptions.Default);

        Assert.Contains(issues, i => i.Code == TriggerIssueCode.Overlap && i.ConflictingSnippetId == "short");
    }

    [Fact]
    public void DelimiterTriggerThatPrefixesAnother_HasNoOverlap()
    {
        var issues = TriggerValidator.Validate(
            new TriggerDefinition("candidate", ";odx", TriggerMode.AfterDelimiter),
            [new TriggerDefinition("short", ";od", TriggerMode.AfterDelimiter)],
            TriggerOptions.Default);

        Assert.DoesNotContain(issues, i => i.Code == TriggerIssueCode.Overlap);
    }

    [Fact]
    public void IgnoreCaseTrigger_DifferingOnlyInCase_IsDuplicate()
    {
        var issues = TriggerValidator.Validate(
            new TriggerDefinition("candidate", "lc", IgnoreCase: true),
            [new TriggerDefinition("other", "LC")],
            TriggerOptions.Default);

        Assert.Contains(issues, i => i.Code == TriggerIssueCode.Duplicate && i.ConflictingSnippetId == "other");
    }

    [Fact]
    public void ExactTriggers_DifferingOnlyInCase_AreNotDuplicates()
    {
        var issues = TriggerValidator.Validate(
            new TriggerDefinition("candidate", "lc"),
            [new TriggerDefinition("other", "LC")],
            TriggerOptions.Default);

        Assert.DoesNotContain(issues, i => i.Code == TriggerIssueCode.Duplicate);
    }
}
