using System;
using System.Linq;
using System.Text;
using EightPlayers;
using Xunit;

public class DialogueMenuTests
{
    private static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    // ---- flat (back-compat) payloads --------------------------------------

    [Fact]
    public void ParsesPlainStringOptionsAsLeavesWithoutReplies()
    {
        var options = DialogueMenuCore.ParseOptions(B64("[\"Ask about the ruins\",\"Who are you?\"]"));
        Assert.Equal(new[] { "Ask about the ruins", "Who are you?" }, options.Select(o => o.Text));
        Assert.All(options, o => Assert.Null(o.Reply));
        Assert.All(options, o => Assert.Null(o.Next));
    }

    [Fact]
    public void TrimsWhitespaceAroundOptionText()
    {
        var options = DialogueMenuCore.ParseOptions(B64("[\"  padded  \"]"));
        Assert.Equal("padded", Assert.Single(options).Text);
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
    public void RejectsNonStringNonObjectOptions()
    {
        Assert.Throws<ArgumentException>(() => DialogueMenuCore.ParseOptions(B64("[1,2]")));
    }

    [Fact]
    public void RejectsMoreThanSixOptionsAtOneLevel()
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
        Assert.Equal(exact, Assert.Single(DialogueMenuCore.ParseOptions(B64($"[\"{exact}\"]"))).Text);
    }

    // ---- rich tree payloads (issue #18) ------------------------------------

    private const string TreeJson = @"[
        {""text"":""Who are you?"",""reply"":""Just a traveler."",""next"":[
            {""text"":""A traveler from where?"",""reply"":""The undercity."",""next"":[
                ""Take me there"",
                {""text"":""Sounds dangerous"",""reply"":""It is.""}
            ]},
            ""Never mind""
        ]},
        ""Goodbye""
    ]";

    [Fact]
    public void ParsesRecursiveTreeNodes()
    {
        var options = DialogueMenuCore.ParseOptions(B64(TreeJson));
        Assert.Equal(2, options.Count);
        var who = options[0];
        Assert.Equal("Who are you?", who.Text);
        Assert.Equal("Just a traveler.", who.Reply);
        Assert.Equal(2, who.Next.Count);
        var from = who.Next[0];
        Assert.Equal("The undercity.", from.Reply);
        Assert.Equal(2, from.Next.Count);
        Assert.Null(from.Next[0].Reply);            // plain string deep in the tree
        Assert.Null(from.Next[0].Next);
        Assert.Equal("It is.", from.Next[1].Reply); // reply without next = leaf with canned line
        Assert.Null(from.Next[1].Next);
        Assert.Null(options[1].Reply);              // top-level plain string
        Assert.Equal(6, DialogueMenuCore.CountNodes(options));
        Assert.Equal(3, DialogueMenuCore.TreeDepth(options));
    }

    [Fact]
    public void MixedStringAndObjectOptionsCoexist()
    {
        var options = DialogueMenuCore.ParseOptions(
            B64("[\"plain\",{\"text\":\"rich\",\"reply\":\"hi\"}]"));
        Assert.Equal(new[] { "plain", "rich" }, options.Select(o => o.Text));
        Assert.Null(options[0].Reply);
        Assert.Equal("hi", options[1].Reply);
    }

    [Fact]
    public void EmptyOrBlankReplyMeansNoReply()
    {
        var options = DialogueMenuCore.ParseOptions(B64("[{\"text\":\"a\",\"reply\":\"  \"}]"));
        Assert.Null(Assert.Single(options).Reply);
    }

    [Fact]
    public void EmptyNextArrayMeansLeaf()
    {
        var options = DialogueMenuCore.ParseOptions(B64("[{\"text\":\"a\",\"next\":[]}]"));
        Assert.Null(Assert.Single(options).Next);
    }

    [Fact]
    public void RejectsObjectWithoutText()
    {
        Assert.Throws<ArgumentException>(() =>
            DialogueMenuCore.ParseOptions(B64("[{\"reply\":\"hi\"}]")));
    }

    [Fact]
    public void RejectsReplyOverNinetyChars()
    {
        var over = new string('r', 91);
        Assert.Throws<ArgumentException>(() =>
            DialogueMenuCore.ParseOptions(B64($"[{{\"text\":\"a\",\"reply\":\"{over}\"}}]")));
    }

    [Fact]
    public void RejectsNonArrayNext()
    {
        Assert.Throws<ArgumentException>(() =>
            DialogueMenuCore.ParseOptions(B64("[{\"text\":\"a\",\"next\":\"nope\"}]")));
    }

    [Fact]
    public void RejectsTreesDeeperThanFiveLevels()
    {
        // 6 nested levels
        var json = "[\"leaf\"]";
        for (int i = 0; i < 5; i++)
            json = $"[{{\"text\":\"L\",\"next\":{json}}}]";
        Assert.Throws<ArgumentException>(() => DialogueMenuCore.ParseOptions(B64(json)));
    }

    [Fact]
    public void AcceptsTreesExactlyFiveLevelsDeep()
    {
        var json = "[\"leaf\"]";
        for (int i = 0; i < 4; i++)
            json = $"[{{\"text\":\"L\",\"next\":{json}}}]";
        Assert.Equal(5, DialogueMenuCore.TreeDepth(DialogueMenuCore.ParseOptions(B64(json))));
    }

    [Fact]
    public void RejectsMoreThanFortyNodesTotal()
    {
        // 6 top-level chains, 5 nodes each = 30 nodes: fine
        string Chain(int len) => len == 1
            ? "\"x\""
            : $"{{\"text\":\"x\",\"next\":[{Chain(len - 1)}]}}";
        var ok = "[" + string.Join(",", Enumerable.Repeat(Chain(5), 6)) + "]";
        Assert.Equal(30, DialogueMenuCore.CountNodes(DialogueMenuCore.ParseOptions(B64(ok))));

        // full 6-wide, 3-level fan-out = 6 + 36 + 216 nodes: over the cap
        string Wide(int depth) => depth == 1
            ? "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\"]"
            : "[" + string.Join(",", Enumerable.Repeat($"{{\"text\":\"x\",\"next\":{Wide(depth - 1)}}}", 6)) + "]";
        Assert.Throws<ArgumentException>(() => DialogueMenuCore.ParseOptions(B64(Wide(3))));
    }

    // ---- state: set / clear / button ids ----------------------------------

    [Fact]
    public void SetClearAndButtonIdsRoundTrip()
    {
        DialogueMenuCore.ClearAll();
        Assert.Null(DialogueMenuCore.ButtonIdsFor(42)); // unflagged → vanilla menu
        Assert.False(DialogueMenuCore.IsFlagged(42));

        DialogueMenuCore.SetMenu(42, B64("[\"Trade\",\"Gossip\"]"));
        Assert.True(DialogueMenuCore.IsFlagged(42));
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "Trade", DialogueMenuCore.Marker + "Gossip" },
            DialogueMenuCore.ButtonIdsFor(42));

        Assert.Equal("menu cleared on agent 42", DialogueMenuCore.Clear(42));
        Assert.Null(DialogueMenuCore.ButtonIdsFor(42));
        Assert.False(DialogueMenuCore.IsFlagged(42));
    }

    [Fact]
    public void FlaggedWithoutOptionsShowsPlaceholder()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(7, B64("[]"));
        Assert.True(DialogueMenuCore.IsFlagged(7));
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + DialogueMenuCore.Placeholder },
            DialogueMenuCore.ButtonIdsFor(7));
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void SetMenuReplyDescribesTreeSize()
    {
        DialogueMenuCore.ClearAll();
        Assert.Equal("menu set on agent 1: 2 option(s)", DialogueMenuCore.SetMenu(1, B64("[\"a\",\"b\"]")));
        Assert.Equal("menu tree set on agent 1: 2 option(s), 6 node(s), depth 3",
            DialogueMenuCore.SetMenu(1, B64(TreeJson)));
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

    // ---- choose: instant replies + cursor descent (issue #18) --------------

    [Fact]
    public void ChoosingAnOptionWithNextDescendsTheCursor()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64(TreeJson));

        var pick = DialogueMenuCore.Choose(5, "Who are you?");
        Assert.Equal("Just a traveler.", pick.Reply);
        Assert.True(pick.HasNext);
        Assert.Equal(new[] { "Who are you?" }, pick.Path);
        Assert.Equal(1, pick.Depth);
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "A traveler from where?", DialogueMenuCore.Marker + "Never mind" },
            DialogueMenuCore.ButtonIdsFor(5)); // menu swapped in place

        var deeper = DialogueMenuCore.Choose(5, "A traveler from where?");
        Assert.Equal("The undercity.", deeper.Reply);
        Assert.True(deeper.HasNext);
        Assert.Equal(new[] { "Who are you?", "A traveler from where?" }, deeper.Path);
        Assert.Equal(2, deeper.Depth);

        var leaf = DialogueMenuCore.Choose(5, "Sounds dangerous");
        Assert.Equal("It is.", leaf.Reply);       // canned reply even on a leaf
        Assert.False(leaf.HasNext);               // menu closes; the live session takes over
        Assert.Equal(3, leaf.Depth);
        Assert.Equal(new[] { "Who are you?", "A traveler from where?", "Sounds dangerous" }, leaf.Path);
        DialogueMenuCore.ClearAll();
    }

    // ---- consumed branches: no-repeat after a leaf (issue #27) -------------

    [Fact]
    public void LeafConsumesItsTopLevelBranchAndResetsCursorToRoot()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64(TreeJson));
        DialogueMenuCore.Choose(5, "Who are you?"); // descend into the branch
        DialogueMenuCore.Choose(5, "Never mind");   // leaf inside that branch
        // Re-interact: the "Who are you?" branch is gone, only "Goodbye" remains.
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "Goodbye" },
            DialogueMenuCore.ButtonIdsFor(5));
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void TopLevelLeafPressConsumesThatOption()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64(TreeJson));
        DialogueMenuCore.Choose(5, "Goodbye"); // top-level plain leaf
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "Who are you?" },
            DialogueMenuCore.ButtonIdsFor(5));
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void AllConsumedFallsBackToPlaceholder()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64("[\"Trade\",\"Gossip\"]"));
        DialogueMenuCore.Choose(5, "Trade");  // leaf, consumes Trade
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "Gossip" },
            DialogueMenuCore.ButtonIdsFor(5));
        var pick = DialogueMenuCore.Choose(5, "Gossip"); // leaf, consumes Gossip
        Assert.False(pick.HasNext);
        // Every branch exhausted → single "..." placeholder (still fires a
        // menu_choice so the character authors a fresh, forward-moving tree).
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + DialogueMenuCore.Placeholder },
            DialogueMenuCore.ButtonIdsFor(5));
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void FreshSetMenuResetsConsumedState()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64("[\"Trade\",\"Gossip\"]"));
        DialogueMenuCore.Choose(5, "Trade");  // consume
        DialogueMenuCore.Choose(5, "Gossip"); // consume → placeholder
        // A new tree wipes consumed state and shows its own options.
        DialogueMenuCore.SetMenu(5, B64("[\"Trade\",\"Gossip\"]"));
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "Trade", DialogueMenuCore.Marker + "Gossip" },
            DialogueMenuCore.ButtonIdsFor(5));
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void ConsumedFilterAppliesOnlyAtRootNotMidDescent()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64(TreeJson));
        // Descend one level; the swapped-in branch level is shown whole,
        // unaffected by consumed tracking (which only prunes the root).
        DialogueMenuCore.Choose(5, "Who are you?");
        Assert.Equal(
            new[] { DialogueMenuCore.Marker + "A traveler from where?", DialogueMenuCore.Marker + "Never mind" },
            DialogueMenuCore.ButtonIdsFor(5));
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void PlainOptionChoiceIsLeafWithoutReply()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64("[\"Trade\",\"Gossip\"]"));
        var pick = DialogueMenuCore.Choose(5, "Trade");
        Assert.Null(pick.Reply);
        Assert.False(pick.HasNext);
        Assert.Equal(new[] { "Trade" }, pick.Path);
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void PlaceholderAndStaleChoicesDegradeToPlainLeaf()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(9, B64("[]"));
        var pick = DialogueMenuCore.Choose(9, DialogueMenuCore.Placeholder);
        Assert.Null(pick.Reply);
        Assert.False(pick.HasNext);
        Assert.Equal(new[] { DialogueMenuCore.Placeholder }, pick.Path);

        var unflagged = DialogueMenuCore.Choose(12345, "ghost"); // never null
        Assert.NotNull(unflagged);
        Assert.Null(unflagged.Reply);
        Assert.False(unflagged.HasNext);
        DialogueMenuCore.ClearAll();
    }

    [Fact]
    public void SetMenuResetsCursorToNewRoot()
    {
        DialogueMenuCore.ClearAll();
        DialogueMenuCore.SetMenu(5, B64(TreeJson));
        DialogueMenuCore.Choose(5, "Who are you?"); // descend
        DialogueMenuCore.SetMenu(5, B64("[\"Fresh start\"]"));
        Assert.Equal(new[] { DialogueMenuCore.Marker + "Fresh start" }, DialogueMenuCore.ButtonIdsFor(5));
        var pick = DialogueMenuCore.Choose(5, "Fresh start");
        Assert.Equal(1, pick.Depth); // path reset too
        DialogueMenuCore.ClearAll();
    }
}
