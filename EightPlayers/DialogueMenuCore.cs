using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace EightPlayers
{
    // Pure state + parsing for custom NPC interaction menus (no Unity/game
    // deps — unit-tested by EightPlayers.Tests). The Harmony hooks that splice
    // these options into the vanilla talk menu live in DialogueMenu.cs.
    //
    // A menu is registered per agent UID via the `setmenu <uid> <b64json>`
    // verb (json = ["option one","option two",...]). While a flagged uid has
    // NO options yet (empty array), the menu shows a single "..." option —
    // pressing it still emits a menu_choice event, so an external character
    // session can lazily author its first real menu.
    internal static class DialogueMenuCore
    {
        /// <summary>Marker prefix carried INSIDE the button id, so the option
        /// text round-trips through the vanilla button plumbing untouched
        /// (DetermineButtons → ShowObjectButtons → PressedButton).</summary>
        internal const string Marker = "EPMENU::";

        internal const int MaxOptions = 6;      // vanilla menu shows 8 rows; keep room for "Done"
        internal const int MaxOptionChars = 40; // fits the button label width

        /// <summary>Placeholder option shown while a flagged uid has no
        /// options authored yet.</summary>
        internal const string Placeholder = "...";

        // uid -> authored options ([] = flagged but unauthored → Placeholder).
        // Main-thread only (verbs and Harmony hooks both run on Unity's main
        // thread). Uids churn per level; stale entries simply never match.
        private static readonly Dictionary<int, List<string>> _menus = new Dictionary<int, List<string>>();

        /// <summary>Register (or replace) the custom menu for an agent.
        /// Throws on malformed/oversized input; returns the verb reply.</summary>
        internal static string SetMenu(int uid, string b64Json)
        {
            var options = ParseOptions(b64Json);
            _menus[uid] = options;
            return options.Count == 0
                ? $"menu flagged on agent {uid} (no options yet — shows \"{Placeholder}\")"
                : $"menu set on agent {uid}: {options.Count} option(s)";
        }

        /// <summary>Decode + validate a base64 JSON array of option strings.</summary>
        internal static List<string> ParseOptions(string b64Json)
        {
            string json;
            try
            {
                json = Encoding.UTF8.GetString(Convert.FromBase64String(b64Json));
            }
            catch (FormatException)
            {
                throw new ArgumentException("not valid base64 — send base64(JSON array of strings)");
            }
            JArray arr;
            try
            {
                arr = JArray.Parse(json);
            }
            catch (Exception)
            {
                throw new ArgumentException($"not a JSON array: {json}");
            }
            if (arr.Count > MaxOptions)
                throw new ArgumentException($"too many options ({arr.Count}) — max {MaxOptions}");
            var options = new List<string>();
            foreach (var token in arr)
            {
                if (token.Type != JTokenType.String)
                    throw new ArgumentException($"options must be strings, got {token.Type}");
                var text = ((string)token ?? "").Trim();
                if (text.Length == 0)
                    throw new ArgumentException("empty option");
                if (text.Length > MaxOptionChars)
                    throw new ArgumentException($"option too long ({text.Length} chars, max {MaxOptionChars}): \"{text}\"");
                options.Add(text);
            }
            return options;
        }

        internal static string Clear(int uid) =>
            _menus.Remove(uid) ? $"menu cleared on agent {uid}" : $"no menu on agent {uid}";

        internal static int ClearAll()
        {
            int n = _menus.Count;
            _menus.Clear();
            return n;
        }

        /// <summary>The marker-prefixed button ids for a flagged uid, or null
        /// if the uid has no custom menu (vanilla buttons untouched).</summary>
        internal static List<string> ButtonIdsFor(int uid)
        {
            if (!_menus.TryGetValue(uid, out var options))
                return null;
            var ids = new List<string>();
            if (options.Count == 0)
                ids.Add(Marker + Placeholder);
            else
                foreach (var option in options)
                    ids.Add(Marker + option);
            return ids;
        }

        /// <summary>Strip the marker: button id → the option text the player
        /// saw (and chose). Null if the id is not one of ours.</summary>
        internal static string ChoiceText(string buttonId) =>
            buttonId != null && buttonId.StartsWith(Marker, StringComparison.Ordinal)
                ? buttonId.Substring(Marker.Length)
                : null;
    }
}
