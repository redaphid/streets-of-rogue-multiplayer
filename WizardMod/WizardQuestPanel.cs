using HarmonyLib;

namespace WizardMod
{
    // Restores the Wizard's Big Quest text on the in-game map/quest screen.
    //
    // QuestSlotBig.GetQuestInfo() sets the title/description from NameDB and then
    // runs `switch (agent.bigQuest)` to append per-quest progress. Every stock
    // character has a case; the Wizard's bigQuest ("Wizard") does not, so it falls
    // into the `default:` branch which WIPES both fields:
    //     questTitle.text = ""; questDescription.text = "";
    // That is why the panel showed blank even though our NameDB patch supplies the
    // "Chaos Ascendant" strings. A postfix runs AFTER that default case, so it can
    // put the text back.
    [HarmonyPatch]
    public static class WizardQuestPanel
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QuestSlotBig), nameof(QuestSlotBig.GetQuestInfo))]
        public static void RestoreWizardText(QuestSlotBig __instance)
        {
            try
            {
                Agent agent = __instance.agent;
                if (agent == null || agent.bigQuest != WizardBigQuest.Quest) return;

                GameController gc = GameController.gameController;
                if (gc == null) return;

                // On the Mayor's floor the vanilla method shows a completed/failed end
                // message and returns BEFORE the switch, so the default never runs and
                // there is nothing to restore. Leave that end-of-run text alone.
                if (gc.loadLevel != null && gc.loadLevel.LevelContainsMayor()) return;

                // Title: pull through NameDB so it stays consistent with our GetName
                // patch (which returns "Chaos Ascendant"), with a literal fallback.
                string title = null;
                try { title = gc.nameDB.GetName(WizardBigQuest.UnlockName, "Unlock"); } catch { }
                if (string.IsNullOrEmpty(title)) title = "Chaos Ascendant";

                // Description: same source. This D2_ string already embeds the live
                // "Chaos kills: N/8" progress (see WizardBigQuest.LocalKills()).
                string desc = null;
                try { desc = gc.nameDB.GetName("D2_" + WizardBigQuest.UnlockName, "Unlock"); } catch { }
                if (string.IsNullOrEmpty(desc)) desc = "Slay " + WizardBigQuest.TargetKills +
                    " foes with Chaos Magic. Prove the arcane bends to your will.\nChaos kills: " +
                    WizardBigQuest.LocalKills() + "/" + WizardBigQuest.TargetKills;

                if (__instance.questTitle != null)
                {
                    __instance.questTitle.gameObject.SetActive(true);
                    __instance.questTitle.text = title;
                }
                if (__instance.questDescription != null)
                    __instance.questDescription.text = desc;
            }
            catch (System.Exception e)
            {
                WizardModPlugin.Log.LogWarning("Wizard quest panel restore failed: " + e);
            }
        }
    }
}
