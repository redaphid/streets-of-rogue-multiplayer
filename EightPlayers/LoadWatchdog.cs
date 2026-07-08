using UnityEngine;

namespace EightPlayers
{
    // Last line of defense against silent level-load wedges. Three distinct
    // root causes have been found and fixed on this path (StatusEffectDisplay
    // NRE killing the load coroutine, partial randomListTable, stale
    // setupRandomness) and a fourth wedge shape still appeared on the solo
    // 0->1 follow reload with NO logged exception: player spawned in the
    // holding area, agents=1, objects=0, loadCompleteReally never set.
    // Root-causing each variant is whack-a-mole; the watchdog converts any
    // "half-loaded forever" state into a bounded level reload.
    internal static class LoadWatchdog
    {
        private const float StuckSeconds = 45f;
        private const int MaxReloads = 3;

        private static float _stuckSince = -1f;
        private static int _reloads;
        private static float _nextAllowedAt;

        internal static void Tick()
        {
            var gc = GameController.gameController;
            if (gc == null || gc.sessionDataBig == null || gc.loadLevel == null
                || gc.playerAgent == null || gc.sessionDataBig.curLevel < 1)
            {
                _stuckSince = -1f;
                return;
            }
            bool wedged = !gc.loadCompleteReally
                && gc.objectRealList != null && gc.objectRealList.Count == 0;
            if (!wedged)
            {
                _stuckSince = -1f;
                return;
            }
            if (_stuckSince < 0f)
            {
                _stuckSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _stuckSince < StuckSeconds || Time.unscaledTime < _nextAllowedAt)
                return;

            if (_reloads >= MaxReloads)
            {
                // Log once per stuck episode, then stay quiet.
                EightPlayersPlugin.Log.LogError(
                    $"LOADWATCHDOG level {gc.sessionDataBig.curLevel} still empty after {MaxReloads} reloads - giving up");
                _stuckSince = -1f;
                _nextAllowedAt = Time.unscaledTime + 300f;
                return;
            }
            _reloads++;
            _nextAllowedAt = Time.unscaledTime + 90f;
            EightPlayersPlugin.Log.LogWarning(
                $"LOADWATCHDOG level {gc.sessionDataBig.curLevel} empty (agents={gc.agentList?.Count}, objects=0) "
                + $"{StuckSeconds:0}s after load began - forcing reload (attempt {_reloads}/{MaxReloads})");
            ForceReload();
            _stuckSince = -1f;
        }

        /// <summary>Regenerate the current level via the vanilla next-level
        /// path (the same lever the world-divergence heal uses).</summary>
        internal static void ForceReload()
        {
            var gc = GameController.gameController;
            if (gc == null || gc.sessionDataBig == null || gc.loadLevel == null)
                return;
            gc.sessionDataBig.curLevel -= 1;
            gc.loadLevel.NextLevel();
        }
    }
}
