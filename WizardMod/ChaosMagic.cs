using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace WizardMod
{
    // The Wizard's special ability: every press casts a random spell.
    //
    // Implemented the same way the game's own abilities are (see MindControl in
    // StatusEffects.PressedSpecialAbility and RechargeSpecialAbility2): the ability
    // is an InvItem in inventory.equippedSpecialAbility, invItemCount is the
    // cooldown counter (0 = ready), and effects go through the game's own
    // projectile/teleport/status APIs, which already sync in multiplayer.
    [HarmonyPatch]
    public static class ChaosMagicAbility
    {
        public const string Name = "ChaosMagic";
        private const int CooldownSeconds = 4;

        private static bool spriteInjected;

        // ---- item definition -------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InvItem), nameof(InvItem.SetupDetails))]
        public static void SetupChaosMagicItem(InvItem __instance)
        {
            if (__instance.invItemName != Name) return;
            InjectSprite();
            __instance.LoadItemSprite(Name);
            __instance.stackable = true;
            __instance.initCount = 0;
            __instance.lowCountThreshold = 100;
        }

        private static void InjectSprite()
        {
            if (spriteInjected) return;
            GameResources gr = GameController.gameController?.gameResources;
            if (gr?.itemDic == null) return;
            if (!gr.itemDic.ContainsKey(Name))
            {
                byte[] png = WizardModPlugin.LoadEmbedded("ChaosMagic.png");
                if (png != null)
                {
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    tex.LoadImage(png);
                    tex.filterMode = FilterMode.Point;
                    gr.itemDic[Name] = Sprite.Create(
                        tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 64f);
                }
            }
            spriteInjected = true;
        }

        // ---- pressing the ability button --------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StatusEffects), nameof(StatusEffects.PressedSpecialAbility))]
        public static bool PressChaosMagic(StatusEffects __instance, ref bool __result)
        {
            Agent agent = __instance.agent;
            if (agent == null || agent.specialAbility != Name) return true;

            __result = false;
            if (agent.ghost || agent.teleporting) return false;
            InvItem item = agent.inventory.equippedSpecialAbility;
            if (item == null) return false;
            GameController gc = GameController.gameController;

            if (item.invItemCount != 0)
            {
                gc.audioHandler.Play(agent, "CantDo");
                return false;
            }

            item.invItemCount = CooldownSeconds;
            __instance.StartCoroutine(Recharge(__instance, item));
            SetSlotUsable(agent, false);

            try
            {
                CastRandomSpell(agent, gc);
            }
            catch (System.Exception e)
            {
                WizardModPlugin.Log.LogWarning("ChaosMagic cast failed: " + e);
            }
            __result = true;
            return false;
        }

        // Mirrors the MindControl branch of StatusEffects.RechargeSpecialAbility2.
        private static IEnumerator Recharge(StatusEffects se, InvItem item)
        {
            Agent agent = se.agent;
            while (item.invItemCount > 0 && agent.inventory.equippedSpecialAbility == item)
            {
                yield return new WaitForSeconds(1f);
                if (!se.CanRecharge()) continue;
                item.invItemCount--;
                if (item.invItemCount == 0)
                {
                    try
                    {
                        se.CreateBuffText("Recharged", agent.objectNetID);
                        GameController.gameController.audioHandler.Play(agent, "Recharge");
                    }
                    catch { }
                    SetSlotUsable(agent, true);
                }
            }
        }

        // HUD may be absent (headless tests, remote players) - never let it kill a cast.
        private static void SetSlotUsable(Agent agent, bool usable)
        {
            try
            {
                EquippedItemSlot slot = agent.inventory.buffDisplay?.specialAbilitySlot;
                if (slot == null) return;
                if (usable) slot.MakeUsable();
                else slot.MakeNotUsable();
            }
            catch { }
        }

        // ---- the spells --------------------------------------------------------

        private static void CastRandomSpell(Agent a, GameController gc)
        {
            switch (Random.Range(0, 8))
            {
                case 0: Bolt(a, gc, bulletStatus.Fireball, "FIREBALL!"); break;
                case 1: Bolt(a, gc, bulletStatus.FreezeRay, "Frost bolt!"); break;
                case 2: Bolt(a, gc, bulletStatus.Taser, "Lightning!"); break;
                case 3: Bolt(a, gc, bulletStatus.Shrink, "Shrink ray!"); break;
                case 4: Bolt(a, gc, bulletStatus.GhostBlaster, "Spirit blast!"); break;
                case 5: Bolt(a, gc, bulletStatus.Tranquilizer, "Sleeeeep..."); break;
                case 6: Blink(a, gc, 3f, 8f, "Blink!"); break;
                default: WildSurge(a, gc); break;
            }
        }

        // Fires in the direction the wizard is aiming, like the MindControl ability.
        private static void Bolt(Agent a, GameController gc, bulletStatus type, string shout)
        {
            WizardModPlugin.Log.LogInfo("ChaosMagic: " + type + " bolt");
            a.Say(shout);
            gc.audioHandler.Play(a, "MindControlFire");
            a.gun.spawnBullet(type, null, -1, specialAbility: true);
            gc.spawnerMain.SpawnNoise(a.tr.position, 2f, null, null, a);
            try
            {
                if (a.isPlayer > 0 && a.localPlayer)
                    gc.ScreenBump(2f, 30, a);
                gc.playerControl.Vibrate(a.isPlayer, 0.25f, 0.2f);
            }
            catch { }
        }

        private static void Blink(Agent a, GameController gc, float near, float far, string shout)
        {
            Vector2 spot = gc.tileInfo.FindLocationNearLocation(
                a.tr.position, a, near, far,
                accountForObstacles: true, notInside: false,
                dontCareAboutDanger: true, teleporting: true, accountForWalls: false);
            if (spot != Vector2.zero)
            {
                WizardModPlugin.Log.LogInfo("ChaosMagic: blink " + near + "-" + far);
                if (shout != "") a.Say(shout);
                a.Teleport(spot, bringOthers: false, immediate: true);
            }
            else
            {
                a.Say("...the spell fizzles.");
                gc.audioHandler.Play(a, "CantDo");
            }
        }

        private static void WildSurge(Agent a, GameController gc)
        {
            WizardModPlugin.Log.LogInfo("ChaosMagic: wild surge");
            switch (Random.Range(0, 5))
            {
                case 0:
                    a.Say("WILD SURGE! I AM MIGHTY!");
                    a.statusEffects.AddStatusEffect("Giant", 12);
                    break;
                case 1:
                    a.Say("Wild surge! Gotta go fast!");
                    a.statusEffects.AddStatusEffect("Fast", 10);
                    break;
                case 2:
                    a.Say("Wild surge! Where did I go?");
                    a.statusEffects.AddStatusEffect("InvisibleLimited", 8);
                    break;
                case 3:
                    a.Say("wild surge! oh no, i'm tiny");
                    a.statusEffects.AddStatusEffect("Shrunk", 10);
                    break;
                default:
                    a.Say("WILD SURGE! WHEEEE!");
                    Blink(a, gc, 8f, 14f, "");
                    break;
            }
        }
    }
}
