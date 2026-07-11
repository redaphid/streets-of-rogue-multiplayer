using System;
using EightPlayers;
using Xunit;

public class LabelTests
{
    [Fact]
    public void NormalizeTrimsAndPreservesCase()
    {
        Assert.Equal("Protect Wren", LabelCore.Normalize("  Protect Wren  "));
    }

    [Fact]
    public void NormalizeKeepsAllCapsAsGiven()
    {
        Assert.Equal("ONE OF US", LabelCore.Normalize("ONE OF US"));
    }

    [Fact]
    public void NormalizeRejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => LabelCore.Normalize(""));
        Assert.Throws<ArgumentException>(() => LabelCore.Normalize("   "));
        Assert.Throws<ArgumentException>(() => LabelCore.Normalize(null));
    }

    [Fact]
    public void NormalizeRejectsOverlong()
    {
        Assert.Throws<ArgumentException>(() => LabelCore.Normalize(new string('X', LabelCore.MaxChars + 1)));
    }

    [Fact]
    public void NormalizeAcceptsExactlyMaxChars()
    {
        var text = new string('X', LabelCore.MaxChars);
        Assert.Equal(text, LabelCore.Normalize(text));
    }

    [Fact]
    public void MakeIdPrefixesMarker()
    {
        Assert.Equal("EPLABEL::TRAITOR", LabelCore.MakeId("TRAITOR"));
    }

    [Fact]
    public void LabelTextStripsMarker()
    {
        Assert.Equal("TALK", LabelCore.LabelText("EPLABEL::TALK"));
    }

    [Fact]
    public void LabelTextRoundTripsThroughMakeId()
    {
        Assert.Equal("BOSS FIGHT", LabelCore.LabelText(LabelCore.MakeId("BOSS FIGHT")));
    }

    [Fact]
    public void LabelTextIgnoresForeignIds()
    {
        Assert.Null(LabelCore.LabelText("RescueMarker"));       // vanilla NameDB id
        Assert.Null(LabelCore.LabelText("EPMENU::an option"));  // dialogue-menu marker
        Assert.Null(LabelCore.LabelText(null));
    }
}
