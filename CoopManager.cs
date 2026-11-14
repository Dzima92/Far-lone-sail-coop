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

    /// <summary>One other player in the session.</summary>
    public sealed class RemotePlayer
    {
        public byte Id;
        public string Name = "";
        public string Scene = "";
        public Connection Conn;                 // host only
        public bool Handshake;
        public float RttMs = -1f;
        public readonly RemoteAvatar Avatar = new RemoteAvatar();
        public readonly PartnerPuppet Puppet = new PartnerPuppet();
        public readonly ItemSync.HoldState Hold = new ItemSync.HoldState();
        public string Label => string.IsNullOrEmpty(Name) ? "Player " + Id : Name;

        public void Destroy()
        {
            Avatar.Reset();
            Puppet.Reset();
        }
    }

    /// <summary>Main-thread brain of the mod: networking pump, sync, hotkeys and HUD.</summary>
    public class CoopSession
    {
        public static CoopSession Instance;

        private readonly CoopNet net;
        private readonly VehicleSync vehicle;
        private readonly RemoteAvatar localSender = new RemoteAvatar();   // only builds our own Player message
        private readonly ItemSync items = new ItemSync();
        private readonly List<RemotePlayer> remotes = new List<RemotePlayer>();
        private Okomotive.FarLoneSails.UIScreenPause pauseScreen;
        private float nextPauseSearch;
        private bool localPaused;
        private bool hostAllPaused;            // client: host says everybody is in the pause menu
        private bool lastSentAllPaused;
        private float nextPauseSend;
        private bool autoResumeDone;
        private float autoResumeAt = -1f;

        private bool showHud = true;
        private string ipField = "";
        private bool ipEditing;
        private readonly List<string> log = new List<string>();
        private readonly List<DiscoveredHost> found = new List<DiscoveredHost>();

        private bool handshakeDone;            // client
        private byte myId;                     // client: id the host gave us (host = 0)
        private string hostName = "";
        private float sendTimer, pingTimer;
        private int errorCount;

        public bool IsHost => net != null && net.Role == NetRole.Host;
        /// <summary>Host: at least one player joined. Client: joined the host.</summary>
        public bool IsConnected => IsHost ? remotes.Exists(r => r.Handshake && r.Conn != null && !r.Conn.Closed)
                                          : (net != null && net.Peer != null && !net.Peer.Closed && handshakeDone);

        public CoopSession()
        {
            net = new CoopNet();
            vehicle = new VehicleSync();
            showHud = FarCoopPlugin.CfgShowHud.Value;
            ipField = FarCoopPlugin.CfgLastHostIp.Value;
            AddLog("Co-op ready. F1 host, F2 find & join, F3 disconnect, F4 HUD, F5 go to partner");
        }

        private RemotePlayer FindRemote(byte id) => remotes.Find(r => r.Id == id);
        private RemotePlayer GetOrAddRemote(byte id)
        {
            var r = FindRemote(id);
            if (r == null) { r = new RemotePlayer { Id = id }; remotes.Add(r); }
            return r;
        }
        private void RemoveRemote(RemotePlayer r)
        {
            r.Destroy();
            remotes.Remove(r);
        }

        // ======================= main loop =======================

        public void Update()
        {
            // Keep simulating + networking when the window is not focused (alt-tab, remote desktop).
            if (!Application.runInBackground) Application.runInBackground = true;
            PartnerInput.ResetFrame();
            PartnerInput.Tick();
            Guard("cmdline", CommandLineAutomation);
            Guard("hotkeys", HandleHotkeys);
            Guard("net", PumpNetwork);
            Guard("pause", HandlePause);
            Guard("puppet", UpdatePuppets);
            Guard("send", SendState);
            Guard("avatar", () => { foreach (var r in remotes) r.Avatar.Update(vehicle, Time.deltaTime); });
            if (IsConnected) Guard("cover", CoverSync.Tick);
            Guard("telemetry", () => { Telemetry.Frame(); if (Telemetry.Enabled) Telemetry.Flush(TelemetrySession()); });
            Guard("diag", Diagnostics);
        }

        public void FixedUpdate()
        {
            Guard("telemetry-fix", () => Telemetry.Fixed(vehicle));
            if (!IsConnected) return;
            // every game: each other player is a real character pulled to where that player really is
            Guard("puppet-fix", () =>
            {
                foreach (var r in remotes)
                {
                    var target = r.Avatar.PartnerRootWorld(vehicle);
                    if (r.Puppet.Exists && target.HasValue) r.Puppet.Correct(target.Value);
                }
            });
            if (IsHost) return;
            Guard("vehicle", () =>
            {
                vehicle.Scan();
                vehicle.Riders.Clear();
                var meC = GameAccess.LocalCharacter;
                if (meC != null && GameAccess.CharacterInsideVehicle(meC))
                {
                    var body = GameAccess.CharacterBody(meC);
                    bool idle = PartnerInput.LocalIdle();
                    if (body != null && !body.isKinematic) vehicle.Riders.Add(new VehicleSync.Rider { Body = body, Idle = idle });
                }
                // the other players' bodies ride along too (otherwise they trail behind the vehicle)
                foreach (var r in remotes)
                {
                    if (!r.Puppet.Exists || !GameAccess.CharacterInsideVehicle(r.Puppet.Character)) continue;
                    var pb = GameAccess.CharacterBody(r.Puppet.Character);
                    var pin = r.Avatar.Input;
                    bool pidle = Mathf.Abs(pin.Horizontal) < 0.1f && !pin.Jump;
                    if (pb != null && !pb.isKinematic) vehicle.Riders.Add(new VehicleSync.Rider { Body = pb, Idle = pidle });
                }
                vehicle.ApplySnapshots();
            });
            Guard("items", () =>
            {
                Okomotive.FarLoneSails.Item held = null;
                try { held = GameAccess.LocalCharacter?.GetHoldingItem(); } catch { }
                items.Apply(held, vehicle);
            });
        }

        private float nextDiag;
        private void Diagnostics()
        {
            if (!FarCoopPlugin.CfgDebugLog.Value || !IsConnected || Time.unscaledTime < nextDiag) return;
            nextDiag = Time.unscaledTime + 5f;
            var ch = GameAccess.LocalCharacter;
            string me = "none";
            if (ch != null)
            {
                var a = GameAccess.CharacterAnimator(ch);
                me = $"root={ch.transform.position} vis={(a != null ? a.transform.position.ToString() : "-")} inside={GameAccess.CharacterInsideVehicle(ch)}";
            }
            foreach (var r in remotes)
                FarCoopPlugin.Logger.LogInfo($"DIAG me {me} | {r.Label}: {r.Avatar.DebugInfo(vehicle)} | puppet={(r.Puppet.Exists ? r.Puppet.Position.ToString() : "none")}");
        }

        /// <summary>Keep a real game character for every other player so they can use the vehicle.</summary>
        private void UpdatePuppets()
        {
            bool allowed = IsConnected && FarCoopPlugin.CfgClientPuppet.Value;
            foreach (var r in remotes)
            {
                if (!allowed || (IsHost && !r.Handshake))
                {
                    if (r.Puppet.Exists) { r.Puppet.Destroy(); FarCoopPlugin.Logger.LogInfo("Partner puppet removed: " + r.Label); }
                    continue;
                }
                bool ready = r.Avatar.PartnerRootWorld(vehicle).HasValue && GameAccess.LocalCharacter != null;
                if (!r.Puppet.Exists && ready) r.Puppet.EnsureBuilt();
                r.Puppet.HideMesh();
                r.Puppet.Slot.Label = r.Label;
                r.Puppet.Slot.SetRemote(r.Avatar.Input);
                r.Avatar.Suppressed = false;   // the animated avatar is what you see; the puppet is the invisible body
                r.Hold.Puppet = r.Puppet.Exists ? r.Puppet.Character : null;
            }
        }

        /// <summary>Telemetry: network, vehicle and every other player (one line per second).</summary>
        private string TelemetrySession()
        {
            var sb = new System.Text.StringBuilder();
            UpdateBandwidth();
            sb.Append(IsHost ? " | HOST" : IsConnected ? $" | CLIENT#{myId} ping={rttMs:0} delay={NetClock.Delay * 1000f:0}ms" : " | offline");
            sb.Append($" net={kbIn:0.0}/{kbOut:0.0}kB/s");
            var ch = vehicle.Chassis;
            if (ch != null && GameAccess.Alive(ch))
            {
                Vector3 cp = ch.position;
                float speed = 0f;
                try { speed = ch.isKinematic ? 0f : ch.velocity.magnitude; } catch { }
                sb.Append($" | vehicle x={cp.x:0.0} y={cp.y:0.0}{(IsHost ? $" speed={speed:0.0}" : "")} fuel={vehicle.Energy:0.0}{(localPaused ? " PAUSED-MENU" : "")} ts={Time.timeScale:0.#}");
            }
            foreach (var r in remotes)
            {
                if (IsHost && !r.Handshake) continue;
                var t = r.Avatar.PartnerRootWorld(vehicle);
                var inp = r.Avatar.Input;
                sb.Append($" | {r.Label}#{r.Id} in(h={inp.Horizontal:0.0} v={inp.Vertical:0.0}{(inp.Jump ? " J" : "")}{(inp.Take ? " T" : "")})");
                if (t.HasValue) sb.Append($" at=({t.Value.x:0.00},{t.Value.y:0.00},{t.Value.z:0.00})");
                if (r.Puppet.Exists && t.HasValue) sb.Append($" body-dist={(r.Puppet.Position - t.Value).magnitude:0.00}");
                else sb.Append(" no-body");
                sb.Append($" age={Time.time - r.Avatar.LastUpdateTime:0.00}s");
                if (IsHost && r.RttMs >= 0) sb.Append($" ping={r.RttMs:0}");
                if (r.Avatar.PartnerPaused) sb.Append(" paused");
            }
            return sb.ToString();
        }

        /// <summary>Diagnostics: how far a partner body is from where its player really is.</summary>
        public string PuppetDebug(PuppetSlot sl)
        {
            foreach (var r in remotes)
            {
                if (r.Puppet.Slot != sl) continue;
                var t = r.Avatar.PartnerRootWorld(vehicle);
                if (!t.HasValue || !r.Puppet.Exists) return "";
                return $"body={r.Puppet.Position} target={t.Value} dist={(r.Puppet.Position - t.Value).magnitude:0.00}";
            }
            return "";
        }

        /// <summary>Host: everybody (host too, unless dedicated) is in the pause menu.</summary>
        private bool EveryonePaused()
        {
            bool any = false;
            foreach (var r in remotes)
            {
                if (!r.Handshake) continue;
                any = true;
                if (!r.Avatar.PartnerPaused) return false;
            }
            return any && (Dedicated || localPaused);
        }

        /// <summary>The game only pauses when ALL players are in the pause menu.</summary>
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
            if (!autoResumeDone && (FarCoopPlugin.CfgAutoResume.Value || Dedicated))
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
            if (!IsConnected) return;
            bool all = IsHost ? EveryonePaused() : hostAllPaused;
            if (IsHost && Dedicated)
            {
                // nobody plays here: the world stops only while every player sits in the menu
                if (all) { if (Time.timeScale > 0f) Time.timeScale = 0f; }
                else if (Time.timeScale < 0.99f) Time.timeScale = 1f;
                return;
            }
            if (!localPaused) return;
            if (!all) { if (Time.timeScale < 0.99f) Time.timeScale = 1f; }
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
                if ((char.IsLetterOrDigit(c) || c == '.' || c == ':' || c == '-') && ipField.Length < 64) ipField += c;
        }

        // ======================= command line (testing two games on one PC) =======================
        //   -farcoop-host                 host as soon as the world is loaded
        //   -farcoop-join 127.0.0.1:7777  join (retries for 2 minutes while the host starts)
        //   -farcoop-name Player2         name tag for this game
        private string cmdJoin;
        private bool cmdHost, cmdParsed, cmdDone;
        private float cmdNextTry, cmdGiveUp;

        private void CommandLineAutomation()
        {
            if (!cmdParsed)
            {
                cmdParsed = true;
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if (a == "-farcoop-host") cmdHost = true;
                    else if (a == "-farcoop-dedicated") { cmdHost = true; Dedicated = true; }
                    else if (a == "-farcoop-join" && i + 1 < args.Length) cmdJoin = args[++i];
                    else if (a == "-farcoop-name" && i + 1 < args.Length) overrideName = args[++i];
                }
                if (Dedicated)
                {
                    // test: headless server, nobody plays on this copy
                    Application.runInBackground = true;
                    Application.targetFrameRate = 60;
                    QualitySettings.vSyncCount = 0;
                    AddLog("DEDICATED server mode (batchmode=" + Application.isBatchMode + "). This copy's own character is hidden from the client.");
                }
                if (cmdHost || cmdJoin != null) AddLog("Command line: " + (cmdHost ? "auto-host" : "auto-join " + cmdJoin));
                else cmdDone = true;
            }
            if (Dedicated && !cmdDone && Time.unscaledTime >= nextDedicatedLog)
            {
                nextDedicatedLog = Time.unscaledTime + 5f;
                string scene = "?";
                try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
                FarCoopPlugin.Logger.LogInfo($"[server] waiting for the world: scene={scene} character={(GameAccess.LocalCharacter != null)} timeScale={Time.timeScale} frame={Time.frameCount}");
            }
            if (cmdDone || GameAccess.LocalCharacter == null) return;   // wait for the world
            if (cmdHost) { StartHost(); cmdDone = true; return; }
            if (IsConnected) { cmdDone = true; return; }
            if (cmdGiveUp == 0f) cmdGiveUp = Time.unscaledTime + 120f;
            if (Time.unscaledTime > cmdGiveUp) { AddLog("Auto-join gave up."); cmdDone = true; return; }
            if (Time.unscaledTime >= cmdNextTry && !net.Connecting && net.Peer == null)
            {
                cmdNextTry = Time.unscaledTime + 10f;
                JoinIp(cmdJoin);
            }
        }

        /// <summary>-farcoop-dedicated: host without a player (its own character is not sent).</summary>
        public static bool Dedicated;
        private float nextDedicatedLog;

        private string overrideName;
        private string MyName => string.IsNullOrEmpty(overrideName) ? FarCoopPlugin.CfgPlayerName.Value : overrideName;

        private string MyAddresses()
        {
            int port = FarCoopPlugin.CfgPort.Value;
            var ips = CoopNet.LocalIPv4s();
            return ips.Count == 0 ? "127.0.0.1:" + port : string.Join("  ", ips.ConvertAll(i => i + ":" + port));
        }

        // ======================= actions =======================

        private bool Busy(string action)
        {
            if (IsHost) { AddLog($"You are hosting - press F3 (Disconnect) before {action}."); return true; }
            if (net.Peer != null && !net.Peer.Closed) { AddLog($"Already connected - press F3 (Disconnect) before {action}."); return true; }
            if (net.Connecting) { AddLog("Still connecting, wait a moment."); return true; }
            return false;
        }

        public void StartHost()
        {
            if (IsHost) { AddLog("Already hosting. Players join with: " + MyAddresses()); return; }
            if (Busy("hosting")) return;
            ResetSession();
            net.MaxClients = Mathf.Clamp(FarCoopPlugin.CfgMaxPlayers.Value - (Dedicated ? 0 : 1), 1, 15);
            net.StartHost(FarCoopPlugin.CfgPort.Value, FarCoopPlugin.CfgDiscoveryPort.Value, MyName);
        }

        public void SearchAndJoin()
        {
            if (IsHost) { AddLog("You are hosting. Press F3 first to stop hosting."); return; }
            if (Busy("searching")) return;
            if (net.Searching) return;
            found.Clear();
            autoJoinFound = true;
            AddLog("Searching LAN...");
            net.SearchLan(FarCoopPlugin.CfgDiscoveryPort.Value);
        }

        /// <summary>Fill the server list without joining.</summary>
        public void RefreshServers(bool quiet = false)
        {
            if (net.Searching || IsHost) return;
            found.Clear();
            autoJoinFound = false;
            quietSearch = quiet;
            if (!quiet) AddLog("Refreshing server list...");
            net.SearchLan(FarCoopPlugin.CfgDiscoveryPort.Value);
        }

        private bool autoJoinFound, quietSearch, initialRefreshDone;
        private string lastJoinAddress;

        private List<string> RecentServers()
        {
            var list = new List<string>();
            foreach (var part in (FarCoopPlugin.CfgRecentServers.Value ?? "").Split('|'))
                if (part.Trim().Length > 0 && !list.Contains(part.Trim())) list.Add(part.Trim());
            return list;
        }

        private void RememberServer(string address)
        {
            if (string.IsNullOrEmpty(address)) return;
            var list = RecentServers();
            list.Remove(address);
            list.Insert(0, address);
            if (list.Count > 5) list.RemoveRange(5, list.Count - 5);
            FarCoopPlugin.CfgRecentServers.Value = string.Join("|", list);
        }

        public void JoinIp(string ip)
        {
            ip = (ip ?? "").Trim();
            if (ip.Length == 0) { AddLog("Enter the host IP first."); return; }
            if (Busy("joining")) return;
            int port = FarCoopPlugin.CfgPort.Value;
            int colon = ip.LastIndexOf(':');
            if (colon > 0 && int.TryParse(ip.Substring(colon + 1), out int p)) { port = p; ip = ip.Substring(0, colon); }
            if (port <= 0 || port > 65535) { AddLog("Port must be 1-65535."); return; }
            FarCoopPlugin.CfgLastHostIp.Value = port == FarCoopPlugin.CfgPort.Value ? ip : ip + ":" + port;
            ResetSession();
            lastJoinAddress = ip + ":" + port;
            AddLog($"Connecting to {ip}:{port}...");
            net.Connect(ip, port);
        }

        public void Disconnect(string reason)
        {
            var bye = new byte[] { (byte)MsgType.Bye };
            if (net.Peer != null) try { net.Peer.Send(bye); } catch { }
            foreach (var c in net.Peers) try { c.Send(bye); } catch { }
            bool wasActive = net.Role != NetRole.None;
            net.StopAll(reason);
            ResetSession();
            if (wasActive) AddLog("Disconnected: " + reason);
        }

        private void ResetSession()
        {
            handshakeDone = false;
            hostName = "";
            myId = 0;
            hostAllPaused = false;
            vehicle.ReleaseControl();
            foreach (var r in remotes) r.Destroy();
            remotes.Clear();
            items.ReleaseAll();
            if (Time.timeScale < 0.99f && !localPaused) Time.timeScale = 1f;
        }

        public void TeleportToPartner()
        {
            RemotePlayer target = null;
            foreach (var r in remotes) if (r.Puppet.Exists || r.Avatar.Exists) { target = r; break; }
            if (!IsConnected || target == null) { AddLog("No partner to teleport to."); return; }
            // cycle through players on repeated presses
            for (int k = 0; k < remotes.Count; k++)
            {
                var r = remotes[(teleportIdx + k) % remotes.Count];
                if (r.Puppet.Exists || r.Avatar.Exists) { target = r; teleportIdx = (remotes.IndexOf(r) + 1) % remotes.Count; break; }
            }
            var ch = GameAccess.LocalCharacter;
            if (ch == null) return;
            Vector3 pos = (target.Puppet.Exists ? target.Puppet.Position : target.Avatar.WorldPosition) + Vector3.up * 0.5f;
            var rb = GameAccess.CharacterBody(ch);
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.position = pos;
            }
            ch.transform.position = pos;
            AddLog("Teleported to " + target.Label + ".");
        }
        private int teleportIdx;

        // ======================= network pump =======================

        private void PumpNetwork()
        {
            while (net.Events.TryDequeue(out var ev))
            {
                if (ev == "#search_none") { if (!quietSearch) AddLog("No host found on LAN. Type the host IP:port and press 'Join IP'."); quietSearch = false; }
                else if (ev == "#connect_failed") ResetSession();
                else AddLog(ev);
            }

            while (net.Discovered.TryDequeue(out var h))
            {
                // one row per host: a host answers once per network adapter
                int same = found.FindIndex(f => f.Name == h.Name && f.Port == h.Port);
                if (same >= 0)
                {
                    if (h.Ip == "127.0.0.1") found[same] = h;   // same PC: loopback is the reliable address
                    continue;
                }
                found.Add(h);
                if (!quietSearch) AddLog($"Found host '{h.Name}' at {h.Ip}:{h.Port}");
                if (autoJoinFound && found.Count == 1 && net.Peer == null && !net.Connecting)
                    JoinIp(h.Ip + ":" + h.Port);
            }

            // fill the server list once when the world is loaded (no auto-join)
            if (!initialRefreshDone && GameAccess.LocalCharacter != null)
            {
                initialRefreshDone = true;
                if (!IsHost && net.Peer == null) RefreshServers(true);
            }

            foreach (var c in net.PollNewPeers())
            {
                if (IsHost)
                {
                    AddLog("Player connecting from " + c.RemoteAddress);
                    var r = new RemotePlayer { Id = NextFreeId(), Conn = c };
                    remotes.Add(r);
                }
                else
                {
                    AddLog("Connected to " + c.RemoteAddress);
                    handshakeDone = false;
                    var w = Proto.Begin(MsgType.Hello, out var ms);
                    w.Write(FarCoopPlugin.ProtocolVersion);
                    w.Write(MyName);
                    w.Write(SceneManager.GetActiveScene().name);
                    c.Send(Proto.End(w, ms));
                }
            }

            if (IsHost)
            {
                foreach (var r in remotes.ToArray())
                {
                    var c = r.Conn;
                    if (c == null) continue;
                    if (c.Closed)
                    {
                        string reason = c.CloseReason;
                        net.DropPeer(c, reason);
                        items.ReleaseHolder(r.Hold);   // their body copy drops what they carried
                        RemoveRemote(r);
                        if (r.Handshake)
                        {
                            AddLog($"'{r.Label}' left ({reason}). {PlayerCountText()}");
                            Broadcast(PlayerLeftMsg(r.Id), null);
                        }
                        continue;
                    }
                    if (c.SecondsSinceReceive > 10) { c.Close("timeout (no data for 10s)"); continue; }
                    Drain(c, r);
                }
                return;
            }

            var peer = net.Peer;
            if (peer == null) return;
            if (peer.Closed)
            {
                string reason = peer.CloseReason;
                net.DropPeer(peer, reason);
                ResetSession();
                AddLog("Connection to the host lost (" + reason + ")");
                return;
            }
            if (peer.SecondsSinceReceive > 10) { peer.Close("timeout (no data for 10s)"); return; }
            Drain(peer, null);
        }

        private void Drain(Connection c, RemotePlayer from)
        {
            int budget = 400;
            while (budget-- > 0 && c.Incoming.TryDequeue(out var frame))
            {
                try { if (IsHost) HandleHost(c, from, frame); else HandleClient(c, frame); }
                catch (Exception e)
                {
                    if (errorCount++ < 30) FarCoopPlugin.Logger.LogError("Bad message: " + e);
                }
            }
        }

        private byte NextFreeId()
        {
            for (int id = 1; id < 250; id++) if (FindRemote((byte)id) == null) return (byte)id;
            return 250;
        }

        private string PlayerCountText()
        {
            int n = remotes.FindAll(r => r.Handshake).Count + (Dedicated ? 0 : 1);
            return $"{n} player(s) in game.";
        }

        // ---------------- host side ----------------

        private void HandleHost(Connection c, RemotePlayer from, byte[] frame)
        {
            var r = new BinaryReader(new MemoryStream(frame));
            var type = (MsgType)r.ReadByte();
            switch (type)
            {
                case MsgType.Hello:
                {
                    int ver = r.ReadInt32();
                    from.Name = r.ReadString();
                    from.Scene = r.ReadString();
                    if (ver != FarCoopPlugin.ProtocolVersion)
                    {
                        AddLog($"'{from.Name}' has a different mod version (protocol {ver} vs {FarCoopPlugin.ProtocolVersion}). Use the same FarCoopMod.dll on every PC.");
                        c.Close("version mismatch");
                        return;
                    }
                    var w = Proto.Begin(MsgType.Welcome, out var ms);
                    w.Write(FarCoopPlugin.ProtocolVersion);
                    w.Write(MyName);
                    w.Write(SceneManager.GetActiveScene().name);
                    w.Write(from.Id);
                    c.Send(Proto.End(w, ms));
                    from.Handshake = true;
                    vehicle.Scan(true);
                    c.Send(vehicle.BuildBodiesMessage());
                    items.Scan(true);
                    c.Send(items.BuildListMessage());
                    // who is already here
                    if (!Dedicated) c.Send(PlayerInfoMsg(0, MyName));
                    foreach (var o in remotes)
                        if (o != from && o.Handshake) c.Send(PlayerInfoMsg(o.Id, o.Label));
                    Broadcast(PlayerInfoMsg(from.Id, from.Label), c);
                    FarCoopPlugin.Logger.LogInfo($"Items: {items.Count} synced ({ItemSync.SaveIdKeys} by save id, {ItemSync.PathKeys} by path)");
                    AddLog($"'{from.Label}' joined. {PlayerCountText()}");
                    CheckScene(from.Scene);
                    break;
                }
                case MsgType.Ping:
                {
                    double t = r.ReadDouble();
                    var w = Proto.Begin(MsgType.Pong, out var ms);
                    w.Write(t);
                    c.Send(Proto.End(w, ms));
                    break;
                }
                case MsgType.Pong:
                    from.RttMs = (float)((Time.realtimeSinceStartup - r.ReadDouble()) * 1000.0);
                    break;
                case MsgType.Player:
                {
                    if (!from.Handshake) break;
                    from.Avatar.ReadPlayerMessage(r);
                    // pass it on to everybody else
                    var w = Proto.Begin(MsgType.PlayerFrom, out var ms);
                    w.Write(from.Id);
                    w.Write(frame, 1, frame.Length - 1);
                    Broadcast(Proto.End(w, ms), c);
                    break;
                }
                case MsgType.ItemHold:
                    if (from.Handshake) items.ReadHold(r, from.Hold);
                    break;
                case MsgType.Hose:
                {
                    string hp = r.ReadString();
                    bool planted = r.ReadBoolean();
                    if (from.Puppet.Exists) HoseSync.ApplyRemote(hp, planted, from.Puppet.Character);
                    Broadcast(HoseFromMsg(from.Id, hp, planted), c);
                    break;
                }
                case MsgType.Button:
                {
                    byte kind = r.ReadByte();
                    string path = r.ReadString();
                    ButtonSync.ApplyRemote(kind, path);
                    Broadcast(frame, c);
                    break;
                }
                case MsgType.Bye:
                    c.Close("left the game");
                    break;
            }
        }

        // ---------------- client side ----------------

        private void HandleClient(Connection peer, byte[] frame)
        {
            var r = new BinaryReader(new MemoryStream(frame));
            var type = (MsgType)r.ReadByte();
            switch (type)
            {
                case MsgType.Welcome:
                {
                    int ver = r.ReadInt32();
                    hostName = r.ReadString();
                    string scene = r.ReadString();
                    if (ver != FarCoopPlugin.ProtocolVersion)
                    {
                        AddLog($"Host has a different mod version (protocol {ver}). Use the same FarCoopMod.dll on every PC.");
                        peer.Close("version mismatch");
                        return;
                    }
                    myId = r.ReadByte();
                    handshakeDone = true;
                    vehicle.Scan(true);
                    AddLog($"Joined '{hostName}' as player {myId}. The host runs the vehicle physics.");
                    RememberServer(lastJoinAddress);
                    CheckScene(scene);
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
                case MsgType.PlayerInfo:
                {
                    byte id = r.ReadByte();
                    string name = r.ReadString();
                    if (id == myId) break;
                    var p = GetOrAddRemote(id);
                    bool isNew = string.IsNullOrEmpty(p.Name);
                    p.Name = name;
                    if (isNew) AddLog($"'{p.Label}' is in the game.");
                    break;
                }
                case MsgType.PlayerLeft:
                {
                    byte id = r.ReadByte();
                    var p = FindRemote(id);
                    if (p != null) { AddLog($"'{p.Label}' left."); RemoveRemote(p); }
                    break;
                }
                case MsgType.PlayerFrom:
                {
                    byte id = r.ReadByte();
                    if (id == myId) break;
                    GetOrAddRemote(id).Avatar.ReadPlayerMessage(r);
                    break;
                }
                case MsgType.PauseAll:
                    hostAllPaused = r.ReadBoolean();
                    break;
                case MsgType.VehicleBodies:
                    vehicle.Scan(true); vehicle.ReadBodiesMessage(r);
                    break;
                case MsgType.ItemList:
                    items.ReadList(r);
                    FarCoopPlugin.Logger.LogInfo($"Items: host has {items.HostCount}, matched here {items.Mapped}");
                    break;
                case MsgType.ItemState:
                    items.ReadState(r);
                    break;
                case MsgType.HoseFrom:
                {
                    byte id = r.ReadByte();
                    string hp = r.ReadString();
                    bool planted = r.ReadBoolean();
                    var p = FindRemote(id);
                    if (id != myId && p != null && p.Puppet.Exists) HoseSync.ApplyRemote(hp, planted, p.Puppet.Character);
                    break;
                }
                case MsgType.VehicleState:
                    vehicle.ReadStateMessage(r);
                    break;
                case MsgType.Button:
                {
                    byte kind = r.ReadByte();
                    string path = r.ReadString();
                    ButtonSync.ApplyRemote(kind, path);
                    break;
                }
                case MsgType.Bye:
                    peer.Close("host left");
                    break;
            }
        }

        private float rttMs = -1f;   // client: ping to the host

        private void CheckScene(string other)
        {
            string mine = SceneManager.GetActiveScene().name;
            if (!string.IsNullOrEmpty(other) && other != mine)
                AddLog($"Warning: other player is in scene '{other}', you are in '{mine}'. Load the same chapter/save on every PC.");
        }

        // ======================= sending =======================

        private static byte[] PlayerInfoMsg(byte id, string name)
        {
            var w = Proto.Begin(MsgType.PlayerInfo, out var ms);
            w.Write(id); w.Write(name ?? "");
            return Proto.End(w, ms);
        }
        private static byte[] PlayerLeftMsg(byte id)
        {
            var w = Proto.Begin(MsgType.PlayerLeft, out var ms);
            w.Write(id);
            return Proto.End(w, ms);
        }
        private static byte[] HoseFromMsg(byte id, string path, bool planted)
        {
            var w = Proto.Begin(MsgType.HoseFrom, out var ms);
            w.Write(id); w.Write(path); w.Write(planted);
            return Proto.End(w, ms);
        }

        /// <summary>Host: send to every joined player except one.</summary>
        private void Broadcast(byte[] frame, Connection except)
        {
            if (frame == null) return;
            foreach (var r in remotes)
                if (r.Handshake && r.Conn != null && r.Conn != except && !r.Conn.Closed) r.Conn.Send(frame);
        }

        private void SendState()
        {
            if (!IsConnected) return;

            pingTimer += Time.unscaledDeltaTime;
            if (pingTimer >= 1f)
            {
                pingTimer = 0f;
                var w = Proto.Begin(MsgType.Ping, out var ms);
                w.Write((double)Time.realtimeSinceStartup);
                var ping = Proto.End(w, ms);
                if (IsHost) Broadcast(ping, null); else net.Peer.Send(ping);
            }

            float interval = 1f / Mathf.Clamp(Mathf.Max(30, FarCoopPlugin.CfgSendRate.Value), 5, 60);   // >= 30 Hz
            sendTimer += Time.unscaledDeltaTime;
            if (sendTimer < interval) return;
            sendTimer = 0f;

            if (IsHost)
            {
                var holders = new List<ItemSync.HoldState>();
                foreach (var r in remotes) if (r.Handshake) holders.Add(r.Hold);
                items.HostTick(holders);
                if (vehicle.Scan()) Broadcast(vehicle.BuildBodiesMessage(), null);
                if (vehicle.Bodies.Count > 0) Broadcast(vehicle.BuildStateMessage(), null);
                if (items.Scan()) Broadcast(items.BuildListMessage(), null);
                Broadcast(items.BuildStateMessage(vehicle.Chassis), null);

                bool all = EveryonePaused();
                if (all != lastSentAllPaused || Time.unscaledTime >= nextPauseSend)
                {
                    lastSentAllPaused = all;
                    nextPauseSend = Time.unscaledTime + 1f;
                    var w = Proto.Begin(MsgType.PauseAll, out var ms);
                    w.Write(all);
                    Broadcast(Proto.End(w, ms), null);
                }

                if (Dedicated)
                {
                    if (Time.unscaledTime >= nextDedicatedLog)
                    {
                        nextDedicatedLog = Time.unscaledTime + 10f;
                        var names = remotes.FindAll(x => x.Handshake).ConvertAll(x => $"{x.Label}({(x.RttMs < 0 ? "-" : x.RttMs.ToString("0"))}ms{(x.Puppet.Exists ? "" : ",no body")})");
                        FarCoopPlugin.Logger.LogInfo($"[server] fps={1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0} players={names.Count} [{string.Join(", ", names)}] vehicleBodies={vehicle.Bodies.Count} items={items.Count} energy={vehicle.Energy:0.0}{(all ? " PAUSED" : "")}");
                    }
                    return;   // no player of our own
                }
                var mine = localSender.BuildLocalPlayerMessage(vehicle, localPaused);
                if (mine != null)
                {
                    var w = Proto.Begin(MsgType.PlayerFrom, out var ms);
                    w.Write((byte)0);
                    w.Write(mine, 1, mine.Length - 1);
                    Broadcast(Proto.End(w, ms), null);
                }
            }
            else
            {
                var peer = net.Peer;
                vehicle.Scan();
                items.Scan();
                var hold = items.BuildHoldMessage();
                if (hold != null) peer.Send(hold);
                var player = localSender.BuildLocalPlayerMessage(vehicle, localPaused);
                if (player != null) peer.Send(player);
            }
        }

        public void SendHose(string path, bool planted)
        {
            if (IsHost) { Broadcast(HoseFromMsg(0, path, planted), null); return; }
            var peer = net.Peer;
            if (peer == null || peer.Closed) return;
            var w = Proto.Begin(MsgType.Hose, out var ms);
            w.Write(path);
            w.Write(planted);
            peer.Send(Proto.End(w, ms));
        }

        public void SendButton(byte kind, string path)
        {
            var w = Proto.Begin(MsgType.Button, out var ms);
            w.Write(kind);
            w.Write(path);
            var f = Proto.End(w, ms);
            if (IsHost) { Broadcast(f, null); return; }
            var peer = net.Peer;
            if (peer == null || peer.Closed) return;
            peer.Send(f);
        }

        public void Quit()
        {
            try { Disconnect("game closed"); } catch { }
        }

        // ======================= HUD =======================

        private void AddLog(string s)
        {
            Telemetry.Event(s);
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
            var servers = BuildServerRows();
            float listH = IsConnected || IsHost ? 0f : 24f + servers.Count * 26f + 6f;
            float playersH = IsConnected ? 20f : 0f;
            float h = 24f + 20f + playersH + 26f + 34f + 32f + listH + 32f + log.Count * 34f + 8f;
            GUI.Box(new Rect(x, y, w, h), "FAR LAN Co-op " + FarCoopPlugin.Version + "   [F4 hide]");
            float cx = x + 10f, cy = y + 24f, cw = w - 20f;

            GUI.Label(new Rect(cx, cy, cw, 20f), "Status: " + StatusText());
            cy += 20f;
            if (IsConnected)
            {
                string bodies = IsHost ? $"{vehicle.Bodies.Count} parts" : $"{vehicle.MappedCount} parts";
                string itemInfo = IsHost ? $"{items.Count} items" : $"{items.Mapped} items";
                UpdateBandwidth();
                string extra = $" | net {kbIn:0.0}/{kbOut:0.0} kB/s" + (IsHost ? "" : $" | delay {NetClock.Delay * 1000f:0} ms");
                float energy = vehicle.Energy;
                string ping = IsHost ? "" : $"Ping {(rttMs < 0 ? "-" : rttMs.ToString("0") + " ms")} | ";
                GUI.Label(new Rect(cx, cy, cw, 20f),
                    $"{ping}{bodies} | {itemInfo} | fuel {(float.IsNaN(energy) ? "-" : energy.ToString("0.0"))}{extra}");
                cy += 20f;
                var names = new List<string>();
                foreach (var r in remotes)
                {
                    if (IsHost && !r.Handshake) continue;
                    string n = r.Label;
                    if (IsHost && r.RttMs >= 0) n += $" {r.RttMs:0}ms";
                    if (!r.Puppet.Exists) n += " (no body)";
                    if (r.Avatar.PartnerPaused) n += " (paused)";
                    names.Add(n);
                }
                GUI.Label(new Rect(cx, cy, cw, 20f), "Players: " + (names.Count == 0 ? "-" : string.Join(", ", names)));
            }
            else
            {
                GUI.Label(new Rect(cx, cy, cw, 20f), (IsHost ? "Share: " : "My IP: ") + MyAddresses());
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
                : (string.IsNullOrEmpty(ipField) ? "click, type IP:port" : ipField + "   (click to edit)");
            if (GUI.Button(new Rect(cx + 60f, cy, cw - 170f, 24f), fieldText))
            {
                ipEditing = !ipEditing;
            }
            if (GUI.Button(new Rect(cx + cw - 100f, cy, 100f, 24f), net.Connecting ? "Connecting" : "Join IP")) { ipEditing = false; JoinIp(ipField); }
            cy += 32f;

            if (listH > 0f)
            {
                GUI.Label(new Rect(cx, cy + 2f, cw - 130f, 22f), servers.Count == 0 ? "Servers: none found yet" : "Servers (click to join):");
                if (GUI.Button(new Rect(cx + cw - 120f, cy, 120f, 22f), net.Searching ? "Searching..." : "Refresh list")) RefreshServers();
                cy += 24f;
                foreach (var row in servers)
                {
                    if (GUI.Button(new Rect(cx, cy, cw, 24f), row.Key)) { ipEditing = false; ipField = row.Value; JoinIp(row.Value); }
                    cy += 26f;
                }
                cy += 6f;
            }

            if (GUI.Button(new Rect(cx, cy, cw, 24f), "Teleport to partner (F5)")) TeleportToPartner();
            cy += 32f;

            for (int i = 0; i < log.Count; i++)
            {
                GUI.Label(new Rect(cx, cy, cw, 34f), log[i]);
                cy += 34f;
            }

            DrawPartnerTag();
        }

        /// <summary>LAN hosts found by search + recently joined servers: (label, address).</summary>
        private List<KeyValuePair<string, string>> BuildServerRows()
        {
            var rows = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>();
            foreach (var f in found)
            {
                string addr = f.Ip + ":" + f.Port;
                if (seen.Add(addr)) rows.Add(new KeyValuePair<string, string>($"{f.Name}   {addr}   (LAN)", addr));
            }
            foreach (var addr in RecentServers())
                if (seen.Add(addr) && rows.Count < 6) rows.Add(new KeyValuePair<string, string>($"{addr}   (recent)", addr));
            return rows;
        }

        private void DrawPartnerTag()
        {
            if (!IsConnected) return;
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var r in remotes)
            {
                // the tag follows where that player really is on their own screen
                var rootW = r.Avatar.PartnerRootWorld(vehicle);
                if (!rootW.HasValue) continue;
                Vector3 basePos = rootW.Value;
                Vector3 sp = cam.WorldToScreenPoint(basePos + Vector3.up * 1.6f);
                if (sp.z <= 0f) continue;
                GUI.Label(new Rect(sp.x - 60f, Screen.height - sp.y - 10f, 120f, 22f), r.Label);
            }
        }

        private float kbIn, kbOut, bwTime;
        private long lastIn, lastOut;
        private void UpdateBandwidth()
        {
            float now = Time.unscaledTime;
            if (now - bwTime < 1f) return;
            long inB = 0, outB = 0;
            if (IsHost) foreach (var c in net.Peers) { inB += System.Threading.Interlocked.Read(ref c.BytesIn); outB += System.Threading.Interlocked.Read(ref c.BytesOut); }
            else if (net.Peer != null) { inB = System.Threading.Interlocked.Read(ref net.Peer.BytesIn); outB = System.Threading.Interlocked.Read(ref net.Peer.BytesOut); }
            float dt = Mathf.Max(0.001f, now - bwTime);
            if (inB >= lastIn && outB >= lastOut) { kbIn = (inB - lastIn) / 1024f / dt; kbOut = (outB - lastOut) / 1024f / dt; }
            lastIn = inB; lastOut = outB; bwTime = now;
        }

        private string StatusText()
        {
            int joined = remotes.FindAll(r => r.Handshake).Count;
            if (net.Role == NetRole.Host)
                return joined > 0 ? $"HOST - {joined}/{net.MaxClients} player(s) joined" : $"HOST - waiting on port {FarCoopPlugin.CfgPort.Value}...";
            if (IsConnected) return $"CLIENT - in '{hostName}' as player {myId} ({remotes.Count + 1} players)";
            if (net.Connecting) return "Connecting...";
            if (net.Peer != null) return "Handshaking...";
            if (net.Searching) return "Searching LAN...";
            return "Offline";
        }
    }
}
