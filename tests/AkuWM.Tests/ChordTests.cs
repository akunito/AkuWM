using AkuWM.Core.Input;
using Xunit;

namespace AkuWM.Tests;

public class ChordTests
{
    [Theory]
    [InlineData("Hyper+L", "Hyper+L")]
    [InlineData("hyper+shift+c", "Hyper+Shift+C")]
    [InlineData("Ctrl+Alt+C", "Ctrl+Alt+C")]
    [InlineData("Hyper+F9", "Hyper+F9")]
    [InlineData("Hyper + Space", "Hyper+SPACE")]
    [InlineData("Win+Tab", "Win+TAB")]
    [InlineData("Hyper+WheelDown", "Hyper+WHEELDOWN")]
    [InlineData("Hyper+WheelUp", "Hyper+WHEELUP")]
    public void ParsesAndCanonicalises(string text, string canonical)
    {
        Assert.True(Chord.TryParse(text, out Chord chord, out string? error), error);
        Assert.Equal(canonical, chord.ToString());
    }

    [Theory]

    [InlineData("Hyper+?")]

    [InlineData("Hyper+Shift+;")]

    [InlineData("Hyper+Shift+-")]

    [InlineData("Ctrl+Alt+:")]

    public void A_shifted_punctuation_key_is_a_key(string text)

    {

        Assert.True(Chord.TryParse(text, out _, out string? error), error);

    }


    [Fact]
    public void HyperIsCtrlAltWin()
    {
        Assert.True(Chord.TryParse("Hyper+L", out Chord hyper, out _));
        Assert.True(Chord.TryParse("Ctrl+Alt+Win+L", out Chord spelled, out _));
        Assert.Equal(hyper, spelled);
    }

    [Fact]
    public void ShiftIsNotPartOfHyper()
    {
        Assert.True(Chord.TryParse("Hyper+S", out Chord plain, out _));
        Assert.True(Chord.TryParse("Hyper+Shift+S", out Chord shifted, out _));
        Assert.NotEqual(plain, shifted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Hyper")]
    [InlineData("Hyper+")]
    [InlineData("Hyper+A+B")]
    [InlineData("Hyper+Nonsense")]
    [InlineData("Hyper+Shift+Shift+A")]
    public void RefusesWhatItCannotBind(string text) =>
        Assert.False(Chord.TryParse(text, out _, out _));
}
