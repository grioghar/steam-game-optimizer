# TODO

## Next up
- [ ] Global hotkey (and tray item) to restore everything immediately
- [ ] Per-game profiles / overrides (e.g. keep Discord for one game, stricter list for another)
- [ ] Stream-health readout on the Dashboard (bitrate, dropped frames, latency from Steam's streaming log)
- [ ] Apps tab: "learn" mode — snapshot the apps open at game start and suggest rules

## Detection & reliability
- [ ] Verify and harden Remote Play host detection against more Steam client versions
- [ ] Detect and handle Microsoft Store (UWP) apps hosted by `ApplicationFrameHost`
- [ ] Confirm notification suppression actually takes effect on all Windows 11 builds; consider Focus Assist
- [ ] Relaunch apps de-elevated even when args are present while running as administrator
- [ ] Handle apps that relaunch themselves / single-instance launchers more gracefully
- [ ] Launcher awareness (close Battle.net / Epic / EA app launchers after the game starts)

## Features
- [ ] Pause OneDrive sync and Windows Update/delivery optimization during a session
- [ ] Flush standby memory (admin) and optional GPU/driver tweaks
- [ ] Auto-lock or sleep the PC after a streaming session ends
- [ ] Per-session summary toast ("released 1.2 GB, restored in 3 s")
- [ ] Export/import rules; ship community rule presets
- [ ] Steam Deck / phone companion for remote Optimize / Restore

## Project
- [ ] Code-sign the binaries
- [ ] Automated tests in CI (build + `--selftest` on a Windows runner)
- [ ] GitHub Actions release workflow
- [ ] Screenshots in the README
