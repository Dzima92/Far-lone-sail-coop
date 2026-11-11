# FAR: Lone Sails – LAN Co-op Mod

A BepInEx plugin that adds **2-player LAN co-op** to *FAR: Lone Sails*.
One player hosts, one player joins. Both walk around separately, and the vehicle,
fuel and boxes are synced.

> **LAN only:** one host plus **one** partner, on the same local network.
> Internet play and more than 2 players are not supported.

## Features

- Host / join over LAN, with automatic LAN search (F2) or by typing the host IP
- The host runs the vehicle; the partner sees the same vehicle position, parts and fuel
- Partner can press vehicle buttons and levers, carry boxes and load fuel
- Each player walks independently; the partner is shown with a name tag
- The game only pauses when **both** players open the pause menu
- RESUME menu skipped automatically at game start

## Installation (do this on both PCs)

1. **Download BepInEx 6.0.0-pre.1 for IL2CPP x64:**
   `BepInEx_UnityIL2CPP_x64_6.0.0-pre.1.zip` from the
   [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases) (tag `v6.0.0-pre.1`).
   Newer BepInEx 6 builds will **not** load this mod.
2. **Extract the zip into the game folder**, next to `FarLoneSails.exe`.
   The folder should now contain `BepInEx\`, `mono\`, `winhttp.dll` and `doorstop_config.ini`.
3. **Start the game once and wait for the main menu.** The first start takes a few minutes,
   because BepInEx generates its files then. Then close the game.
4. **Copy `FarCoopMod.dll`** (from [Releases](../../releases)) into `BepInEx\plugins\`.
5. Start the game. A co-op panel appears in the top-left corner (F4 shows/hides it).

You do **not** need `cpp2il_out`, Cpp2IL or UnityExplorer.

## How to play

1. Both players load the **same save / chapter**.
2. Host presses **F1**.
3. Partner presses **F2** to find the host on the LAN, or clicks the IP field, types the host IP and presses Enter.
4. Allow the game through Windows Firewall (Private networks) when asked.
   Ports used: TCP 7777, UDP 7778.

| Key | Action |
| --- | --- |
| F1 | Host |
| F2 | Find host on LAN and join |
| F3 | Disconnect |
| F4 | Show / hide co-op panel |
| F5 | Teleport to partner |

## Settings

`BepInEx\config\com.dima.farlonesailscoop.cfg` is created after the first start. Edit it with the game closed.

| Setting | Default | Meaning |
| --- | --- | --- |
| Port | 7777 | TCP port the host listens on |
| DiscoveryPort | 7778 | UDP port for LAN search |
| Name | PC name | Name shown above you |
| PartnerBody | true | Partner gets a real body (buttons, pushing). Set false if the partner behaves strangely |
| SendRateHz | 30 | Sync updates per second (minimum 30) |
| AutoResumeOnStart | true | Skip the RESUME menu once at start |

## Troubleshooting

| Problem | Fix |
| --- | --- |
| No co-op panel | Check `FarCoopMod.dll` is in `BepInEx\plugins` and `BepInEx\LogOutput.log` contains `Loading [FAR LAN Coop` |
| F2 finds nothing | Type the host IP and press Enter; check the firewall on both PCs |
| "different mod version" | Both players need the same `FarCoopMod.dll` |
| Boxes in different places | Load the same save on both PCs |

Bug reports: please attach `BepInEx\LogOutput.log` from **both** PCs.

## Building from source

Visual Studio 2022+ with .NET Framework 4.7.2. In `FarCoopMod.csproj`, set `GameDir` to your game
folder (BepInEx must have been run once, so `BepInEx\unhollowed` exists), and clear `PartnerPluginsDir`.
Build Release; the DLL is copied to `BepInEx\plugins` automatically.

## Credits

Made by Dzima92. Not affiliated with Okomotive or Mixtvision.

My contacts discord: milky_way7084
