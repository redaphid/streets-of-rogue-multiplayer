using System;

namespace EightPlayers
{
    // Pure text handling for GM labels (no Unity/game deps — unit-tested by
    // EightPlayers.Tests). The game-facing half (QuestMarker creation, the
    // NameDB Harmony hook, lifecycle tick) lives in Label.cs.
    //
    // A label is quest-marker-style world-space text pinned over an agent or
    // object (rogue-gm issue #17) — the same rendering the vanilla RESCUE /
    // NEUTRALIZE / DESTROY quest labels use. The text rides INSIDE the
    // marker's NameDB id ("EPLABEL::<text>"), so it flows through the vanilla
    // QuestMarkerText plumbing untouched; a NameDB.GetName prefix strips the
    // marker and returns the raw text (same smuggle as DialogueMenuCore).
    internal static class LabelCore
    {
        /// <summary>Marker prefix carried inside the NameDB id, so arbitrary
        /// text survives the localization lookup (which throws on ids it
        /// doesn't know).</summary>
        internal const string Marker = "EPLABEL::";

        /// <summary>Vanilla labels are one shouted word ("NEUTRALIZE"); allow
        /// a short phrase but keep it readable over a head.</summary>
        internal const int MaxChars = 48;

        /// <summary>Trim and validate label text. Case is PRESERVED — vanilla
        /// style is ALL CAPS, but that's the caller's choice.</summary>
        internal static string Normalize(string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0)
                throw new ArgumentException("empty label text");
            if (t.Length > MaxChars)
                throw new ArgumentException($"label too long ({t.Length} chars, max {MaxChars}): \"{t}\"");
            return t;
        }

        /// <summary>The NameDB id that carries this label's text.</summary>
        internal static string MakeId(string text) => Marker + Normalize(text);

        /// <summary>Strip the marker: NameDB id → the raw label text, or null
        /// if the id is not one of ours (vanilla lookup proceeds).</summary>
        internal static string LabelText(string id) =>
            id != null && id.StartsWith(Marker, StringComparison.Ordinal)
                ? id.Substring(Marker.Length)
                : null;
    }
}
