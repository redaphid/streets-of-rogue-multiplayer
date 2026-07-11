using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EightPlayers
{
    // Pure parsing + coordinate math for INTENTIONAL MAPS (rogue-gm issue #20):
    // an authored ASCII level layout a campaign can materialize onto whatever
    // floor is loaded. No Unity/game deps — unit-tested by EightPlayers.Tests.
    // The game-facing half (clearing the region, building walls/floors, placing
    // objects) lives in MapBuilder.cs and calls GameStateApi tile primitives.
    //
    // ── The .map format ────────────────────────────────────────────────────
    // A header section, a line that is exactly "---", then the ASCII grid:
    //
    //     ; comments start with ';' or '//'
    //     origin: 100 -40           world cell of grid cell (col=0,row=0) = top-left
    //     legend: B=ExplodingBarrel T=Table S=Safe
    //     anchor: WREN=5,2          named anchor at grid cell (col,row)
    //     anchor: ENTRY=1,3
    //     ---
    //     ###########
    //     #.........#
    //     #..T...B..#
    //     ###########
    //
    // Grid chars: '#'=wall  '.'=floor  ' '=leave-as-is (don't touch the cell)
    //             any legend char = place that object on floor.
    // Anchors are declared in the header (they keep the grid pure walls/floors/
    // objects) and are reported back as WORLD positions so the GM can spawn its
    // cast/beats at them by name.
    //
    // ── Coordinate convention ──────────────────────────────────────────────
    // Grid cell (col,row) maps to world cell (origin.x + col, origin.y - row):
    // col grows east (+x), row grows south (−y), so the grid reads top-down the
    // way it's written and top-left sits at `origin`.
    internal static class MapBuilderCore
    {
        internal const string Separator = "---";
        internal const int MaxWidth = 120;
        internal const int MaxHeight = 120;
        internal const int MaxCells = 4000; // guard against a channel-sized bomb

        internal enum CellKind { Skip, Floor, Wall, Object }

        internal struct Cell
        {
            internal int Col;
            internal int Row;
            internal CellKind Kind;
            internal char Ch;
            internal string ObjectName; // set when Kind == Object
        }

        internal sealed class ParsedMap
        {
            internal int OriginX;
            internal int OriginY;
            internal bool HasOrigin;
            internal readonly List<string> Rows = new List<string>();
            internal readonly Dictionary<char, string> Legend = new Dictionary<char, string>();
            // name -> (col,row) grid cell
            internal readonly Dictionary<string, int[]> Anchors = new Dictionary<string, int[]>(StringComparer.Ordinal);
            internal int Width;
            internal int Height;
        }

        /// <summary>Grid cell (col,row) → world cell (x,y). North is −row.</summary>
        internal static int[] CellToWorld(int originX, int originY, int col, int row)
            => new[] { originX + col, originY - row };

        /// <summary>Parse the full .map text. Throws ArgumentException on any
        /// malformed directive, unknown legend char, or out-of-bounds anchor.</summary>
        internal static ParsedMap Parse(string text)
        {
            if (text == null)
                throw new ArgumentException("empty map");
            var map = new ParsedMap();
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            bool inGrid = false;
            foreach (var raw in lines)
            {
                if (!inGrid)
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith(";") || line.StartsWith("//")) continue;
                    if (line == Separator) { inGrid = true; continue; }
                    ParseHeaderLine(line, map);
                }
                else
                {
                    // Grid rows are kept verbatim (trailing whitespace trimmed —
                    // it is "leave-as-is" anyway). Skip a single blank tail line.
                    map.Rows.Add(raw.TrimEnd());
                }
            }

            if (!inGrid)
                throw new ArgumentException("no grid: a line that is exactly \"---\" must separate the header from the ASCII grid");
            // Drop trailing empty rows (from a final newline).
            while (map.Rows.Count > 0 && map.Rows[map.Rows.Count - 1].Length == 0)
                map.Rows.RemoveAt(map.Rows.Count - 1);
            if (map.Rows.Count == 0)
                throw new ArgumentException("empty grid");
            if (!map.HasOrigin)
                throw new ArgumentException("missing `origin: X Y` header");

            map.Height = map.Rows.Count;
            foreach (var r in map.Rows)
                if (r.Length > map.Width) map.Width = r.Length;
            if (map.Width > MaxWidth || map.Height > MaxHeight)
                throw new ArgumentException($"grid too big ({map.Width}x{map.Height}) — max {MaxWidth}x{MaxHeight}");
            if (map.Width * map.Height > MaxCells)
                throw new ArgumentException($"grid too big ({map.Width * map.Height} cells) — max {MaxCells}");

            ValidateCellsAndAnchors(map);
            return map;
        }

        private static void ParseHeaderLine(string line, ParsedMap map)
        {
            // key : rest   (colon optional — first token is the key)
            string key, rest;
            int colon = line.IndexOf(':');
            if (colon >= 0)
            {
                key = line.Substring(0, colon).Trim().ToLowerInvariant();
                rest = line.Substring(colon + 1).Trim();
            }
            else
            {
                int sp = line.IndexOf(' ');
                if (sp < 0) throw new ArgumentException($"bad header line: \"{line}\"");
                key = line.Substring(0, sp).Trim().ToLowerInvariant();
                rest = line.Substring(sp + 1).Trim();
            }

            switch (key)
            {
                case "origin":
                {
                    var t = rest.Replace(",", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length != 2 || !TryInt(t[0], out int ox) || !TryInt(t[1], out int oy))
                        throw new ArgumentException($"origin needs two integers (world cell): \"{rest}\"");
                    map.OriginX = ox; map.OriginY = oy; map.HasOrigin = true;
                    break;
                }
                case "legend":
                case "object":
                {
                    // one or more `C=Name` tokens, space-separated
                    foreach (var tok in rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = tok.IndexOf('=');
                        if (eq != 1)
                            throw new ArgumentException($"legend token must be `C=Name` (single char): \"{tok}\"");
                        char c = tok[0];
                        string name = tok.Substring(eq + 1);
                        if (c == '#' || c == '.' || c == ' ')
                            throw new ArgumentException($"legend char '{c}' is reserved (#/./space)");
                        if (name.Length == 0)
                            throw new ArgumentException($"legend token missing object name: \"{tok}\"");
                        map.Legend[c] = name;
                    }
                    break;
                }
                case "anchor":
                {
                    // NAME=COL,ROW  (also tolerate a leading '@' on the name)
                    int eq = rest.IndexOf('=');
                    if (eq < 0)
                        throw new ArgumentException($"anchor needs `NAME=COL,ROW`: \"{rest}\"");
                    string name = rest.Substring(0, eq).Trim().TrimStart('@');
                    var cr = rest.Substring(eq + 1).Replace(" ", "").Split(',');
                    if (name.Length == 0 || cr.Length != 2 || !TryInt(cr[0], out int col) || !TryInt(cr[1], out int row))
                        throw new ArgumentException($"anchor needs `NAME=COL,ROW`: \"{rest}\"");
                    map.Anchors[name] = new[] { col, row };
                    break;
                }
                default:
                    throw new ArgumentException($"unknown header directive \"{key}\" (want origin/legend/anchor)");
            }
        }

        private static void ValidateCellsAndAnchors(ParsedMap map)
        {
            foreach (var kv in map.Anchors)
            {
                int col = kv.Value[0], row = kv.Value[1];
                if (col < 0 || row < 0 || col >= map.Width || row >= map.Height)
                    throw new ArgumentException($"anchor \"{kv.Key}\" at {col},{row} is outside the {map.Width}x{map.Height} grid");
            }
            // Fail fast on unknown legend chars (before we touch the world).
            for (int row = 0; row < map.Rows.Count; row++)
            {
                var line = map.Rows[row];
                for (int col = 0; col < line.Length; col++)
                {
                    char ch = line[col];
                    if (ch == ' ' || ch == '#' || ch == '.') continue;
                    if (!map.Legend.ContainsKey(ch))
                        throw new ArgumentException($"grid char '{ch}' at col {col} row {row} is not in the legend");
                }
            }
        }

        /// <summary>Every non-skip cell, in row-major order, resolved to its
        /// kind + object name — what MapBuilder.cs iterates to sculpt the world.</summary>
        internal static IEnumerable<Cell> Cells(ParsedMap map)
        {
            for (int row = 0; row < map.Rows.Count; row++)
            {
                var line = map.Rows[row];
                for (int col = 0; col < line.Length; col++)
                {
                    char ch = line[col];
                    if (ch == ' ') continue;
                    var cell = new Cell { Col = col, Row = row, Ch = ch };
                    if (ch == '#') cell.Kind = CellKind.Wall;
                    else if (ch == '.') cell.Kind = CellKind.Floor;
                    else { cell.Kind = CellKind.Object; cell.ObjectName = map.Legend[ch]; }
                    yield return cell;
                }
            }
        }

        /// <summary>name → world cell [x,y] for every declared anchor.</summary>
        internal static Dictionary<string, int[]> AnchorWorldPositions(ParsedMap map)
        {
            var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var kv in map.Anchors)
                result[kv.Key] = CellToWorld(map.OriginX, map.OriginY, kv.Value[0], kv.Value[1]);
            return result;
        }

        /// <summary>Anchors as a compact one-line JSON object
        /// {"NAME":{"x":..,"y":..},...} — the verb reply the GM reads.</summary>
        internal static string AnchorsJson(ParsedMap map)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in AnchorWorldPositions(map))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(Escape(kv.Key)).Append("\":{\"x\":")
                  .Append(kv.Value[0].ToString(CultureInfo.InvariantCulture))
                  .Append(",\"y\":")
                  .Append(kv.Value[1].ToString(CultureInfo.InvariantCulture))
                  .Append('}');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static bool TryInt(string s, out int v)
            => int.TryParse(s, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
    }
}
