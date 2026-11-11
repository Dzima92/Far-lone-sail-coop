using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using InControl;
using Okomotive.FarLoneSails;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>Input state of the partner, as sent over the network.</summary>
    public struct RemoteInput
    {
        public float Horizontal, Vertical;
        public bool Jump, Take;
        public byte JumpPresses, TakePresses;
    }

    /// <summary>
    /// Feeds the partner's input into the host's game for the partner puppet.
    /// The game reads input through InControl actions (InputHandler.GetControls()).  While the game
    /// runs code "on behalf of" the puppet (its Character/Motor/Input updates, or a vehicle button
    /// whose pusher is the puppet), reads of those actions return the partner's values instead.
    /// </summary>
    public static class PartnerInput
    {
        public static int Depth;          // >0 while executing puppet-related game code
        public static IntPtr PuppetCharacter, PuppetInput, PuppetMotor;

        private static RemoteInput current;
        private static bool jumpPrev, takePrev;
        private static int lastFrame = -1;
        private static bool jumpPressedThisFrame, takePressedThisFrame, jumpReleasedThisFrame, takeReleasedThisFrame;
        private static int pendingJumpPresses, pendingTakePresses;
        private static byte lastJumpCounter, lastTakeCounter;
        private static bool countersInit;

        private static IntPtr pHorizontal, pVertical, pLeft, pRight, pUp, pDown, pJump, pTake, pZoomIn, pZoomOut, pPause;
        private static bool actionsResolved;

        public static bool Active => PuppetCharacter != IntPtr.Zero;

        // diagnostics: how many input reads were answered with partner input
        private static int overrideCount, contextCount;
        private static float statTime;
        public static int OverridesPerSec { get; private set; }
        private static void Stat()
        {
            float now = Time.unscaledTime;
            if (now - statTime < 1f) return;
            OverridesPerSec = overrideCount;
            if (Active && contextCount > 0 && overrideCount == 0 && Time.frameCount % 600 < 60)
                FarCoopPlugin.Logger.LogWarning($"PartnerInput: {contextCount} puppet contexts/s but 0 input reads overridden");
            overrideCount = 0; contextCount = 0; statTime = now;
        }
        public static void Tick() { Stat(); }

        public static void SetRemote(RemoteInput input)
        {
            if (!countersInit)
            {
                lastJumpCounter = input.JumpPresses; lastTakeCounter = input.TakePresses; countersInit = true;
            }
            pendingJumpPresses += (byte)(input.JumpPresses - lastJumpCounter);
            pendingTakePresses += (byte)(input.TakePresses - lastTakeCounter);
            lastJumpCounter = input.JumpPresses; lastTakeCounter = input.TakePresses;
            current = input;
        }

        public static void Clear()
        {
            PuppetCharacter = PuppetInput = PuppetMotor = IntPtr.Zero;
            current = default;
            countersInit = false;
            pendingJumpPresses = pendingTakePresses = 0;
        }

        /// <summary>Read the local player's input (client side) to send to the host.</summary>
        private static byte localJumpCounter, localTakeCounter;
        public static RemoteInput ReadLocal()
        {
            var r = new RemoteInput();
            try
            {
                var c = InputHandler.GetControls();
                if (c == null) return r;
                r.Horizontal = c.Horizontal.Value;
                r.Vertical = c.Vertical.Value;
                r.Jump = c.Jump.IsPressed;
                r.Take = c.Take.IsPressed;
                if (c.Jump.WasPressed) localJumpCounter++;
                if (c.Take.WasPressed) localTakeCounter++;
            }
            catch { }
            r.JumpPresses = localJumpCounter;
            r.TakePresses = localTakeCounter;
            return r;
        }

        private static void ResolveActions()
        {
            if (actionsResolved) return;
            try
            {
                var c = InputHandler.GetControls();
                if (c == null) return;
                pHorizontal = c.Horizontal.Pointer; pVertical = c.Vertical.Pointer;
                pLeft = c.Left.Pointer; pRight = c.Right.Pointer; pUp = c.Up.Pointer; pDown = c.Down.Pointer;
                pJump = c.Jump.Pointer; pTake = c.Take.Pointer;
                pZoomIn = c.ZoomIn.Pointer; pZoomOut = c.ZoomOut.Pointer; pPause = c.Pause.Pointer;
                actionsResolved = true;
            }
            catch { }
        }

        private static void UpdateEdges()
        {
            int f = Time.frameCount;
            if (f == lastFrame) return;
            lastFrame = f;
            jumpPressedThisFrame = pendingJumpPresses > 0 || (current.Jump && !jumpPrev);
            takePressedThisFrame = pendingTakePresses > 0 || (current.Take && !takePrev);
            if (pendingJumpPresses > 0) pendingJumpPresses--;
            if (pendingTakePresses > 0) pendingTakePresses--;
            jumpReleasedThisFrame = !current.Jump && jumpPrev;
            takeReleasedThisFrame = !current.Take && takePrev;
            jumpPrev = current.Jump;
            takePrev = current.Take;
        }

        // ---------------- Harmony wiring ----------------

        public static void Install(Harmony h)
        {
            // contexts: puppet's own components
            Ctx(h, typeof(Okomotive.SideScrollerCharacterController.CharacterInput), "Update", nameof(PreInput));
            Ctx(h, typeof(Character), "Update", nameof(PreCharacter));
            Ctx(h, typeof(Character), "FixedUpdate", nameof(PreCharacter));
            Ctx(h, typeof(Okomotive.SideScrollerCharacterController.CharacterMotor), "FixedUpdate", nameof(PreMotor));

            // contexts: vehicle force buttons whose pusher is the puppet
            foreach (var t in new[] { typeof(ButtonForce), typeof(VehicleMotorButtonEngine), typeof(VehicleSailButtonForce) })
                foreach (var m in new[] { "FixedUpdate", "PushInTrigger", "PushBackTrigger" })
                    Ctx(h, t, m, nameof(PrePusher));
            Ctx(h, typeof(VehicleMotorButtonBrake), "StayAction", nameof(PrePusher));

            // direct path: the puppet's own input mapping (independent of the InControl override)
            Direct(h, "GetHorizontalMapping", nameof(MapHorizontal));
            Direct(h, "GetVerticalMapping", nameof(MapVertical));
            Direct(h, "JumpIsPressedMapping", nameof(MapJumpHeld));
            Direct(h, "JumpWasPressedMapping", nameof(MapJumpPressed));

            // the input reads themselves
            Read(h, "get_Value", nameof(ValuePrefix));
            Read(h, "get_RawValue", nameof(ValuePrefix));
            Read(h, "get_State", nameof(StatePrefix));          // same native code as get_IsPressed
            Read(h, "get_WasPressed", nameof(WasPressedPrefix));
            Read(h, "get_WasReleased", nameof(WasReleasedPrefix));
        }

        private static void Ctx(Harmony h, Type t, string method, string prefix)
        {
            try
            {
                var m = t.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null || m.DeclaringType != t) { FarCoopPlugin.Logger.LogWarning($"PartnerInput: {t.Name}.{method} not found"); return; }
                h.Patch(m,
                    prefix: new HarmonyMethod(typeof(PartnerInput).GetMethod(prefix, BindingFlags.Static | BindingFlags.Public)),
                    postfix: new HarmonyMethod(typeof(PartnerInput).GetMethod(nameof(EndCtx), BindingFlags.Static | BindingFlags.Public)));
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning($"PartnerInput: patch {t.Name}.{method} failed: {e.Message}"); }
        }

        private static void Direct(Harmony h, string method, string prefix)
        {
            try
            {
                var t = typeof(Okomotive.FarLoneSails.CharacterInput);
                var m = t.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null || m.DeclaringType != t) { FarCoopPlugin.Logger.LogWarning("PartnerInput: CharacterInput." + method + " not found"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(PartnerInput).GetMethod(prefix, BindingFlags.Static | BindingFlags.Public)));
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning($"PartnerInput: patch {method} failed: {e.Message}"); }
        }

        private static bool IsPuppetInput(Okomotive.FarLoneSails.CharacterInput i) => Active && i.Pointer == PuppetInput;

        public static bool MapHorizontal(Okomotive.FarLoneSails.CharacterInput __instance, ref float __result)
        { if (!IsPuppetInput(__instance)) return true; __result = current.Horizontal; overrideCount++; return false; }

        public static bool MapVertical(Okomotive.FarLoneSails.CharacterInput __instance, ref float __result)
        { if (!IsPuppetInput(__instance)) return true; __result = current.Vertical; return false; }

        public static bool MapJumpHeld(Okomotive.FarLoneSails.CharacterInput __instance, ref bool __result)
        { if (!IsPuppetInput(__instance)) return true; __result = current.Jump; return false; }

        public static bool MapJumpPressed(Okomotive.FarLoneSails.CharacterInput __instance, ref bool __result)
        { if (!IsPuppetInput(__instance)) return true; UpdateEdges(); __result = jumpPressedThisFrame; return false; }

        private static void Read(Harmony h, string getter, string prefix)
        {
            try
            {
                var m = typeof(OneAxisInputControl).GetMethod(getter, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) { FarCoopPlugin.Logger.LogWarning("PartnerInput: OneAxisInputControl." + getter + " not found"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(PartnerInput).GetMethod(prefix, BindingFlags.Static | BindingFlags.Public)));
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning($"PartnerInput: patch {getter} failed: {e.Message}"); }
        }

        // __state tells the finalizer whether this call opened a context
        public static void PreInput(Okomotive.SideScrollerCharacterController.CharacterInput __instance, out bool __state)
        { __state = Active && __instance.Pointer == PuppetInput; if (__state) { Depth++; contextCount++; } else Depth = 0; }

        public static void PreCharacter(Character __instance, out bool __state)
        { __state = Active && __instance.Pointer == PuppetCharacter; if (__state) { Depth++; contextCount++; } else Depth = 0; }

        public static void PreMotor(Okomotive.SideScrollerCharacterController.CharacterMotor __instance, out bool __state)
        { __state = Active && __instance.Pointer == PuppetMotor; if (__state) { Depth++; contextCount++; } else Depth = 0; }

        private static readonly Dictionary<Type, PropertyInfo> pusherProps = new Dictionary<Type, PropertyInfo>();
        public static void PrePusher(object __instance, out bool __state)
        {
            __state = false;
            if (!Active) return;
            try
            {
                var t = __instance.GetType();
                if (!pusherProps.TryGetValue(t, out var p))
                {
                    p = t.GetProperty("pusher", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    pusherProps[t] = p;
                }
                var pusher = p?.GetValue(__instance) as Character;
                if (pusher != null && pusher.Pointer == PuppetCharacter) { __state = true; Depth++; contextCount++; }
            }
            catch { }
        }

        public static void EndCtx(bool __state)
        {
            if (__state && Depth > 0) Depth--;
        }

        // ---------------- action overrides ----------------

        private static bool Map(IntPtr p, out float value, out bool known)
        {
            known = true; value = 0f;
            if (p == pHorizontal) value = current.Horizontal;
            else if (p == pVertical) value = current.Vertical;
            else if (p == pRight) value = Mathf.Max(0f, current.Horizontal);
            else if (p == pLeft) value = Mathf.Max(0f, -current.Horizontal);
            else if (p == pUp) value = Mathf.Max(0f, current.Vertical);
            else if (p == pDown) value = Mathf.Max(0f, -current.Vertical);
            else if (p == pJump) value = current.Jump ? 1f : 0f;
            else if (p == pTake) value = 0f;   // items are carried client-side (ItemSync), not by the puppet
            else if (p == pZoomIn || p == pZoomOut || p == pPause) value = 0f;
            else known = false;
            return known;
        }

        public static bool ValuePrefix(OneAxisInputControl __instance, ref float __result)
        {
            if (Depth <= 0) return true;
            ResolveActions();
            if (!Map(__instance.Pointer, out float v, out _)) return true;
            __result = v;
            overrideCount++;
            return false;
        }

        public static bool StatePrefix(OneAxisInputControl __instance, ref bool __result)
        {
            if (Depth <= 0) return true;
            ResolveActions();
            if (!Map(__instance.Pointer, out float v, out _)) return true;
            __result = Mathf.Abs(v) > 0.5f;
            return false;
        }

        public static bool WasPressedPrefix(OneAxisInputControl __instance, ref bool __result)
        {
            if (Depth <= 0) return true;
            ResolveActions();
            UpdateEdges();
            IntPtr p = __instance.Pointer;
            if (p == pJump) { __result = jumpPressedThisFrame; return false; }
            if (p == pTake) { __result = false; return false; }
            if (Map(p, out _, out _)) { __result = false; return false; }
            return true;
        }

        public static bool WasReleasedPrefix(OneAxisInputControl __instance, ref bool __result)
        {
            if (Depth <= 0) return true;
            ResolveActions();
            UpdateEdges();
            IntPtr p = __instance.Pointer;
            if (p == pJump) { __result = jumpReleasedThisFrame; return false; }
            if (p == pTake) { __result = false; return false; }
            if (Map(p, out _, out _)) { __result = false; return false; }
            return true;
        }
    }
}
