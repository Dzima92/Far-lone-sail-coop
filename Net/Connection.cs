using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace FarCoopMod.Net
{
    /// <summary>
    /// One TCP peer. Length-prefixed frames, a receive thread and a send thread.
    /// Never touches Unity / IL2CPP objects (background threads are not attached to IL2CPP).
    /// </summary>
    public sealed class Connection
    {
        private const int MaxFrame = 4 * 1024 * 1024;

        private readonly TcpClient tcp;
        private readonly NetworkStream stream;
        private readonly BlockingCollection<byte[]> sendQueue =
            new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 4096);

        public readonly ConcurrentQueue<byte[]> Incoming = new ConcurrentQueue<byte[]>();
        public readonly string RemoteAddress;

        private int closed;
        public bool Closed => Volatile.Read(ref closed) != 0;
        public string CloseReason { get; private set; } = "";

        private long lastReceiveTicks;
        public double SecondsSinceReceive =>
            TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastReceiveTicks)).TotalSeconds;

        public long BytesIn, BytesOut, FramesIn, FramesOut;

        public Connection(TcpClient client)
        {
            tcp = client;
            tcp.NoDelay = true;
            tcp.ReceiveBufferSize = 256 * 1024;
            tcp.SendBufferSize = 256 * 1024;
            stream = tcp.GetStream();
            RemoteAddress = client.Client.RemoteEndPoint?.ToString() ?? "?";
            Interlocked.Exchange(ref lastReceiveTicks, DateTime.UtcNow.Ticks);

            new Thread(ReceiveLoop) { IsBackground = true, Name = "FarCoop-RX" }.Start();
            new Thread(SendLoop) { IsBackground = true, Name = "FarCoop-TX" }.Start();
        }

        public void Send(byte[] frame)
        {
            if (Closed || frame == null) return;
            try { sendQueue.TryAdd(frame); } catch { /* adding completed */ }
        }

        private void ReceiveLoop()
        {
            try
            {
                var reader = new BinaryReader(stream);
                while (!Closed)
                {
                    int len = reader.ReadInt32();
                    if (len <= 0 || len > MaxFrame) throw new InvalidDataException("bad frame length " + len);
                    byte[] buf = reader.ReadBytes(len);
                    if (buf.Length != len) throw new EndOfStreamException();
                    Interlocked.Exchange(ref lastReceiveTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Add(ref BytesIn, len + 4);
                    Interlocked.Increment(ref FramesIn);
                    Incoming.Enqueue(buf);
                }
            }
            catch (Exception e) { Close("receive: " + e.GetType().Name + " " + e.Message); }
        }

        private void SendLoop()
        {
            try
            {
                // everything queued since the last write goes out in ONE socket write (one TCP packet
                // when it fits) instead of two writes per message
                var batch = new MemoryStream(16 * 1024);
                while (!Closed)
                {
                    byte[] frame;
                    try { frame = sendQueue.Take(); }
                    catch (InvalidOperationException) { break; }   // CompleteAdding
                    batch.SetLength(0);
                    int frames = 0;
                    do
                    {
                        int len = frame.Length;
                        batch.WriteByte((byte)len); batch.WriteByte((byte)(len >> 8));
                        batch.WriteByte((byte)(len >> 16)); batch.WriteByte((byte)(len >> 24));
                        batch.Write(frame, 0, len);
                        frames++;
                    } while (batch.Length < 60 * 1024 && sendQueue.TryTake(out frame));
                    stream.Write(batch.GetBuffer(), 0, (int)batch.Length);
                    Interlocked.Add(ref BytesOut, batch.Length);
                    Interlocked.Add(ref FramesOut, frames);
                }
            }
            catch (Exception e) { Close("send: " + e.GetType().Name + " " + e.Message); }
        }

        public void Close(string reason = "closed")
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            CloseReason = reason;
            try { sendQueue.CompleteAdding(); } catch { }
            try { stream.Close(); } catch { }
            try { tcp.Close(); } catch { }
        }
    }
}
