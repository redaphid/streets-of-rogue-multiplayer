using System;
using System.Text;
using UnityEngine;

namespace EightPlayers
{
    // Game-facing half of INTENTIONAL MAPS (rogue-gm issue #20). Takes an
    // authored .map (parsed by MapBuilderCore) and sculpts it onto whatever
    // level is currently loaded, using the existing tile/build/spawn primitives
    // (GameStateApi.BuildWall/DestroyWall/BuildFloor/SpawnObject). Returns a
    // one-line JSON summary whose `anchors` map lets the GM spawn cast/beats at
    // named positions.
    //
    // Path B ("build-primitive sculpting") — the tractable path. Path A
    // (LevelEditor chunk packs) is deferred; see issue #20 for the findings.
    // The build runs after the level is loaded (GM waits for wait-loaded first),
    // so it sits on real floor. Wall/floor tile ops here reuse the same
    // primitives the shipped `buildwall`/`destroywall` verbs already drive.
    internal static class MapBuilder
    {
        /// <summary>Materialize a base64-encoded .map onto the live level.
        /// Returns JSON: {"anchors":{NAME:{x,y}},"built":{walls,floors,objects,failed},"bounds":{x,y,w,h}}.</summary>
        internal static string BuildFromBase64(string b64)
        {
            string text;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch (FormatException) { throw new ArgumentException("not valid base64 — send base64(.map text)"); }
            return Build(text);
        }

        /// <summary>Materialize a raw .map text onto the live level.</summary>
        internal static string Build(string text)
        {
            var map = MapBuilderCore.Parse(text);
            int walls = 0, floors = 0, objects = 0, failed = 0;

            foreach (var cell in MapBuilderCore.Cells(map))
            {
                var w = MapBuilderCore.CellToWorld(map.OriginX, map.OriginY, cell.Col, cell.Row);
                var pos = new Vector2(w[0], w[1]);
                try
                {
                    switch (cell.Kind)
                    {
                        case MapBuilderCore.CellKind.Wall:
                            // floor first so razing this wall later leaves ground
                            GameStateApi.BuildFloor(pos);
                            GameStateApi.BuildWall(pos);
                            walls++;
                            break;
                        case MapBuilderCore.CellKind.Floor:
                            GameStateApi.DestroyWall(pos);
                            GameStateApi.BuildFloor(pos);
                            floors++;
                            break;
                        case MapBuilderCore.CellKind.Object:
                            GameStateApi.DestroyWall(pos);
                            GameStateApi.BuildFloor(pos);
                            var obj = GameStateApi.SpawnObject(cell.ObjectName, pos);
                            if (obj != null) objects++; else failed++;
                            break;
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    Debug.LogWarning($"[EP] buildmap: cell '{cell.Ch}' at {w[0]},{w[1]} failed: {e.Message}");
                }
            }

            var anchors = MapBuilderCore.AnchorsJson(map);
            var sb = new StringBuilder();
            sb.Append("{\"anchors\":").Append(anchors)
              .Append(",\"built\":{\"walls\":").Append(walls)
              .Append(",\"floors\":").Append(floors)
              .Append(",\"objects\":").Append(objects)
              .Append(",\"failed\":").Append(failed)
              .Append("},\"bounds\":{\"x\":").Append(map.OriginX)
              .Append(",\"y\":").Append(map.OriginY)
              .Append(",\"w\":").Append(map.Width)
              .Append(",\"h\":").Append(map.Height).Append("}}");
            return sb.ToString();
        }

        /// <summary>Raze a rectangular region to bare floor: destroy every wall
        /// and lay floor across w columns (east, +x) x h rows (south, −y) from
        /// the top-left world cell (x,y). Returns a short status line.</summary>
        internal static string ClearRegion(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0)
                throw new ArgumentException("clearmap needs positive width and height");
            if (w > MapBuilderCore.MaxWidth || h > MapBuilderCore.MaxHeight || w * h > MapBuilderCore.MaxCells)
                throw new ArgumentException($"region too big ({w}x{h}) — max {MapBuilderCore.MaxWidth}x{MapBuilderCore.MaxHeight}");
            int cleared = 0;
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++)
                {
                    var pos = new Vector2(x + col, y - row);
                    GameStateApi.DestroyWall(pos);
                    GameStateApi.BuildFloor(pos);
                    cleared++;
                }
            return $"cleared {cleared} cell(s) to floor: {w}x{h} from {x},{y}";
        }
    }
}
