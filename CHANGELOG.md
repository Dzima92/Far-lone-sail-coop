# Changelog

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
