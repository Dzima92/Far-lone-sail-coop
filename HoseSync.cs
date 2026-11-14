using System;
using System.Reflection;
using HarmonyLib;
using Okomotive.FarLoneSails;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// The fire hose (VehicleHydrant) is grabbed/released with the "take" action, which plants its
    /// rope on the character. When the local player does that, the partner's game makes the
    /// partner's body copy do the same, so both players see who holds the hose and the water.
    /// </summary>
    public static class HoseSync
    {
        private static bool applying;
        private static MethodInfo pressTake;
        public static int Sent, Received;

        public static void Install(Harmony h)
        {
            try
            {
                pressTake = typeof(VehicleHydrant).GetMethod("OnPressTake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pressTake == null) { FarCoopPlugin.Logger.LogWarning("HoseSync: VehicleHydrant.OnPressTake not found"); return; }
                h.Patch(pressTake, postfix: new HarmonyMethod(typeof(HoseSync).GetMethod(nameof(AfterPressTake), BindingFlags.Static | BindingFlags.Public)));
                FarCoopPlugin.Logger.LogInfo("HoseSync: patched VehicleHydrant.OnPressTake");
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("HoseSync: patch failed: " + e.Message); }
        }

        private static bool IsPlanted(VehicleHydrant hy)
        {
            try { var r = hy.rope; return r != null && r.IsPlanted(); } catch { return false; }
        }

        public static void AfterPressTake(VehicleHydrant __instance)
        {
            if (applying) return;
            try
            {
                var s = CoopSession.Instance;
                if (s == null || !s.IsConnected) return;
                Character active = null;
                try { active = __instance.activePlayer; } catch { }
                // only report what the LOCAL player did (never the partner's body copy)
                if (active != null && PartnerInput.IsPuppet(active.Pointer)) return;
                bool planted = IsPlanted(__instance);
                if (planted && (active == null || GameAccess.LocalCharacter == null || active.Pointer != GameAccess.LocalCharacter.Pointer)) return;
                s.SendHose(GameAccess.GetPath(__instance.transform), planted);
                Sent++;
            }
            catch { }
        }

        public static void ApplyRemote(string path, bool planted, Character puppet)
        {
            Received++;
            if (pressTake == null || puppet == null) return;
            var go = GameAccess.FindByPath(path);
            var hy = GameAccess.GetComp<VehicleHydrant>(go);
            if (hy == null) { FarCoopPlugin.Logger.LogDebug("HoseSync: hydrant not found " + path); return; }
            bool now = IsPlanted(hy);
            if (now == planted) return;
            Character active = null;
            try { active = hy.activePlayer; } catch { }
            // releasing: only if the partner's copy is the one holding it
            if (!planted && active != null && active.Pointer != puppet.Pointer) return;
            applying = true;
            try { pressTake.Invoke(hy, new object[] { puppet }); }
            catch (Exception e) { FarCoopPlugin.Logger.LogDebug("HoseSync: apply failed: " + e.Message); }
            finally { applying = false; }
            FarCoopPlugin.Logger.LogInfo("Partner " + (planted ? "took" : "released") + " the hose");
            Telemetry.Event("partner " + (planted ? "took" : "released") + " hose");
        }
    }
}
