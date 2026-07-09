using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace WizardMod
{
    // The Wizard's Big Quest: "Chaos Ascendant" - slay a number of foes using the
    // Chaos Magic ability. Kills are attributed by tagging the bolts the ability
    // fires (Bullet.cameFromWeapon) and, when a tagged victim dies, counting it.
    //
    // Counting is server-authoritative, mirroring how the game's own big quests only
    // tally on the host (see Quests.AddBigQuestPoints, which early-returns off-server).
    // On completion the wizard gets a big in-run payoff rather than a meta-unlock.
    [HarmonyPatch]
    public static class WizardBigQuest
    {
        public const string Quest = WizardCharacter.AgentName; // "Wizard"
        public const string UnlockName = Quest + "_BQ";
        public const int TargetKills = 8;
        public const string BulletTag = "ChaosMagic";

        // How long after a Chaos bolt strikes a foe a following kill still counts
        // (covers fireball burn / freeze-shatter delays without crediting unrelated kills).
        private const float AttributionWindow = 6f;

        // Per-run progress lives on the wizard Agent instance: it survives floor
        // transitions (same Agent streams across levels) and resets naturally for a
        // new run (new Agent).
        private static readonly ConditionalWeakTable<Agent, Progress> progress =
            new ConditionalWeakTable<Agent, Progress>();

        // Foes recently struck by a Wizard's Chaos bolt, so the kill that follows can
        // be attributed to the ability rather than a knife or a picked-up gun.
        private static readonly ConditionalWeakTable<Agent, HitTag> chaosHits =
            new ConditionalWeakTable<Agent, HitTag>();

        private class Progress { public int kills; public bool completed; }
        private class HitTag { public Agent wizard; public float time; }

        // Read by the NameDB patch so the quest panel shows live progress.
        public static int LocalKills()
        {
            Agent w = LocalWizard();
            return w == null ? 0 : progress.GetOrCreateValue(w).kills;
        }

        private static Agent LocalWizard()
        {
            GameController gc = GameController.gameController;
            if (gc == null) return null;
            if (gc.playerAgent != null && gc.playerAgent.agentName == Quest) return gc.playerAgent;
            if (gc.playerAgentList != null)
                foreach (Agent a in gc.playerAgentList)
                    if (a != null && a.localPlayer && a.agentName == Quest) return a;
            return null;
        }

        // Tag a foe struck by a Wizard's Chaos bolt.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BulletHitbox), nameof(BulletHitbox.HitAftermath))]
        public static void TagChaosVictim(BulletHitbox __instance, Agent agent)
        {
            Bullet b = __instance.myBullet;
            if (b == null || agent == null || agent.isPlayer != 0) return;
            if (b.cameFromWeapon != BulletTag) return;
            if (b.agent == null || b.agent.agentName != Quest) return;
            HitTag tag = chaosHits.GetOrCreateValue(agent);
            tag.wizard = b.agent;
            tag.time = Time.time;
        }

        // The game fires AddBigQuestPoints(killer, victim, "Neutralize") once per kill
        // on the server - the canonical per-kill hook. Count it if the victim was a
        // recent Chaos-bolt victim of this wizard.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Quests), nameof(Quests.AddBigQuestPoints),
            new[] { typeof(Agent), typeof(Agent), typeof(InvItem), typeof(string) })]
        public static void CountChaosKill(Agent myAgent, Agent otherAgent, string pointsType)
        {
            GameController gc = GameController.gameController;
            if (myAgent == null || otherAgent == null || gc == null || !gc.serverPlayer) return;
            if (myAgent.bigQuest != Quest || pointsType != "Neutralize") return;

            HitTag tag;
            if (!chaosHits.TryGetValue(otherAgent, out tag)) return;
            chaosHits.Remove(otherAgent);
            if (tag.wizard != myAgent || Time.time - tag.time > AttributionWindow) return;

            Progress pr = progress.GetOrCreateValue(myAgent);
            if (pr.completed) return;
            pr.kills++;
            try { myAgent.Say("Chaos kills: " + pr.kills + "/" + TargetKills); } catch { }
            if (pr.kills >= TargetKills)
            {
                pr.completed = true;
                Complete(myAgent, gc);
            }
        }

        private static void Complete(Agent w, GameController gc)
        {
            WizardModPlugin.Log.LogInfo("Wizard Big Quest 'Chaos Ascendant' complete!");
            MarkUnlockComplete(gc);
            try
            {
                if (gc.sessionData.agentsCompletedBigQuest != null &&
                    !gc.sessionData.agentsCompletedBigQuest.Contains(w.isPlayer))
                    gc.sessionData.agentsCompletedBigQuest.Add(w.isPlayer);
            }
            catch { }
            try { gc.spawnerMain.SpawnStatusText(w, "BigQuestCompleted", "BigQuest", "Interface"); } catch { }
            try { w.Say("CHAOS ASCENDANT!"); } catch { }
            GrantPayoff(w);
        }

        private static void MarkUnlockComplete(GameController gc)
        {
            try
            {
                if (gc.sessionDataBig?.unlocks == null) return;
                foreach (Unlock u in gc.sessionDataBig.unlocks)
                {
                    if (u.unlockName == UnlockName && u.unlockType == "BigQuest")
                    {
                        u.unlocked = true;
                        gc.unlocks.SaveUnlockData();
                        return;
                    }
                }
            }
            catch (System.Exception e) { WizardModPlugin.Log.LogWarning("BQ unlock mark failed: " + e); }
        }

        // Big in-run payoff: full heal, an arcane power surge, a burst of XP, and a
        // loot stash - then Chaos Magic is recharged for the victory lap.
        private static void GrantPayoff(Agent w)
        {
            try { w.currentHealth = (int)w.healthMax; } catch { }
            try
            {
                w.statusEffects.AddStatusEffect("Giant", 20);
                w.statusEffects.AddStatusEffect("Fast", 30);
            }
            catch { }
            try { for (int i = 0; i < 3; i++) w.skillPoints.AddPoints("KillPoints"); } catch { }
            try
            {
                w.inventory.AddItem("Money", 1000);
                w.inventory.DontPlayPickupSounds(yesNo: true);
                w.inventory.AddRandWeapon();
                w.inventory.AddRandWeapon();
                w.inventory.DontPlayPickupSounds(yesNo: false);
            }
            catch (System.Exception e) { WizardModPlugin.Log.LogWarning("BQ loot failed: " + e); }
            try
            {
                InvItem ability = w.inventory.equippedSpecialAbility;
                if (ability != null) ability.invItemCount = 0;
            }
            catch { }
        }
    }
}
