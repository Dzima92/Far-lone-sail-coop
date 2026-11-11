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
