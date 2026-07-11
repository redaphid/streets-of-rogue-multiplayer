using System;
using System.Collections.Generic;
using System.Linq;
using EightPlayers;
using Xunit;
using static EightPlayers.MapBuilderCore;

public class MapBuilderTests
{
    private const string Sample =
        "; a tiny room\n" +
        "origin: 100 -40\n" +
        "legend: B=ExplodingBarrel T=Table\n" +
        "anchor: WREN=5,2\n" +
        "anchor: ENTRY=1,3\n" +
        "---\n" +
        "###########\n" +
        "#.........#\n" +
        "#..T...B..#\n" +
        "###########\n";

    [Fact]
    public void ParsesOriginLegendAnchorsAndGrid()
    {
        var m = Parse(Sample);
        Assert.True(m.HasOrigin);
        Assert.Equal(100, m.OriginX);
        Assert.Equal(-40, m.OriginY);
        Assert.Equal(11, m.Width);
        Assert.Equal(4, m.Height);
        Assert.Equal("ExplodingBarrel", m.Legend['B']);
        Assert.Equal("Table", m.Legend['T']);
        Assert.Equal(new[] { 5, 2 }, m.Anchors["WREN"]);
        Assert.Equal(new[] { 1, 3 }, m.Anchors["ENTRY"]);
    }

    [Fact]
    public void CellToWorldGrowsEastAndSouthIsNegativeY()
    {
        // col +x, row -y
        Assert.Equal(new[] { 105, -42 }, CellToWorld(100, -40, 5, 2));
        Assert.Equal(new[] { 100, -40 }, CellToWorld(100, -40, 0, 0));
    }

    [Fact]
    public void AnchorWorldPositionsUseOrigin()
    {
        var m = Parse(Sample);
        var w = AnchorWorldPositions(m);
        Assert.Equal(new[] { 105, -42 }, w["WREN"]);
        Assert.Equal(new[] { 101, -43 }, w["ENTRY"]);
    }

    [Fact]
    public void AnchorsJsonIsCompactObject()
    {
        var m = Parse(Sample);
        var json = AnchorsJson(m);
        Assert.Contains("\"WREN\":{\"x\":105,\"y\":-42}", json);
        Assert.Contains("\"ENTRY\":{\"x\":101,\"y\":-43}", json);
        Assert.StartsWith("{", json);
        Assert.EndsWith("}", json);
    }

    [Fact]
    public void CellsClassifiesWallFloorObjectAndSkipsSpaces()
    {
        var m = Parse(Sample);
        var cells = Cells(m).ToList();
        var barrel = cells.Single(c => c.Ch == 'B');
        Assert.Equal(CellKind.Object, barrel.Kind);
        Assert.Equal("ExplodingBarrel", barrel.ObjectName);
        Assert.Equal(7, barrel.Col);
        Assert.Equal(2, barrel.Row);
        Assert.All(cells, c => Assert.NotEqual(CellKind.Skip, c.Kind));
        Assert.Contains(cells, c => c.Kind == CellKind.Wall && c.Ch == '#');
        Assert.Contains(cells, c => c.Kind == CellKind.Floor && c.Ch == '.');
        // corners of the 11x4 box are walls; interior top row all walls => 11
        Assert.Equal(11, cells.Count(c => c.Row == 0 && c.Kind == CellKind.Wall));
    }

    [Fact]
    public void LeaveAsIsSpacesAreNotEmitted()
    {
        var m = Parse("origin: 0 0\n---\n#  #\n");
        var cells = Cells(m).ToList();
        Assert.Equal(2, cells.Count); // only the two '#'
        Assert.All(cells, c => Assert.Equal(CellKind.Wall, c.Kind));
    }

    [Fact]
    public void OriginToleratesCommaAndSpaces()
    {
        var m = Parse("origin: 3, 4\n---\n.\n");
        Assert.Equal(3, m.OriginX);
        Assert.Equal(4, m.OriginY);
    }

    [Fact]
    public void AnchorToleratesLeadingAtSign()
    {
        var m = Parse("origin: 0 0\nanchor: @A = 1,0\n---\n..\n");
        Assert.Equal(new[] { 1, 0 }, m.Anchors["A"]);
    }

    [Fact]
    public void MissingOriginThrows()
    {
        Assert.Throws<ArgumentException>(() => Parse("legend: B=Barrel\n---\n#.#\n"));
    }

    [Fact]
    public void MissingSeparatorThrows()
    {
        Assert.Throws<ArgumentException>(() => Parse("origin: 0 0\n###\n"));
    }

    [Fact]
    public void UnknownLegendCharThrows()
    {
        Assert.Throws<ArgumentException>(() => Parse("origin: 0 0\n---\n#Z#\n"));
    }

    [Fact]
    public void AnchorOutsideGridThrows()
    {
        Assert.Throws<ArgumentException>(() => Parse("origin: 0 0\nanchor: A=9,9\n---\n..\n..\n"));
    }

    [Fact]
    public void ReservedLegendCharThrows()
    {
        Assert.Throws<ArgumentException>(() => Parse("origin: 0 0\nlegend: .=Table\n---\n..\n"));
    }

    [Fact]
    public void UnknownDirectiveThrows()
    {
        Assert.Throws<ArgumentException>(() => Parse("wat: 1 2\norigin: 0 0\n---\n..\n"));
    }

    [Fact]
    public void EmptyGridThrows()
    {
        Assert.Throws<ArgumentException>(() => Parse("origin: 0 0\n---\n\n\n"));
    }

    [Fact]
    public void RaggedRowsUseMaxWidthAndShortRowsAreLeaveAsIs()
    {
        var m = Parse("origin: 0 0\n---\n####\n#\n");
        Assert.Equal(4, m.Width);
        Assert.Equal(2, m.Height);
        // second row has only one '#'; the missing 3 cells are leave-as-is
        Assert.Equal(5, Cells(m).Count());
    }
}
