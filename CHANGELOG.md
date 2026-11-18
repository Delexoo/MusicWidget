# Changelog

All notable changes to **MusicWidget** are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.3.4] - 2026-11-18

### Changed
- Launch position is horizontally centered near the **top** of the screen (not mid-screen)

## [2.3.3] - 2026-11-18

### Changed
- Center the widget on launch
- EFX and Add music open as compact popovers from their buttons (no full-screen modal)

## [2.3.2] - 2026-11-18

### Added
- In-app EFX panel (4-tick bass, reverb slider, vocals toggle, speed)
- Add music flow: select files/folder or download URL (no always-visible download bar)
- Playlist rename / delete / add controls; track like, move-to-playlist, and delete menus
- In-app prompt and confirm modals; disabled WebView context menu

### Changed
- Empty playlists show a dotted **Add music** button instead of static “No tracks yet”

## [2.3.1] - 2026-11-18

### Changed
- Restored classic MusicWidget UI on Tauri: charcoal radius-14 pill, white-circle transport, clip-reveal loop/gear, accordion dashboard, footer EFX + now-playing
- Window sizes to pill-only vs expanded dashboard; continuous reverb slider kept in the effects row

## [2.3.0] - 2026-11-18

### Changed
- **Framework rewrite:** MusicWidget now ships on **Tauri 2 + React + Web Audio** (same music engine patterns as Mochi)
- Live lowshelf bass + delay/feedback reverb with a **continuous reverb slider** (0–100%)
- Spring-smooth pill hover and dashboard open/close (Motion)
- System tray, Alt+\ hide, folder playlists, yt-dlp downloads, and UVR-MDX HQ → WAV vocals on the new stack
- Installer produced by Tauri NSIS, published as `MusicWidget-Setup-x64.exe`

### Removed
- WPF / LibVLC shipping path (legacy `MusicWidget/` C# project retained in-repo for reference only)

## [2.2.42] - 2026-11-18

### Added
- Continuous hall reverb (Off → 25% → 50% → 75% → 100%) without restarting the audio engine
- Dual-player ~70 ms crossfade when swapping original ↔ instrumental mid-track
- Per-track vocal-remover memory (`VocalsOffByPath`)

### Changed
- Playback EFX redesigned after Mochi’s live graph: always-on LibVLC spatializer via media options, lowshelf-style bass
- Vocal remover upgraded to **UVR-MDX-NET-Inst_HQ_3** → 44.1 kHz WAV (rejects empty / center-cancel fakes)
- Race-safe playback generation tokens; removed fragile post-Play `Thread.Sleep` seeks


## [2.2.41] - 2026-11-10

### Added
- System tray icon in Windows **Show hidden icons** (show/hide, open dashboard, exit)

### Changed
- Docs, issue templates, and landing page fully branded as **MusicWidget** (GitHub release titles updated)

## [2.2.40] - 2026-11-10

### Removed
- **Saves** playlist and footer save button (Liked Songs covers this)

## [2.2.39] - 2026-11-10

### Fixed
- Dashboard mouse-wheel scrolling works again (playlist list was swallowing wheel events)

## [2.2.38] - 2026-11-10

### Added
- Right-click a song → **Move to playlist** (or **Add to playlist** from Liked Songs)

### Fixed
- Downloads no longer freeze the whole window (worker no longer runs on the UI thread; progress updates are coalesced)

## [2.2.37] - 2026-10-25

### Changed
- Volume always starts at **100%** on launch (session-only, like speed)
- Vocal remover is labeled **beta**; instrumentals are built when you toggle it on — not during downloads (faster downloads)

### Fixed
- Volume, speed, and EQ are re-applied when the next track starts (LibVLC was resetting them)

## [2.2.36] - 2026-10-25

### Fixed
- Footer volume/speed/time labels no longer clipped on the left
- Download status text no longer cut off by the progress bar
- Spotify downloads no longer hang at 92% while installing optional Deno (runs in background; drains process output)

## [2.2.35] - 2026-10-25

### Added
- Vocal remover **prefetches** instrumentals in the background (on download + next tracks in queue) so play is usually ready instantly
- Smooth animated download and playback progress bars

### Changed
- Vocal remover toolchain warms up when the effect is enabled; demucs runs at lower process priority

## [2.2.34] - 2026-10-25

### Fixed
- Removed clipping/static from loud volume and bass boost (EQ headroom + safer level-3 curve)

## [2.2.33] - 2026-10-25

### Changed
- Volume slider now goes to **200%** with a live percent label (same style as speed)
- Bass boost **level 3** is much louder (max low-band EQ + mild preamp lift)
- Downloads use highest quality encode (yt-dlp V0 / spotDL **320k**)

## [2.2.32] - 2026-10-25

### Fixed
- Vocal remover bootstrap no longer deadlocks while installing pip (drain redirected process output)

## [2.2.31] - 2026-10-25

### Added
- **Vocal remover** Effects toggle — AI instrumental via demucs-onnx (cached under AppData; playlist files unchanged). First use downloads Python + model.

## [2.2.30] - 2026-10-25

### Fixed
- Spotify downloads failing with `Requested format is not available` (upgrade spotDL, permissive yt-dlp format fallbacks, updated YouTube clients)

## [2.2.29] - 2026-10-25

### Changed
- **Slowed & reverb** is plain **Reverb** again (hall only; no 0.8x slowdown)

### Removed
- Vocal remover effect

## [2.2.28] - 2026-10-25

### Fixed
- Playlist play/pause icon now follows the playlist that owns the current track (selecting another playlist no longer shows a false Pause on empty lists)

## [2.2.27] - 2026-10-25

### Added
- **Keep open** pin next to the update/download button — dashboard stays open when clicking outside (minimize manually)
- App version shown next to the Dashboard title

### Changed
- Library renamed to **Dashboard**; removed the “Playlists and now playing” subtitle
- Nested dialogs (Paste audio link, confirmations) dismiss first; a second outside click closes the dashboard
- Install/update and other prompts center on the dashboard (no more stuck-at-top positioning)

## [2.2.26] - 2026-10-25

### Changed
- Reverb effect is now **Slowed & reverb** (0.8x + hall reverb)
- Bass boost is a 3-level cycle (Off → 1 → 2 → 3 extreme)
- Vocal remover uses a deeper mid scoop on top of karaoke center-cancel

## [2.2.25] - 2026-10-25

### Changed
- Playlist cards are darker; selection only lightens slightly (no blue highlight)
- Track covers in playlists are center-cropped

## [2.2.24] - 2026-10-25

### Added
- Effects row above the now-playing bar: Bass boost, Reverb, and Vocal remover toggles

### Changed
- Missing covers use a white tile with a black music note
- Artwork cache stores the correct image extension and reloads via stream decode

### Fixed
- Album covers from embedded tags and online lookup now appear in playlist rows
- Parallel cover loading so expanded playlists fill artwork faster

## [2.2.23] - 2026-10-25

### Added
- Update button downloads the official installer from GitHub Releases with progress, then silent-installs and relaunches

### Changed
- Playlist rows use a lighter card surface so they read as playlists
- Missing album art uses a white tile with a dark music icon
- Library folder migrates from `Music\Beats` to `Music\MusicWidget`

### Fixed
- Album covers load more reliably from embedded tags and online lookup
- Volume, speed, and seek sliders no longer jump or stutter while dragging

## [2.2.22] - 2026-10-25

### Changed
- Installer default path is now `Program Files\MusicWidget` (no Delexo subfolder)
- GitHub repository renamed from `beats` to `MusicWidget`
- Project site and links now use `delexoo.github.io/MusicWidget` and `github.com/Delexoo/MusicWidget`

## [2.2.21] - 2026-10-25

### Added
- Product renamed to **MusicWidget** (app, shortcuts, AppData, and installer)
- Playback speed slider; always starts at 1.0x on launch
- Custom playlist icons and larger playlist rows with left-aligned names
- Download queue so new links wait while another download runs
- Click-outside closes rename, paste-link, and other dialogs
- MusicWidget License Agreement (RTF) in the installer, personalized for Delexo
- Installer default path `Program Files\Delexo\MusicWidget`

### Changed
- Smoother volume, speed, and seek sliders
- Crisp Paste-link brand icons
- Rounded widget edges and cleaner library layout
- Album covers from embedded tags plus online lookup when missing

### Fixed
- Startup crash on some machines
- Widget expanding infinitely from the shadow layout loop
- Resize jump when dragging the widget width
- Multi-monitor and DPI centering for the floating pill

## [2.2.20] - 2026-10-08

### Fixed
- Widget centering on different screen sizes and DPI scaling (125%/150%/200%)

## [2.2.19] - 2026-10-08

### Added
- Microsoft / Windows trademark disclaimer on the website and installer
- Music-note cover placeholders when artwork is missing

### Changed
- Product display name is now **Windows Beats**
- Library dashboard UI simplified (clearer track rows, quieter playlist actions)
- Track list mouse-wheel scrolls normally instead of jumping selection
- Artwork lookup caches misses and prefetches more covers per playlist
- In-app update button waits for startup checks and installs more reliably

### Fixed
- spotDL reporting success when no files were saved
- Update installer relaunch race after silent install

## [2.2.18] - 2026-07-09

### Added
- Playlist accordion dashboard with expandable rows and full-row click targets
- Album artwork in playlist track lists (embedded tags, disk cache, and online lookup)
- Auto-minimize dashboard when clicking outside the panel or focusing another app
- In-app update checks with user-confirmed install from the dashboard
- Global media key support for headset and keyboard transport controls
- Download thumbnails embedded in new MP3 files (`--embed-thumbnail`)

### Changed
- Faster dashboard refresh with debounced playlist and artwork loading
- Improved playlist row hit-testing so the entire header toggles expand/collapse
- Smoother song list interaction: wheel scroll per track, no scroll jump on play
- Startup shows the widget immediately; update check runs in the background

### Fixed
- Update service shutdown and relaunch reliability
- Playlist artwork not loading when opening existing playlists
- Download temp files (`.part`) excluded from track lists
- Various stability and responsiveness improvements across the dashboard

## [2.2.17] and earlier

See [GitHub Releases](https://github.com/Delexoo/MusicWidget/releases) for prior version notes.
