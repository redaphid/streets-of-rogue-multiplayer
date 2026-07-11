using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace EightPlayers
{
    // Custom NPC interaction menus wired to external character sessions
    // (rogue-gm issues #13/#18/#19): when a player talks to a FLAGGED NPC, the
    // vanilla talk menu shows options authored from outside via
    // `setmenu <uid> <b64json>` — optionally a whole prefetched conversation
    // TREE ({text, reply, next[]} nodes): pressing an option instantly pops
    // its canned `reply` as a say bubble and swaps the menu to its `next`
    // level in place, while every press is pushed on the HTTP event stream as
    // {"event":"menu_choice","uid","playerUid","choice","path","depth",
    //  "reply","leaf"} so the character session tracks the path and authors
    // deeper branches in the background. State/parsing: DialogueMenuCore.cs.
    //
    // Vanilla flow being hooked (mapped via reflection over Assembly-CSharp):
    //   open      Agent.Interact → PlayfieldObject.Interact →
    //             Agent.DetermineButtons → AgentInteractions.DetermineButtons(...)
    //             → PlayfieldObject.ShowObjectButtons →
    //             WorldSpaceGUI.ShowObjectButtons(go, buttons, extras, prices)
    //             — each button id is localized via NameDB.GetName(id,
    //             "Interface") and a "Done" row is appended automatically
    //   press     ButtonHelper → WorldSpaceGUI.PressedButton(int) →
    //             PlayfieldObject.PressedButton(name[, price]) →
    //             Agent.PressedButton(string,int) →
    //             AgentInteractions.PressedButton ("Done" → StopInteraction)
    //   refresh   WorldSpaceGUI.RefreshObjectButtons(obj) → (next frame)
    //             DetermineButtons + ShowObjectButtons — the in-place redraw
    //             vanilla uses after a press leaves the menu open
    //
    // All patches are applied by the PatchAll in Plugin.Awake.

    // 1. POPULATE — for flagged uids, PREFIX-skip the vanilla population
    //    entirely and install our buttons instead (base.DetermineButtons only
    //    clears the three lists — replicated here).
    //
    //    Issue #19 root cause (a): the old POSTFIX let
    //    AgentInteractions.DetermineButtons run first, and that method doesn't
    //    just add buttons — it fires stock greeting chatter as a side effect
    //    (agent.SayDialogue("Interact"/"InteractHB"/"CantHear"/
    //    "MindControlInteract") + AgentTalk audio), so vanilla text bubbled up
    //    ALONGSIDE the custom menu. Skipping vanilla also kills its early
    //    `return`s (CanUnderstandEachOther, zombie-hate, annoyed) that left
    //    the button list empty → ShowObjectButtons(0 buttons) → menu closed.
    //    Covers every population path: Interact, InteractFar, and the
    //    RefreshObjectButtons re-populate after an action (a re-run just
    //    re-injects the current tree level — nothing to overwrite).
    [HarmonyPatch(typeof(Agent), nameof(Agent.DetermineButtons))]
    internal static class DialogueMenu_Populate_Patch
    {
        private static bool Prefix(Agent __instance)
        {
            List<string> ids = DialogueMenuCore.ButtonIdsFor(__instance.UID);
            if (ids == null)
                return true; // unflagged — vanilla populate untouched
            __instance.buttons.Clear();
            __instance.buttonsExtra.Clear();
            __instance.buttonPrices.Clear();
            __instance.buttons.AddRange(ids);
            return false;
        }
    }

    // 2. OPEN — issue #19 root cause (b): Agent.Interact has an early-out for
    //    Annoyed NPCs that says stock "AnnoyedWontTalk" text and stops the
    //    interaction BEFORE ShowObjectButtons — a flagged NPC the player had
    //    irritated (bumped, failed a chat, gang friction) showed vanilla
    //    refusal text INSTEAD of its menu. For flagged uids we run the same
    //    flow minus that block: the vanilla base setup (via a reverse-patch
    //    stub, so no game code is copied), then show the buttons. A flagged
    //    NPC ALWAYS presents its authored menu.
    [HarmonyPatch(typeof(Agent), nameof(Agent.Interact))]
    internal static class DialogueMenu_Open_Patch
    {
        private static bool Prefix(Agent __instance, Agent otherAgent)
        {
            if (!DialogueMenuCore.IsFlagged(__instance.UID))
                return true; // vanilla interact untouched
            if (!__instance.go.activeSelf)
                return false;
            // vanilla base setup: interactingAgent, DetermineButtons (→ patch
            // 1 installs our menu), interaction collider + limbo bookkeeping
            BasePlayfieldObjectInteract(__instance, otherAgent);
            otherAgent.statusEffects.TempExitBox();
            otherAgent.statusEffects.RemoveInvisibleLimited();
            if (__instance.interactingAgent != null)
            {
                __instance.ShowObjectButtons();
                __instance.StartCoroutine(__instance.TemporarilyStop());
            }
            return false;
        }

        // Calls the ORIGINAL PlayfieldObject.Interact body non-virtually
        // (what `base.Interact(otherAgent)` does inside Agent.Interact).
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(PlayfieldObject), nameof(PlayfieldObject.Interact))]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void BasePlayfieldObjectInteract(PlayfieldObject instance, Agent agent) =>
            throw new NotImplementedException("stub — body injected by Harmony reverse patch");
    }

    // 3. RENDER — our button ids are not in the localization table (GetName
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

    // 4. MUTE — issue #19 belt-and-braces: a flagged NPC's voice is entirely
    //    externally authored (say/reply), so suppress ALL stock SayDialogue
    //    lines from it (ambient chatter, battle barks, refusal lines — any
    //    path not covered above). Vanilla treats "" as "said nothing", and
    //    the external `say` path (Agent.Say) is untouched. All SayDialogue
    //    overloads funnel into this one.
    [HarmonyPatch(typeof(Agent), nameof(Agent.SayDialogue),
        new[] { typeof(bool), typeof(string), typeof(bool), typeof(uint) })]
    internal static class DialogueMenu_Mute_Patch
    {
        private static bool Prefix(Agent __instance, ref string __result)
        {
            if (!DialogueMenuCore.IsFlagged(__instance.UID))
                return true;
            __result = "";
            return false;
        }
    }

    // 5. PRESS — a chosen custom option never reaches vanilla logic. Always
    //    broadcast it to the event-stream subscribers (same pattern as
    //    agent_died / player_hp), then (issue #18, prefetched trees):
    //      reply → pop the canned line as an instant say bubble
    //      next  → swap the menu to the pre-authored next level IN PLACE via
    //              vanilla's own refresh coroutine (keeps pad selection)
    //      leaf  → close the menu like vanilla "Done"; the external session
    //              answers the menu_choice event live and re-authors the tree
    [HarmonyPatch(typeof(Agent), nameof(Agent.PressedButton), new[] { typeof(string), typeof(int) })]
    internal static class DialogueMenu_Press_Patch
    {
        private static bool Prefix(Agent __instance, string buttonText)
        {
            var choice = DialogueMenuCore.ChoiceText(buttonText);
            if (choice == null)
                return true; // vanilla button (incl. "Done") — untouched
            var chooser = __instance.interactingAgent;
            var result = DialogueMenuCore.Choose(__instance.UID, choice);
            EightPlayersPlugin.Log.LogInfo(
                $"EPMENU choice uid={__instance.UID} playerUid={chooser?.UID.ToString() ?? "?"} depth={result.Depth} \"{choice}\""
                + (result.Reply != null ? " reply=canned" : "") + (result.HasNext ? " next=prefetched" : " leaf"));
            HttpChannel.Broadcast(new JObject
            {
                ["event"] = "menu_choice",
                ["uid"] = __instance.UID,
                ["playerUid"] = chooser != null ? (JToken)chooser.UID : JValue.CreateNull(),
                ["choice"] = choice,
                ["path"] = new JArray(result.Path),
                ["depth"] = result.Depth,
                ["reply"] = result.Reply != null ? (JToken)result.Reply : JValue.CreateNull(),
                ["leaf"] = !result.HasNext,
            });
            if (result.Reply != null)
                GameStateApi.Say(__instance.UID, result.Reply);
            if (result.HasNext && chooser != null && chooser.worldSpaceGUI != null)
            {
                // vanilla's post-press redraw path (WorldSpaceGUI.
                // RefreshObjectButtons2) re-runs DetermineButtons next frame —
                // patch 1 serves the NEW tree level. Its re-show gate is
                // `buttonsBeforePush > 2`; force it so short menus stay open.
                __instance.buttonsBeforePush = int.MaxValue;
                chooser.worldSpaceGUI.RefreshObjectButtons(__instance);
            }
            else
            {
                __instance.StopInteraction();
            }
            return false;
        }
    }
}
