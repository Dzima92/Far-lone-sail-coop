using System;
using System.Collections.Generic;
using System.IO;
using FarCoopMod.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FarCoopMod
{
    /// <summary>
    /// Thin Unity component (injected into IL2CPP). Only Unity callbacks live here; all logic is in
    /// the plain C# <see cref="CoopSession"/> so the IL2CPP class injector sees a minimal type.
    /// </summary>
    public class CoopManager : MonoBehaviour
    {
        public CoopManager(IntPtr ptr) : base(ptr) { }

        private static GameObject host;

        public static void EnsureExists()
        {
            if (GameAccess.Alive(host)) return;
            if (CoopSession.Instance == null) CoopSession.Instance = new CoopSession();
            host = new GameObject("FarCoopManager");
            DontDestroyOnLoad(host);
            host.AddComponent<CoopManager>();
        }

        private void Update() { CoopSession.Instance?.Update(); }
        private void FixedUpdate() { CoopSession.Instance?.FixedUpdate(); }
        private void OnGUI() { CoopSession.Instance?.OnGUI(); }
        private void OnApplicationQuit() { CoopSession.Instance?.Quit(); }
    }

    /// <summary>Main-thread brain of the mod: networking pump, sync, hotkeys and HUD.</summary>
    public class CoopSession
    {
        public static CoopSession Instance;

        private readonly CoopNet net;
        private readonly VehicleSync vehicle;
        private readonly RemoteAvatar partner;
        private readonly PartnerPuppet puppet = new PartnerPuppet();
        private readonly ItemSync items = new ItemSync();
        private Okomotive.FarLoneSails.UIScreenPause pauseScreen;
        private float nextPauseSearch;
        private bool localPaused;
        private bool autoResumeDone;
        private float autoResumeAt = -1f;

        private bool showHud = true;
        private string ipField = "";
        private bool ipEditing;
        private readonly List<string> log = new List<string>();
        private readonly List<DiscoveredHost> found = new List<DiscoveredHost>();

        private bool handshakeDone;
        private string partnerName = "";
        private string partnerScene = "";
        private float sendTimer, pingTimer, rttMs = -1f;
        private int errorCount;

        public bool IsConnected => net != null && net.Peer != null && !net.Peer.Closed && handshakeDone;
        public bool IsHost => net != null && net.Role == NetRole.Host;

        public CoopSession()
        {
            net = new CoopNet();
            vehicle = new VehicleSync();
            partner = new RemoteAvatar();
            showHud = FarCoopPlugin.CfgShowHud.Value;
            ipField = FarCoopPlugin.CfgLastHostIp.Value;
            AddLog("Co-op ready. F1 host, F2 find & join, F3 disconnect, F4 HUD, F5 go to partner");
        }

        // ======================= main loop =======================

        public void Update()
        {
            // Keep simulating + networking when the window is not focused (alt-tab, remote desktop).
            if (!Application.runInBackground) Application.runInBackground = true;
            PartnerInput.Depth = 0; // safety: never leak an input override across frames
            PartnerInput.Tick();
            Guard("hotkeys", HandleHotkeys);
            Guard("net", PumpNetwork);
            Guard("pause", HandlePause);
            Guard("puppet", UpdatePuppet);
            Guard("send", SendState);
            Guard("avatar", () => partner.Update(vehicle, Time.deltaTime));
            Guard("diag", Diagnostics);
        }

        public void FixedUpdate()
        {
            if (!IsConnected) return;
            // both sides: the partner is a real character pulled to where the partner really is
            Guard("puppet-fix", () =>
            {
                var target = partner.PartnerRootWorld(vehicle);
                if (puppet.Exists && target.HasValue) puppet.Correct(target.Value);
            });
            if (IsHost) return;
            Guard("vehicle", () =>
            {
                vehicle.Scan();
                vehicle.ApplySnapshots();
            });
            Guard("items", () =>
            {
                Okomotive.FarLoneSails.Item held = null;
                try { held = GameAccess.LocalCharacter?.GetHoldingItem(); } catch { }
                items.Apply(held);
            });
        }

        private float nextDiag;
        private void Diagnostics()
        {
            if (!IsConnected || Time.unscaledTime < nextDiag) return;
            nextDiag = Time.unscaledTime + 5f;
            var ch = GameAccess.LocalCharacter;
            string me = "none";
            if (ch != null)
            {
                var a = GameAccess.CharacterAnimator(ch);
                me = $"root={ch.transform.position} vis={(a != null ? a.transform.position.ToString() : "-")} inside={GameAccess.CharacterInsideVehicle(ch)}";
            }
            FarCoopPlugin.Logger.LogInfo($"DIAG me {me} | {partner.DebugInfo(vehicle)} | puppet={(puppet.Exists ? puppet.Position.ToString() : "none")} | frame={(vehicle.Frame != null ? vehicle.Frame.position.ToString() : "-")}");
        }

        /// <summary>Host: keep a real game character for the partner so they can use the vehicle.</summary>
        private void UpdatePuppet()
        {
            bool allowed = IsConnected && FarCoopPlugin.CfgClientPuppet.Value;
            if (!allowed)
            {
                if (puppet.Exists) { puppet.Destroy(); FarCoopPlugin.Logger.LogInfo("Partner puppet removed (not connected/host)"); }
                partner.Suppressed = false;
                return;
            }
            bool ready = partner.PartnerRootWorld(vehicle).HasValue && GameAccess.LocalCharacter != null;
            if (!puppet.Exists && ready) puppet.EnsureBuilt();
            puppet.HideMesh();
            PartnerInput.SetRemote(partner.Input);
            partner.Suppressed = false;   // the animated avatar is what you see; the puppet is the invisible body
        }

        /// <summary>The game only pauses when BOTH players are in the pause menu.</summary>
        private void HandlePause()
        {
            if (!GameAccess.Alive(pauseScreen) && Time.unscaledTime >= nextPauseSearch)
            {
                nextPauseSearch = Time.unscaledTime + 2f;
                try
                {
                    var o = UnityEngine.Object.FindObjectOfType(UnhollowerRuntimeLib.Il2CppType.Of<Okomotive.FarLoneSails.UIScreenPause>());
                    pauseScreen = o?.TryCast<Okomotive.FarLoneSails.UIScreenPause>();
                }
                catch { }
            }
            localPaused = false;
            try { if (GameAccess.Alive(pauseScreen)) localPaused = pauseScreen.IsPaused(); } catch { }

            // skip the start menu once per launch (the game opens paused on the RESUME menu)
            if (!autoResumeDone && FarCoopPlugin.CfgAutoResume.Value)
            {
                if (localPaused && GameAccess.LocalCharacter != null)
                {
                    if (autoResumeAt < 0f) autoResumeAt = Time.unscaledTime + 2f;   // let the menu finish loading
                    else if (Time.unscaledTime >= autoResumeAt)
                    {
                        autoResumeDone = true;
                        try { pauseScreen.Resume(); AddLog("Auto-resumed the game (AutoResumeOnStart)."); }
                        catch (Exception e) { FarCoopPlugin.Logger.LogWarning("Auto-resume failed: " + e.Message); }
                    }
                }
                else if (!localPaused && GameAccess.LocalCharacter != null && Time.unscaledTime > 60f) autoResumeDone = true;
            }
            if (!IsConnected || !localPaused) return;
            if (!partner.PartnerPaused) { if (Time.timeScale < 0.99f) Time.timeScale = 1f; }
            else if (Time.timeScale > 0f) Time.timeScale = 0f;
        }

        private void Guard(string what, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                if (errorCount++ < 30) FarCoopPlugin.Logger.LogError($"[{what}] {e}");
            }
        }

        private void HandleHotkeys()
        {
            if (ipEditing) { HandleIpTyping(); return; }
            if (Input.GetKeyDown(KeyCode.F4)) showHud = !showHud;
            if (Input.GetKeyDown(KeyCode.F1)) StartHost();
            if (Input.GetKeyDown(KeyCode.F2)) SearchAndJoin();
            if (Input.GetKeyDown(KeyCode.F3)) Disconnect("you disconnected");
            if (Input.GetKeyDown(KeyCode.F5)) TeleportToPartner();
        }

        private void HandleIpTyping()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { ipEditing = false; return; }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                ipEditing = false;
                JoinIp(ipField);
                return;
            }
            if (Input.GetKeyDown(KeyCode.Backspace) && ipField.Length > 0)
                ipField = ipField.Substring(0, ipField.Length - 1);
            string typed = Input.inputString;
            if (string.IsNullOrEmpty(typed)) return;
            foreach (char c in typed)
                if ((char.IsDigit(c) || c == '.' || c == ':') && ipField.Length < 21) ipField += c;
        }

        // ======================= actions =======================

        private bool Busy(string action)
        {
            if (net.Peer != null && !net.Peer.Closed) { AddLog($"Already connected - press F3 (Disconnect) before {action}."); return true; }
            if (net.Connecting) { AddLog("Still connecting, wait a moment."); return true; }
            return false;
        }

        public void StartHost()
        {
            if (IsHost) { AddLog("Already hosting. Partner: press F2 or Join IP " + string.Join(" / ", CoopNet.LocalIPv4s())); return; }
            if (Busy("hosting")) return;
            ResetSession();
            net.StartHost(FarCoopPlugin.CfgPort.Value, FarCoopPlugin.CfgDiscoveryPort.Value, FarCoopPlugin.CfgPlayerName.Value);
        }

        public void SearchAndJoin()
        {
            if (IsHost) { AddLog("You are hosting. Press F3 first to stop hosting."); return; }
            if (Busy("searching")) return;
            if (net.Searching) return;
            found.Clear();
            AddLog("Searching LAN...");
            net.SearchLan(FarCoopPlugin.CfgDiscoveryPort.Value);
        }

        public void JoinIp(string ip)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) { AddLog("Enter the host IP first."); return; }
            if (Busy("joining")) return;
            int port = FarCoopPlugin.CfgPort.Value;
            int colon = ip.LastIndexOf(':');
            if (colon > 0 && int.TryParse(ip.Substring(colon + 1), out int p)) { port = p; ip = ip.Substring(0, colon); }
            FarCoopPlugin.CfgLastHostIp.Value = ip;
            ResetSession();
            AddLog($"Connecting to {ip}:{port}...");
            net.Connect(ip, port);
        }

        public void Disconnect(string reason)
        {
            if (net.Peer != null) try { net.Peer.Send(new byte[] { (byte)MsgType.Bye }); } catch { }
            bool wasActive = net.Role != NetRole.None;
            net.StopAll(reason);
            ResetSession();
            if (wasActive) AddLog("Disconnected: " + reason);
        }

        private void ResetSession()
        {
            handshakeDone = false;
            partnerName = partnerScene = "";
            rttMs = -1f;
            vehicle.ReleaseControl();
            partner.Reset();
            puppet.Reset();
            items.ReleaseAll();
        }

        public void TeleportToPartner()
        {
            if (!IsConnected || !(partner.Exists || puppet.Exists)) { AddLog("No partner to teleport to."); return; }
            var ch = GameAccess.LocalCharacter;
            if (ch == null) return;
            Vector3 target = (puppet.Exists ? puppet.Position : partner.WorldPosition) + Vector3.up * 0.5f;
            var rb = GameAccess.CharacterBody(ch);
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.position = target;
            }
            ch.transform.position = target;
            AddLog("Teleported to partner.");
        }

        // ======================= network pump =======================

        private void PumpNetwork()
        {
            while (net.Events.TryDequeue(out var ev))
            {
                if (ev == "#search_none") AddLog("No host found on LAN. Type the host IP and press 'Join IP'.");
                else if (ev == "#connect_failed") ResetSession();
                else AddLog(ev);
            }

            while (net.Discovered.TryDequeue(out var h))
            {
                found.Add(h);
                AddLog($"Found host '{h.Name}' at {h.Ip}:{h.Port}");
                if (found.Count == 1 && net.Peer == null && !net.Connecting)
                    JoinIp(h.Ip + ":" + h.Port);
            }

            var newPeer = net.PollNewPeer();
            if (newPeer != null)
            {
                AddLog((IsHost ? "Partner connected from " : "Connected to ") + newPeer.RemoteAddress);
                handshakeDone = false;
                if (!IsHost)
                {
                    var w = Proto.Begin(MsgType.Hello, out var ms);
                    w.Write(FarCoopPlugin.ProtocolVersion);
                    w.Write(FarCoopPlugin.CfgPlayerName.Value);
                    w.Write(SceneManager.GetActiveScene().name);
                    newPeer.Send(Proto.End(w, ms));
                }
            }

            var peer = net.Peer;
            if (peer == null) return;

            if (peer.Closed)
            {
                string reason = peer.CloseReason;
                bool host = IsHost;
                net.DropPeer(reason);
                ResetSession();
                AddLog("Partner connection lost (" + reason + ")" + (host ? ". Still hosting, waiting for partner." : ""));
                return;
            }

            if (peer.SecondsSinceReceive > 10)
            {
                peer.Close("timeout (no data for 10s)");
                return;
            }

            int budget = 400;
            while (budget-- > 0 && peer.Incoming.TryDequeue(out var frame))
            {
                try { Handle(peer, frame); }
                catch (Exception e)
                {
                    if (errorCount++ < 30) FarCoopPlugin.Logger.LogError("Bad message: " + e);
                }
            }
        }

        private void Handle(Connection peer, byte[] frame)
        {
            var r = new BinaryReader(new MemoryStream(frame));
            var type = (MsgType)r.ReadByte();
            switch (type)
            {
                case MsgType.Hello:
                {
                    int ver = r.ReadInt32();
                    partnerName = r.ReadString();
                    partnerScene = r.ReadString();
                    if (ver != FarCoopPlugin.ProtocolVersion)
                    {
                        AddLog($"Partner has a different mod version (protocol {ver} vs {FarCoopPlugin.ProtocolVersion}). Copy the same FarCoopMod.dll to both PCs.");
                        peer.Close("version mismatch");
                        return;
                    }
                    var w = Proto.Begin(MsgType.Welcome, out var ms);
                    w.Write(FarCoopPlugin.ProtocolVersion);
                    w.Write(FarCoopPlugin.CfgPlayerName.Value);
                    w.Write(SceneManager.GetActiveScene().name);
                    peer.Send(Proto.End(w, ms));
                    handshakeDone = true;
                    vehicle.Scan(true);
                    peer.Send(vehicle.BuildBodiesMessage());
                    items.Scan(true);
                    peer.Send(items.BuildListMessage());
                    FarCoopPlugin.Logger.LogInfo($"Items: {items.Count} synced ({ItemSync.SaveIdKeys} by save id, {ItemSync.PathKeys} by path)");
                    AddLog($"'{partnerName}' joined.");
                    CheckScene();
                    break;
                }
                case MsgType.Welcome:
                {
                    int ver = r.ReadInt32();
                    partnerName = r.ReadString();
                    partnerScene = r.ReadString();
                    if (ver != FarCoopPlugin.ProtocolVersion)
                    {
                        AddLog($"Host has a different mod version (protocol {ver}). Copy the same FarCoopMod.dll to both PCs.");
                        peer.Close("version mismatch");
                        return;
                    }
                    handshakeDone = true;
                    vehicle.Scan(true);
                    AddLog($"Joined '{partnerName}'. The host drives the vehicle physics.");
                    CheckScene();
                    break;
                }
                case MsgType.Ping:
                {
                    double t = r.ReadDouble();
                    var w = Proto.Begin(MsgType.Pong, out var ms);
                    w.Write(t);
                    peer.Send(Proto.End(w, ms));
                    break;
                }
                case MsgType.Pong:
                    rttMs = (float)((Time.realtimeSinceStartup - r.ReadDouble()) * 1000.0);
                    break;
                case MsgType.Player:
                    partner.ReadPlayerMessage(r);
                    break;
                case MsgType.VehicleBodies:
                    if (!IsHost) { vehicle.Scan(true); vehicle.ReadBodiesMessage(r); }
                    break;
                case MsgType.ItemList:
                    if (!IsHost) items.ReadList(r);
                    if (!IsHost) FarCoopPlugin.Logger.LogInfo($"Items: host has {items.HostCount}, matched here {items.Mapped}");
                    break;
                case MsgType.ItemState:
                    if (!IsHost) items.ReadState(r);
                    break;
                case MsgType.ItemHold:
                    if (IsHost) items.ReadHold(r);
                    break;
                case MsgType.VehicleState:
                    if (!IsHost) vehicle.ReadStateMessage(r);
                    break;
                case MsgType.Button:
                {
                    byte kind = r.ReadByte();
                    string path = r.ReadString();
                    ButtonSync.ApplyRemote(kind, path);
                    break;
                }
                case MsgType.Bye:
                    peer.Close("partner left");
                    break;
            }
        }

        private void CheckScene()
        {
            string mine = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrEmpty(partnerScene) && partnerScene != mine)
                AddLog($"Warning: partner is in scene '{partnerScene}', you are in '{mine}'. Load the same chapter/save on both PCs.");
        }

        // ======================= sending =======================

        private void SendState()
        {
            if (!IsConnected) return;
            var peer = net.Peer;

            pingTimer += Time.unscaledDeltaTime;
            if (pingTimer >= 1f)
            {
                pingTimer = 0f;
                var w = Proto.Begin(MsgType.Ping, out var ms);
                w.Write((double)Time.realtimeSinceStartup);
                peer.Send(Proto.End(w, ms));
            }

            float interval = 1f / Mathf.Clamp(Mathf.Max(30, FarCoopPlugin.CfgSendRate.Value), 5, 60);   // >= 30 Hz
            sendTimer += Time.unscaledDeltaTime;
            if (sendTimer < interval) return;
            sendTimer = 0f;

            if (IsHost)
            {
                if (vehicle.Scan()) peer.Send(vehicle.BuildBodiesMessage());
                if (vehicle.Bodies.Count > 0) peer.Send(vehicle.BuildStateMessage());
                if (items.Scan()) peer.Send(items.BuildListMessage());
                var itemState = items.BuildStateMessage();
                if (itemState != null) peer.Send(itemState);
            }
            else
            {
                vehicle.Scan();
                items.Scan();
                var hold = items.BuildHoldMessage();
                if (hold != null) peer.Send(hold);
            }

            var player = partner.BuildLocalPlayerMessage(vehicle, localPaused);
            if (player != null) peer.Send(player);
        }

        public void SendButton(byte kind, string path)
        {
            var peer = net.Peer;
            if (peer == null || peer.Closed) return;
            var w = Proto.Begin(MsgType.Button, out var ms);
            w.Write(kind);
            w.Write(path);
            peer.Send(Proto.End(w, ms));
        }

        public void Quit()
        {
            try { Disconnect("game closed"); } catch { }
        }

        // ======================= HUD =======================

        private void AddLog(string s)
        {
            FarCoopPlugin.Logger.LogInfo(s);
            log.Add(DateTime.Now.ToString("HH:mm:ss ") + s);
            if (log.Count > 5) log.RemoveAt(0);
        }

        public void OnGUI()
        {
            if (!showHud) return;
            try { DrawHud(); }
            catch (Exception e)
            {
                if (errorCount++ < 30) FarCoopPlugin.Logger.LogError("[hud] " + e);
            }
        }

        private void DrawHud()
        {
            const float w = 420f;
            float x = 10f, y = 10f;
            float h = 24f + 20f + 26f + 34f + 32f + 32f + log.Count * 34f + 8f;
            GUI.Box(new Rect(x, y, w, h), "FAR LAN Co-op " + FarCoopPlugin.Version + "   [F4 hide]");
            float cx = x + 10f, cy = y + 24f, cw = w - 20f;

            GUI.Label(new Rect(cx, cy, cw, 20f), "Status: " + StatusText());
            cy += 20f;
            if (IsConnected)
            {
                string bodies = IsHost ? $"{vehicle.Bodies.Count} parts" : $"{vehicle.MappedCount} parts";
                string itemInfo = IsHost ? $"{items.Count} items" : $"{items.Mapped} items";
                string extra = (puppet.Exists ? " | partner body OK" : " | partner body -") + $" | inp {PartnerInput.OverridesPerSec}/s";
                if (partner.PartnerPaused) extra += " | partner PAUSED";
                float energy = vehicle.Energy;
                GUI.Label(new Rect(cx, cy, cw, 20f),
                    $"Ping {(rttMs < 0 ? "-" : rttMs.ToString("0") + " ms")} | {bodies} | {itemInfo} | fuel {(float.IsNaN(energy) ? "-" : energy.ToString("0.0"))}{extra}");
            }
            else
            {
                GUI.Label(new Rect(cx, cy, cw, 20f), "My IP: " + string.Join(", ", CoopNet.LocalIPv4s()));
            }
            cy += 26f;

            float bw = (cw - 20f) / 3f;
            if (GUI.Button(new Rect(cx, cy, bw, 26f), IsHost ? "Hosting" : "Host (F1)")) StartHost();
            if (GUI.Button(new Rect(cx + bw + 10f, cy, bw, 26f), net.Searching ? "Searching..." : "Find LAN (F2)")) SearchAndJoin();
            if (GUI.Button(new Rect(cx + 2 * (bw + 10f), cy, bw, 26f), "Disconnect (F3)")) Disconnect("you disconnected");
            cy += 34f;

            GUI.Label(new Rect(cx, cy + 4f, 60f, 22f), "Host IP:");
            string fieldText = ipEditing
                ? ipField + ((Time.unscaledTime % 1f) < 0.5f ? "_" : " ") + "   (Enter = join, Esc = cancel)"
                : (string.IsNullOrEmpty(ipField) ? "click to type IP" : ipField + "   (click to edit)");
            if (GUI.Button(new Rect(cx + 60f, cy, cw - 170f, 24f), fieldText))
            {
                ipEditing = !ipEditing;
            }
            if (GUI.Button(new Rect(cx + cw - 100f, cy, 100f, 24f), net.Connecting ? "Connecting" : "Join IP")) { ipEditing = false; JoinIp(ipField); }
            cy += 32f;

            if (GUI.Button(new Rect(cx, cy, cw, 24f), "Teleport to partner (F5)")) TeleportToPartner();
            cy += 32f;

            for (int i = 0; i < log.Count; i++)
            {
                GUI.Label(new Rect(cx, cy, cw, 34f), log[i]);
                cy += 34f;
            }

            DrawPartnerTag();
        }

        private void DrawPartnerTag()
        {
            if (!IsConnected || !(partner.Exists || puppet.Exists)) return;
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 basePos = puppet.Exists ? puppet.Position : partner.WorldPosition;
            Vector3 sp = cam.WorldToScreenPoint(basePos + Vector3.up * 1.6f);
            if (sp.z <= 0f) return;
            string label = string.IsNullOrEmpty(partnerName) ? "Partner" : partnerName;
            GUI.Label(new Rect(sp.x - 60f, Screen.height - sp.y - 10f, 120f, 22f), label);
        }

        private string StatusText()
        {
            if (net.Role == NetRole.Host)
                return IsConnected ? $"HOST - partner '{partnerName}' connected" : $"HOST - waiting on port {FarCoopPlugin.CfgPort.Value}...";
            if (IsConnected) return $"CLIENT - connected to '{partnerName}'";
            if (net.Connecting) return "Connecting...";
            if (net.Peer != null) return "Handshaking...";
            if (net.Searching) return "Searching LAN...";
            return "Offline";
        }
    }
}
