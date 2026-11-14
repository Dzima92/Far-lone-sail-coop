# Changelog

## v4.2.2
- Fixed: on a driving vehicle clients walked slower to the left (backwards) than to the right. The game's own extra push is learned while standing and removed while walking too.

## v4.2.1
- Fixed (found with telemetry): clients slid forward ~0.8 m/s inside a driving vehicle with no key pressed, then jittered against walls. Riders are now carried rigidly with the client's vehicle and, while their player presses nothing, their spot in the vehicle is held.

## v4.2.0
- Telemetry: every PC writes one line per second to `BepInEx\FarCoop-telemetry.log` with keys pressed/held (A/D/W/S, jump, take), own position (also relative to the vehicle), SLIDE detection (moving inside the vehicle without pressing left/right), vehicle position/speed/fuel, ping, interpolation delay, traffic, every other player (input, position, body distance, update age) and events (buttons, items, hose, joins). Turn off with `[Debug] Telemetry = false`.

## v4.1.3
- Fixed: when one player left the vehicle, the hull became opaque for everyone still inside. The front cover now follows only your own character.
- Start button: verified from the logs that a client's push now hooks the engine (Out -> OnIn -> In -> Hooked).
- Diagnostics: [button] lines show the engine button state, push depth and how far a partner body is from its player.

## v4.1.2
- Fixed: players could drift out of the side-view plane (clipping in front of the vehicle). Depth is now always kept exactly for other players' bodies and for players carried by the vehicle.

## v4.1.1
- Fixed: clients could not press the start button again. The partner body on the host was pulled back to the client's position while pushing (the client's copy of the button moves only later). Small differences are now left alone (35 cm dead zone, gentle pull beyond it).
- Name tags follow where each player really is, not their invisible body.

## v4.1.0
- Netcode like other co-op games: snapshots carry the host's clock and clients show the vehicle and items slightly in the past, blended between two snapshots (interpolation buffer, delay adapts to ping jitter 50-300 ms). Much smoother on clients, no more rubber-banding from uneven packets.
- Items inside the vehicle are sent relative to it: resting boxes in a driving vehicle cost no bandwidth and sit exactly on the client's vehicle.
- Rotations compressed to 7 bytes (smallest-three), angular velocity dropped; all queued messages go out in one socket write.
- HUD shows network traffic (kB/s in/out) and the client's interpolation delay.
- Protocol 5: every PC needs this version.

## v4.0.2
- Diagnostics: the host logs who pushes which vehicle button ([button] lines).

## v4.0.1
- Fixed: other players slid around on the vehicle while it moved. Their visible body is now smoothed in the vehicle's own space, and their invisible body is carried with the vehicle on clients.

## v4.0.0
- More than 2 players: up to `MaxPlayers` (default 4, host included; a dedicated server doesn't count) in one game.
- The host relays every player's movement to everyone; each game keeps an invisible body for every other player, so all of them can drive, carry boxes and use the hose.
- The game pauses only when every player is in the pause menu.
- HUD shows the list of players (with ping on the host); F5 cycles through players.
- Protocol 4: every PC needs this version.

## v3.5.0
- Test: dedicated server mode. `-farcoop-dedicated` hosts without a player of its own (Tools/Test-Server.bat starts it hidden with -batchmode -nographics; Tools/Stop-Server.bat stops it). Still one player joins at a time.

## v3.4.2
- Fixed: the client slid off the vehicle while driving fast. The client's vehicle copy is moved by the mod, so the game didn't carry the player with it; the mod now carries the client's player along with the vehicle.

## v3.4.1
- Boxes the partner carries are now really taken by the partner's body on the host, so the game handles them the normal way. Before, they were moved by hand and kept being pushed by the vehicle's physics after being dropped, which made them fly.
- If the partner stops holding an item without dropping it (for example it went into the burner), the host's copy of the partner drops it.

## v3.4.0
- Fire hose sync: when one player takes or releases the hose, the other player sees it in their hands and sees the water spray.
- Fixed the "flying box": on the host, an item the partner stopped holding (put in the burner, swapped for another item, lag or disconnect) now goes back to normal physics instead of freezing in the air.
- Ctrl+Shift+B now copies FarCoopMod.dll into both the coop and coop2 plugins folders.

## v3.3.3
- Fixed: the partner's body copy could pick up boxes and grab ropes by itself, which made boxes float,
  drew a stretched rope and spammed NullReferenceException errors in the log.

## v3.3.2
- Fixed: players became invisible after the partner joined (the partner's body copy ran the game's
  motion smoothing on the local player's model a second time).
- Server list in the co-op panel: hosts found on the LAN plus the last 5 joined servers; click to join.
- Join by `IP` or `IP:port` (works over Radmin VPN / Hamachi / ZeroTier).
- LAN search also finds a host on the same PC (127.0.0.1); each host is listed once.
- Less network traffic: vehicle parts that do not move are not re-sent (full refresh every second).
- Command-line options for testing two games on one PC: `-farcoop-host`, `-farcoop-join IP[:port]`,
  `-farcoop-name Name`, plus `Tools\Test-Host.bat` / `Tools\Test-Client.bat`.
- Position diagnostics are off by default (`PositionLog` in the config).

## v3.2
- Partner body can press vehicle buttons, carry boxes and load fuel; boxes and fuel synced.
- Game only pauses when both players open the pause menu; RESUME menu skipped at start.
