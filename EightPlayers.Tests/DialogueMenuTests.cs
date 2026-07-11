using System;
using System.Text;
using EightPlayers;
using Xunit;

public class DialogueMenuTests
{
    private static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParsesOptionsFromBase64Json()
    {
        var options = DialogueMenuCore.ParseOptions(B64("[\"Ask about the ruins\",\"Who are you?\"]"));
        Assert.Equal(new[] { "Ask about the ruins", "Who are you?" }, options);
    }

    [Fact]
    public void TrimsWhitespaceAroundOptions()
    {
        var options = DialogueMenuCore.ParseOptions(B64("[\"  padded  \"]"));
        Assert.Equal("padded", Assert.Single(options));
    }

    [Fact]
    public void EmptyArrayFlagsWithoutOptions()
    {
        Assert.Empty(DialogueMenuCore.ParseOptions(B64("[]")));
    }

    [Fact]
    public void RejectsBadBase64()
    {
        Assert.Throws<ArgumentException>(() => DialogueMenuCore.ParseOptions("not base64!!"));
    }

    [Fact]
    public void RejectsNonArrayJson()
    {
        Assert.Throws<ArgumentException>(() => DialogueMenuCore.ParseOptions(B64("{\"a\":1}")));
    }

    [Fact]
    public void RejectsNonStringOptions()
    {
        Assert.Throws<ArgumentException>(() => DialogueMenuCore.ParseOptions(B64("[1,2]")));
    }

    [Fact]
    public void RejectsMoreThanSixOptions()
    {
        Assert.Throws<ArgumentException>(() =>
            DialogueMenuCore.ParseOptions(B64("[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]")));
    }

    [Fact]
    public void RejectsOptionsOverFortyChars()
    {
        var over = new string('x', 41);
        Assert.Throws<ArgumentException>(() => DialogueMenuCore.ParseOptions(B64($"[\"{over}\"]")));
    }

    [Fact]
    public void AcceptsExactlyFortyChars()
    {
        var exact = new string('x', 40);
        Assert.Equal(exact, Assert.Single(DialogueMenuCore.ParseOptions(B64($"[\"{exact}\"]"))));
    }

    [Fact]
    public void SetClearAndButtonIdsRoundTrip()
    {
        DialogueMenuCore.ClearAll();
        Assert.Null(DialogueMenuCore.ButtonIdsFor(42)); // unflagged → vanilla menu

        DialogueMenuCore.SetMenu(42, B64("[\"Trade\",\"Gossip\"]"));
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "Trade", DialogueMenuCore.Marker + "Gossip" },
            DialogueMenuCore.ButtonIdsFor(42));

        Assert.Equal("menu cleared on agent 42", DialogueMenuCore.Clear(42));
        Assert.Null(DialogueMenuCore.ButtonIdsFor(42));
    }

    [Fact]
    public void FlaggedWithoutOptionsShowsPlaceholder()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(7, B64("[]"));
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + DialogueMenuCore.Placeholder },
            DialogueMenuCore.ButtonIdsFor(7));
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void ChoiceTextStripsMarkerAndIgnoresVanillaIds()
    {
        Assert.Equal("Trade", DialogueMenuCore.ChoiceText(DialogueMenuCore.Marker + "Trade"));
        Assert.Null(DialogueMenuCore.ChoiceText("Done"));
        Assert.Null(DialogueMenuCore.ChoiceText("FollowMe"));
        Assert.Null(DialogueMenuCore.ChoiceText(null));
    }
}
