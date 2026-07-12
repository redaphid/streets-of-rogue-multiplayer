using System;
using EightPlayers;
using Xunit;

public class StoryQuestTests
{
    [Theory]
    [InlineData("reach", "reach")]
    [InlineData("KILL", "kill")]
    [InlineData("Interact", "interact")]
    [InlineData("  protect ", "protect")]
    public void ParseTypeIsCaseAndSpaceInsensitive(string input, string expectedName)
    {
        // keep the public signature free of the internal enum; compare via name
        Assert.Equal(expectedName, StoryQuestCore.TypeName(StoryQuestCore.ParseType(input)));
    }

    [Fact]
    public void ParseTypeRejectsUnknown()
    {
        Assert.Throws<ArgumentException>(() => StoryQuestCore.ParseType("steal"));
        Assert.Throws<ArgumentException>(() => StoryQuestCore.ParseType(""));
    }

    [Fact]
    public void TypeNameRoundTrips()
    {
        foreach (StoryQuestType t in Enum.GetValues(typeof(StoryQuestType)))
            Assert.Equal(t, StoryQuestCore.ParseType(StoryQuestCore.TypeName(t)));
    }

    [Fact]
    public void ProximityTypesAreReachAndInteractOnly()
    {
        Assert.True(StoryQuestCore.IsProximityType(StoryQuestType.Reach));
        Assert.True(StoryQuestCore.IsProximityType(StoryQuestType.Interact));
        Assert.False(StoryQuestCore.IsProximityType(StoryQuestType.Kill));
        Assert.False(StoryQuestCore.IsProximityType(StoryQuestType.Protect));
    }

    [Fact]
    public void InteractRadiusIsTighterThanReach()
    {
        Assert.True(StoryQuestCore.Radius(StoryQuestType.Interact) < StoryQuestCore.Radius(StoryQuestType.Reach));
        Assert.Equal(0f, StoryQuestCore.Radius(StoryQuestType.Kill));
    }

    [Fact]
    public void ReachedByDistanceRespectsRadius()
    {
        // reach completes within its radius, not beyond
        Assert.True(StoryQuestCore.ReachedByDistance(StoryQuestType.Reach, StoryQuestCore.ReachRadius - 0.1f));
        Assert.True(StoryQuestCore.ReachedByDistance(StoryQuestType.Reach, StoryQuestCore.ReachRadius));
        Assert.False(StoryQuestCore.ReachedByDistance(StoryQuestType.Reach, StoryQuestCore.ReachRadius + 0.1f));
        // a reach distance is NOT enough for the tighter interact radius
        Assert.False(StoryQuestCore.ReachedByDistance(StoryQuestType.Interact, StoryQuestCore.ReachRadius));
        Assert.True(StoryQuestCore.ReachedByDistance(StoryQuestType.Interact, StoryQuestCore.InteractRadius));
    }

    [Fact]
    public void ReachedByDistanceIsFalseForNonProximityTypes()
    {
        Assert.False(StoryQuestCore.ReachedByDistance(StoryQuestType.Kill, 0f));
        Assert.False(StoryQuestCore.ReachedByDistance(StoryQuestType.Protect, 0f));
    }

    [Fact]
    public void ReachedByDistanceRejectsNegativeDistance()
    {
        // -1 is the "no live player" sentinel — must never count as reached
        Assert.False(StoryQuestCore.ReachedByDistance(StoryQuestType.Reach, -1f));
    }

    [Fact]
    public void NormalizeIdTrimsAndValidates()
    {
        Assert.Equal("slim-cart", StoryQuestCore.NormalizeId("  slim-cart "));
        Assert.Throws<ArgumentException>(() => StoryQuestCore.NormalizeId(""));
        Assert.Throws<ArgumentException>(() => StoryQuestCore.NormalizeId("two words"));
        Assert.Throws<ArgumentException>(() => StoryQuestCore.NormalizeId(new string('x', StoryQuestCore.MaxIdChars + 1)));
    }

    [Fact]
    public void NormalizeTextPreservesCaseAndBudget()
    {
        Assert.Equal("KNOCK OVER THE CART", StoryQuestCore.NormalizeText("  KNOCK OVER THE CART "));
        Assert.Throws<ArgumentException>(() => StoryQuestCore.NormalizeText(""));
        Assert.Throws<ArgumentException>(() => StoryQuestCore.NormalizeText(new string('X', StoryQuestCore.MaxTextChars + 1)));
    }

    [Fact]
    public void NativeQuestTypeIsNotAVanillaType()
    {
        // must not collide with the vanilla quest types QuestUpdate/QuestSlot switch on
        Assert.Equal("EPStory", StoryQuestCore.NativeQuestType);
        Assert.NotEqual("Kill", StoryQuestCore.NativeQuestType);
    }

    // ---- protect witness gate (rogue-gm 2026-07-11 playtest hardening) ----
    // A protect target dying OFF-SCREEN must not flash "QUEST FAILED" at a
    // player who saw nothing; only a witnessed death is a fair failure.

    [Fact]
    public void ProtectFailureRequiresAPlayerCloseEnoughToWitness()
    {
        Assert.True(StoryQuestCore.ProtectFailureIsWitnessed(0f));
        Assert.True(StoryQuestCore.ProtectFailureIsWitnessed(StoryQuestCore.ProtectWitnessRadius));
        Assert.False(StoryQuestCore.ProtectFailureIsWitnessed(StoryQuestCore.ProtectWitnessRadius + 0.1f));
    }

    [Fact]
    public void ProtectFailureUnwitnessedWhenNoLivePlayerMeasurable()
    {
        // -1 is the "no live player" sentinel from ClosestPlayerDistance
        Assert.False(StoryQuestCore.ProtectFailureIsWitnessed(-1f));
    }

    [Fact]
    public void ProtectWitnessRadiusCoversAScreenNotTheMap()
    {
        // sanity-pin the tuning: roughly one screen of tiles, nowhere near map-wide
        Assert.InRange(StoryQuestCore.ProtectWitnessRadius, 10f, 40f);
    }
}
