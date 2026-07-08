using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EightPlayers
{
    // Programmatic gameplay input for the live debug harness: overwrite the
    // game's per-frame held-input arrays AFTER PlayerControl.Update read the
    // real devices and BEFORE Movement/Agent consume them in FixedUpdate.
    // Works in both controller modes (heldAxisX/Y for gamepad,
    // heldLeftK/RightK/UpK/DownK for keyboard) and needs no fake devices.
    //
    // Command-channel surface (player 1):
    //   move <dx> <dy> <seconds>     hold a direction (unit-ish vector)
    //   walkto <x> <y> [timeout]     steer toward a point (direct line, no
    //                                pathfinding - route around walls with
    //                                waypoints)
    //   hold <button> <seconds>      attack|interact|special|useitem|cancel
    //   stop                         clear all virtual input
    internal static class VirtualInput
    {
        private static Vector2 _dir;
        private static float _dirUntil;
        private static Vector2? _target;
        private static float _targetUntil;
        private static readonly Dictionary<string, float> _buttonsUntil = new Dictionary<string, float>();

        internal static void Move(Vector2 dir, float seconds)
        {
            _dir = dir.normalized;
            _dirUntil = Time.unscaledTime + seconds;
            _target = null;
        }

        internal static void WalkTo(Vector2 target, float timeout)
        {
            _target = target;
            _targetUntil = Time.unscaledTime + timeout;
            _dirUntil = 0f;
        }

        internal static void Hold(string button, float seconds) =>
            _buttonsUntil[button] = Time.unscaledTime + seconds;

        internal static void Stop()
        {
            _dirUntil = 0f;
            _target = null;
            _buttonsUntil.Clear();
        }

        internal static string Describe()
        {
            var gc = GameController.gameController;
            var agent = gc?.playerAgent;
            Vector2 p = agent != null ? (Vector2)agent.tr.position : Vector2.zero;
            string mode = _target != null ? $"walkto {_target.Value}" : (Time.unscaledTime < _dirUntil ? $"move {_dir}" : "idle");
            return $"virtualinput {mode} buttons={_buttonsUntil.Count} playerPos=({p.x:0.##},{p.y:0.##})";
        }

        // Runs after PlayerControl.Update each frame (see patch below).
        internal static void Apply()
        {
            var gc = GameController.gameController;
            var pc = gc != null ? gc.playerControl : null;
            var agent = gc != null ? gc.playerAgent : null;
            if (pc == null || agent == null || agent.dead || pc.heldAxisX == null || pc.heldAxisX.Length == 0)
                return;

            Vector2 dir = Vector2.zero;
            if (_target != null)
            {
                Vector2 delta = _target.Value - (Vector2)agent.tr.position;
                if (delta.magnitude < 0.4f || Time.unscaledTime > _targetUntil)
                    _target = null;
                else
                    dir = delta.normalized;
            }
            else if (Time.unscaledTime < _dirUntil)
            {
                dir = _dir;
            }

            if (dir != Vector2.zero)
            {
                // Gamepad path reads the axes; keyboard path reads the bools.
                pc.heldAxisX[0] = dir.x;
                pc.heldAxisY[0] = dir.y;
                pc.heldLeftK[0] = dir.x < -0.3f;
                pc.heldRightK[0] = dir.x > 0.3f;
                pc.heldUpK[0] = dir.y > 0.3f;
                pc.heldDownK[0] = dir.y < -0.3f;
            }

            if (_buttonsUntil.Count == 0)
                return;
            var expired = new List<string>();
            foreach (var kv in _buttonsUntil)
            {
                bool on = Time.unscaledTime < kv.Value;
                if (!on)
                    expired.Add(kv.Key);
                switch (kv.Key)
                {
                    case "attack": pc.heldAttack[0] = on; break;
                    case "interact": pc.heldInteract[0] = on; break;
                    case "special": pc.heldSpecialAbility[0] = on; break;
                    case "useitem": pc.heldUseItem[0] = on; break;
                    case "cancel": pc.heldCancel[0] = on; break;
                }
            }
            foreach (var k in expired)
                _buttonsUntil.Remove(k);
        }
    }

    [HarmonyPatch(typeof(PlayerControl), "Update")]
    internal static class VirtualInput_Patch
    {
        private static void Postfix() => VirtualInput.Apply();
    }
}
