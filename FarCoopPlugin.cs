using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using UnhollowerRuntimeLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace FarCoopMod
{
    [BepInPlugin(Guid, "FAR LAN Coop", Version)]
    public class FarCoopPlugin : BasePlugin
    {
        public const string Guid = "com.dima.farlonesailscoop";
        public const string Version = "3.3.2";
        public const int ProtocolVersion = 3;

        internal static ManualLogSource Logger;
        internal static Harmony HarmonyInstance;

        internal static ConfigEntry<int> CfgPort;
        internal static ConfigEntry<int> CfgDiscoveryPort;
        internal static ConfigEntry<string> CfgLastHostIp;
        internal static ConfigEntry<string> CfgPlayerName;
        internal static ConfigEntry<bool> CfgSyncButtons;
        internal static ConfigEntry<bool> CfgShowHud;
        internal static ConfigEntry<int> CfgSendRate;
        internal static ConfigEntry<bool> CfgClientPuppet;
        internal static ConfigEntry<bool> CfgAutoResume;
        internal static ConfigEntry<bool> CfgDebugLog;
        internal static ConfigEntry<string> CfgRecentServers;

        public override void Load()
        {
            Logger = Log;

            CfgPort = Config.Bind("Network", "Port", 7777, "TCP port the host listens on.");
            CfgDiscoveryPort = Config.Bind("Network", "DiscoveryPort", 7778, "UDP port used for LAN search.");
            CfgLastHostIp = Config.Bind("Network", "LastHostIp", "192.168.1.2", "Host address for Join IP: IP or IP:port, LAN or VPN such as Radmin (edited from the HUD as well).");
            CfgPlayerName = Config.Bind("Player", "Name", Environment.MachineName, "Name shown to the other player.");
            CfgSyncButtons = Config.Bind("Sync", "MirrorButtonsV2", false, "Old v2 button mirroring (off: the partner body presses buttons for real).");
            CfgClientPuppet = Config.Bind("Sync", "PartnerBody", true, "Host spawns a real character for the partner so they can drive the vehicle, carry boxes and fuel.");
            CfgSendRate = Config.Bind("Sync", "SendRateHz", 20, "State packets per second.");
            CfgAutoResume = Config.Bind("UI", "AutoResumeOnStart", true, "Skip the RESUME menu once when the game starts.");
            CfgRecentServers = Config.Bind("Network", "RecentServers", "", "Last joined servers (IP:port separated by |), shown in the server list.");
            CfgDebugLog = Config.Bind("Debug", "PositionLog", false, "Write position diagnostics to LogOutput.log every 5 s.");
            CfgShowHud = Config.Bind("UI", "ShowHud", true, "Show the co-op HUD on start (toggle with F4).");

            ClassInjector.RegisterTypeInIl2Cpp<CoopManager>();

            HarmonyInstance = new Harmony(Guid);
            ButtonSync.Install(HarmonyInstance);
            PartnerInput.Install(HarmonyInstance);

            SceneManager.add_sceneLoaded((UnityAction<Scene, LoadSceneMode>)((scene, mode) =>
            {
                try { CoopManager.EnsureExists(); }
                catch (Exception e) { Log.LogError("EnsureExists failed: " + e); }
            }));

            Log.LogInfo($"FAR LAN Coop {Version} loaded. F1 host | F2 search & join | F3 disconnect | F4 HUD | F5 teleport to partner");
        }
    }
}
