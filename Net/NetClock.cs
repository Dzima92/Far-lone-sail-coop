using UnityEngine;

namespace FarCoopMod.Net
{
    /// <summary>
    /// Client: estimate of the host's clock and the "render time" we show the world at.
    /// Like most networked games we draw the host's world a little in the past (interpolation
    /// delay) so there are always two snapshots to blend between - smooth even with jitter.
    /// </summary>
    public static class NetClock
    {
        private static bool init;
        private static float offset;          // host time - local time (lowest-latency estimate)
        private static float jitter;          // smoothed deviation from that
        private static float lastHostT;
        private static float interval = 1f / 30f;   // measured host send interval

        public static float Delay { get; private set; } = 0.1f;
        public static float HostNow => Time.realtimeSinceStartup + offset;
        public static float RenderTime => HostNow - Delay;

        public static void Reset() { init = false; jitter = 0f; Delay = 0.1f; lastHostT = 0f; }

        /// <summary>Call for every timestamped message from the host.</summary>
        public static void OnHostTime(float hostT)
        {
            float sample = hostT - Time.realtimeSinceStartup;
            if (!init) { offset = sample; init = true; lastHostT = hostT; return; }
            if (sample > offset) offset = sample;                    // arrived faster than ever: adopt
            else offset += (sample - offset) * 0.002f;               // slowly forget old fast samples
            jitter = Mathf.Lerp(jitter, offset - sample, 0.05f);
            float dt = hostT - lastHostT;
            if (dt > 0.001f && dt < 0.5f) interval = Mathf.Lerp(interval, dt, 0.05f);
            if (hostT > lastHostT) lastHostT = hostT;
            Delay = Mathf.Clamp(interval * 1.5f + jitter * 2f + 0.015f, 0.05f, 0.3f);
        }
    }

    /// <summary>Small ring buffer of timestamped poses with interpolation / short extrapolation.</summary>
    public sealed class PoseBuffer
    {
        private const int N = 8;
        private readonly float[] t = new float[N];
        private readonly Vector3[] p = new Vector3[N];
        private readonly Quaternion[] q = new Quaternion[N];
        private readonly Vector3[] v = new Vector3[N];
        private int head = -1, count;

        public bool HasData => count > 0;
        public float NewestTime => count > 0 ? t[head] : 0f;

        public void Clear() { head = -1; count = 0; }

        public void Add(float time, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (count > 0 && time <= t[head]) { if (time == t[head]) { p[head] = pos; q[head] = rot; v[head] = vel; } return; }
            head = (head + 1) % N;
            t[head] = time; p[head] = pos; q[head] = rot; v[head] = vel;
            if (count < N) count++;
        }

        public bool Sample(float rt, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (count == 0) return false;
            // newest first
            int newer = -1;
            for (int k = 0; k < count; k++)
            {
                int i = (head - k + N) % N;
                if (t[i] <= rt)
                {
                    if (newer < 0)
                    {
                        // past the newest sample: extrapolate a little with its velocity
                        float ahead = Mathf.Min(rt - t[i], 0.25f);
                        pos = p[i] + v[i] * ahead;
                        rot = q[i];
                        return true;
                    }
                    float span = t[newer] - t[i];
                    float a = span > 1e-5f ? (rt - t[i]) / span : 1f;
                    pos = Vector3.LerpUnclamped(p[i], p[newer], a);
                    rot = Quaternion.Slerp(q[i], q[newer], a);
                    return true;
                }
                newer = i;
            }
            // older than everything we have
            int oldest = (head - count + 1 + N) % N;
            pos = p[oldest]; rot = q[oldest];
            return true;
        }
    }
}
