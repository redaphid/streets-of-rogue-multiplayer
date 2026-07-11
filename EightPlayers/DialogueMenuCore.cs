using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace EightPlayers
{
    // One node of a prefetched conversation tree (rogue-gm issue #18).
    // Plain-string options parse to { Text, Reply=null, Next=null } — the
    // original flat-menu behavior (press → menu_choice event → close).
    internal sealed class MenuOption
    {
        internal readonly string Text;              // button label the player sees
        internal readonly string Reply;             // canned instant say-bubble (null = none)
        internal readonly List<MenuOption> Next;    // pre-authored next menu level (null/empty = leaf)

        internal MenuOption(string text, string reply, List<MenuOption> next)
        {
            Text = text;
            Reply = reply;
            Next = next;
        }
    }

    // What happened when the player pressed an option — consumed by the
    // press hook in DialogueMenu.cs to say the reply / swap the menu / close.
    internal sealed class MenuChoice
    {
        internal string Reply;          // canned reply to say instantly (null = none)
        internal bool HasNext;          // true → swap menu to the pre-authored next level
        internal List<string> Path;     // option texts from the tree root through this choice
        internal int Depth;             // 1-based depth of this choice in the tree
    }

    // Pure state + parsing for custom NPC interaction menus (no Unity/game
    // deps — unit-tested by EightPlayers.Tests). The Harmony hooks that splice
    // these options into the vanilla talk menu live in DialogueMenu.cs.
    //
    // A menu is registered per agent UID via `setmenu <uid> <b64json>`. The
    // json is an array whose elements are EITHER plain strings ("opt") OR
    // recursive nodes {"text":"opt","reply":"canned line","next":[...]} — a
    // whole conversation tree installed in one call (issue #18): each press
    // pops the canned reply instantly and descends the cursor into that
    // option's `next` menu, no external round-trip needed until a leaf.
    // Reaching a leaf CONSUMES that top-level branch (issue #27): the cursor
    // resets to the root and the exhausted branch is dropped from future
    // interactions, so a conversation the player already walked never re-shows.
    // While a flagged uid has NO options yet (empty array) — or once EVERY
    // top-level branch is consumed — the menu shows a single "..." option;
    // pressing it still emits a menu_choice event, so an external character
    // session can lazily author its first (or next) real menu. A fresh setmenu
    // REPLACES the tree and RESETS consumed state.
    internal static class DialogueMenuCore
    {
        /// <summary>Marker prefix carried INSIDE the button id, so the option
        /// text round-trips through the vanilla button plumbing untouched
        /// (DetermineButtons → ShowObjectButtons → PressedButton).</summary>
        internal const string Marker = "EPMENU::";

        internal const int MaxOptions = 6;      // per level; vanilla menu shows 8 rows, keep room for "Done"
        internal const int MaxOptionChars = 40; // fits the button label width
        internal const int MaxReplyChars = 90;  // fits a say bubble
        internal const int MaxDepth = 5;        // tree depth bound
        internal const int MaxNodes = 40;       // total option-node bound

        /// <summary>Placeholder option shown while a flagged uid has no
        /// options authored yet.</summary>
        internal const string Placeholder = "...";

        // Per-uid installed tree + a cursor into it (the level currently shown).
        private sealed class MenuState
        {
            internal List<MenuOption> Root;
            internal List<MenuOption> Cursor;               // current level
            internal readonly List<string> Path = new List<string>(); // choices taken to reach Cursor
            // Top-level option texts the player has already walked to a leaf
            // (issue #27): once consumed, a branch is dropped from the root menu
            // on re-interact so an exhausted conversation never re-shows itself.
            internal readonly HashSet<string> Consumed = new HashSet<string>();
        }

        // Main-thread only (verbs and Harmony hooks both run on Unity's main
        // thread). Uids churn per level; stale entries simply never match.
        private static readonly Dictionary<int, MenuState> _menus = new Dictionary<int, MenuState>();

        /// <summary>Register (or replace) the custom menu/tree for an agent.
        /// Resets the cursor to the root. Throws on malformed/oversized input;
        /// returns the verb reply.</summary>
        internal static string SetMenu(int uid, string b64Json)
        {
            var options = ParseOptions(b64Json);
            _menus[uid] = new MenuState { Root = options, Cursor = options };
            if (options.Count == 0)
                return $"menu flagged on agent {uid} (no options yet — shows \"{Placeholder}\")";
            int nodes = CountNodes(options);
            int depth = TreeDepth(options);
            return nodes == options.Count
                ? $"menu set on agent {uid}: {options.Count} option(s)"
                : $"menu tree set on agent {uid}: {options.Count} option(s), {nodes} node(s), depth {depth}";
        }

        /// <summary>Decode + validate a base64 JSON array of options. Each
        /// element is a plain string OR {"text","reply"?,"next"?[]}, `next`
        /// recursing into the same shape.</summary>
        internal static List<MenuOption> ParseOptions(string b64Json)
        {
            string json;
            try
            {
                json = Encoding.UTF8.GetString(Convert.FromBase64String(b64Json));
            }
            catch (FormatException)
            {
                throw new ArgumentException("not valid base64 — send base64(JSON array of options)");
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
            int nodes = 0;
            return ParseLevel(arr, 1, ref nodes);
        }

        private static List<MenuOption> ParseLevel(JArray arr, int depth, ref int nodes)
        {
            if (depth > MaxDepth)
                throw new ArgumentException($"tree too deep — max depth {MaxDepth}");
            if (arr.Count > MaxOptions)
                throw new ArgumentException($"too many options at one level ({arr.Count}) — max {MaxOptions}");
            var options = new List<MenuOption>();
            foreach (var token in arr)
            {
                if (++nodes > MaxNodes)
                    throw new ArgumentException($"tree too big — max {MaxNodes} option nodes total");
                if (token.Type == JTokenType.String)
                {
                    options.Add(new MenuOption(ValidText((string)token), null, null));
                    continue;
                }
                if (token.Type != JTokenType.Object)
                    throw new ArgumentException($"options must be strings or objects, got {token.Type}");
                var obj = (JObject)token;
                string text = ValidText((string)obj["text"]);
                string reply = ((string)obj["reply"])?.Trim();
                if (string.IsNullOrEmpty(reply))
                    reply = null;
                else if (reply.Length > MaxReplyChars)
                    throw new ArgumentException($"reply too long ({reply.Length} chars, max {MaxReplyChars}): \"{reply}\"");
                List<MenuOption> next = null;
                var nextToken = obj["next"];
                if (nextToken != null && nextToken.Type != JTokenType.Null)
                {
                    if (nextToken.Type != JTokenType.Array)
                        throw new ArgumentException("\"next\" must be an array of options");
                    next = ParseLevel((JArray)nextToken, depth + 1, ref nodes);
                    if (next.Count == 0)
                        next = null; // empty next = leaf
                }
                options.Add(new MenuOption(text, reply, next));
            }
            return options;
        }

        private static string ValidText(string raw)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0)
                throw new ArgumentException("empty option text");
            if (text.Length > MaxOptionChars)
                throw new ArgumentException($"option too long ({text.Length} chars, max {MaxOptionChars}): \"{text}\"");
            return text;
        }

        internal static int CountNodes(List<MenuOption> options)
        {
            int n = 0;
            foreach (var o in options)
            {
                n++;
                if (o.Next != null)
                    n += CountNodes(o.Next);
            }
            return n;
        }

        internal static int TreeDepth(List<MenuOption> options)
        {
            int deepest = 0;
            foreach (var o in options)
                if (o.Next != null)
                {
                    int d = TreeDepth(o.Next);
                    if (d > deepest)
                        deepest = d;
                }
            return 1 + deepest;
        }

        internal static string Clear(int uid) =>
            _menus.Remove(uid) ? $"menu cleared on agent {uid}" : $"no menu on agent {uid}";

        internal static int ClearAll()
        {
            int n = _menus.Count;
            _menus.Clear();
            return n;
        }

        /// <summary>Whether this uid has a custom menu installed (its whole
        /// interaction — buttons AND dialogue — is externally authored).</summary>
        internal static bool IsFlagged(int uid) => _menus.ContainsKey(uid);

        /// <summary>The marker-prefixed button ids for the CURRENT tree level
        /// of a flagged uid, or null if the uid has no custom menu (vanilla
        /// buttons untouched).</summary>
        internal static List<string> ButtonIdsFor(int uid)
        {
            if (!_menus.TryGetValue(uid, out var state))
                return null;
            var ids = new List<string>();
            // At the ROOT level (a fresh interaction) exclude branches the
            // player already exhausted (issue #27). Deeper levels — reached by
            // prefetch descent — are shown whole. When every top-level branch
            // is consumed (or the tree has no options yet), fall back to the
            // single "..." placeholder, which still fires a menu_choice so the
            // character can author a fresh, forward-moving tree.
            bool atRoot = ReferenceEquals(state.Cursor, state.Root);
            foreach (var option in state.Cursor)
                if (!atRoot || !state.Consumed.Contains(option.Text))
                    ids.Add(Marker + option.Text);
            if (atRoot && ids.Count == 0)
                ids.Add(Marker + Placeholder);
            return ids;
        }

        /// <summary>Strip the marker: button id → the option text the player
        /// saw (and chose). Null if the id is not one of ours.</summary>
        internal static string ChoiceText(string buttonId) =>
            buttonId != null && buttonId.StartsWith(Marker, StringComparison.Ordinal)
                ? buttonId.Substring(Marker.Length)
                : null;

        /// <summary>The player pressed <paramref name="text"/> on a flagged
        /// uid's menu: descend the cursor into that option's pre-authored
        /// `next` level (if any) and report the canned reply + path. Never
        /// null — an unknown/stale/placeholder press degrades to a plain leaf
        /// choice (event only, menu closes).</summary>
        internal static MenuChoice Choose(int uid, string text)
        {
            _menus.TryGetValue(uid, out var state);
            MenuOption chosen = null;
            if (state != null)
                foreach (var option in state.Cursor)
                    if (option.Text == text)
                    {
                        chosen = option;
                        break;
                    }
            var result = new MenuChoice
            {
                Reply = chosen?.Reply,
                HasNext = chosen?.Next != null,
                Path = state == null ? new List<string>() : new List<string>(state.Path),
            };
            result.Path.Add(text);
            result.Depth = result.Path.Count;
            if (result.HasNext)
            {
                // descend in place — the press hook re-renders this level
                state.Cursor = chosen.Next;
                state.Path.Add(text);
            }
            else if (state != null)
            {
                // leaf (issue #27): the player exhausted this branch. Mark its
                // top-level root consumed and reset the cursor back to the root
                // so the NEXT interaction shows the remaining, unconsumed
                // top-level options — never the conversation just finished.
                // Path[0] is the branch root; if empty, the leaf press WAS a
                // top-level option, so its own text is the root.
                string branchRoot = state.Path.Count > 0 ? state.Path[0] : text;
                state.Consumed.Add(branchRoot);
                state.Cursor = state.Root;
                state.Path.Clear();
            }
            return result;
        }
    }
}
