using System;
using System.Reflection;
using HarmonyLib;
using Okomotive.FarLoneSails;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// Mirrors vehicle button / switch activations between the two games.
    /// When the local game fires EnterAction/ExitAction on a button, the partner fires the same
    /// method on the same object (found by hierarchy path).
    /// Only concrete overrides are patched (base/empty methods can be shared native code in IL2CPP).
    /// </summary>
    public static class ButtonSync
    {
        public const byte SwitchEnter = 0, SwitchExit = 1, PushEnter = 2, ForceEnter = 3, ForceExit = 4;

        [ThreadStatic] private static bool applyingRemote;
        public static int Sent, Received, Failed;

        public static void Install(Harmony h)
        {
            TryPatch(h, typeof(ButtonSwitch), "EnterAction", nameof(OnSwitchEnter));
            TryPatch(h, typeof(ButtonSwitch), "ExitAction", nameof(OnSwitchExit));
            TryPatch(h, typeof(ButtonPushGeneric), "EnterAction", nameof(OnPushEnter));
            TryPatch(h, typeof(ButtonForce), "EnterAction", nameof(OnForceEnter));
            TryPatch(h, typeof(ButtonForce), "ExitAction", nameof(OnForceExit));
        }

        private static void TryPatch(Harmony h, Type type, string method, string handler)
        {
            try
            {
                MethodInfo target = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (target == null || target.DeclaringType != type)
                {
                    FarCoopPlugin.Logger.LogWarning($"ButtonSync: {type.Name}.{method} not found, skipped");
                    return;
                }
                h.Patch(target, prefix: new HarmonyMethod(typeof(ButtonSync).GetMethod(handler, BindingFlags.Static | BindingFlags.Public)));
                FarCoopPlugin.Logger.LogInfo($"ButtonSync: patched {type.Name}.{method}");
            }
            catch (Exception e)
            {
                FarCoopPlugin.Logger.LogWarning($"ButtonSync: could not patch {type.Name}.{method}: {e.Message}");
            }
        }

        public static void OnSwitchEnter(ButtonSwitch __instance) => Report(__instance, SwitchEnter);
        public static void OnSwitchExit(ButtonSwitch __instance) => Report(__instance, SwitchExit);
        public static void OnPushEnter(ButtonPushGeneric __instance) => Report(__instance, PushEnter);
        public static void OnForceEnter(ButtonForce __instance) => Report(__instance, ForceEnter);
        public static void OnForceExit(ButtonForce __instance) => Report(__instance, ForceExit);

        private static void Report(Component button, byte kind)
        {
            if (applyingRemote) return;
            try
            {
                if (!FarCoopPlugin.CfgSyncButtons.Value) return;
                var mgr = CoopSession.Instance;
                if (mgr == null || !mgr.IsConnected) return;
                mgr.SendButton(kind, GameAccess.GetPath(button.transform));
                Sent++;
            }
            catch { }
        }

        public static void ApplyRemote(byte kind, string path)
        {
            Received++;
            var go = GameAccess.FindByPath(path);
            if (go == null)
            {
                Failed++;
                FarCoopPlugin.Logger.LogDebug("ButtonSync: object not found " + path);
                return;
            }

            applyingRemote = true;
            try
            {
                switch (kind)
                {
                    case SwitchEnter: GameAccess.GetComp<ButtonSwitch>(go)?.EnterAction(); break;
                    case SwitchExit: GameAccess.GetComp<ButtonSwitch>(go)?.ExitAction(); break;
                    case PushEnter: GameAccess.GetComp<ButtonPushGeneric>(go)?.EnterAction(); break;
                    case ForceEnter: GameAccess.GetComp<ButtonForce>(go)?.EnterAction(); break;
                    case ForceExit: GameAccess.GetComp<ButtonForce>(go)?.ExitAction(); break;
                }
            }
            catch (Exception e)
            {
                Failed++;
                FarCoopPlugin.Logger.LogDebug($"ButtonSync: apply {kind} on {path} failed: {e.Message}");
            }
            finally { applyingRemote = false; }
        }
    }
}
