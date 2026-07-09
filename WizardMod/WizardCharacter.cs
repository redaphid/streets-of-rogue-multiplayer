using HarmonyLib;
using UnityEngine;

namespace WizardMod
{
    // Injects the "Wizard" playable character: roster slot, unlock, name/description,
    // sprites (reuses the Vampire body, tinted purple), stats and the Chaos Magic ability.
    [HarmonyPatch]
    public static class WizardCharacter
    {
        public const string AgentName = "Wizard";
        private const string BaseBody = "Vampire";
        private static readonly Color32 RobeColor = new Color32(94, 0, 148, 255);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterSelect), nameof(CharacterSelect.RealAwake))]
        public static void AddToRoster(CharacterSelect __instance)
        {
            if (!__instance.slotAgentTypes.Contains(AgentName))
                __instance.slotAgentTypes.Add(AgentName);
            if (!__instance.slotAgentTypesComplete.Contains(AgentName))
                __instance.slotAgentTypesComplete.Add(AgentName);
            AliasPortraitSprites();
            WizardModPlugin.Log.LogInfo("Wizard added to character select roster");
        }

        // The select screen looks up portrait sprites by "<agentName>S" in GameResources
        // dictionaries; alias the Wizard to an existing body so the lookups succeed.
        private static void AliasPortraitSprites()
        {
            GameResources gr = GameController.gameController?.gameResources;
            if (gr == null) return;
            if (gr.bodyDic != null && gr.bodyDic.ContainsKey(BaseBody + "S") && !gr.bodyDic.ContainsKey(AgentName + "S"))
                gr.bodyDic[AgentName + "S"] = gr.bodyDic[BaseBody + "S"];
            if (gr.bodyGDic != null && gr.bodyGDic.ContainsKey(BaseBody + "S") && !gr.bodyGDic.ContainsKey(AgentName + "S"))
                gr.bodyGDic[AgentName + "S"] = gr.bodyGDic[BaseBody + "S"];
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Unlocks), nameof(Unlocks.LoadInitialUnlocks))]
        public static void AddWizardUnlock(Unlocks __instance)
        {
            GameController gc = GameController.gameController;
            Unlock unlock = __instance.AddUnlock(AgentName, "Agent", isUnlocked: true);
            unlock.unlocked = true;
            // LoadInitialUnlocks fans unlocks into per-type lists before this postfix
            // runs, so the Wizard has to be added to agentUnlocks by hand.
            if (gc.sessionDataBig.agentUnlocks != null && !gc.sessionDataBig.agentUnlocks.Contains(unlock))
            {
                gc.sessionDataBig.agentUnlocks.Add(unlock);
                Unlock.agentCount++;
            }
            WizardModPlugin.Log.LogInfo("Wizard unlock registered (unlocked=" + unlock.unlocked + ")");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NameDB), nameof(NameDB.GetName))]
        public static bool WizardNames(string myName, string type, ref string __result)
        {
            if (myName == AgentName && type == "Agent")
            {
                __result = "Wizard";
                return false;
            }
            if (myName == AgentName && type == "Description")
            {
                __result = "A frail scholar of the arcane. Their Chaos Magic casts a different random spell " +
                           "every time - fire, frost, lightning, shrinking, blinking... even the wizard " +
                           "doesn't know what comes out next.";
                return false;
            }
            if (myName == AgentName + "_BQ" && type == "Unlock")
            {
                __result = "";
                return false;
            }
            if (myName == ChaosMagicAbility.Name && type == "Item")
            {
                __result = "Chaos Magic";
                return false;
            }
            if (myName == ChaosMagicAbility.Name && type == "Description")
            {
                __result = "Casts a completely random spell: fireball, frost bolt, lightning, shrink ray, " +
                           "spirit blast, sleep dart, a short-range blink... or a Wild Surge that does " +
                           "something unpredictable to YOU. Embrace the chaos.";
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Agent), nameof(Agent.SetupAgentStats))]
        public static void WizardStats(Agent __instance)
        {
            if (__instance.agentName != AgentName) return;
            Agent a = __instance;

            // Glass cannon: fast and perceptive, folds like wet paper in a fistfight.
            a.SetStrength(1);
            a.SetEndurance(1);
            a.SetAccuracy(3);
            a.SetSpeed(3);
            a.modMeleeSkill = 0;
            a.modGunSkill = 1;
            a.modToughness = 0;
            a.modVigilant = 1;

            a.statusEffects.GiveSpecialAbility("ChaosMagic");
            a.inventory.AddItemPlayerStart("Knife", 0);
            a.agentHitboxScript.legsColor = RobeColor;
        }

        // In-world body sprites are looked up as "<agentName><Direction>"; point the
        // Wizard's at the base body's sprites.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AgentHitbox), nameof(AgentHitbox.SetupBodyStrings))]
        public static void WizardBody(AgentHitbox __instance)
        {
            if (__instance.agent == null || __instance.agent.agentName != AgentName) return;
            for (int i = 0; i < __instance.agentBodyStrings.Count; i++)
            {
                string s = __instance.agentBodyStrings[i];
                if (s.StartsWith(AgentName))
                    __instance.agentBodyStrings[i] = BaseBody + s.Substring(AgentName.Length);
            }
        }
    }
}
