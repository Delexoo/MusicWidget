import { trackCoverUrl } from "../services/music/musicApi";
import { useMusicStore } from "../stores/musicStore";
import {
  IconChevron,
  IconClose,
  IconLike,
  IconLoop,
  IconMusic,
  IconNext,
  IconPause,
  IconPlay,
  IconPrev,
  IconShuffle,
} from "./icons";

function formatTime(sec: number) {
  if (!Number.isFinite(sec) || sec < 0) return "0:00";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}

export function Dashboard() {
  const {
    playlists,
    expandedPlaylist,
    tracks,
    queue,
    index,
    playing,
    currentTime,
    duration,
    volume,
    rate,
    loop,
    shuffle,
    effects,
    vocalsOffByPath,
    liked,
    status,
    downloadUrl,
    setDashboardOpen,
    togglePlaylistExpanded,
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
    setShuffle,
    toggleVocals,
    createPlaylist,
    download,
    toggleLike,
    setDownloadUrl,
  } = useMusicStore();

  const track = index >= 0 ? queue[index] : null;
  const cover = trackCoverUrl(track?.coverPath);
  const vocalsOff = track ? Boolean(vocalsOffByPath[track.filePath]) : false;
  const likedNow = track
    ? liked.some((p) => p.toLowerCase() === track.filePath.toLowerCase())
    : false;

  return (
    <div
      className="flex h-full min-h-0 flex-col border border-[var(--border)] bg-[var(--surface)]"
      style={{
        borderRadius: "var(--radius-pill)",
        boxShadow: "2px 4px 16px rgba(0,0,0,0.33)",
      }}
    >
      <div className="flex items-start justify-between px-4 pb-2 pt-3.5">
        <div>
          <div className="text-[17px] font-semibold tracking-tight">Dashboard</div>
          <div className="text-[11px] font-semibold text-[var(--muted)]">
            Playlists · downloads · EFX
          </div>
        </div>
        <button
          type="button"
          className="ghost-btn"
          title="Close"
          onClick={() => setDashboardOpen(false)}
        >
          <IconClose />
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-auto px-4">
        <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-[var(--muted)]">
          Playlists
        </div>
        {playlists.map((p) => {
          const open = expandedPlaylist === p.name;
          return (
            <div key={p.id} className={`playlist-card ${open ? "is-open" : ""}`}>
              <button
                type="button"
                className="flex w-full items-center gap-3 text-left"
                onClick={() => void togglePlaylistExpanded(p.name)}
              >
                <div
                  className="flex h-11 w-11 shrink-0 items-center justify-center bg-[var(--surface-high)] text-[var(--text-dim)]"
                  style={{ borderRadius: "var(--radius-card)" }}
                >
                  <IconMusic size={18} />
                </div>
                <div className="min-w-0 flex-1">
                  <div className="truncate text-sm font-semibold">{p.name}</div>
                  <div className="text-[11px] text-[var(--muted)]">{p.trackCount} tracks</div>
                </div>
                <span className={`text-[var(--muted)] transition-transform ${open ? "rotate-180" : ""}`}>
                  <IconChevron />
                </span>
              </button>

              {open ? (
                <div className="mt-2 max-h-[360px] overflow-auto">
                  {tracks.length === 0 ? (
                    <div className="px-2 py-3 text-xs text-[var(--muted)]">No tracks yet.</div>
                  ) : (
                    tracks.map((t, i) => {
                      const isCurrent =
                        track?.filePath.toLowerCase() === t.filePath.toLowerCase();
                      const rowCover = trackCoverUrl(t.coverPath);
                      return (
                        <button
                          key={t.id}
                          type="button"
                          className={`track-row ${isCurrent ? "is-current" : ""}`}
                          onClick={() => void playTrackAt(i, tracks)}
                        >
                          <div
                            className="flex h-10 w-10 shrink-0 items-center justify-center overflow-hidden bg-white"
                            style={{ borderRadius: 8 }}
                          >
                            {rowCover ? (
                              <img src={rowCover} alt="" className="h-full w-full object-cover" draggable={false} />
                            ) : (
                              <span className="text-[var(--pill-icon)]">
                                <IconMusic size={16} />
                              </span>
                            )}
                          </div>
                          <div className="min-w-0 flex-1">
                            <div className="truncate text-[13px] font-semibold">{t.title}</div>
                            <div className="truncate text-xs text-[var(--text-dim)]">{t.artist}</div>
                          </div>
                        </button>
                      );
                    })
                  )}
                </div>
              ) : null}
            </div>
          );
        })}

        <button
          type="button"
          className="dashed-btn"
          onClick={() => {
            const name = window.prompt("Playlist name");
            if (name?.trim()) void createPlaylist(name.trim());
          }}
        >
          + New playlist
        </button>
      </div>

      <div className="border-t border-[var(--border)] px-4 pb-4 pt-3">
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <span className="mr-1 text-[11px] font-semibold uppercase tracking-wide text-[var(--muted)]">
            Effects
          </span>
          <button
            type="button"
            className={`effect-chip ${effects.bassBoost > 0 ? "is-on" : ""}`}
            onClick={() => setBass((effects.bassBoost + 1) % 4)}
          >
            {effects.bassBoost === 0 ? "Bass off" : `Bass ${effects.bassBoost}`}
          </button>
          <div className="flex min-w-[160px] flex-1 items-center gap-2">
            <span className="whitespace-nowrap text-xs text-[var(--text-dim)]">
              Reverb {Math.round(effects.reverb * 100)}%
            </span>
            <input
              className="slider"
              type="range"
              min={0}
              max={1}
              step={0.01}
              value={effects.reverb}
              onChange={(e) => setReverb(Number(e.target.value))}
            />
          </div>
          <button
            type="button"
            className={`effect-chip ${vocalsOff ? "is-on" : ""}`}
            onClick={() => void toggleVocals()}
          >
            {vocalsOff ? "Vocals off" : "Vocals on"}
          </button>
        </div>

        <div className="grid grid-cols-[minmax(140px,1fr)_auto_minmax(250px,1.2fr)] items-center gap-3">
          <div className="flex min-w-0 items-center gap-3">
            <div
              className="flex h-[52px] w-[52px] shrink-0 items-center justify-center overflow-hidden bg-white"
              style={{ borderRadius: "var(--radius-card)" }}
            >
              {cover ? (
                <img src={cover} alt="" className="h-full w-full object-cover" draggable={false} />
              ) : (
                <span className="text-[var(--pill-icon)]">
                  <IconMusic size={20} />
                </span>
              )}
            </div>
            <div className="min-w-0 flex-1">
              <div className="truncate text-[13.5px] font-semibold">
                {track?.title ?? "Nothing playing"}
              </div>
              <div className="truncate text-xs text-[var(--text-dim)]">
                {track?.artist ?? "—"}
              </div>
            </div>
            <button
              type="button"
              className={`ghost-btn ${likedNow ? "text-[var(--danger)]" : ""}`}
              title="Like"
              onClick={() => void toggleLike()}
            >
              <IconLike />
            </button>
          </div>

          <div className="flex items-center gap-1">
            <button
              type="button"
              className={`ghost-btn ${shuffle ? "is-on" : ""}`}
              title="Shuffle"
              onClick={() => setShuffle(!shuffle)}
            >
              <IconShuffle />
            </button>
            <button type="button" className="ghost-btn" title="Previous" onClick={() => void prev()}>
              <IconPrev size={14} />
            </button>
            <button
              type="button"
              className="blue-circle"
              title={playing ? "Pause" : "Play"}
              onClick={() => void togglePlay()}
            >
              {playing ? <IconPause size={16} /> : <IconPlay size={16} />}
            </button>
            <button type="button" className="ghost-btn" title="Next" onClick={() => void next()}>
              <IconNext size={14} />
            </button>
            <button
              type="button"
              className={`ghost-btn ${loop ? "is-on" : ""}`}
              title="Loop"
              onClick={() => setLoop(!loop)}
            >
              <IconLoop size={14} />
            </button>
          </div>

          <div className="min-w-0 space-y-2">
            <div className="grid grid-cols-2 gap-3">
              <div>
                <div className="mb-1 text-[10.5px] text-[var(--muted)]">
                  Volume {Math.round(volume * 100)}%
                </div>
                <input
                  className="slider"
                  type="range"
                  min={0}
                  max={1}
                  step={0.01}
                  value={volume}
                  onChange={(e) => setVolume(Number(e.target.value))}
                />
              </div>
              <div>
                <div className="mb-1 text-[10.5px] text-[var(--muted)]">
                  Speed {rate.toFixed(2)}x
                </div>
                <input
                  className="slider"
                  type="range"
                  min={0.5}
                  max={2}
                  step={0.01}
                  value={rate}
                  onChange={(e) => setRate(Number(e.target.value))}
                />
              </div>
            </div>
            <div>
              <div className="mb-1 flex justify-between font-mono text-[10.5px] text-[var(--muted)]">
                <span>{formatTime(currentTime)}</span>
                <span>{formatTime(duration)}</span>
              </div>
              <input
                className="slider"
                type="range"
                min={0}
                max={duration || 0}
                step={0.1}
                value={Math.min(currentTime, duration || 0)}
                onChange={(e) => seek(Number(e.target.value))}
              />
            </div>
          </div>
        </div>

        <div className="mt-3 flex gap-2">
          <input
            className="min-w-0 flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-xs text-[var(--text)] outline-none placeholder:text-[var(--muted)] focus:border-[var(--blue)]"
            placeholder="Paste YouTube / Spotify / TikTok URL"
            value={downloadUrl}
            onChange={(e) => setDownloadUrl(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") void download();
            }}
          />
          <button
            type="button"
            className="rounded-lg bg-[var(--blue)] px-3 py-2 text-xs font-semibold text-white hover:bg-[var(--blue-hover)]"
            onClick={() => void download()}
          >
            Download
          </button>
        </div>
        {status ? (
          <div className="mt-2 text-[11px] text-[var(--text-dim)]">{status}</div>
        ) : null}
      </div>
    </div>
  );
}
