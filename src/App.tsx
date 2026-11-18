import { useEffect, useMemo, useRef } from "react";
import { animate, motion, useMotionValue } from "motion/react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { register, unregister } from "@tauri-apps/plugin-global-shortcut";
import { trackCoverUrl } from "./services/music/musicApi";
import { useMusicStore } from "./stores/musicStore";

function formatTime(sec: number) {
  if (!Number.isFinite(sec) || sec < 0) return "0:00";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}

export default function App() {
  const leaveTimer = useRef<number | null>(null);
  const dashHeight = useMotionValue(0);

  const {
    ready,
    dashboardOpen,
    hoverExpanded,
    playlists,
    activePlaylist,
    tracks,
    queue,
    index,
    playing,
    currentTime,
    duration,
    volume,
    rate,
    loop,
    effects,
    vocalsOffByPath,
    liked,
    status,
    downloadUrl,
    hydrate,
    setDashboardOpen,
    setHoverExpanded,
    selectPlaylist,
    playTrackAt,
    togglePlay,
    next,
    prev,
    seek,
    setVolume,
    setRate,
    setBass,
    setReverb,
    setLoop,
    toggleVocals,
    createPlaylist,
    download,
    toggleLike,
  } = useMusicStore();

  const track = index >= 0 ? queue[index] : null;
  const cover = trackCoverUrl(track?.coverPath);
  const vocalsOff = track ? Boolean(vocalsOffByPath[track.filePath]) : false;
  const likedNow = track
    ? liked.some((p) => p.toLowerCase() === track.filePath.toLowerCase())
    : false;

  const progress = useMemo(() => {
    if (!duration) return 0;
    return Math.min(100, (currentTime / duration) * 100);
  }, [currentTime, duration]);

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  useEffect(() => {
    const controls = animate(dashHeight, dashboardOpen ? 520 : 0, {
      type: "spring",
      stiffness: 380,
      damping: 34,
    });
    return () => controls.stop();
  }, [dashboardOpen, dashHeight]);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    void listen("musicwidget://open-dashboard", () => {
      setDashboardOpen(true);
    }).then((fn) => {
      unlisten = fn;
    });
    return () => unlisten?.();
  }, [setDashboardOpen]);

  useEffect(() => {
    const hotkey = "Alt+\\";
    void register(hotkey, async () => {
      const win = getCurrentWindow();
      const visible = await win.isVisible();
      if (visible) await invoke("hide_main_window");
      else await invoke("show_main_window");
    }).catch(() => undefined);
    return () => {
      void unregister(hotkey).catch(() => undefined);
    };
  }, []);

  const onPillEnter = () => {
    if (leaveTimer.current) window.clearTimeout(leaveTimer.current);
    setHoverExpanded(true);
  };

  const onPillLeave = () => {
    if (dashboardOpen) return;
    leaveTimer.current = window.setTimeout(() => setHoverExpanded(false), 120);
  };

  if (!ready) {
    return (
      <div className="flex h-full items-center justify-center text-sm text-[var(--muted)]">
        Loading MusicWidget…
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col items-center justify-start gap-3 p-3">
      <motion.div
        className="flex w-full max-w-[380px] items-center gap-2 rounded-full border border-[var(--border)] bg-[rgba(18,22,30,0.92)] px-2 py-2 shadow-[0_12px_40px_rgba(0,0,0,0.45)] backdrop-blur-xl"
        onMouseEnter={onPillEnter}
        onMouseLeave={onPillLeave}
        layout
        transition={{ type: "spring", stiffness: 420, damping: 36 }}
      >
        <motion.div
          className="overflow-hidden"
          animate={{ width: hoverExpanded || dashboardOpen ? 56 : 0, opacity: hoverExpanded || dashboardOpen ? 1 : 0 }}
          transition={{ type: "spring", stiffness: 420, damping: 36 }}
        >
          <div className="flex w-14 flex-col items-center gap-1 pr-1">
            <button
              className={`rounded-full px-2 py-1 text-[10px] ${loop ? "bg-[var(--accent)] text-white" : "bg-white/5 text-[var(--muted)]"}`}
              onClick={() => setLoop(!loop)}
              title="Loop"
            >
              Loop
            </button>
            <button
              className="rounded-full bg-white/5 px-2 py-1 text-[10px] text-[var(--muted)] hover:bg-white/10"
              onClick={() => setDashboardOpen(!dashboardOpen)}
              title="Dashboard"
            >
              Dash
            </button>
          </div>
        </motion.div>

        <div
          className="h-11 w-11 shrink-0 overflow-hidden rounded-full bg-[var(--surface-2)]"
          onMouseDown={() => void getCurrentWindow().startDragging()}
        >
          {cover ? (
            <img src={cover} alt="" className="h-full w-full object-cover" draggable={false} />
          ) : (
            <div className="flex h-full w-full items-center justify-center text-xs text-[var(--muted)]">MW</div>
          )}
        </div>

        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold">{track?.title ?? "MusicWidget"}</div>
          <div className="truncate text-xs text-[var(--muted)]">{track?.artist ?? "Idle"}</div>
        </div>

        <div className="flex items-center gap-1">
          <button className="rounded-full px-2 py-1 text-xs text-[var(--muted)] hover:bg-white/10" onClick={() => void prev()}>
            ‹
          </button>
          <button
            className="rounded-full bg-[var(--accent)] px-3 py-1.5 text-xs font-semibold text-white"
            onClick={() => void togglePlay()}
          >
            {playing ? "Pause" : "Play"}
          </button>
          <button className="rounded-full px-2 py-1 text-xs text-[var(--muted)] hover:bg-white/10" onClick={() => void next()}>
            ›
          </button>
        </div>
      </motion.div>

      <motion.div
        style={{ height: dashHeight }}
        className="w-full max-w-[380px] overflow-hidden"
      >
        <div className="h-[520px] overflow-hidden rounded-[28px] border border-[var(--border)] bg-[rgba(14,17,24,0.96)] shadow-[0_20px_60px_rgba(0,0,0,0.5)] backdrop-blur-xl">
          <div className="flex h-full flex-col gap-3 p-4">
            <div className="flex items-center justify-between">
              <div>
                <div className="text-lg font-semibold">Dashboard</div>
                <div className="text-xs text-[var(--muted)]">Playlists · downloads · EFX</div>
              </div>
              <button
                className="rounded-full bg-white/5 px-3 py-1 text-xs text-[var(--muted)] hover:bg-white/10"
                onClick={() => setDashboardOpen(false)}
              >
                Close
              </button>
            </div>

            <div className="grid grid-cols-[118px_1fr] gap-3 min-h-0 flex-1">
              <div className="overflow-auto rounded-2xl bg-[var(--surface)] p-2">
                <div className="mb-2 text-[10px] uppercase tracking-wide text-[var(--muted)]">Playlists</div>
                {playlists.map((p) => (
                  <button
                    key={p.id}
                    className={`mb-1 w-full rounded-xl px-2 py-2 text-left text-xs transition-colors ${
                      activePlaylist === p.name ? "bg-[var(--accent)]/20 text-white" : "hover:bg-white/5 text-[var(--muted)]"
                    }`}
                    onClick={() => void selectPlaylist(p.name)}
                  >
                    <div className="truncate font-medium text-[var(--text)]">{p.name}</div>
                    <div className="text-[10px] opacity-70">{p.trackCount} tracks</div>
                  </button>
                ))}
                <button
                  className="mt-2 w-full rounded-xl border border-dashed border-white/10 px-2 py-2 text-xs text-[var(--muted)] hover:bg-white/5"
                  onClick={() => {
                    const name = window.prompt("Playlist name");
                    if (name?.trim()) void createPlaylist(name.trim());
                  }}
                >
                  + New
                </button>
              </div>

              <div className="flex min-h-0 flex-col gap-2">
                <div className="min-h-0 flex-1 overflow-auto rounded-2xl bg-[var(--surface)] p-2">
                  {tracks.length === 0 ? (
                    <div className="p-3 text-xs text-[var(--muted)]">No tracks in this playlist.</div>
                  ) : (
                    tracks.map((t, i) => (
                      <button
                        key={t.id}
                        className="mb-1 flex w-full items-center gap-2 rounded-xl px-2 py-2 text-left hover:bg-white/5"
                        onDoubleClick={() => void playTrackAt(i, tracks)}
                        onClick={() => void playTrackAt(i, tracks)}
                      >
                        <div className="min-w-0 flex-1">
                          <div className="truncate text-xs font-medium">{t.title}</div>
                          <div className="truncate text-[10px] text-[var(--muted)]">{t.artist}</div>
                        </div>
                      </button>
                    ))
                  )}
                </div>

                <div className="rounded-2xl bg-[var(--surface)] p-3">
                  <div className="mb-2 flex items-center justify-between text-[10px] uppercase tracking-wide text-[var(--muted)]">
                    <span>Effects</span>
                    <span>{Math.round(effects.reverb * 100)}% reverb</span>
                  </div>
                  <div className="mb-3 flex items-center gap-2">
                    <button
                      className="rounded-full bg-white/5 px-3 py-1 text-xs hover:bg-white/10"
                      onClick={() => setBass((effects.bassBoost + 1) % 4)}
                    >
                      Bass {effects.bassBoost || "off"}
                    </button>
                    <button
                      className={`rounded-full px-3 py-1 text-xs ${vocalsOff ? "bg-[var(--accent)] text-white" : "bg-white/5 text-[var(--muted)]"}`}
                      onClick={() => void toggleVocals()}
                    >
                      Vocals {vocalsOff ? "off" : "on"}
                    </button>
                    <button
                      className={`rounded-full px-3 py-1 text-xs ${likedNow ? "bg-[var(--danger)]/80 text-white" : "bg-white/5 text-[var(--muted)]"}`}
                      onClick={() => void toggleLike()}
                    >
                      Like
                    </button>
                  </div>
                  <label className="mb-1 block text-xs text-[var(--muted)]">Reverb</label>
                  <input
                    className="slider w-full"
                    type="range"
                    min={0}
                    max={1}
                    step={0.01}
                    value={effects.reverb}
                    onChange={(e) => setReverb(Number(e.target.value))}
                  />

                  <div className="mt-3 grid grid-cols-2 gap-3">
                    <div>
                      <label className="mb-1 block text-xs text-[var(--muted)]">Volume</label>
                      <input
                        className="slider w-full"
                        type="range"
                        min={0}
                        max={1}
                        step={0.01}
                        value={volume}
                        onChange={(e) => setVolume(Number(e.target.value))}
                      />
                    </div>
                    <div>
                      <label className="mb-1 block text-xs text-[var(--muted)]">Speed {rate.toFixed(2)}x</label>
                      <input
                        className="slider w-full"
                        type="range"
                        min={0.5}
                        max={2}
                        step={0.01}
                        value={rate}
                        onChange={(e) => setRate(Number(e.target.value))}
                      />
                    </div>
                  </div>

                  <div className="mt-3">
                    <div className="mb-1 flex justify-between text-[10px] text-[var(--muted)]">
                      <span>{formatTime(currentTime)}</span>
                      <span>{formatTime(duration)}</span>
                    </div>
                    <input
                      className="slider w-full"
                      type="range"
                      min={0}
                      max={duration || 0}
                      step={0.1}
                      value={currentTime}
                      onChange={(e) => seek(Number(e.target.value))}
                    />
                    <div className="mt-1 h-1 overflow-hidden rounded-full bg-white/5">
                      <div className="h-full bg-[var(--accent)]" style={{ width: `${progress}%` }} />
                    </div>
                  </div>

                  <div className="mt-3 flex gap-2">
                    <input
                      className="min-w-0 flex-1 rounded-xl border border-white/10 bg-black/20 px-3 py-2 text-xs outline-none focus:border-[var(--accent)]"
                      placeholder="Paste YouTube / Spotify / TikTok URL"
                      value={downloadUrl}
                      onChange={(e) => useMusicStore.setState({ downloadUrl: e.target.value })}
                    />
                    <button
                      className="rounded-xl bg-[var(--accent)] px-3 py-2 text-xs font-semibold text-white"
                      onClick={() => void download()}
                    >
                      Get
                    </button>
                  </div>
                  {status ? <div className="mt-2 text-[11px] text-[var(--muted)]">{status}</div> : null}
                </div>
              </div>
            </div>
          </div>
        </div>
      </motion.div>
    </div>
  );
}
