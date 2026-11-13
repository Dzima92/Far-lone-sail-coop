using System;
using System.Collections.Generic;
using System.IO;
using FarCoopMod.Net;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// Host-authoritative vehicle. The host simulates; the client turns its own vehicle rigidbodies
    /// kinematic and drives them to the host's poses.
    /// </summary>
    public sealed class VehicleSync
    {
        private struct Snapshot
        {
            public bool Valid;
            public Vector3 Pos, Vel, AngVel;
            public Quaternion Rot;
        }

        public GameObject Root { get; private set; }
        public readonly List<Rigidbody> Bodies = new List<Rigidbody>();
        public readonly List<string> Paths = new List<string>();
        public Rigidbody Chassis { get; private set; }
        public string Signature { get; private set; } = "";

        private float nextScan;

        // client side
        private List<string> hostPaths = new List<string>();
        private int[] hostToLocal = new int[0];
        private Snapshot[] snapshots = new Snapshot[0];
        private float snapshotTime;
        private readonly Dictionary<IntPtr, bool> originalKinematic = new Dictionary<IntPtr, bool>();
        public int MappedCount { get; private set; }
        public bool HasSnapshot => snapshotTime > 0f;

        /// <summary>Finds the vehicle and its rigidbodies. Returns true if the body list changed.</summary>
        public bool Scan(bool force = false)
        {
            bool rootAlive = GameAccess.Alive(Root);
            if (!force && rootAlive && Time.unscaledTime < nextScan) return false;
            nextScan = Time.unscaledTime + 2f;

            GameObject root = rootAlive ? Root : GameObject.Find("Vehicle");
            if (!GameAccess.Alive(root))
            {
                bool changed = Bodies.Count > 0;
                Root = null; Bodies.Clear(); Paths.Clear(); Chassis = null; Signature = "";
                if (changed) RebuildMapping();
                return changed;
            }
            Root = root;

            var found = GameAccess.GetCompsInChildren<Rigidbody>(root);
            var entries = new List<KeyValuePair<string, Rigidbody>>();
            foreach (var rb in found)
            {
                // characters (players / puppet) and loose items are synced separately
                if (IsCharacterOrItem(rb)) continue;
                entries.Add(new KeyValuePair<string, Rigidbody>(GameAccess.GetRelativePath(rb.transform, root.transform), rb));
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            string sig = entries.Count + ":" + string.Join(";", entries.ConvertAll(e => e.Key));
            if (sig == Signature) return false;

            Bodies.Clear(); Paths.Clear(); Chassis = null;
            float heaviest = -1f;
            foreach (var e in entries)
            {
                Paths.Add(e.Key);
                Bodies.Add(e.Value);
                float m = 0f;
                try { m = e.Value.mass; } catch { }
                if (m > heaviest) { heaviest = m; Chassis = e.Value; }
            }
            Signature = sig;
            try { motor = GameAccess.GetCompInChildren<Okomotive.FarLoneSails.VehicleMotor>(root); } catch { motor = null; }
            FarCoopPlugin.Logger.LogInfo($"Vehicle scan: {Bodies.Count} rigidbodies, chassis = {(Chassis != null ? Chassis.name : "none")}");
            RebuildMapping();
            return true;
        }

        private Okomotive.FarLoneSails.VehicleMotor motor;

        private static bool IsCharacterOrItem(Rigidbody rb)
        {
            try
            {
                if (rb.GetComponentInParent(UnhollowerRuntimeLib.Il2CppType.Of<Okomotive.FarLoneSails.Character>()) != null) return true;
                if (rb.GetComponentInParent(UnhollowerRuntimeLib.Il2CppType.Of<Okomotive.FarLoneSails.Item>()) != null) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Vehicle fuel/energy (NaN if unknown).</summary>
        public float Energy
        {
            get
            {
                try { if (GameAccess.Alive(motor)) return motor.GetEnergy().GetValue(); } catch { }
                return float.NaN;
            }
            set
            {
                try { if (GameAccess.Alive(motor) && !float.IsNaN(value)) motor.GetEnergy().SetValue(value); } catch { }
            }
        }

        public Transform Frame => GameAccess.Alive(Chassis) ? Chassis.transform : (GameAccess.Alive(Root) ? Root.transform : null);

        // ---------------- host ----------------

        public byte[] BuildBodiesMessage()
        {
            var w = Proto.Begin(MsgType.VehicleBodies, out var ms);
            w.Write((ushort)Paths.Count);
            foreach (var p in Paths) w.Write(p);
            return Proto.End(w, ms);
        }

        // host: what was last sent per body, so resting parts are not re-sent 30x a second
        private Vector3[] sentPos = new Vector3[0];
        private Quaternion[] sentRot = new Quaternion[0];
        private float nextFullState;

        public byte[] BuildStateMessage()
        {
            if (sentPos.Length != Bodies.Count) { sentPos = new Vector3[Bodies.Count]; sentRot = new Quaternion[Bodies.Count]; nextFullState = 0f; }
            bool full = Time.unscaledTime >= nextFullState;
            if (full) nextFullState = Time.unscaledTime + 1f;

            var idx = new List<int>(Bodies.Count);
            for (int i = 0; i < Bodies.Count; i++)
            {
                var rb = Bodies[i];
                if (!GameAccess.Alive(rb)) continue;
                if (full) { idx.Add(i); continue; }
                Vector3 p = rb.position;
                bool moved = (p - sentPos[i]).sqrMagnitude > 0.000004f                  // 2 mm
                             || Quaternion.Angle(rb.rotation, sentRot[i]) > 0.2f
                             || rb.velocity.sqrMagnitude > 0.0004f                       // still moving
                             || rb.angularVelocity.sqrMagnitude > 0.0004f;
                if (moved) idx.Add(i);
            }

            var w = Proto.Begin(MsgType.VehicleState, out var ms);
            w.Write((ushort)idx.Count);
            foreach (int i in idx)
            {
                var rb = Bodies[i];
                Vector3 p = rb.position; Quaternion q = rb.rotation;
                sentPos[i] = p; sentRot[i] = q;
                w.Write((ushort)i);
                w.Write(p);
                w.Write(q);
                w.Write(rb.velocity);
                w.Write(rb.angularVelocity);
            }
            w.Write(Energy);
            return Proto.End(w, ms);
        }

        // ---------------- client ----------------

        public void ReadBodiesMessage(BinaryReader r)
        {
            int n = r.ReadUInt16();
            hostPaths = new List<string>(n);
            for (int i = 0; i < n; i++) hostPaths.Add(r.ReadString());
            snapshots = new Snapshot[n];
            RebuildMapping();
        }

        public void ReadStateMessage(BinaryReader r)
        {
            int n = r.ReadUInt16();
            for (int k = 0; k < n; k++)
            {
                int i = r.ReadUInt16();
                var s = new Snapshot
                {
                    Valid = true,
                    Pos = r.ReadVector3(),
                    Rot = r.ReadQuaternion(),
                    Vel = r.ReadVector3(),
                    AngVel = r.ReadVector3(),
                };
                if (i < snapshots.Length) snapshots[i] = s;
            }
            snapshotTime = Time.time;
            if (r.BaseStream.Position + 4 <= r.BaseStream.Length) Energy = r.ReadSingle();
        }

        private void RebuildMapping()
        {
            var local = new Dictionary<string, int>();
            for (int i = 0; i < Paths.Count; i++) local[Paths[i]] = i;
            hostToLocal = new int[hostPaths.Count];
            MappedCount = 0;
            for (int i = 0; i < hostPaths.Count; i++)
            {
                hostToLocal[i] = local.TryGetValue(hostPaths[i], out int li) ? li : -1;
                if (hostToLocal[i] >= 0) MappedCount++;
            }
            if (hostPaths.Count > 0)
                FarCoopPlugin.Logger.LogInfo($"Vehicle mapping: {MappedCount}/{hostPaths.Count} host bodies matched locally");
        }

        /// <summary>Client: call from FixedUpdate.</summary>
        public void ApplySnapshots()
        {
            if (!HasSnapshot) return;
            float age = Mathf.Clamp(Time.time - snapshotTime, 0f, 0.25f);

            for (int h = 0; h < hostToLocal.Length; h++)
            {
                int li = hostToLocal[h];
                if (li < 0 || li >= Bodies.Count || h >= snapshots.Length || !snapshots[h].Valid) continue;
                var rb = Bodies[li];
                if (!GameAccess.Alive(rb)) continue;

                try
                {
                    if (!rb.isKinematic)
                    {
                        if (!originalKinematic.ContainsKey(rb.Pointer)) originalKinematic[rb.Pointer] = false;
                        rb.isKinematic = true;
                    }
                    else if (!originalKinematic.ContainsKey(rb.Pointer))
                    {
                        originalKinematic[rb.Pointer] = true;
                    }

                    var s = snapshots[h];
                    Vector3 target = s.Pos + s.Vel * age;
                    Quaternion targetRot = s.Rot;
                    float angSpeed = s.AngVel.magnitude;
                    if (angSpeed > 0.001f)
                        targetRot = Quaternion.AngleAxis(angSpeed * age * Mathf.Rad2Deg, s.AngVel / angSpeed) * s.Rot;

                    if ((rb.position - target).sqrMagnitude > 9f)
                    {
                        rb.position = target;
                        rb.rotation = targetRot;
                        rb.transform.position = target;
                        rb.transform.rotation = targetRot;
                    }
                    else
                    {
                        rb.MovePosition(Vector3.Lerp(rb.position, target, 0.5f));
                        rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRot, 0.5f));
                    }
                }
                catch { }
            }
        }

        /// <summary>Client: give physics back to the local game (on disconnect).</summary>
        public void ReleaseControl()
        {
            foreach (var rb in Bodies)
            {
                if (!GameAccess.Alive(rb)) continue;
                try
                {
                    if (originalKinematic.TryGetValue(rb.Pointer, out bool wasKinematic))
                        rb.isKinematic = wasKinematic;
                }
                catch { }
            }
            originalKinematic.Clear();
            snapshotTime = 0f;
            hostPaths.Clear();
            hostToLocal = new int[0];
            snapshots = new Snapshot[0];
            MappedCount = 0;
        }
    }
}
