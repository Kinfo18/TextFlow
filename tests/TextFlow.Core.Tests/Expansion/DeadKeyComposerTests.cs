using TextFlow.Core.Expansion;

namespace TextFlow.Core.Tests.Expansion;

public class DeadKeyComposerTests
{
    [Theory]
    [InlineData('´', 'a', "á")]
    [InlineData('´', 'E', "É")]
    [InlineData('`', 'a', "à")]
    [InlineData('^', 'o', "ô")]
    [InlineData('¨', 'u', "ü")]
    [InlineData('~', 'n', "ñ")]
    public void DeadKeyFollowedByComposableLetter_ProducesPrecomposedCharacter(char dead, char next, string expected)
    {
        Assert.Equal(expected, DeadKeyComposer.Compose(dead, next));
    }

    [Fact]
    public void DeadKeyFollowedBySpace_ProducesSpacingAccentOnly()
    {
        Assert.Equal("´", DeadKeyComposer.Compose('´', ' '));
    }

    [Fact]
    public void DeadKeyFollowedByNonComposableCharacter_ProducesBoth()
    {
        Assert.Equal("´x", DeadKeyComposer.Compose('´', 'x'));
    }

    [Fact]
    public void UnknownDeadKey_ProducesBoth()
    {
        Assert.Equal("°a", DeadKeyComposer.Compose('°', 'a'));
    }
}
