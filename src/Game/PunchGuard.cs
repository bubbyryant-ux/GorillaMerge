using System;
using GorillaLocomotion;
using HarmonyLib;
using UnityEngine;

namespace GorillaMerge
{
    /// <summary>
    /// Stops a fist punch from bouncing you off blocks. Gorilla Tag pushes your body away from anything your
    /// hand moves into, so a hard punch throws you backwards. When a closed fist hits one of our blocks fast,
    /// that hand stops pushing your body until it leaves the block. Open hands and slow fists climb as usual.
    /// </summary>
    internal static class PunchGuard
    {
        /// <summary>[0] left, [1] right: fist closed and swinging fast. Set by HandBuilder every frame.</summary>
        public static readonly bool[] Punching = new bool[2];
        private static readonly bool[] _active = new bool[2];

        public static void Install(Harmony harmony)
        {
            try
            {
                var original = AccessTools.Method(typeof(GTPlayer.HandState), "FirstIteration");
                harmony.Patch(original,
                    prefix: new HarmonyMethod(typeof(PunchGuard), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(PunchGuard), nameof(Postfix)));
                Plugin.Log.LogInfo("Punch guard on: fist punches won't bounce you off blocks.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Punch guard not installed, punches will bounce you: " + e.Message); }
        }

        private static void Prefix(ref Vector3 totalMove, ref int divisor, out Vector4 __state)
        {
            __state = new Vector4(totalMove.x, totalMove.y, totalMove.z, divisor);
        }

        private static void Postfix(ref GTPlayer.HandState __instance, ref Vector3 totalMove, ref int divisor, Vector4 __state)
        {
            int h = __instance.isLeftHand ? 0 : 1;
            if (!__instance.isColliding) { _active[h] = false; return; }
            if (!_active[h])
            {
                // Only a fresh touch starts a punch, so leaning a slow fist on a wall still climbs.
                if (__instance.wasColliding || !Punching[h]) return;
                var col = __instance.lastHitInfo.collider;
                if (col == null || !col.TryGetComponent<VoxelCollider>(out _)) return;
                _active[h] = true;
            }
            // Undo this hand's push on your body.
            totalMove = new Vector3(__state.x, __state.y, __state.z);
            divisor = (int)__state.w;
        }
    }
}
