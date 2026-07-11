using Newtonsoft.Json;

namespace EightPlayers
{
    // Pure (Unity-free) coercion helpers for the reflection surface (Reflect.cs).
    // Kept in its own file with no UnityEngine dependency so the logic can be
    // linked into EightPlayers.Tests and unit-tested directly.
    internal static class ReflectCoerce
    {
        /// <summary>Coerce a raw command-channel token destined for a STRING
        /// member. A JSON-quoted token (<c>"MAULER"</c>) is unwrapped to its
        /// literal (<c>MAULER</c>) so <c>set</c>/<c>set_field</c> doesn't store
        /// the surrounding quotes (rogue-gm#16); a bare token (<c>MAULER</c>)
        /// passes straight through. Malformed JSON falls back to the raw text.</summary>
        internal static string CoerceStringValue(string raw)
        {
            if (raw != null && raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
            {
                try { return JsonConvert.DeserializeObject<string>(raw); }
                catch { }
            }
            return raw;
        }
    }
}
