using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace EightPlayers
{
    // Custom NPC interaction menus wired to external character sessions
    // (rogue-gm issue #13): when a player walks up and talks to a FLAGGED
    // NPC, the vanilla talk menu shows options authored from outside via
    // `setmenu <uid> <b64json>`; the selection is pushed on the HTTP event
    // stream as {"event":"menu_choice","uid":...,"playerUid":...,"choice":"..."},
    // the menu closes, and the character session responds (say) and re-authors
    // the menu — a live dialogue tree. State/parsing: DialogueMenuCore.cs.
    //
    // Vanilla flow being hooked (mapped via reflection over Assembly-CSharp):
    //   populate  Agent.Interact → PlayfieldObject.Interact →
    //             Agent.DetermineButtons → AgentInteractions.DetermineButtons(
    //             agent, interactingAgent, buttons, buttonsExtra, buttonPrices)
    //   render    Agent.Interact → PlayfieldObject.ShowObjectButtons →
    //             WorldSpaceGUI.ShowObjectButtons(go, buttons, extras, prices)
    //             — each button id is localized via NameDB.GetName(id,
    //             "Interface") and a "Done" row is appended automatically
    //   press     ButtonHelper → WorldSpaceGUI.PressedButton(int) →
    //             PlayfieldObject.PressedButton(name[, price]) →
    //             Agent.PressedButton(string,int) →
    //             AgentInteractions.PressedButton ("Done" → StopInteraction)
    //
    // All three patches are applied by the PatchAll in Plugin.Awake.

    // 1. POPULATE — after vanilla fills the talk menu for this agent, replace
    //    it wholesale for flagged uids (prices/extras cleared; vanilla appends
    //    its own "Done" row at render time, so closing keeps working).
    [HarmonyPatch(typeof(Agent), nameof(Agent.DetermineButtons))]
    internal static class DialogueMenu_Populate_Patch
    {
        private static void Postfix(Agent __instance)
        {
            List<string> ids = DialogueMenuCore.ButtonIdsFor(__instance.UID);
            if (ids == null)
                return;
            __instance.buttons.Clear();
            __instance.buttonsExtra.Clear();
            __instance.buttonPrices.Clear();
            __instance.buttons.AddRange(ids);
        }
    }

    // 2. RENDER — our button ids are not in the localization table (GetName
    //    would throw on the unknown enum row); short-circuit marker ids to
    //    their raw option text.
    [HarmonyPatch(typeof(NameDB), nameof(NameDB.GetName))]
    internal static class DialogueMenu_Label_Patch
    {
        private static bool Prefix(string myName, ref string __result)
        {
            var text = DialogueMenuCore.ChoiceText(myName);
            if (text == null)
                return true;
            __result = text;
            return false;
        }
    }

    // 3. PRESS — a chosen custom option never reaches vanilla logic: broadcast
    //    it to the event-stream subscribers (same pattern as agent_died /
    //    player_hp) and close the menu, exactly like vanilla "Done" does.
    [HarmonyPatch(typeof(Agent), nameof(Agent.PressedButton), new[] { typeof(string), typeof(int) })]
    internal static class DialogueMenu_Press_Patch
    {
        private static bool Prefix(Agent __instance, string buttonText)
        {
            var choice = DialogueMenuCore.ChoiceText(buttonText);
            if (choice == null)
                return true; // vanilla button (incl. "Done") — untouched
            var chooser = __instance.interactingAgent;
            EightPlayersPlugin.Log.LogInfo(
                $"EPMENU choice uid={__instance.UID} playerUid={chooser?.UID.ToString() ?? "?"} \"{choice}\"");
            HttpChannel.Broadcast(new JObject
            {
                ["event"] = "menu_choice",
                ["uid"] = __instance.UID,
                ["playerUid"] = chooser != null ? (JToken)chooser.UID : JValue.CreateNull(),
                ["choice"] = choice,
            });
            __instance.StopInteraction();
            return false;
        }
    }
}
