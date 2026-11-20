import { useEffect, useRef, useState } from "react";
import { trackCoverUrl } from "../services/music/musicApi";
import type { PlaylistInfo, TrackInfo } from "../services/music/types";
import { useMusicStore } from "../stores/musicStore";
import { openDonationPage, recenterWindow } from "../services/windowPlacement";
import { AddMusicPanel, type AnchorRect } from "./AddMusicPanel";
import { ConfirmModal } from "./ConfirmModal";
import { EfxPanel } from "./EfxPanel";
import { TextPromptModal } from "./TextPromptModal";
import {
  IconChevron,
  IconClose,
  IconEdit,
  IconLike,
  IconLoop,
  IconMore,
  IconMusic,
  IconNext,
  IconPause,
  IconPin,
  IconPlay,
  IconPlus,
  IconPrev,
  IconRecenter,
  IconShuffle,
  IconTrash,
} from "./icons";

function formatTime(sec: number) {
  if (!Number.isFinite(sec) || sec < 0) return "0:00";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}

export function Dashboard() {
  const [playlistPromptOpen, setPlaylistPromptOpen] = useState(false);
  const [efxOpen, setEfxOpen] = useState(false);
  const [addMusicOpen, setAddMusicOpen] = useState(false);
  const [addMusicPlaylist, setAddMusicPlaylist] = useState<string | null>(null);
  const [addMusicAnchor, setAddMusicAnchor] = useState<AnchorRect | null>(null);
  const [renameTarget, setRenameTarget] = useState<PlaylistInfo | null>(null);
  const [deletePlaylistTarget, setDeletePlaylistTarget] = useState<PlaylistInfo | null>(null);
  const [deleteTrackTarget, setDeleteTrackTarget] = useState<TrackInfo | null>(null);
  const [trackMenuId, setTrackMenuId] = useState<string | null>(null);
  const trackMenuRef = useRef<HTMLDivElement | null>(null);

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
    setDashboardOpen,
    keepDashboardOpen,
    setKeepDashboardOpen,
    togglePlaylistExpanded,
    playTrackAt,
    togglePlay,
    next,
    prev,
    seek,
    setVolume,
    setLoop,
    setShuffle,
    createPlaylist,
    renamePlaylist,
    deletePlaylist,
    deleteTrack,
    moveTrack,
    toggleLike,
    toggleLikePath,
  } = useMusicStore();

  const openAddMusic = (playlistName: string, el: HTMLElement) => {
    const rect = el.getBoundingClientRect();
    setAddMusicPlaylist(playlistName);
    setAddMusicAnchor({
      top: rect.top,
      left: rect.left,
      bottom: rect.bottom,
      right: rect.right,
      width: rect.width,
      height: rect.height,
    });
    setAddMusicOpen(true);
    setEfxOpen(false);
  };

  useEffect(() => {
    if (!trackMenuId) return;
    const onPointer = (e: MouseEvent) => {
      if (!trackMenuRef.current?.contains(e.target as Node)) {
        setTrackMenuId(null);
      }
    };
    window.addEventListener("mousedown", onPointer);
    return () => window.removeEventListener("mousedown", onPointer);
  }, [trackMenuId]);

  const track = index >= 0 ? queue[index] : null;
  const cover = trackCoverUrl(track?.coverPath);
  const vocalsOff = track ? Boolean(vocalsOffByPath[track.filePath]) : false;
  const efxActive =
    effects.bassBoost > 0 || effects.reverb > 0 || vocalsOff || Math.abs(rate - 1) > 0.001;
  const likedNow = track
    ? liked.some((p) => p.toLowerCase() === track.filePath.toLowerCase())
    : false;
  const moveTargets = playlists.filter((p) => p.kind === "normal");

  return (
    <div
      data-dashboard
      className="flex h-full min-h-0 flex-col border border-[var(--border)] bg-[var(--surface)]"
      style={{
        borderRadius: "var(--radius-pill)",
        boxShadow: "2px 4px 16px rgba(0,0,0,0.33)",
      }}
    >
      <div className="flex items-center justify-between px-4 pb-2 pt-3.5">
        <div>
          <div className="text-[17px] font-semibold tracking-tight">Dashboard</div>
          <div className="text-[11px] font-semibold text-[var(--muted)]">
            Playlists · downloads · EFX
          </div>
        </div>
        <div className="flex items-center gap-1">
          <button
            type="button"
            className="ghost-btn"
            title="Recenter"
            onClick={() => void recenterWindow()}
          >
            <IconRecenter />
          </button>
          <button
            type="button"
            className={`ghost-btn ${keepDashboardOpen ? "is-on" : ""}`}
            title={
              keepDashboardOpen
                ? "Auto-collapse off — dashboard stays open until you close it"
                : "Auto-collapse on — dashboard closes when you click away"
            }
            aria-pressed={keepDashboardOpen}
            onClick={() => setKeepDashboardOpen(!keepDashboardOpen)}
          >
            <IconPin />
          </button>
          <button type="button" className="outline-btn" onClick={() => void openDonationPage()}>
            Donate
          </button>
          <button
            type="button"
            className="ghost-btn"
            title="Close"
            onClick={() => setDashboardOpen(false)}
          >
            <IconClose />
          </button>
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-auto px-4">
        <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-[var(--muted)]">
          Playlists
        </div>
        {playlists.map((p) => {
          const open = expandedPlaylist === p.name;
          const canManage = p.kind === "normal";
          return (
            <div key={p.id} className={`playlist-card ${open ? "is-open" : ""}`}>
              <div className="flex w-full items-center gap-2">
                <button
                  type="button"
                  className="flex min-w-0 flex-1 items-center gap-3 text-left"
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
                </button>

                {canManage ? (
                  <div className="flex shrink-0 items-center gap-0.5">
                    <button
                      type="button"
                      className="row-icon-btn"
                      title="Rename playlist"
                      onClick={(e) => {
                        e.stopPropagation();
                        setRenameTarget(p);
                      }}
                    >
                      <IconEdit />
                    </button>
                    <button
                      type="button"
                      className="row-icon-btn danger"
                      title="Delete playlist"
                      onClick={(e) => {
                        e.stopPropagation();
                        setDeletePlaylistTarget(p);
                      }}
                    >
                      <IconTrash />
                    </button>
                    <button
                      type="button"
                      className="row-icon-btn"
                      title="Add music"
                      onClick={(e) => {
                        e.stopPropagation();
                        openAddMusic(p.name, e.currentTarget);
                      }}
                    >
                      <IconPlus />
                    </button>
                  </div>
                ) : null}

                <button
                  type="button"
                  className="row-icon-btn"
                  title={open ? "Collapse" : "Expand"}
                  onClick={() => void togglePlaylistExpanded(p.name)}
                >
                  <span className={`transition-transform ${open ? "rotate-180" : ""}`}>
                    <IconChevron />
                  </span>
                </button>
              </div>

              {open ? (
                <div className="mt-2 max-h-[360px] overflow-auto">
                  {tracks.length === 0 ? (
                    canManage ? (
                      <button
                        type="button"
                        className="dashed-btn"
                        style={{ marginTop: 4 }}
                        onClick={(e) => openAddMusic(p.name, e.currentTarget)}
                      >
                        + Add music
                      </button>
                    ) : (
                      <div className="px-2 py-3 text-xs text-[var(--muted)]">No liked songs yet.</div>
                    )
                  ) : (
                    <>
                      {tracks.map((t, i) => {
                        const isCurrent =
                          track?.filePath.toLowerCase() === t.filePath.toLowerCase();
                        const rowCover = trackCoverUrl(t.coverPath);
                        const menuOpen = trackMenuId === t.id;
                        const isLiked = liked.some(
                          (path) => path.toLowerCase() === t.filePath.toLowerCase(),
                        );
                        const destinations = moveTargets.filter(
                          (dest) => dest.name.toLowerCase() !== t.playlist.toLowerCase(),
                        );
                        return (
                          <div
                            key={t.id}
                            className={`track-row relative ${isCurrent ? "is-current" : ""} ${menuOpen ? "is-menu-open" : ""}`}
                          >
                            <button
                              type="button"
                              className="flex min-w-0 flex-1 items-center gap-2.5 text-left"
                              onClick={() => void playTrackAt(i, tracks)}
                            >
                              <div
                                className="flex h-10 w-10 shrink-0 items-center justify-center overflow-hidden bg-white"
                                style={{ borderRadius: 8 }}
                              >
                                {rowCover ? (
                                  <img
                                    src={rowCover}
                                    alt=""
                                    className="h-full w-full object-cover"
                                    draggable={false}
                                  />
                                ) : (
                                  <span className="text-[var(--pill-icon)]">
                                    <IconMusic size={16} />
                                  </span>
                                )}
                              </div>
                              <div className="min-w-0 flex-1">
                                <div className="truncate text-[13px] font-semibold">{t.title}</div>
                                <div className="truncate text-xs text-[var(--text-dim)]">
                                  {t.artist}
                                </div>
                              </div>
                            </button>

                            <button
                              type="button"
                              className="row-icon-btn"
                              title="Track options"
                              onClick={(e) => {
                                e.stopPropagation();
                                setTrackMenuId(menuOpen ? null : t.id);
                              }}
                            >
                              <IconMore />
                            </button>

                            {menuOpen ? (
                              <div ref={trackMenuRef} className="track-menu">
                                <button
                                  type="button"
                                  className="track-menu-item"
                                  onClick={() => {
                                    void toggleLikePath(t.filePath);
                                    setTrackMenuId(null);
                                  }}
                                >
                                  {isLiked ? "Remove from Liked" : "Add to Liked Songs"}
                                </button>
                                {destinations.length > 0 ? (
                                  <>
                                    <div className="track-menu-sep" />
                                    <div className="track-menu-label">Move to playlist</div>
                                    {destinations.map((dest) => (
                                      <button
                                        key={dest.id}
                                        type="button"
                                        className="track-menu-item"
                                        onClick={() => {
                                          void moveTrack(t.filePath, dest.name);
                                          setTrackMenuId(null);
                                        }}
                                      >
                                        {dest.name}
                                      </button>
                                    ))}
                                  </>
                                ) : null}
                                <div className="track-menu-sep" />
                                <button
                                  type="button"
                                  className="track-menu-item danger"
                                  onClick={() => {
                                    setDeleteTrackTarget(t);
                                    setTrackMenuId(null);
                                  }}
                                >
                                  Delete from playlist
                                </button>
                              </div>
                            ) : null}
                          </div>
                        );
                      })}
                      {canManage ? (
                        <button
                          type="button"
                          className="dashed-btn"
                          onClick={(e) => openAddMusic(p.name, e.currentTarget)}
                        >
                          + Add music
                        </button>
                      ) : null}
                    </>
                  )}
                </div>
              ) : null}
            </div>
          );
        })}

        <button
          type="button"
          className="dashed-btn"
          onClick={() => setPlaylistPromptOpen(true)}
        >
          + New playlist
        </button>
      </div>

      <TextPromptModal
        open={playlistPromptOpen}
        title="New playlist"
        label="Playlist name"
        placeholder="e.g. Focus, Gym, Late night"
        confirmLabel="Create"
        onCancel={() => setPlaylistPromptOpen(false)}
        onConfirm={(name) => {
          setPlaylistPromptOpen(false);
          void createPlaylist(name);
        }}
      />

      <TextPromptModal
        open={Boolean(renameTarget)}
        title="Rename playlist"
        label="Playlist name"
        initialValue={renameTarget?.name ?? ""}
        confirmLabel="Rename"
        onCancel={() => setRenameTarget(null)}
        onConfirm={(name) => {
          const current = renameTarget;
          setRenameTarget(null);
          if (current) void renamePlaylist(current.name, name);
        }}
      />

      <ConfirmModal
        open={Boolean(deletePlaylistTarget)}
        title="Delete playlist"
        message={
          deletePlaylistTarget
            ? `Delete “${deletePlaylistTarget.name}” and its copied tracks from the library?`
            : ""
        }
        confirmLabel="Delete"
        onCancel={() => setDeletePlaylistTarget(null)}
        onConfirm={() => {
          const target = deletePlaylistTarget;
          setDeletePlaylistTarget(null);
          if (target) void deletePlaylist(target.name);
        }}
      />

      <ConfirmModal
        open={Boolean(deleteTrackTarget)}
        title="Delete track"
        message={
          deleteTrackTarget
            ? `Remove “${deleteTrackTarget.title}” from this playlist?`
            : ""
        }
        confirmLabel="Delete"
        onCancel={() => setDeleteTrackTarget(null)}
        onConfirm={() => {
          const target = deleteTrackTarget;
          setDeleteTrackTarget(null);
          if (target) void deleteTrack(target.filePath);
        }}
      />

      <AddMusicPanel
        open={addMusicOpen}
        playlistName={addMusicPlaylist}
        anchor={addMusicAnchor}
        onClose={() => {
          setAddMusicOpen(false);
          setAddMusicAnchor(null);
        }}
      />

      <div className="border-t border-[var(--border)] px-4 pb-4 pt-3">
        <div className="mb-3 flex items-center justify-between gap-3">
          <div className="efx-anchor relative shrink-0">
            <button
              type="button"
              className={`efx-btn ${efxActive || efxOpen ? "is-active" : ""}`}
              title="Open effects"
              onClick={() => {
                setEfxOpen((v) => !v);
                setAddMusicOpen(false);
              }}
            >
              EFX
            </button>
            <EfxPanel open={efxOpen} onClose={() => setEfxOpen(false)} />
          </div>
          <div className="min-w-0 flex-1">
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

        <div className="grid grid-cols-[minmax(140px,1fr)_auto_minmax(160px,0.9fr)] items-center gap-3">
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

          <div className="min-w-0">
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
        </div>
        {status ? (
          <div className="mt-2 text-[11px] text-[var(--text-dim)]">{status}</div>
        ) : null}
      </div>
    </div>
  );
}
