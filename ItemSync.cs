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

        /// <summary>Items that moved since last send (all of them every 2 s).</summary>
        public byte[] BuildStateMessage()
        {
            bool full = Time.unscaledTime >= nextFull;
            if (full) nextFull = Time.unscaledTime + 2f;

            var w = Proto.Begin(MsgType.ItemState, out var ms);
            var idxs = new List<int>();
            for (int i = 0; i < local.Count; i++)
            {
                var e = local[i];
                if (i == clientIdx && Time.unscaledTime < clientUntil) continue;
                if (e.Gone || !GameAccess.Alive(e.Item)) { if (full) idxs.Add(i); continue; }
                var t = e.Item.transform;
                if (full || (t.position - e.LastPos).sqrMagnitude > 0.0001f || Quaternion.Angle(t.rotation, e.LastRot) > 0.5f)
                    idxs.Add(i);
            }
            if (idxs.Count == 0) return null;

            w.Write((ushort)idxs.Count);
            foreach (int i in idxs)
            {
                var e = local[i];
                w.Write((ushort)i);
                bool alive = !e.Gone && GameAccess.Alive(e.Item);
                byte flags = 0;
                if (alive) flags |= 1;
                if (alive) { try { if (e.Item.IsTaken()) flags |= 2; } catch { } }
                w.Write(flags);
                if (!alive) continue;
                var t = e.Item.transform;
                e.LastPos = t.position; e.LastRot = t.rotation;
                w.Write(t.position);
                w.Write(t.rotation);
                Vector3 vel = Vector3.zero;
                try { if (e.Body != null && !e.Body.isKinematic) vel = e.Body.velocity; } catch { }
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
            Scan(true);
            RebuildMapping();
        }

        public void ReadState(BinaryReader r)
        {
            int n = r.ReadUInt16();
            for (int k = 0; k < n; k++)
            {
                int i = r.ReadUInt16();
                byte flags = r.ReadByte();
                var s = new Snap();
                if ((flags & 1) != 0)
                {
                    s.Valid = true;
                    s.Held = (flags & 2) != 0;
                    s.Pos = r.ReadVector3();
                    s.Rot = r.ReadQuaternion();
                    s.Vel = r.ReadVector3();
                }
                if (i < snaps.Length)
                {
                    snaps[i] = s;
                    if (!s.Valid) HideGone(i);
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
        public void Apply(Item heldLocally)
        {
            IntPtr held = heldLocally != null ? heldLocally.Pointer : IntPtr.Zero;
            float age = Mathf.Clamp(Time.time - snapTime, 0f, 0.2f);

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
                    var s = snaps[h];
                    Vector3 target = s.Pos + s.Vel * age;
                    if (rb != null)
                    {
                        if (!originalKinematic.ContainsKey(rb.Pointer)) originalKinematic[rb.Pointer] = rb.isKinematic;
                        if (!rb.isKinematic) rb.isKinematic = true;
                        if ((rb.position - target).sqrMagnitude > 4f) { rb.position = target; rb.rotation = s.Rot; }
                        else { rb.MovePosition(Vector3.Lerp(rb.position, target, 0.5f)); rb.MoveRotation(Quaternion.Slerp(rb.rotation, s.Rot, 0.5f)); }
                    }
                    else
                    {
                        var t = e.Item.transform;
                        t.position = Vector3.Lerp(t.position, target, 0.5f);
                        t.rotation = Quaternion.Slerp(t.rotation, s.Rot, 0.5f);
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
        public void ReadHold(BinaryReader r)
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
            if (held && lastLoggedHold != key) { lastLoggedHold = key; FarCoopPlugin.Logger.LogInfo("Partner picked up " + key); }
            if (!held && lastLoggedHold == key) { lastLoggedHold = null; FarCoopPlugin.Logger.LogInfo("Partner dropped " + key); }

            bool wasHeld = clientIdx == idx && clientHeld && Time.unscaledTime < clientUntil;
            clientIdx = idx;
            clientUntil = Time.unscaledTime + 0.6f;
            clientHeld = held;
            var rb = e.Body;
            try
            {
                if (held)
                {
                    try { e.Item.SetTaken(true); } catch { }
                    if (rb != null)
                    {
                        if (!originalKinematic.ContainsKey(rb.Pointer)) originalKinematic[rb.Pointer] = rb.isKinematic;
                        rb.isKinematic = true;
                        // set both: FAR items have a separate interpolated visual that ignores MovePosition
                        rb.position = pos; rb.rotation = rot;
                        e.Item.transform.position = pos; e.Item.transform.rotation = rot;
                    }
                    else { e.Item.transform.position = pos; e.Item.transform.rotation = rot; }
                }
                else
                {
                    if (wasHeld || (rb != null && originalKinematic.ContainsKey(rb.Pointer)))
                    {
                        try { e.Item.SetTaken(false); } catch { }
                        if (rb != null)
                        {
                            originalKinematic.Remove(rb.Pointer);
                            rb.position = pos; rb.rotation = rot;
                            e.Item.transform.position = pos; e.Item.transform.rotation = rot;
                            rb.isKinematic = false;           // dropped items always fall
                            rb.detectCollisions = true;
                            rb.WakeUp();
                            rb.velocity = vel;
                        }
                    }
                    else if (rb != null && !rb.isKinematic && (rb.position - pos).sqrMagnitude > 0.25f)
                    {
                        // keep the dropped item where the client sees it while it settles
                        rb.position = pos; rb.rotation = rot; rb.velocity = vel;
                    }
                }
            }
            catch { }
        }

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
            Mapped = 0;
        }
    }
}
