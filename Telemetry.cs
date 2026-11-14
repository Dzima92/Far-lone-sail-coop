using System;
using System.IO;
using System.Text;
using InControl;
using Okomotive.FarLoneSails;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// Once per second writes one line to BepInEx\FarCoop-telemetry.log: what keys you pressed/held,
    /// where your character is (also relative to the vehicle), whether it SLID (moved inside the
    /// vehicle without you pressing left/right), vehicle speed, network, and every other player.
    /// Made for finding sync bugs: compare the files of all PCs side by side (same clock).
    /// </summary>
    public static class Telemetry
    {
        private static StreamWriter file;
        private static float nextFlush;
        private static readonly StringBuilder events = new StringBuilder();

        // input during the current second
        private static int pressLeft, pressRight, pressUp, pressDown, pressJump, pressTake;
        private static float heldLeft, heldRight, heldUp, heldDown, heldJump, heldTake;
        private static bool wasLeft, wasRight, wasUp, wasDown;

        // sliding: movement inside the vehicle with no left/right input
        private static bool haveLocal;
        private static Vector3 lastLocal;
        private static float slide, walked;
        private static float maxSlideStep;

        public static bool Enabled => FarCoopPlugin.CfgTelemetry != null && FarCoopPlugin.CfgTelemetry.Value;

        private static void Open()
        {
            if (file != null) return;
            try
            {
                string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "FarCoop-telemetry.log");
                file = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
                file.WriteLine($"# FAR LAN Coop {FarCoopPlugin.Version} telemetry, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                file.WriteLine("# keys: L/R/U/D/J/T = presses (held seconds). me: pos, local=pos in vehicle, slide=m moved in vehicle without left/right input");
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("Telemetry file: " + e.Message); file = null; }
        }

        /// <summary>Something happened (button, item, hose, join...). Goes into the next line.</summary>
        public static void Event(string what)
        {
            if (!Enabled) return;
            if (events.Length > 600) return;
            events.Append(" | ").Append(DateTime.Now.ToString("ss.f")).Append(' ').Append(what);
        }

        /// <summary>Every frame (Update).</summary>
        public static void Frame()
        {
            if (!Enabled) return;
            float dt = Time.unscaledDeltaTime;
            try
            {
                var c = InputHandler.GetControls();
                if (c == null) return;
                float h = c.Horizontal.Value, v = c.Vertical.Value;
                bool l = h < -0.3f, r = h > 0.3f, u = v > 0.3f, d = v < -0.3f;
                if (l && !wasLeft) pressLeft++;
                if (r && !wasRight) pressRight++;
                if (u && !wasUp) pressUp++;
                if (d && !wasDown) pressDown++;
                wasLeft = l; wasRight = r; wasUp = u; wasDown = d;
                if (l) heldLeft += dt;
                if (r) heldRight += dt;
                if (u) heldUp += dt;
                if (d) heldDown += dt;
                if (c.Jump.WasPressed) pressJump++;
                if (c.Take.WasPressed) pressTake++;
                if (c.Jump.IsPressed) heldJump += dt;
                if (c.Take.IsPressed) heldTake += dt;
            }
            catch { }
        }

        /// <summary>Every physics step (FixedUpdate): sliding detection.</summary>
        public static void Fixed(VehicleSync vehicle)
        {
            if (!Enabled) return;
            var me = GameAccess.LocalCharacter;
            var f = vehicle.Frame;
            if (me == null || f == null || !GameAccess.CharacterInsideVehicle(me)) { haveLocal = false; return; }
            Vector3 lp = f.InverseTransformPoint(me.transform.position);
            if (haveLocal)
            {
                float step = Mathf.Abs(lp.x - lastLocal.x);
                bool input = wasLeft || wasRight;
                if (input) walked += step;
                else if (step > 0.0005f) { slide += step; if (step > maxSlideStep) maxSlideStep = step; }
            }
            lastLocal = lp; haveLocal = true;
        }

        /// <summary>Once per second; <paramref name="session"/> adds network + other players.</summary>
        public static void Flush(string session)
        {
            if (!Enabled || Time.unscaledTime < nextFlush) return;
            nextFlush = Time.unscaledTime + 1f;
            Open();
            if (file == null) return;
            var sb = new StringBuilder(512);
            sb.Append(DateTime.Now.ToString("HH:mm:ss.f"));
            sb.Append($" keys L{pressLeft}({heldLeft:0.0}) R{pressRight}({heldRight:0.0}) U{pressUp}({heldUp:0.0}) D{pressDown}({heldDown:0.0}) J{pressJump}({heldJump:0.0}) T{pressTake}({heldTake:0.0})");
            var me = GameAccess.LocalCharacter;
            if (me != null)
            {
                Vector3 p = me.transform.position;
                bool inside = GameAccess.CharacterInsideVehicle(me);
                sb.Append($" | me pos=({p.x:0.00},{p.y:0.00},{p.z:0.00}) inside={(inside ? 1 : 0)}");
                if (haveLocal) sb.Append($" local=({lastLocal.x:0.00},{lastLocal.y:0.00},{lastLocal.z:0.00})");
                try { var rb = GameAccess.CharacterBody(me); if (rb != null) sb.Append($" vel={rb.velocity.magnitude:0.00}"); } catch { }
                try { var it = me.GetHoldingItem(); if (it != null) sb.Append(" holds=" + it.name); } catch { }
                if (slide > 0.02f) sb.Append($" SLIDE={slide:0.00}m(max {maxSlideStep * 50f:0.00}m/s)");
                if (walked > 0f) sb.Append($" walked={walked:0.00}m");
            }
            sb.Append(session);
            if (events.Length > 0) { sb.Append(" || events").Append(events); events.Length = 0; }
            try { file.WriteLine(sb.ToString()); } catch { }

            pressLeft = pressRight = pressUp = pressDown = pressJump = pressTake = 0;
            heldLeft = heldRight = heldUp = heldDown = heldJump = heldTake = 0f;
            slide = walked = maxSlideStep = 0f;
        }
    }
}
