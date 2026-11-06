# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- Stricter default Keep list: only Claude and remote-access/streaming tools (Tailscale, Sunshine, Parsec, AnyDesk, TeamViewer, RustDesk) are kept; Discord, OBS, overlay tools and terminals are now closed during a game unless you add a Keep rule. Existing installs keep their current rules.

## [0.1.0] - 2026-11-05

First release.

### Added
- Automatic detection of Steam game sessions (local and Remote Play), non-Steam trigger executables, and the Remote Play client.
- Per-app rules (Keep / Suspend / Close) with defaults for windowed and background apps; protected-process list that can't be overridden.
- Graceful close with timeout and optional force-kill; relaunch of closed apps with their original command line; suspend/resume with memory trim.
- Local and Streaming profiles: power plan, game priority, notification suppression, keep-awake, optional Windows service stop/restart.
- Crash-safe session journal with automatic recovery on next launch; restore on app exit.
- WinForms configuration UI (Dashboard, Apps, Profiles, Settings, History), system tray menu, dry-run mode.
- Per-user installer (`SteamOptimizer-Setup-0.1.0.exe`) with Start Menu/Desktop shortcuts, optional start with Windows, and uninstaller; portable `SteamOptimizer.exe`.
- Headless `--dry-run` and `--selftest` modes.

### Known issues
- Microsoft Store (UWP) apps are not detected.
- Remote Play host detection is heuristic (recent `streaming_log.txt` activity).
- Binaries are unsigned.

[0.1.0]: ../../releases/tag/v0.1.0
