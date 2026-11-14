using System;
using System.Collections.Generic;
using System.IO;
using FarCoopMod.Net;
using Okomotive.FarLoneSails;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// Host-authoritative loose items (boxes, fuel...). Items are matched between the two games by
    /// their save-game id. The host streams item poses; the client mirrors them kinematically,
    /// except the item its own character is carrying.
    /// </summary>
    public sealed class ItemSync
    {
        private sealed class Entry
        {
            public string Key;
            public Item Item;
            public Rigidbody Body;
            public Vector3 LastPos;
            public Quaternion LastRot;
            public bool LastLocal;
            public bool Gone;
        }

        private struct Snap
        {
            public bool Valid, Held;
            public Vector3 Pos, Vel;
            public Quaternion Rot;
        }

        private readonly List<Entry> local = new List<Entry>();
        private readonly Dictionary<string, int> localByKey = new Dictionary<string, int>();
        private string signature = "";
        private float nextScan, nextFull;

        // client
        private List<string> hostKeys = new List<string>();
        private int[] hostToLocal = new int[0];
        private Snap[] snaps = new Snap[0];
        private PoseBuffer[] bufs = new PoseBuffer[0];
        private bool[] bufLocal = new bool[0];
        private float snapTime;
        private readonly Dictionary<IntPtr, bool> originalKinematic = new Dictionary<IntPtr, bool>();

        // client ownership of the item our character carries (and shortly after dropping it)
        private int ownHostIdx = -1;
        private Entry ownEntry;
        private string lastLoggedOwn;
        private bool ownHeld;
        private float ownUntil;
        private const float OwnGrace = 2.0f;

        // host side: item currently moved by the client
        private int clientIdx = -1;
        private float clientUntil;
        private bool clientHeld;

        public int Count => local.Count;
        public int HostCount => hostKeys.Count;
        public int Mapped { get; private set; }

        // key cache: an item keeps the key it got the first time we saw it (its hierarchy path then)
        private static readonly Dictionary<IntPtr, string> keyCache = new Dictionary<IntPtr, string>();
        public static int SaveIdKeys, PathKeys;

        private static string KeyOf(Item item)
        {
            if (keyCache.TryGetValue(item.Pointer, out string cached)) return cached;
            string key = null;
            try
            {
                var saver = item.GetComponent(Il2CppType.Of<ItemSaver>())?.TryCast<ItemSaver>();
                string id = saver?.GetSaveId();
                if (!string.IsNullOrEmpty(id)) { key = "S:" + id; SaveIdKeys++; }
            }
            catch { }
            if (key == null)
            {
                try
                {
                    // full hierarchy path (with sibling indices) where we first saw it
                    key = "P:" + GameAccess.GetPath(item.transform);
                    PathKeys++;
                }
                catch { }
            }
            if (key != null) keyCache[item.Pointer] = key;
            return key;
        }

        /// <summary>Returns true if the set of items changed.</summary>
        public bool Scan(bool force = false)
        {
            if (!force && Time.unscaledTime < nextScan) return false;
            nextScan = Time.unscaledTime + 3f;

            var found = new List<KeyValuePair<string, Item>>();
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Item>());
                for (int i = 0; i < arr.Length; i++)
                {
                    var it = arr[i]?.TryCast<Item>();
                    if (it == null) continue;
                    string key = KeyOf(it);
                    if (key != null) found.Add(new KeyValuePair<string, Item>(key, it));
                }
                // identical keys (same name under same parent): number them in a stable order
                var counts = new Dictionary<string, int>();
                found.Sort((a, b) =>
                {
                    int c0 = string.CompareOrdinal(a.Key, b.Key);
                    if (c0 != 0) return c0;
                    try { return a.Value.transform.position.x.CompareTo(b.Value.transform.position.x); } catch { return 0; }
                });
                for (int i = 0; i < found.Count; i++)
                {
                    counts.TryGetValue(found[i].Key, out int c);
                    counts[found[i].Key] = c + 1;
                    if (c > 0) found[i] = new KeyValuePair<string, Item>(found[i].Key + "#" + c, found[i].Value);
                }
            }
            catch (Exception e) { FarCoopPlugin.Logger.LogDebug("Item scan: " + e.Message); }

            found.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            string sig = found.Count + ":" + string.Join(",", found.ConvertAll(f => f.Key));

            // mark destroyed ones
            foreach (var e in local) if (!e.Gone && !GameAccess.Alive(e.Item)) e.Gone = true;

            if (sig == signature) return false;
            signature = sig;

            // keep entries stable by key, append new ones
            var seen = new HashSet<string>();
            foreach (var f in found)
            {
                seen.Add(f.Key);
                if (localByKey.TryGetValue(f.Key, out int idx))
                {
                    local[idx].Item = f.Value;
                    local[idx].Body = SafeBody(f.Value);
                    local[idx].Gone = false;
                }
                else
                {
                    localByKey[f.Key] = local.Count;
                    local.Add(new Entry { Key = f.Key, Item = f.Value, Body = SafeBody(f.Value) });
                }
            }
            foreach (var e in local) if (!seen.Contains(e.Key)) e.Gone = true;
            RebuildMapping();
            return true;
        }

        private static Rigidbody SafeBody(Item it)
        {
            try { return it.GetComponent(Il2CppType.Of<Rigidbody>())?.TryCast<Rigidbody>(); } catch { return null; }
        }

        // ---------------- host ----------------

        public byte[] BuildListMessage()
        {
            var w = Proto.Begin(MsgType.ItemList, out var ms);
            w.Write((ushort)local.Count);
            foreach (var e in local) w.Write(e.Key);
            return Proto.End(w, ms);
        }

        private static bool InVehicle(Entry e)
        {
            try { return e.Item.inVehicle; } catch { return false; }
        }

        /// <summary>
        /// Items that moved since last send (all of them every 2 s). Items inside the vehicle are sent
        /// relative to it: a box resting in a driving vehicle does not move relative to it, so it is not
        /// re-sent every tick, and the client places it on its own (interpolated) vehicle exactly.
        /// </summary>
        public byte[] BuildStateMessage(Rigidbody chassis)
        {
            bool full = Time.unscaledTime >= nextFull;
            if (full) nextFull = Time.unscaledTime + 2f;
            bool haveFrame = chassis != null && GameAccess.Alive(chassis);
            Vector3 cp = Vector3.zero; Quaternion cr = Quaternion.identity, cinv = Quaternion.identity;
            if (haveFrame) { cp = chassis.position; cr = chassis.rotation; cinv = Quaternion.Inverse(cr); }

            var idxs = new List<int>();
            var poses = new List<KeyValuePair<Vector3, Quaternion>>();
            var locals = new List<bool>();
            for (int i = 0; i < local.Count; i++)
            {
                var e = local[i];
                if (e.Gone || !GameAccess.Alive(e.Item)) { if (full) { idxs.Add(i); poses.Add(default); locals.Add(false); } continue; }
                Vector3 pos; Quaternion rot;
                if (e.Body != null) { pos = e.Body.position; rot = e.Body.rotation; }
                else { var t = e.Item.transform; pos = t.position; rot = t.rotation; }
                bool loc = haveFrame && InVehicle(e);
                if (loc) { pos = cinv * (pos - cp); rot = cinv * rot; }
                if (full || loc != e.LastLocal || (pos - e.LastPos).sqrMagnitude > 0.0001f || Quaternion.Angle(rot, e.LastRot) > 0.5f)
                {
                    idxs.Add(i); poses.Add(new KeyValuePair<Vector3, Quaternion>(pos, rot)); locals.Add(loc);
                }
            }
            if (idxs.Count == 0) return null;

            var w = Proto.Begin(MsgType.ItemState, out var ms);
            w.Write(Time.realtimeSinceStartup);
            w.Write((ushort)idxs.Count);
            for (int k = 0; k < idxs.Count; k++)
            {
                int i = idxs[k];
                var e = local[i];
                w.Write((ushort)i);
                bool alive = !e.Gone && GameAccess.Alive(e.Item);
                byte flags = 0;
                if (alive) flags |= 1;
                if (alive) { try { if (e.Item.IsTaken()) flags |= 2; } catch { } }
                if (locals[k]) flags |= 4;
                w.Write(flags);
                if (!alive) continue;
                Vector3 pos = poses[k].Key; Quaternion rot = poses[k].Value;
                e.LastPos = pos; e.LastRot = rot; e.LastLocal = locals[k];
                w.Write(pos);
                w.WriteQuatC(rot);
                Vector3 vel = Vector3.zero;
                try
                {
                    if (e.Body != null && !e.Body.isKinematic)
                    {
                        vel = e.Body.velocity;
                        if (locals[k]) vel = cinv * (vel - chassis.GetPointVelocity(e.Body.position));
                    }
                }
                catch { }
                w.Write(vel);
            }
            return Proto.End(w, ms);
        }

        // ---------------- client ----------------

        public void ReadList(BinaryReader r)
        {
            int n = r.ReadUInt16();
            hostKeys = new List<string>(n);
            for (int i = 0; i < n; i++) hostKeys.Add(r.ReadString());
            var old = snaps;
            snaps = new Snap[n];
            Array.Copy(old, snaps, Math.Min(old.Length, n));
            var oldB = bufs; var oldL = bufLocal;
            bufs = new PoseBuffer[n]; bufLocal = new bool[n];
            for (int i = 0; i < n; i++) { bufs[i] = i < oldB.Length ? oldB[i] : new PoseBuffer(); bufLocal[i] = i < oldL.Length && oldL[i]; }
            Scan(true);
            RebuildMapping();
        }

        public void ReadState(BinaryReader r)
        {
            float time = r.ReadSingle();
            NetClock.OnHostTime(time);
            int n = r.ReadUInt16();
            for (int k = 0; k < n; k++)
            {
                int i = r.ReadUInt16();
                byte flags = r.ReadByte();
                var s = new Snap();
                bool loc = (flags & 4) != 0;
                if ((flags & 1) != 0)
                {
                    s.Valid = true;
                    s.Held = (flags & 2) != 0;
                    s.Pos = r.ReadVector3();
                    s.Rot = r.ReadQuatC();
                    s.Vel = r.ReadVector3();
                }
                if (i < snaps.Length)
                {
                    snaps[i] = s;
                    if (!s.Valid) { HideGone(i); continue; }
                    if (bufLocal[i] != loc) { bufs[i].Clear(); bufLocal[i] = loc; }
                    bufs[i].Add(time, s.Pos, s.Rot, s.Vel);
                }
            }
            snapTime = Time.time;
        }

        private void HideGone(int hostIdx)
        {
            if (hostIdx >= hostToLocal.Length) return;
            int li = hostToLocal[hostIdx];
            if (li < 0) return;
            var e = local[li];
            if (e.Gone || !GameAccess.Alive(e.Item)) return;
            try { e.Item.gameObject.SetActive(false); } catch { }
            e.Gone = true;
        }

        private void RebuildMapping()
        {
            hostToLocal = new int[hostKeys.Count];
            Mapped = 0;
            for (int i = 0; i < hostKeys.Count; i++)
            {
                hostToLocal[i] = localByKey.TryGetValue(hostKeys[i], out int li) ? li : -1;
                if (hostToLocal[i] >= 0) Mapped++;
            }
        }

        /// <summary>Client FixedUpdate: mirror host item poses, except what we are carrying.</summary>
        public void Apply(Item heldLocally, VehicleSync vehicle)
        {
            IntPtr held = heldLocally != null ? heldLocally.Pointer : IntPtr.Zero;
            float rt = NetClock.RenderTime;

            // track which item we own
            Entry heldEntry = null;
            if (held != IntPtr.Zero)
            {
                foreach (var le in local)
                    if (!le.Gone && GameAccess.Alive(le.Item) && le.Item.Pointer == held) { heldEntry = le; break; }
                if (heldEntry == null && heldLocally != null)
                {
                    // not registered yet (no save id at scan time?) - try now
                    string k = KeyOf(heldLocally);
                    if (k != null) { Scan(true); if (localByKey.TryGetValue(k, out int li2)) heldEntry = local[li2]; }
                    if (heldEntry == null && lastLoggedOwn != "?")
                    { lastLoggedOwn = "?"; FarCoopPlugin.Logger.LogWarning("Carrying an item without save id - it cannot be synced: " + heldLocally.name); }
                }
            }
            int heldHost = -1;
            if (heldEntry != null)
            {
                int li = local.IndexOf(heldEntry);
                for (int h = 0; h < hostToLocal.Length; h++) if (hostToLocal[h] == li) { heldHost = h; break; }
                ownEntry = heldEntry; ownHostIdx = heldHost; ownHeld = true; ownUntil = Time.unscaledTime + OwnGrace;
                if (lastLoggedOwn != heldEntry.Key)
                {
                    lastLoggedOwn = heldEntry.Key;
                    FarCoopPlugin.Logger.LogInfo($"Carrying item {heldEntry.Key} (host index {heldHost})");
                    Telemetry.Event("I took " + heldEntry.Item.name);
                }
            }
            else if (ownHeld) { ownHeld = false; ownUntil = Time.unscaledTime + OwnGrace; lastLoggedOwn = null; }

            if (snapTime <= 0f) return;

            for (int h = 0; h < hostToLocal.Length && h < snaps.Length; h++)
            {
                int li = hostToLocal[h];
                if (li < 0 || !snaps[h].Valid) continue;
                var e = local[li];
                if (e.Gone || !GameAccess.Alive(e.Item)) continue;
                if (ownEntry != null && local[li] == ownEntry && Time.unscaledTime < ownUntil)
                {
                    if (e.Body != null) originalKinematic.Remove(e.Body.Pointer);
                    continue;
                }
                if (e.Item.Pointer == held)
                {
                    // our own character carries it: the game owns its physics now
                    if (e.Body != null) originalKinematic.Remove(e.Body.Pointer);
                    continue;
                }
                var rb = e.Body;
                try
                {
                    if (h >= bufs.Length || !bufs[h].Sample(rt, out Vector3 target, out Quaternion targetRot)) continue;
                    if (bufLocal[h])
                    {
                        // relative to the vehicle: place it on OUR vehicle's pose of this very step
                        if (!vehicle.HasFramePose) continue;
                        target = vehicle.FramePos + vehicle.FrameRot * target;
                        targetRot = vehicle.FrameRot * targetRot;
                    }
                    if (rb != null)
                    {
                        if (!originalKinematic.ContainsKey(rb.Pointer)) originalKinematic[rb.Pointer] = rb.isKinematic;
                        if (!rb.isKinematic) rb.isKinematic = true;
                        if ((rb.position - target).sqrMagnitude > 4f) { rb.position = target; rb.rotation = targetRot; }
                        else { rb.MovePosition(target); rb.MoveRotation(targetRot); }
                    }
                    else
                    {
                        var t = e.Item.transform;
                        t.position = target;
                        t.rotation = targetRot;
                    }
                }
                catch { }
            }
        }

        /// <summary>Client: pose of the item we carry / just dropped, for the host.</summary>
        public byte[] BuildHoldMessage()
        {
            if (ownEntry == null || Time.unscaledTime >= ownUntil) return null;
            var e = ownEntry;
            if (e.Gone || !GameAccess.Alive(e.Item)) return null;
            var w = Proto.Begin(MsgType.ItemHold, out var ms);
            w.Write(e.Key);
            string nm = "";
            try { nm = e.Item.name; } catch { }
            w.Write(nm);
            w.Write(ownHeld);
            var t = e.Item.transform;
            w.Write(t.position);
            w.Write(t.rotation);
            Vector3 vel = Vector3.zero;
            try { if (e.Body != null && !e.Body.isKinematic) vel = e.Body.velocity; } catch { }
            w.Write(vel);
            return Proto.End(w, ms);
        }

        /// <summary>Host: the client carries / dropped an item; follow its pose.</summary>
        /// <summary>Host: what one connected player is carrying.</summary>
        public sealed class HoldState
        {
            public int Idx = -1;
            public float Until;
            public bool Held;
            public Character Puppet;
            public string LastLogged;
            public bool Active => Idx >= 0 && Held && Time.unscaledTime < Until + 0.5f;
        }

        private void Load(HoldState hs) { clientIdx = hs.Idx; clientUntil = hs.Until; clientHeld = hs.Held; Puppet = hs.Puppet; lastLoggedHold = hs.LastLogged; }
        private void Save(HoldState hs) { hs.Idx = clientIdx; hs.Until = clientUntil; hs.Held = clientHeld; hs.LastLogged = lastLoggedHold; }

        /// <summary>Host: a player carries / dropped an item.</summary>
        public void ReadHold(BinaryReader r, HoldState hs)
        {
            Load(hs);
            try { ReadHoldInner(r); }
            finally { Save(hs); }
        }

        private void ReadHoldInner(BinaryReader r)
        {
            string key = r.ReadString();
            string name = r.ReadString();
            bool held = r.ReadBoolean();
            Vector3 pos = r.ReadVector3();
            Quaternion rot = r.ReadQuaternion();
            Vector3 vel = r.ReadVector3();
            int idx;
            if (!aliases.TryGetValue(key, out idx) && !localByKey.TryGetValue(key, out idx))
            {
                Scan(true);
                if (!localByKey.TryGetValue(key, out idx))
                {
                    // ids differ between the two games: take the nearest free item with the same name
                    idx = -1; float best = 16f;
                    for (int i = 0; i < local.Count; i++)
                    {
                        var c = local[i];
                        if (c.Gone || !GameAccess.Alive(c.Item) || aliases.ContainsValue(i)) continue;
                        try
                        {
                            if (c.Item.name != name || c.Item.IsTaken()) continue;
                            float d = (c.Item.transform.position - pos).sqrMagnitude;
                            if (d < best) { best = d; idx = i; }
                        }
                        catch { }
                    }
                    if (idx < 0) { WarnOnce("Partner carries unknown item " + key + " (" + name + ")"); return; }
                    aliases[key] = idx;
                    FarCoopPlugin.Logger.LogInfo($"Matched partner item {key} to {local[idx].Key} by name/position");
                }
            }
            var e = local[idx];
            if (e.Gone || !GameAccess.Alive(e.Item)) { WarnOnce("Partner carries an item that no longer exists here: " + key); return; }
            if (held && lastLoggedHold != key) { lastLoggedHold = key; FarCoopPlugin.Logger.LogInfo("Partner picked up " + key); Telemetry.Event("partner took " + name); }
            if (!held && lastLoggedHold == key) { lastLoggedHold = null; FarCoopPlugin.Logger.LogInfo("Partner dropped " + key); Telemetry.Event("partner dropped " + name); }

            bool wasHeld = clientIdx == idx && clientHeld && Time.unscaledTime < clientUntil;
            if (clientIdx >= 0 && clientIdx != idx) PuppetRelease(local[clientIdx], null, Vector3.zero);
            clientIdx = idx;
            clientUntil = Time.unscaledTime + 0.6f;
            clientHeld = held;
            var rb = e.Body;
            try
            {
                if (held)
                {
                    // best: the partner's body copy really takes the item, so the game itself
                    // handles parenting, colliders and the vehicle's physics-parent system
                    if (PuppetTake(e)) return;

                    try { e.Item.SetTaken(true); } catch { }
                    if (rb != null)
                    {
                        if (!originalKinematic.ContainsKey(rb.Pointer)) originalKinematic[rb.Pointer] = rb.isKinematic;
                        rb.isKinematic = true;
                        rb.position = pos; rb.rotation = rot;
                        e.Item.transform.position = pos; e.Item.transform.rotation = rot;
                    }
                    else { e.Item.transform.position = pos; e.Item.transform.rotation = rot; }
                }
                else
                {
                    if (PuppetRelease(e, (Vector3?)pos, vel)) { lastDropPos = pos; return; }
                    if (wasHeld || (rb != null && originalKinematic.ContainsKey(rb.Pointer)))
                    {
                        try { e.Item.SetTaken(false); } catch { }
                        try { e.Item.EnablePhysics(true, null); } catch { }
                        if (rb != null)
                        {
                            originalKinematic.Remove(rb.Pointer);
                            rb.position = pos; rb.rotation = rot;
                            e.Item.transform.position = pos; e.Item.transform.rotation = rot;
                            rb.isKinematic = false;
                            rb.detectCollisions = true;
                            rb.WakeUp();
                            rb.velocity = vel;
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>Host: partner body copy (set by the session every frame).</summary>
        public Character Puppet;
        private Vector3 lastDropPos;

        private bool PuppetTake(Entry e)
        {
            var pc = Puppet;
            if (pc == null || !GameAccess.Alive(pc)) return false;
            try
            {
                var cur = pc.GetHoldingItem();
                if (cur != null && cur.Pointer == e.Item.Pointer) return true;       // already in hand
                var me = GameAccess.LocalCharacter;
                var mine = me != null ? me.GetHoldingItem() : null;
                if (mine != null && mine.Pointer == e.Item.Pointer) return true;      // host holds it: ignore
                if (cur != null) { PartnerInput.AllowPuppetTake = true; try { pc.ReleaseItem(cur); } finally { PartnerInput.AllowPuppetTake = false; } }
                PartnerInput.AllowPuppetTake = true;
                try { pc.TakeItem(e.Item); } finally { PartnerInput.AllowPuppetTake = false; }
                cur = pc.GetHoldingItem();
                bool ok = cur != null && cur.Pointer == e.Item.Pointer;
                if (ok) FarCoopPlugin.Logger.LogInfo("Partner body took " + e.Key);
                return ok;
            }
            catch (Exception ex) { FarCoopPlugin.Logger.LogDebug("PuppetTake: " + ex.Message); PartnerInput.AllowPuppetTake = false; return false; }
        }

        /// <summary>Puppet drops the item (if it holds it). Optionally place it where the partner dropped it.</summary>
        private bool PuppetRelease(Entry e, Vector3? pos, Vector3 vel)
        {
            var pc = Puppet;
            if (pc == null || !GameAccess.Alive(pc) || e == null || !GameAccess.Alive(e.Item)) return false;
            try
            {
                var cur = pc.GetHoldingItem();
                if (cur == null || cur.Pointer != e.Item.Pointer) return false;
                PartnerInput.AllowPuppetTake = true;
                try { pc.ReleaseItem(cur); } finally { PartnerInput.AllowPuppetTake = false; }
                var rb = e.Body;
                if (pos.HasValue && rb != null && !rb.isKinematic)
                {
                    if ((rb.position - pos.Value).sqrMagnitude < 4f) { rb.position = pos.Value; e.Item.transform.position = pos.Value; }
                    rb.velocity = vel;
                }
                FarCoopPlugin.Logger.LogInfo("Partner body dropped " + e.Key);
                return true;
            }
            catch (Exception ex) { FarCoopPlugin.Logger.LogDebug("PuppetRelease: " + ex.Message); PartnerInput.AllowPuppetTake = false; return false; }
        }

        /// <summary>
        /// Host: if the partner stops reporting an item they held (put it in the burner, disconnected,
        /// lag), give it back to physics - otherwise it hangs frozen in the air ("flying box").
        /// </summary>
        /// <summary>Host: a player left - their body copy drops whatever it carried.</summary>
        public void ReleaseHolder(HoldState hs)
        {
            Load(hs);
            if (clientIdx >= 0 && clientIdx < local.Count) PuppetRelease(local[clientIdx], null, Vector3.zero);
            if (Puppet != null && GameAccess.Alive(Puppet))
            {
                try { var it = Puppet.GetHoldingItem(); if (it != null) { PartnerInput.AllowPuppetTake = true; try { Puppet.ReleaseItem(it); } finally { PartnerInput.AllowPuppetTake = false; } } } catch { }
            }
            hs.Idx = -1; hs.Held = false;
            nextHostTick = 0f;   // next HostTick gives frozen (kinematic) items back to physics
        }

        public void HostTick(List<HoldState> holders)
        {
            if (Time.unscaledTime < nextHostTick) return;
            nextHostTick = Time.unscaledTime + 0.25f;
            var keep = new HashSet<int>();
            foreach (var hs in holders)
            {
                if (hs.Active) { keep.Add(hs.Idx); continue; }
                Load(hs);
                if (clientIdx >= 0 && clientIdx < local.Count) PuppetRelease(local[clientIdx], null, Vector3.zero);
                if (Puppet != null && GameAccess.Alive(Puppet))
                {
                    // a partner's body copy must never keep something that partner no longer holds
                    try { var it = Puppet.GetHoldingItem(); if (it != null) { PartnerInput.AllowPuppetTake = true; try { Puppet.ReleaseItem(it); } finally { PartnerInput.AllowPuppetTake = false; } } } catch { }
                }
                hs.Idx = -1;
            }
            for (int i = 0; i < local.Count; i++)
            {
                if (keep.Contains(i)) continue;     // still really in someone's hands
                var e = local[i];
                var rb = e.Body;
                if (rb == null || !originalKinematic.ContainsKey(rb.Pointer)) continue;
                originalKinematic.Remove(rb.Pointer);
                if (e.Gone || !GameAccess.Alive(e.Item)) continue;
                try { e.Item.SetTaken(false); } catch { }
                try { rb.isKinematic = false; rb.detectCollisions = true; rb.WakeUp(); } catch { }
                FarCoopPlugin.Logger.LogInfo("Released item a partner stopped holding: " + e.Key);
            }
        }
        private float nextHostTick;

        private string lastLoggedHold;
        private readonly Dictionary<string, int> aliases = new Dictionary<string, int>();
        private readonly HashSet<string> warned = new HashSet<string>();
        private void WarnOnce(string msg) { if (warned.Add(msg)) FarCoopPlugin.Logger.LogWarning(msg); }

        private void Release(Entry e)
        {
            var rb = e.Body;
            if (rb == null) return;
            try
            {
                if (originalKinematic.TryGetValue(rb.Pointer, out bool was))
                {
                    originalKinematic.Remove(rb.Pointer);
                    rb.isKinematic = was;
                }
            }
            catch { }
        }

        public void ReleaseAll()
        {
            foreach (var e in local) Release(e);
            originalKinematic.Clear();
            ownHostIdx = -1; ownHeld = false; clientIdx = -1; ownEntry = null;
            snapTime = 0f;
            hostKeys.Clear();
            hostToLocal = new int[0];
            snaps = new Snap[0];
            bufs = new PoseBuffer[0];
            bufLocal = new bool[0];
            Mapped = 0;
        }
    }
}
