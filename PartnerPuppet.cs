using System;
using System.Collections.Generic;
using Okomotive.FarLoneSails;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// HOST ONLY. A real second game Character that represents the partner inside the host's
    /// simulation. It is driven by the partner's input (see <see cref="PartnerInput"/>) and gently
    /// pulled to the partner's reported position, so it can press vehicle buttons, push levers,
    /// carry boxes and throw fuel into the oven exactly like the host's own character.
    /// </summary>
    public sealed class PartnerPuppet
    {
        private static readonly HashSet<string> StripTypes = new HashSet<string>
        {
            "DataSaverCharacter", "SaveTransform", "AudioListener", "CameraFreeMovement",
            // FAR's motion smoothing drives a separate "virtual" body; a second copy of it moved the
            // LOCAL player's visible body to twice its position (invisible on your own screen)
            "InterpolationTarget", "InterpolationAgent", "InterpolationUpdater"
        };

        public Character Character { get; private set; }
        /// <summary>Input of the player this body belongs to.</summary>
        public readonly PuppetSlot Slot = new PuppetSlot();
        private static readonly List<PartnerPuppet> all = new List<PartnerPuppet>();
        private Rigidbody body;
        private float nextBuildAttempt;
        private int failures;
        private bool everBuilt;

        // visibility: renderers of the puppet mirror the local character's (the game hides the
        // clone's mesh while it "wakes up", which made the partner invisible)
        private readonly List<Renderer> puppetRenderers = new List<Renderer>();
        private readonly List<Renderer> sourceRenderers = new List<Renderer>();
        private float nextVisibilityFix;
        private bool loggedVisibility;

        public bool Exists => GameAccess.Alive(Character);

        public void KeepVisible()
        {
            if (!Exists || Time.unscaledTime < nextVisibilityFix) return;
            nextVisibilityFix = Time.unscaledTime + 0.5f;
            try { Character.ShowMesh(true); } catch { }
            int fixedCount = 0;
            for (int i = 0; i < puppetRenderers.Count; i++)
            {
                var r = puppetRenderers[i];
                if (!GameAccess.Alive(r)) continue;
                bool want = true;
                if (i < sourceRenderers.Count && sourceRenderers[i] != null && GameAccess.Alive(sourceRenderers[i]))
                    want = sourceRenderers[i].enabled && sourceRenderers[i].gameObject.activeInHierarchy;
                try
                {
                    if (want && !r.gameObject.activeSelf) { r.gameObject.SetActive(true); fixedCount++; }
                    if (r.enabled != want) { r.enabled = want; fixedCount++; }
                }
                catch { }
            }
            if (fixedCount > 0 && !loggedVisibility)
            {
                loggedVisibility = true;
                FarCoopPlugin.Logger.LogInfo($"Partner puppet: re-enabled {fixedCount} hidden renderers");
            }
        }

        /// <summary>The puppet is the partner's physical body only; the visual avatar draws them.</summary>
        public void HideMesh()
        {
            if (!Exists || Time.unscaledTime < nextVisibilityFix) return;
            nextVisibilityFix = Time.unscaledTime + 0.5f;
            foreach (var r in puppetRenderers)
            {
                try { if (GameAccess.Alive(r) && r.enabled) r.enabled = false; } catch { }
            }
        }


        private static bool IsInside(Transform t, Transform root)
        {
            while (t != null) { if (t.Pointer == root.Pointer) return true; t = t.parent; }
            return false;
        }

        private static Transform TransformOf(object o)
        {
            if (o is Component c) return c.transform;
            if (o is GameObject g) return g.transform;
            return null;
        }

        /// <summary>
        /// FAR keeps the character's visual body (animator + mesh) outside the physics object.
        /// A plain clone therefore still points at the ORIGINAL body, so the partner's puppet would
        /// animate / hide / move the local player's body. Give the puppet its own (hidden) body copy
        /// and re-point every reference that leaves the clone.
        /// </summary>
        private void DetachSharedReferences(Character local, Character ch, Okomotive.SideScrollerCharacterController.CharacterMotor motor, GameObject clone)
        {
            Animator localAnim = GameAccess.CharacterAnimator(local);
            Transform visualSrc = localAnim != null ? localAnim.transform : null;
            Transform visualCopy = null;
            var report = new List<string>();

            // every script on the clone (Character, motor, interpolation/follow helpers, ...)
            var owners = new List<object>();
            var asm = typeof(Character).Assembly;
            var castM = typeof(UnhollowerBaseLib.Il2CppObjectBase).GetMethod("TryCast");
            var scripts = clone.GetComponentsInChildren(Il2CppType.Of<MonoBehaviour>(), true);
            for (int i = 0; i < scripts.Length; i++)
            {
                var sc = scripts[i];
                if (sc == null) continue;
                try
                {
                    string full = sc.GetIl2CppType().FullName;
                    Type mt = asm.GetType(full) ?? typeof(Component).Assembly.GetType(full);
                    if (mt == null) continue;
                    object typed = castM.MakeGenericMethod(mt).Invoke(sc, null);
                    if (typed != null) owners.Add(typed);
                }
                catch { }
            }
            foreach (var owner in owners)
            {
                foreach (var prop in owner.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!prop.CanRead || !prop.CanWrite) continue;
                    Type pt = prop.PropertyType;
                    if (!typeof(Component).IsAssignableFrom(pt) && pt != typeof(GameObject)) continue;
                    object val;
                    try { val = prop.GetValue(owner); } catch { continue; }
                    if (val == null) continue;
                    Transform vt;
                    try { vt = TransformOf(val); } catch { continue; }
                    if (vt == null || IsInside(vt, clone.transform)) continue;

                    // reference leaves the clone
                    if (visualSrc != null && IsInside(vt, visualSrc))
                    {
                        if (visualCopy == null)
                        {
                            visualCopy = RemoteAvatar.CloneVisual(visualSrc.gameObject, clone.transform);
                            if (visualCopy != null)
                            {
                                visualCopy.name = "PuppetBody";
                                visualCopy.localPosition = local.transform.InverseTransformPoint(visualSrc.position);
                                visualCopy.localRotation = Quaternion.Inverse(local.transform.rotation) * visualSrc.rotation;
                                visualCopy.localScale = visualSrc.lossyScale;
                            }
                        }
                        if (visualCopy == null) continue;
                        string rel = RelPath(vt, visualSrc);
                        Transform mapped = rel.Length == 0 ? visualCopy : visualCopy.Find(rel);
                        if (mapped == null) mapped = visualCopy;
                        object replacement = null;
                        try
                        {
                            if (pt == typeof(GameObject)) replacement = mapped.gameObject;
                            else if (pt == typeof(Transform)) replacement = mapped;
                            else
                            {
                                var comp = mapped.GetComponent(UnhollowerRuntimeLib.Il2CppType.From(pt));
                                if (comp != null) replacement = typeof(UnhollowerBaseLib.Il2CppObjectBase).GetMethod("Cast").MakeGenericMethod(pt).Invoke(comp, null);
                            }
                        }
                        catch { }
                        if (replacement != null)
                        {
                            try { prop.SetValue(owner, replacement); report.Add(owner.GetType().Name + "." + prop.Name + "->copy"); } catch { }
                        }
                        else report.Add(prop.Name + " (shared, kept)");
                    }
                    else report.Add(owner.GetType().Name + "." + prop.Name + " (external, kept)");
                }
            }
            FarCoopPlugin.Logger.LogInfo("Partner puppet references: " + (report.Count == 0 ? "none shared" : string.Join(", ", report)));
        }

        /// <summary>Players do not bump into each other (the puppet still touches the world and buttons).</summary>
        private static void IgnorePlayerCollisions(GameObject local, GameObject puppet)
        {
            try
            {
                var a = local.GetComponentsInChildren(Il2CppType.Of<Collider>(), true);
                var b = puppet.GetComponentsInChildren(Il2CppType.Of<Collider>(), true);
                int n = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    var ca = a[i]?.TryCast<Collider>();
                    if (ca == null) continue;
                    for (int j = 0; j < b.Length; j++)
                    {
                        var cb = b[j]?.TryCast<Collider>();
                        if (cb == null) continue;
                        Physics.IgnoreCollision(ca, cb, true);
                        n++;
                    }
                }
                FarCoopPlugin.Logger.LogInfo($"Partner puppet: ignoring {n} player-player collider pairs");
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogWarning("IgnorePlayerCollisions: " + e.Message); }
        }

        private static string RelPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t.Pointer != root.Pointer) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void CollectRenderers(GameObject go, List<Renderer> into)
        {
            into.Clear();
            var arr = go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; i < arr.Length; i++)
            {
                var r = arr[i]?.TryCast<Renderer>();
                if (r != null) into.Add(r);
            }
        }
        public bool GaveUp => failures >= 3;
        public Vector3 Position => Exists ? Character.transform.position : Vector3.zero;

        public void EnsureBuilt()
        {
            if (Exists || GaveUp || Time.unscaledTime < nextBuildAttempt) return;
            if (everBuilt) FarCoopPlugin.Logger.LogWarning("Partner puppet disappeared (destroyed by the game?) - recreating");
            nextBuildAttempt = Time.unscaledTime + 3f;
            var local = GameAccess.LocalCharacter;
            if (local == null) return;

            GameObject holder = null;
            try
            {
                holder = new GameObject("FarCoop_PuppetHolder");
                holder.SetActive(false);
                var obj = UnityEngine.Object.Instantiate((UnityEngine.Object)local.gameObject, holder.transform);
                var clone = obj?.TryCast<GameObject>();
                if (clone == null) throw new Exception("instantiate returned null");
                clone.name = "FarCoop_PartnerPuppet";

                // strip save/camera related components before anything wakes up
                var comps = clone.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    string n;
                    try { n = c.GetIl2CppType().Name; } catch { continue; }
                    if (StripTypes.Contains(n)) try { UnityEngine.Object.DestroyImmediate(c); } catch { }
                }

                // never duplicate an item the host is carrying
                var heldItems = clone.GetComponentsInChildren(Il2CppType.Of<Item>(), true);
                for (int i = 0; i < heldItems.Length; i++)
                {
                    var it = heldItems[i];
                    if (it == null) continue;
                    try { if (it.gameObject.Pointer != clone.Pointer) UnityEngine.Object.DestroyImmediate(it.gameObject); } catch { }
                }

                var ch = GameAccess.GetComp<Character>(clone) ?? GameAccess.GetCompInChildren<Character>(clone);
                if (ch == null) throw new Exception("clone has no Character");
                try { ch.pNr = PlayerNr.Two; } catch { }

                var input = GameAccess.GetCompInChildren<Okomotive.SideScrollerCharacterController.CharacterInput>(clone);
                var motor = GameAccess.GetCompInChildren<Okomotive.SideScrollerCharacterController.CharacterMotor>(clone);

                // register before activation so its very first Update already uses partner input
                Slot.Character = ch.Pointer;
                Slot.Input = input != null ? input.Pointer : IntPtr.Zero;
                Slot.Motor = motor != null ? motor.Pointer : IntPtr.Zero;
                Slot.CountersInit = false;
                PartnerInput.Register(Slot);

                DetachSharedReferences(local, ch, motor, clone);

                CollectRenderers(clone, puppetRenderers);
                // match each puppet renderer to the same-named object in the local character
                sourceRenderers.Clear();
                foreach (var pr in puppetRenderers)
                {
                    Renderer src = null;
                    try
                    {
                        string rel = RelPath(pr.transform, clone.transform);
                        var t = rel.Length == 0 ? local.transform : local.transform.Find(rel);
                        if (t != null) src = t.GetComponent(Il2CppType.Of<Renderer>())?.TryCast<Renderer>();
                    }
                    catch { }
                    sourceRenderers.Add(src);
                }
                loggedVisibility = false;
                nextVisibilityFix = 0f;

                clone.transform.SetParent(null, true);   // becomes active -> Awake/Start
                Character = ch;
                everBuilt = true;
                IgnorePlayerCollisions(local.gameObject, clone);
                foreach (var other in all)
                    if (other != this && other.Exists) IgnorePlayerCollisions(other.Character.gameObject, clone);
                if (!all.Contains(this)) all.Add(this);
                body = GameAccess.CharacterBody(ch);
                FarCoopPlugin.Logger.LogInfo($"Partner puppet created (input:{input != null}, motor:{motor != null}, body:{body != null})");
            }
            catch (Exception e)
            {
                failures++;
                FarCoopPlugin.Logger.LogWarning($"Partner puppet failed ({failures}/3): {e.Message}");
                Destroy();
            }
            finally
            {
                if (holder != null) try { UnityEngine.Object.Destroy(holder); } catch { }
            }
        }

        /// <summary>
        /// Pull the puppet toward where the partner really is (call in FixedUpdate).
        /// The puppet walks with the partner's own input, so it normally stays close by itself.
        /// Small differences are left alone: correcting them fought the puppet while it pushed a
        /// button (the partner's own copy of the button only moves later, so the partner looks
        /// "stuck" in front of it and the puppet kept being pulled back -> button never pressed).
        /// </summary>
        public void Correct(Vector3 target)
        {
            if (!Exists) return;
            try
            {
                Vector3 pos = body != null ? body.position : Character.transform.position;
                if (Mathf.Abs(pos.z - target.z) > 0.05f)
                {
                    // depth (side-view plane) is always taken exactly
                    pos.z = target.z;
                    if (body != null) body.position = pos; else Character.transform.position = pos;
                }
                Vector3 delta = target - pos;
                float d = delta.magnitude;
                if (d > 2.5f)
                {
                    if (body != null) { body.velocity = Vector3.zero; body.position = target; }
                    Character.transform.position = target;
                }
                else if (d > 0.35f)
                {
                    // only the part beyond the dead zone, gently
                    Vector3 step = delta * ((d - 0.35f) / d) * 0.2f;
                    if (body != null) body.position = pos + step;
                    else Character.transform.position = pos + step;
                }
            }
            catch { }
        }

        public void Destroy()
        {
            PartnerInput.Unregister(Slot);
            Slot.Character = Slot.Input = Slot.Motor = IntPtr.Zero;
            all.Remove(this);
            if (GameAccess.Alive(Character))
            {
                try { UnityEngine.Object.Destroy(Character.gameObject); } catch { }
            }
            Character = null;
            body = null;
        }

        public void Reset()
        {
            Destroy();
            failures = 0;
            everBuilt = false;
            nextBuildAttempt = 0f;
        }
    }
}
