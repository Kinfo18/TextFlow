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
    public void ImmediateTrigger_ThatPrefixesAnother_WaitsInsteadOfShadowingIt()
    {
        var matcher = Immediate("OD", "ODX");

        Assert.Null(Type(matcher, "OD"));
        Assert.Equal("i1", matcher.OnCharacter('X')?.SnippetId);
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

    private static TriggerMatcher Ambiguous() => new(
        [
            new TriggerDefinition("menu-dir", "DIR", IgnoreCase: true),
            new TriggerDefinition("dir1", "dir1", IgnoreCase: true),
            new TriggerDefinition("dir12", "dir12", IgnoreCase: true),
        ],
        Defaults);

    [Fact]
    public void AmbiguousTrigger_IsHeldPending_InsteadOfFiring()
    {
        var matcher = Ambiguous();

        Assert.Null(Type(matcher, "dir"));
        Assert.True(matcher.HasPending);
    }

    [Fact]
    public void AmbiguousTrigger_LongerTriggerCompleted_FiresLonger()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");

        Assert.Null(matcher.OnCharacter('1')); // "dir1" is itself a prefix of "dir12": still pending
        var match = matcher.OnCharacter('2');

        Assert.Equal("dir12", match?.SnippetId);
        Assert.Equal(5, match?.Backspaces);
        Assert.False(matcher.HasPending);
    }

    [Fact]
    public void AmbiguousTrigger_BrokenByOtherCharacter_FiresPending_WithThatCharacterAsSwallowedDelimiter()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");

        var match = matcher.OnCharacter('x');

        Assert.Equal("menu-dir", match?.SnippetId);
        Assert.Equal('x', match?.Delimiter);
        Assert.Equal(3, match?.Backspaces);
    }

    [Fact]
    public void AmbiguousTrigger_BrokenByDelimiter_FiresPending()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir1");

        var match = matcher.OnCharacter(' ');

        Assert.Equal("dir1", match?.SnippetId);
        Assert.Equal(' ', match?.Delimiter);
    }

    [Fact]
    public void FlushPending_FiresHeldTrigger_AfterTimeout()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");

        var match = matcher.FlushPending(matcher.PendingVersion);

        Assert.Equal("menu-dir", match?.SnippetId);
        Assert.Null(match?.Delimiter);
        Assert.Equal(3, match?.Backspaces);
        Assert.False(matcher.HasPending);
    }

    [Fact]
    public void FlushPending_WithStaleVersion_DoesNothing()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");
        var stale = matcher.PendingVersion;
        matcher.OnCharacter('1');

        Assert.Null(matcher.FlushPending(stale));
        Assert.True(matcher.HasPending);
    }

    [Fact]
    public void Backspace_CancelsPending()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");

        matcher.OnBackspace();

        Assert.False(matcher.HasPending);
        Assert.Null(matcher.FlushPending(matcher.PendingVersion));
    }

    [Fact]
    public void Reset_CancelsPending()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");

        matcher.Reset();

        Assert.False(matcher.HasPending);
    }

    [Fact]
    public void UnambiguousTrigger_StillFiresImmediately()
    {
        var matcher = new TriggerMatcher([new TriggerDefinition("cc", "cc"), new TriggerDefinition("s1", "s1")], Defaults);

        Assert.Equal("cc", Type(matcher, "cc")?.SnippetId);
        Assert.False(matcher.HasPending);
    }

    [Fact]
    public void PrefixOfLongerTrigger_WithoutOwnMatch_IsNotPending()
    {
        var matcher = new TriggerMatcher([new TriggerDefinition("orca3", "orca3")], Defaults);

        Assert.Null(Type(matcher, "orca"));
        Assert.False(matcher.HasPending);
        Assert.Equal("orca3", matcher.OnCharacter('3')?.SnippetId);
    }

    [Fact]
    public void ImmediateTriggerWithSpaces_Matches()
    {
        var matcher = new TriggerMatcher([new TriggerDefinition("foto", "Foto valida", IgnoreCase: true)], Defaults);

        var match = Type(matcher, "foto valida");

        Assert.Equal("foto", match?.SnippetId);
        Assert.Equal("Foto valida".Length, match?.Backspaces);
    }

    [Fact]
    public void SpaceInsideLongerTrigger_KeepsPendingAlive()
    {
        var matcher = new TriggerMatcher(
            [new TriggerDefinition("short", "foto"), new TriggerDefinition("long", "foto valida")],
            Defaults);

        Assert.Null(Type(matcher, "foto "));
        Assert.True(matcher.HasPending);
        Assert.Equal("long", Type(matcher, "valida")?.SnippetId);
    }

    [Fact]
    public void SpaceBreakingPending_FiresHeldTrigger()
    {
        var matcher = new TriggerMatcher(
            [new TriggerDefinition("short", "foto"), new TriggerDefinition("long", "foto valida")],
            Defaults);

        var match = Type(matcher, "foto x");

        Assert.Null(match); // "foto " is still a prefix; 'x' breaks it after the space was already typed
        Assert.False(matcher.HasPending);
    }

    [Fact]
    public void AfterDelimiterTriggerWithDelimiter_IsStillRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new TriggerMatcher([new TriggerDefinition("x", "a b", TriggerMode.AfterDelimiter)], Defaults));
    }

    [Fact]
    public void PendingMatch_DescribesHeldTrigger()
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");

        Assert.Equal("menu-dir", matcher.PendingMatch?.SnippetId);
        Assert.Equal(3, matcher.PendingMatch?.Backspaces);
    }

    [Theory]
    [InlineData('1', true)]
    [InlineData('x', false)]
    [InlineData('2', false)]
    public void ContinuesPending_TellsWhetherCharacterExtendsTowardsLongerTrigger(char c, bool expected)
    {
        var matcher = Ambiguous();
        Type(matcher, "dir");

        Assert.Equal(expected, matcher.ContinuesPending(c));
    }
}
