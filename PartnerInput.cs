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
    /// <summary>One partner body (puppet) and the input its player sends.</summary>
    public sealed class PuppetSlot
    {
        public IntPtr Character, Input, Motor;
        public RemoteInput Current;
        public string Label = "partner";
        internal bool JumpPrev, TakePrev;
        internal int LastFrame = -1;
        internal bool JumpPressed, JumpReleased;
        internal int PendingJump, PendingTake;
        internal byte LastJumpCounter, LastTakeCounter;
        internal bool CountersInit;

        public void SetRemote(RemoteInput input)
        {
            if (!CountersInit) { LastJumpCounter = input.JumpPresses; LastTakeCounter = input.TakePresses; CountersInit = true; }
            PendingJump += (byte)(input.JumpPresses - LastJumpCounter);
            PendingTake += (byte)(input.TakePresses - LastTakeCounter);
            LastJumpCounter = input.JumpPresses; LastTakeCounter = input.TakePresses;
            Current = input;
        }

        internal void UpdateEdges()
        {
            int f = Time.frameCount;
            if (f == LastFrame) return;
            LastFrame = f;
            JumpPressed = PendingJump > 0 || (Current.Jump && !JumpPrev);
            if (PendingJump > 0) PendingJump--;
            if (PendingTake > 0) PendingTake--;
            JumpReleased = !Current.Jump && JumpPrev;
            JumpPrev = Current.Jump;
            TakePrev = Current.Take;
        }
    }

    /// <summary>
    /// Feeds each partner's input into this game for that partner's puppet.
    /// The game reads input through InControl actions (InputHandler.GetControls()).  While the game
    /// runs code "on behalf of" a puppet (its Character/Motor/Input updates, or a vehicle button
    /// whose pusher is the puppet), reads of those actions return that partner's values instead.
    /// </summary>
    public static class PartnerInput
    {
        public static int Depth;          // >0 while executing puppet-related game code
        /// <summary>True while the mod itself makes a partner body take/drop an item.</summary>
        public static bool AllowPuppetTake;

        private static readonly List<PuppetSlot> slots = new List<PuppetSlot>();
        private static readonly List<PuppetSlot> stack = new List<PuppetSlot>();
        private static PuppetSlot Cur => stack.Count > 0 ? stack[stack.Count - 1] : null;

        private static IntPtr pHorizontal, pVertical, pLeft, pRight, pUp, pDown, pJump, pTake, pZoomIn, pZoomOut, pPause;
        private static bool actionsResolved;

        public static bool Active => slots.Count > 0;

        public static void Register(PuppetSlot s) { if (!slots.Contains(s)) slots.Add(s); }
        public static void Unregister(PuppetSlot s) { slots.Remove(s); stack.Remove(s); }

        public static bool IsPuppet(IntPtr character)
        {
            if (character == IntPtr.Zero) return false;
            for (int i = 0; i < slots.Count; i++) if (slots[i].Character == character) return true;
            return false;
        }
        private static PuppetSlot ByCharacter(IntPtr p) { for (int i = 0; i < slots.Count; i++) if (slots[i].Character == p) return slots[i]; return null; }
        private static PuppetSlot ByInput(IntPtr p) { for (int i = 0; i < slots.Count; i++) if (slots[i].Input == p) return slots[i]; return null; }
        private static PuppetSlot ByMotor(IntPtr p) { for (int i = 0; i < slots.Count; i++) if (slots[i].Motor == p) return slots[i]; return null; }

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
        /// <summary>Safety: never leak an input override across frames.</summary>
        public static void ResetFrame() { Depth = 0; stack.Clear(); }

        /// <summary>Read the local player's input to send to the others.</summary>
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

        /// <summary>The local player presses no left/right and no jump (no side effects).</summary>
        public static bool LocalIdle()
        {
            try
            {
                var c = InputHandler.GetControls();
                if (c == null) return true;
                return Mathf.Abs(c.Horizontal.Value) < 0.1f && !c.Jump.IsPressed;
            }
            catch { return true; }
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

            // the partner's body copy must never pick items up or grab ropes itself: the real partner
            // carries items on their own PC (ItemSync). It did, which floated boxes, stretched ropes
            // and threw NullReferenceExceptions from its silent sound component.
            foreach (var m in typeof(Character).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "TakeItem") continue;   // OnTake / SetRope stay: the throttle lever needs them
                try
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(PartnerInput).GetMethod(nameof(BlockForPuppet), BindingFlags.Static | BindingFlags.Public)));
                    FarCoopPlugin.Logger.LogInfo("PartnerInput: puppet blocked from Character." + m.Name);
                }
                catch (Exception e) { FarCoopPlugin.Logger.LogWarning($"PartnerInput: patch Character.{m.Name} failed: {e.Message}"); }
            }

            // the copy has no FMOD sound events: skip its pick-up sound instead of throwing
            try
            {
                var snd = typeof(CharacterSound).GetMethod("OnTake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (snd != null) h.Patch(snd, prefix: new HarmonyMethod(typeof(PartnerInput).GetMethod(nameof(BlockSoundForPuppet), BindingFlags.Static | BindingFlags.Public)));
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("PartnerInput: patch CharacterSound.OnTake failed: " + e.Message); }

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

        public static bool BlockForPuppet(Character __instance)
        {
            if (AllowPuppetTake) return true;
            try { return !(__instance != null && IsPuppet(__instance.Pointer)); }
            catch { return true; }
        }

        public static bool BlockSoundForPuppet(Character lone)
        {
            try { return !(lone != null && IsPuppet(lone.Pointer)); }
            catch { return true; }
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

        public static bool MapHorizontal(Okomotive.FarLoneSails.CharacterInput __instance, ref float __result)
        { var sl = ByInput(__instance.Pointer); if (sl == null) return true; __result = sl.Current.Horizontal; overrideCount++; return false; }

        public static bool MapVertical(Okomotive.FarLoneSails.CharacterInput __instance, ref float __result)
        { var sl = ByInput(__instance.Pointer); if (sl == null) return true; __result = sl.Current.Vertical; return false; }

        public static bool MapJumpHeld(Okomotive.FarLoneSails.CharacterInput __instance, ref bool __result)
        { var sl = ByInput(__instance.Pointer); if (sl == null) return true; __result = sl.Current.Jump; return false; }

        public static bool MapJumpPressed(Okomotive.FarLoneSails.CharacterInput __instance, ref bool __result)
        { var sl = ByInput(__instance.Pointer); if (sl == null) return true; sl.UpdateEdges(); __result = sl.JumpPressed; return false; }

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

        // __state tells the finalizer which puppet this call opened a context for (null = none)
        private static void Open(PuppetSlot sl, out PuppetSlot state)
        {
            state = sl;
            if (sl == null) { stack.Clear(); Depth = 0; return; }   // a real player's code: never leak partner input into it
            stack.Add(sl); Depth = stack.Count; contextCount++;
        }

        public static void PreInput(Okomotive.SideScrollerCharacterController.CharacterInput __instance, out PuppetSlot __state)
        { Open(Active ? ByInput(__instance.Pointer) : null, out __state); }

        public static void PreCharacter(Character __instance, out PuppetSlot __state)
        { Open(Active ? ByCharacter(__instance.Pointer) : null, out __state); }

        public static void PreMotor(Okomotive.SideScrollerCharacterController.CharacterMotor __instance, out PuppetSlot __state)
        { Open(Active ? ByMotor(__instance.Pointer) : null, out __state); }

        private static readonly Dictionary<Type, PropertyInfo> pusherProps = new Dictionary<Type, PropertyInfo>();
        public static void PrePusher(object __instance, out PuppetSlot __state)
        {
            __state = null;
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
                if (pusher != null) { var sl = ByCharacter(pusher.Pointer); if (sl != null) Open(sl, out __state); }
                LogPusher(__instance, pusher, __state);
            }
            catch { }
        }

        // diagnostics: who is standing on / pushing which vehicle button (every 2 s per button)
        private static readonly Dictionary<IntPtr, float> pusherLogTime = new Dictionary<IntPtr, float>();
        private static void LogPusher(object button, Character pusher, PuppetSlot sl)
        {
            try
            {
                var comp = button as Component;
                if (comp == null) return;
                IntPtr key = comp.Pointer;
                float now = Time.unscaledTime;
                if (pusherLogTime.TryGetValue(key, out float t) && now - t < 0.5f) return;
                string who = pusher == null ? "nobody" : sl != null ? "partner body of " + sl.Label + $" (input h={sl.Current.Horizontal:0.0} v={sl.Current.Vertical:0.0} jump={sl.Current.Jump})" : "local player";
                if (pusher == null) return;
                pusherLogTime[key] = now;
                string extra = "";
                try
                {
                    var eng = comp.TryCast<VehicleMotorButtonEngine>();
                    if (eng != null) extra = $" state={eng.state} hookTime={eng.hookTime:0.00} elem={comp.transform.localPosition}";
                    var bf = comp.TryCast<ButtonForce>();
                    if (bf != null) extra += $" pusherPos={pusher.transform.position} btnPos={comp.transform.position}";
                    if (sl != null && CoopSession.Instance != null) extra += " " + CoopSession.Instance.PuppetDebug(sl);
                }
                catch (Exception e) { extra += " (" + e.Message + ")"; }
                FarCoopPlugin.Logger.LogInfo($"[button] {comp.GetType().Name} '{comp.name}' pushed by {who}{extra}");
                Telemetry.Event($"button {comp.name} by {(sl != null ? sl.Label : "me")}{(comp.TryCast<VehicleMotorButtonEngine>() is VehicleMotorButtonEngine en ? " " + en.state : "")}");
            }
            catch { }
        }

        public static void EndCtx(PuppetSlot __state)
        {
            if (__state == null) return;
            int i = stack.LastIndexOf(__state);
            if (i >= 0) stack.RemoveAt(i);
            Depth = stack.Count;
        }

        // ---------------- action overrides ----------------

        private static bool Map(IntPtr p, out float value, out bool known)
        {
            known = true; value = 0f;
            var sl = Cur;
            if (sl == null) { known = false; return false; }
            var current = sl.Current;
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
            if (Cur == null) return true;
            ResolveActions();
            if (!Map(__instance.Pointer, out float v, out _)) return true;
            __result = v;
            overrideCount++;
            return false;
        }

        public static bool StatePrefix(OneAxisInputControl __instance, ref bool __result)
        {
            if (Cur == null) return true;
            ResolveActions();
            if (!Map(__instance.Pointer, out float v, out _)) return true;
            __result = Mathf.Abs(v) > 0.5f;
            return false;
        }

        public static bool WasPressedPrefix(OneAxisInputControl __instance, ref bool __result)
        {
            var sl = Cur;
            if (sl == null) return true;
            ResolveActions();
            sl.UpdateEdges();
            IntPtr p = __instance.Pointer;
            if (p == pJump) { __result = sl.JumpPressed; return false; }
            if (p == pTake) { __result = false; return false; }
            if (Map(p, out _, out _)) { __result = false; return false; }
            return true;
        }

        public static bool WasReleasedPrefix(OneAxisInputControl __instance, ref bool __result)
        {
            var sl = Cur;
            if (sl == null) return true;
            ResolveActions();
            sl.UpdateEdges();
            IntPtr p = __instance.Pointer;
            if (p == pJump) { __result = sl.JumpReleased; return false; }
            if (p == pTake) { __result = false; return false; }
            if (Map(p, out _, out _)) { __result = false; return false; }
            return true;
        }
    }
}
