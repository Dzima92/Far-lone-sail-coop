using System.IO;
using UnityEngine;

namespace FarCoopMod.Net
{
    public enum MsgType : byte
    {
        Hello = 1,          // client -> host : version, name, scene
        Welcome = 2,        // host -> client : version, name, scene
        Ping = 3,
        Pong = 4,
        Player = 5,         // both ways: avatar transform + animator
        VehicleBodies = 6,  // host -> client : list of rigidbody paths under "Vehicle"
        VehicleState = 7,   // host -> client : rigidbody states
        Button = 8,         // both ways: vehicle button / switch press
        Bye = 9,
        ItemList = 10,      // host -> client : item save-ids
        ItemState = 11,     // host -> client : item poses
        ItemHold = 12,      // client -> host : item the client carries / just dropped
        Hose = 13,          // client -> host : fire hose taken / released
        PlayerFrom = 14,    // host -> clients: [player id] + Player payload (host = id 0)
        PlayerInfo = 15,    // host -> clients: [player id][name]
        PlayerLeft = 16,    // host -> clients: [player id]
        HoseFrom = 17,      // host -> clients: [player id][path][planted]
        PauseAll = 18,      // host -> clients: everybody is in the pause menu
    }

    public static class Proto
    {
        public static BinaryWriter Begin(MsgType type, out MemoryStream ms)
        {
            ms = new MemoryStream(64);
            var w = new BinaryWriter(ms);
            w.Write((byte)type);
            return w;
        }

        public static byte[] End(BinaryWriter w, MemoryStream ms)
        {
            w.Flush();
            return ms.ToArray();
        }

        public static void Write(this BinaryWriter w, Vector3 v)
        {
            w.Write(v.x); w.Write(v.y); w.Write(v.z);
        }

        public static void Write(this BinaryWriter w, Quaternion q)
        {
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
        }

        /// <summary>Rotation in 7 bytes ("smallest three": drop the largest component).</summary>
        public static void WriteQuatC(this BinaryWriter w, Quaternion q)
        {
            float[] c = { q.x, q.y, q.z, q.w };
            int big = 0;
            for (int i = 1; i < 4; i++) if (Mathf.Abs(c[i]) > Mathf.Abs(c[big])) big = i;
            float sign = c[big] < 0f ? -1f : 1f;
            w.Write((byte)big);
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                w.Write((short)Mathf.Clamp(Mathf.RoundToInt(c[i] * sign * 32767f / 0.70711f), -32767, 32767));
            }
        }

        public static Quaternion ReadQuatC(this BinaryReader r)
        {
            int big = r.ReadByte() & 3;
            float[] c = new float[4];
            float sum = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (i == big) continue;
                c[i] = r.ReadInt16() * 0.70711f / 32767f;
                sum += c[i] * c[i];
            }
            c[big] = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));
            return new Quaternion(c[0], c[1], c[2], c[3]);
        }

        public static Vector3 ReadVector3(this BinaryReader r)
        {
            return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        public static Quaternion ReadQuaternion(this BinaryReader r)
        {
            return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }
    }
}
