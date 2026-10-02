using TextFlow.Core.Expansion;

namespace TextFlow.Core.Tests.Expansion;

public class TriggerMatcherTests
{
    private static readonly TriggerOptions Defaults = TriggerOptions.Default;

    private static TriggerMatcher Matcher(TriggerOptions? options = null, params string[] triggers) =>
        new(triggers.Select((t, i) => new TriggerDefinition($"s{i}", t, TriggerMode.AfterDelimiter)), options ?? Defaults);

    private static TriggerMatcher Immediate(params string[] triggers) =>
        new(triggers.Select((t, i) => new TriggerDefinition($"i{i}", t)), Defaults);

    private static TriggerMatch? Type(TriggerMatcher matcher, string keys)
    {
        TriggerMatch? last = null;
        foreach (var c in keys)
        {
            last = c == '\b' ? null : matcher.OnCharacter(c);
            if (c == '\b')
            {
                matcher.OnBackspace();
            }
        }

        return last;
    }

    [Fact]
    public void TriggerFollowedByDelimiter_Matches_WithTriggerLengthBackspaces()
    {
        var match = Type(Matcher(null, ";firma"), ";firma ");

        Assert.NotNull(match);
        Assert.Equal("s0", match.SnippetId);
        Assert.Equal(";firma".Length, match.Backspaces);
        Assert.Equal(' ', match.Delimiter);
    }

    [Fact]
    public void TriggerWithoutDelimiter_DoesNotMatch()
    {
        Assert.Null(Type(Matcher(null, ";firma"), ";firma"));
    }

    [Fact]
    public void NonConfiguredDelimiter_DoesNotMatch()
    {
        var options = Defaults with { Delimiters = new HashSet<char> { ' ' } };

        Assert.Null(Type(Matcher(options, ";firma"), ";firma."));
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\t')]
    [InlineData('.')]
    [InlineData(',')]
    public void DefaultDelimiters_IncludeEnterTabAndPunctuation(char delimiter)
    {
        Assert.NotNull(Type(Matcher(null, ";firma"), ";firma" + delimiter));
    }

    [Fact]
    public void TriggerGluedToPreviousWord_DoesNotMatch_WhenWordBoundaryRequired()
    {
        Assert.Null(Type(Matcher(null, "fir"), "hola mundo afir "));
    }

    [Fact]
    public void TriggerAfterSpace_Matches()
    {
        Assert.NotNull(Type(Matcher(null, "fir"), "hola fir "));
    }

    [Fact]
    public void TriggerGluedToPreviousWord_Matches_WhenBoundaryNotRequired()
    {
        var options = Defaults with { RequireWordBoundary = false };

        Assert.NotNull(Type(Matcher(options, "fir"), "afir "));
    }

    [Fact]
    public void LongestMatchingTriggerWins()
    {
        var match = Type(Matcher(new TriggerOptions(Defaults.Delimiters, RequireWordBoundary: false, Defaults.MaxBufferLength), "ma", ";firma"), ";firma ");

        Assert.Equal("s1", match?.SnippetId);
    }

    [Fact]
    public void Backspace_RemovesLastCharacterFromBuffer()
    {
        Assert.NotNull(Type(Matcher(null, ";firma"), ";firmx\ba "));
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var matcher = Matcher(null, ";firma");
        Type(matcher, ";fir");

        matcher.Reset();

        Assert.Null(Type(matcher, "ma "));
    }

    [Fact]
    public void Matching_IsCaseSensitiveByDefault()
    {
        Assert.Null(Type(Matcher(null, ";firma"), ";FIRMA "));
    }

    [Fact]
    public void Buffer_IsBoundedToMaxLength()
    {
        var matcher = Matcher(Defaults with { MaxBufferLength = 8 }, ";firma");

        Type(matcher, new string('x', 100));

        Assert.True(matcher.BufferLength <= 8);
    }

    [Fact]
    public void AfterMatch_BufferIsCleared()
    {
        var matcher = Matcher(null, ";firma");
        Type(matcher, ";firma ");

        Assert.Equal(0, matcher.BufferLength);
    }

    [Fact]
    public void DelimiterCharactersInsideTrigger_AreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => Matcher(null, ";fir ma"));
    }

    [Fact]
    public void ReplaceTriggers_SwapsDefinitionsAndClearsBuffer()
    {
        var matcher = Matcher(null, ";a1");
        Type(matcher, ";b");

        matcher.ReplaceTriggers([new TriggerDefinition("new", ";b1", TriggerMode.AfterDelimiter)]);

        Assert.Null(Type(matcher, "1 "));
        Assert.Equal("new", Type(matcher, ";b1 ")?.SnippetId);
    }

    [Fact]
    public void DefaultMode_IsImmediate()
    {
        Assert.Equal(TriggerMode.Immediate, new TriggerDefinition("s", "CC").Mode);
    }

    [Fact]
    public void ImmediateTrigger_MatchesOnLastCharacter_WithoutDelimiter()
    {
        var matcher = Immediate("CC");

        Assert.Null(matcher.OnCharacter('C'));
        var match = matcher.OnCharacter('C');

        Assert.NotNull(match);
        Assert.Equal("i0", match.SnippetId);
        Assert.False(match.Delimiter.HasValue);
        Assert.Equal(2, match.Backspaces); // last key is not swallowed: it reached the target
    }

    [Fact]
    public void ImmediateTrigger_RespectsWordBoundary()
    {
        Assert.Null(Type(Immediate("CC"), "ACC"));
        Assert.NotNull(Type(Immediate("CC"), "hola CC"));
    }

    [Fact]
    public void ImmediateTrigger_IsCaseSensitive()
    {
        Assert.Null(Type(Immediate("CC"), "cc"));
    }

    [Fact]
    public void ImmediateTrigger_ShorterPrefixFiresFirst()
    {
        var matcher = Immediate("OD", "ODX");

        Assert.Equal("i0", Type(matcher, "OD")?.SnippetId);
    }

    [Fact]
    public void ImmediateTrigger_ClearsBufferAfterMatch()
    {
        var matcher = Immediate("CC");
        Type(matcher, "CC");

        Assert.Equal(0, matcher.BufferLength);
        Assert.Null(matcher.OnCharacter('C'));
    }

    [Fact]
    public void MixedModes_DelimiterTriggerStillNeedsDelimiter()
    {
        var matcher = new TriggerMatcher(
            [new TriggerDefinition("now", "CC"), new TriggerDefinition("later", ";firma", TriggerMode.AfterDelimiter)],
            Defaults);

        Assert.Null(Type(matcher, ";firma"));
        Assert.Equal("later", matcher.OnCharacter(' ')?.SnippetId);
        Assert.Equal("now", Type(matcher, "CC")?.SnippetId);
    }

    [Fact]
    public void ImmediateTrigger_AfterBackspaceCorrection_Matches()
    {
        Assert.NotNull(Type(Immediate("CC"), "CX\bC"));
    }

    [Theory]
    [InlineData("lc")]
    [InlineData("LC")]
    [InlineData("Lc")]
    public void IgnoreCaseTrigger_MatchesAnyCasing(string typed)
    {
        var matcher = new TriggerMatcher([new TriggerDefinition("g", "LC", IgnoreCase: true)], Defaults);

        var match = Type(matcher, typed);

        Assert.Equal("g", match?.SnippetId);
        Assert.Equal(2, match?.Backspaces);
    }

    [Fact]
    public void IgnoreCaseTrigger_StillRespectsWordBoundary()
    {
        var matcher = new TriggerMatcher([new TriggerDefinition("g", "LC", IgnoreCase: true)], Defaults);

        Assert.Null(Type(matcher, "blc"));
    }

    [Fact]
    public void ExactCaseTrigger_WinsOverIgnoreCaseTriggerOfSameText()
    {
        var matcher = new TriggerMatcher(
            [new TriggerDefinition("loose", "lc", IgnoreCase: true), new TriggerDefinition("exact", "LC")],
            Defaults);

        Assert.Equal("exact", Type(matcher, "LC")?.SnippetId);
        Assert.Equal("loose", Type(matcher, " lc")?.SnippetId);
    }
}
