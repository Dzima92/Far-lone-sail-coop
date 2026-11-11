using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FarCoopMod.Net
{
    public enum NetRole { None, Host, Client }

    public struct DiscoveredHost
    {
        public string Ip;
        public int Port;
        public string Name;
    }

    /// <summary>
    /// Host / client / LAN discovery. All results are handed to the main thread through queues.
    /// </summary>
    public sealed class CoopNet
    {
        private const string DiscoverRequest = "FARCOOP_DISCOVER_V2";
        private const string DiscoverReply = "FARCOOP_HOST_V2";

        public NetRole Role { get; private set; } = NetRole.None;
        public Connection Peer { get; private set; }
        public bool Searching => Volatile.Read(ref searching) != 0;
        public bool Connecting => Volatile.Read(ref connecting) != 0;

        public readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();
        public readonly ConcurrentQueue<DiscoveredHost> Discovered = new ConcurrentQueue<DiscoveredHost>();
        private readonly ConcurrentQueue<Connection> pendingConnections = new ConcurrentQueue<Connection>();

        private TcpListener listener;
        private UdpClient discoveryResponder;
        private volatile bool hostRunning;
        private int searching;
        private int connecting;
        private int hostPort;
        private string hostName = "Host";

        // ---------------- host ----------------

        public bool StartHost(int port, int discoveryPort, string name)
        {
            StopAll("restart");
            try
            {
                hostPort = port;
                hostName = name;
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                hostRunning = true;
                Role = NetRole.Host;
                new Thread(AcceptLoop) { IsBackground = true, Name = "FarCoop-Accept" }.Start();
            }
            catch (Exception e)
            {
                Events.Enqueue("Host failed on port " + port + ": " + e.Message);
                StopAll("host failed");
                return false;
            }

            try
            {
                discoveryResponder = new UdpClient();
                discoveryResponder.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                discoveryResponder.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                discoveryResponder.EnableBroadcast = true;
                new Thread(DiscoveryResponderLoop) { IsBackground = true, Name = "FarCoop-Discovery" }.Start();
            }
            catch (Exception e)
            {
                Events.Enqueue("LAN search responder unavailable (" + e.Message + "). Clients can still join by IP.");
            }

            Events.Enqueue("Hosting on port " + port + ". Local IPs: " + string.Join(", ", LocalIPv4s()));
            return true;
        }

        private void AcceptLoop()
        {
            while (hostRunning)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    pendingConnections.Enqueue(new Connection(client));
                }
                catch (Exception e)
                {
                    if (hostRunning) Events.Enqueue("Accept error: " + e.Message);
                    Thread.Sleep(200);
                }
            }
        }

        private void DiscoveryResponderLoop()
        {
            var udp = discoveryResponder;
            while (hostRunning && udp != null)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref from);
                    string text = Encoding.UTF8.GetString(data);
                    if (!text.StartsWith(DiscoverRequest)) continue;
                    byte[] reply = Encoding.UTF8.GetBytes(DiscoverReply + "|" + hostPort + "|" + hostName.Replace("|", "/"));
                    udp.Send(reply, reply.Length, from);
                }
                catch (Exception)
                {
                    if (!hostRunning) break;
                    Thread.Sleep(100);
                }
            }
        }

        // ---------------- client ----------------

        public void Connect(string ip, int port)
        {
            if (Interlocked.Exchange(ref connecting, 1) != 0) return;
            StopAll("connect");
            Role = NetRole.Client;
            new Thread(() =>
            {
                TcpClient client = new TcpClient();
                try
                {
                    var ar = client.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("no answer in 5s (is the host running and port " + port + " allowed in the firewall?)");
                    client.EndConnect(ar);
                    pendingConnections.Enqueue(new Connection(client));
                }
                catch (Exception e)
                {
                    try { client.Close(); } catch { }
                    Events.Enqueue("Connect to " + ip + ":" + port + " failed: " + e.Message);
                    Events.Enqueue("#connect_failed");
                }
                finally { Interlocked.Exchange(ref connecting, 0); }
            }) { IsBackground = true, Name = "FarCoop-Connect" }.Start();
        }

        public void SearchLan(int discoveryPort)
        {
            if (Interlocked.Exchange(ref searching, 1) != 0) return;
            new Thread(() =>
            {
                var seen = new HashSet<string>();
                try
                {
                    using (var udp = new UdpClient(0))
                    {
                        udp.EnableBroadcast = true;
                        udp.Client.ReceiveTimeout = 400;
                        byte[] req = Encoding.UTF8.GetBytes(DiscoverRequest);
                        var targets = new List<IPAddress> { IPAddress.Broadcast };
                        targets.AddRange(SubnetBroadcasts());

                        DateTime end = DateTime.UtcNow.AddSeconds(3);
                        DateTime nextSend = DateTime.MinValue;
                        while (DateTime.UtcNow < end)
                        {
                            if (DateTime.UtcNow >= nextSend)
                            {
                                foreach (var t in targets)
                                {
                                    try { udp.Send(req, req.Length, new IPEndPoint(t, discoveryPort)); } catch { }
                                }
                                nextSend = DateTime.UtcNow.AddSeconds(1);
                            }
                            try
                            {
                                var from = new IPEndPoint(IPAddress.Any, 0);
                                byte[] data = udp.Receive(ref from);
                                string[] parts = Encoding.UTF8.GetString(data).Split('|');
                                if (parts.Length < 3 || parts[0] != DiscoverReply) continue;
                                string ip = from.Address.ToString();
                                if (!seen.Add(ip)) continue;
                                int.TryParse(parts[1], out int port);
                                Discovered.Enqueue(new DiscoveredHost { Ip = ip, Port = port, Name = parts[2] });
                            }
                            catch (SocketException) { /* timeout */ }
                        }
                    }
                }
                catch (Exception e) { Events.Enqueue("LAN search error: " + e.Message); }
                finally
                {
                    if (seen.Count == 0) Events.Enqueue("#search_none");
                    Interlocked.Exchange(ref searching, 0);
                }
            }) { IsBackground = true, Name = "FarCoop-Search" }.Start();
        }

        // ---------------- main thread ----------------

        /// <summary>Call from Unity main thread. Returns a newly established peer connection, if any.</summary>
        public Connection PollNewPeer()
        {
            Connection newPeer = null;
            while (pendingConnections.TryDequeue(out var c))
            {
                if (Peer != null && !Peer.Closed)
                {
                    c.Close("busy");
                    Events.Enqueue("Rejected extra connection from " + c.RemoteAddress + " (only 1 partner supported).");
                    continue;
                }
                Peer = c;
                newPeer = c;
            }
            return newPeer;
        }

        public void DropPeer(string reason)
        {
            Peer?.Close(reason);
            Peer = null;
            if (Role == NetRole.Client) Role = NetRole.None;
        }

        public void StopAll(string reason)
        {
            hostRunning = false;
            try { listener?.Stop(); } catch { }
            listener = null;
            try { discoveryResponder?.Close(); } catch { }
            discoveryResponder = null;
            Peer?.Close(reason);
            Peer = null;
            Role = NetRole.None;
        }

        // ---------------- helpers ----------------

        public static List<string> LocalIPv4s()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add(ua.Address.ToString());
                }
            }
            catch { }
            return list;
        }

        private static IEnumerable<IPAddress> SubnetBroadcasts()
        {
            var result = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4) continue;
                        var b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                        result.Add(new IPAddress(b));
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
