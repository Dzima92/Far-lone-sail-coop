using System;
using System.Reflection;
using HarmonyLib;
using Okomotive.FarLoneSails;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// The vehicle hides its front cover (so you see inside) when "a character" enters it, and shows it
    /// when "a character" leaves. With other players' bodies around, one of THEM leaving made the hull
    /// opaque for you while you were still inside. Here the cover follows only YOUR own character.
    /// </summary>
    public static class CoverSync
    {
        private static bool lastByZoom;
        private static bool applying;
        private static float mismatchSince = -1f;
        private static Vehicle vehicle;
        private static float nextFind;

        public static void Install(Harmony h)
        {
            try
            {
                foreach (var m in typeof(Vehicle).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "SetFrontCover" || m.GetParameters().Length != 2) continue;
                    h.Patch(m, prefix: new HarmonyMethod(typeof(CoverSync).GetMethod(nameof(Pre2), BindingFlags.Static | BindingFlags.Public)));
                }
                FarCoopPlugin.Logger.LogInfo("CoverSync: watching Vehicle.SetFrontCover");
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("CoverSync: patch failed: " + e.Message); }
        }

        // the 2-argument overload tells us whether the camera zoom caused it (leave those alone)
        public static void Pre2(bool state, bool triggerdByZoom)
        {
            if (!applying) lastByZoom = triggerdByZoom;
        }

        /// <summary>Call every frame while other players are in the game.</summary>
        public static void Tick()
        {
            if (!GameAccess.Alive(vehicle))
            {
                if (Time.unscaledTime < nextFind) return;
                nextFind = Time.unscaledTime + 2f;
                var go = GameObject.Find("Vehicle");
                vehicle = go != null ? GameAccess.GetComp<Vehicle>(go) : null;
                if (vehicle == null) return;
            }
            var me = GameAccess.LocalCharacter;
            if (me == null || lastByZoom) { mismatchSince = -1f; return; }
            bool want = !GameAccess.CharacterInsideVehicle(me);   // cover on while I am outside
            bool active;
            try { active = vehicle.IsFrontCoverActive(); } catch { return; }
            if (active == want) { mismatchSince = -1f; return; }
            // give the game's own trigger a moment (my own enter/exit fires it slightly earlier/later)
            if (mismatchSince < 0f) { mismatchSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - mismatchSince < 0.3f) return;
            mismatchSince = -1f;
            applying = true;
            try { vehicle.SetFrontCover(want); }
            catch (Exception e) { FarCoopPlugin.Logger.LogDebug("CoverSync: " + e.Message); }
            finally { applying = false; }
        }
    }
}
