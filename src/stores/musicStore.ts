import { create } from "zustand";
import { audioEngine } from "../services/music/audioEngine";
import * as api from "../services/music/musicApi";
import type {
  MusicEffects,
  PlaylistInfo,
  TrackInfo,
} from "../services/music/types";

type MusicState = {
  ready: boolean;
  dashboardOpen: boolean;
  hoverExpanded: boolean;
  playlists: PlaylistInfo[];
  activePlaylist: string | null;
  expandedPlaylist: string | null;
  tracks: TrackInfo[];
  queue: TrackInfo[];
  index: number;
  playing: boolean;
  currentTime: number;
  duration: number;
  volume: number;
  rate: number;
  loop: boolean;
  shuffle: boolean;
  effects: MusicEffects;
  vocalsOffByPath: Record<string, boolean>;
  liked: string[];
  status: string | null;
  downloadUrl: string;
  /** When true, clicking away does not close the dashboard. */
  keepDashboardOpen: boolean;
  /** Native dialogs increment this so a focus loss does not auto-collapse. */
  collapseHold: number;
  hydrate: () => Promise<void>;
  setDashboardOpen: (open: boolean) => void;
  setKeepDashboardOpen: (on: boolean) => void;
  pushCollapseHold: () => () => void;
  setHoverExpanded: (open: boolean) => void;
  setDownloadUrl: (url: string) => void;
  selectPlaylist: (name: string) => Promise<void>;
  togglePlaylistExpanded: (name: string) => Promise<void>;
  playTrackAt: (index: number, queue?: TrackInfo[]) => Promise<void>;
  togglePlay: () => Promise<void>;
  next: () => Promise<void>;
  prev: () => Promise<void>;
  seek: (seconds: number) => void;
  setVolume: (v: number) => void;
  setRate: (v: number) => void;
  setBass: (level: number) => void;
  setReverb: (amount: number) => void;
  setLoop: (on: boolean) => void;
  setShuffle: (on: boolean) => void;
  toggleVocals: () => Promise<void>;
  createPlaylist: (name: string) => Promise<void>;
  renamePlaylist: (name: string, newName: string) => Promise<void>;
  deletePlaylist: (name: string) => Promise<void>;
  deleteTrack: (filePath: string) => Promise<void>;
  moveTrack: (filePath: string, playlist: string) => Promise<void>;
  download: (playlist?: string) => Promise<void>;
  importSources: (playlist: string, sources: string[]) => Promise<void>;
  toggleLike: () => Promise<void>;
  toggleLikePath: (filePath: string) => Promise<void>;
};

function resolvePlayPath(track: TrackInfo, vocalsOff: boolean) {
  if (!vocalsOff) return track.filePath;
  const stem = track.filePath.replace(/\.[^.]+$/, ".instrumental.wav");
  return stem;
}

let playbackGen = 0;

const KEEP_OPEN_KEY = "musicwidget.keepDashboardOpen";

function readKeepDashboardOpen() {
  try {
    return localStorage.getItem(KEEP_OPEN_KEY) === "1";
  } catch {
    return false;
  }
}

export const useMusicStore = create<MusicState>((set, get) => {
  audioEngine.setHandlers({
    onTime: (current, duration) => set({ currentTime: current, duration }),
    onPlayState: (playing) => set({ playing }),
    onEnded: () => {
      void get().next();
    },
    onError: (message) => set({ status: message }),
  });

  return {
    ready: false,
    dashboardOpen: false,
    hoverExpanded: false,
    playlists: [],
    activePlaylist: null,
    expandedPlaylist: null,
    tracks: [],
    queue: [],
    index: -1,
    playing: false,
    currentTime: 0,
    duration: 0,
    volume: 1,
    rate: 1,
    loop: false,
    shuffle: false,
    effects: { bassBoost: 0, reverb: 0 },
    vocalsOffByPath: {},
    liked: [],
    status: null,
    downloadUrl: "",
    keepDashboardOpen: readKeepDashboardOpen(),
    collapseHold: 0,

    hydrate: async () => {
      try {
        const snap = await api.getMusicSnapshot();
        const playlists = snap.playlists;
        const active =
          playlists.find((p) => p.kind === "normal")?.name ??
          playlists[0]?.name ??
          null;
        let tracks: TrackInfo[] = [];
        if (active) {
          tracks = await api.listTracks(active);
        }
        set({
          ready: true,
          playlists,
          activePlaylist: active,
          expandedPlaylist: active,
          tracks,
          liked: snap.liked,
          status: null,
        });
      } catch (err) {
        set({
          ready: true,
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    setDashboardOpen: (open) =>
      set({ dashboardOpen: open, hoverExpanded: open || get().hoverExpanded }),
    setKeepDashboardOpen: (on) => {
      try {
        localStorage.setItem(KEEP_OPEN_KEY, on ? "1" : "0");
      } catch {
        /* private mode */
      }
      set({ keepDashboardOpen: on });
    },
    pushCollapseHold: () => {
      set((s) => ({ collapseHold: s.collapseHold + 1 }));
      return () => {
        set((s) => ({ collapseHold: Math.max(0, s.collapseHold - 1) }));
      };
    },
    setHoverExpanded: (open) => set({ hoverExpanded: open }),
    setDownloadUrl: (url) => set({ downloadUrl: url }),

    selectPlaylist: async (name) => {
      const tracks = await api.listTracks(name);
      set({ activePlaylist: name, expandedPlaylist: name, tracks });
    },

    togglePlaylistExpanded: async (name) => {
      const { expandedPlaylist } = get();
      if (expandedPlaylist === name) {
        set({ expandedPlaylist: null });
        return;
      }
      const tracks = await api.listTracks(name);
      set({ activePlaylist: name, expandedPlaylist: name, tracks });
    },

    playTrackAt: async (index, queue) => {
      const q = queue ?? get().queue;
      if (!q.length || index < 0 || index >= q.length) return;
      const gen = ++playbackGen;
      const track = q[index];
      const vocalsOff = Boolean(get().vocalsOffByPath[track.filePath]);
      let path = track.filePath;
      if (vocalsOff) {
        try {
          const has = await api.hasInstrumental(track.filePath);
          if (has) path = resolvePlayPath(track, true);
          else {
            set({ status: "Removing vocals (MDX HQ)…" });
            path = await api.removeVocals(track.filePath);
          }
        } catch (err) {
          set({
            status: err instanceof Error ? err.message : String(err),
          });
          path = track.filePath;
        }
      }
      if (gen !== playbackGen) return;
      set({ queue: q, index, status: null });
      await audioEngine.load(path);
      if (gen !== playbackGen) return;
      audioEngine.setVolume(get().volume);
      audioEngine.setRate(get().rate);
      audioEngine.setEffects(get().effects);
      audioEngine.setLoop(get().loop);
      await audioEngine.play();
    },

    togglePlay: async () => {
      const { queue, index, tracks } = get();
      if (index < 0 || !queue.length) {
        if (!tracks.length) return;
        await get().playTrackAt(0, tracks);
        return;
      }
      await audioEngine.toggle();
    },

    next: async () => {
      const { queue, index, shuffle, loop } = get();
      if (!queue.length) return;
      if (loop && queue[index]) {
        await get().playTrackAt(index);
        return;
      }
      let next = index + 1;
      if (shuffle && queue.length > 1) {
        next = Math.floor(Math.random() * (queue.length - 1));
        if (next >= index) next += 1;
      }
      if (next >= queue.length) next = 0;
      await get().playTrackAt(next);
    },

    prev: async () => {
      const { queue, index } = get();
      if (!queue.length) return;
      const prev = (index - 1 + queue.length) % queue.length;
      await get().playTrackAt(prev);
    },

    seek: (seconds) => audioEngine.seek(seconds),

    setVolume: (v) => {
      const volume = Math.min(1, Math.max(0, v));
      audioEngine.setVolume(volume);
      set({ volume });
    },

    setRate: (v) => {
      const rate = Math.min(2, Math.max(0.5, v));
      audioEngine.setRate(rate);
      set({ rate });
    },

    setBass: (level) => {
      const effects = {
        ...get().effects,
        bassBoost: Math.min(3, Math.max(0, Math.round(level))),
      };
      audioEngine.setEffects(effects);
      set({ effects });
    },

    setReverb: (amount) => {
      const effects = {
        ...get().effects,
        reverb: Math.min(1, Math.max(0, amount)),
      };
      audioEngine.setEffects(effects);
      set({ effects });
    },

    setLoop: (on) => {
      audioEngine.setLoop(on);
      set({ loop: on });
    },

    setShuffle: (on) => set({ shuffle: on }),

    toggleVocals: async () => {
      const { queue, index, vocalsOffByPath, currentTime, playing } = get();
      const track = queue[index];
      if (!track) {
        set({ status: "Play a track first to toggle vocals." });
        return;
      }
      const nextOff = !vocalsOffByPath[track.filePath];
      const map = { ...vocalsOffByPath, [track.filePath]: nextOff };
      set({ vocalsOffByPath: map });

      try {
        let path = track.filePath;
        if (nextOff) {
          set({ status: "Preparing instrumental…" });
          const has = await api.hasInstrumental(track.filePath);
          path = has
            ? resolvePlayPath(track, true)
            : await api.removeVocals(track.filePath);
        }
        set({ status: null });
        await audioEngine.replaceSource(path);
        audioEngine.seek(currentTime);
        if (playing) await audioEngine.play();
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    createPlaylist: async (name) => {
      await api.createPlaylist(name);
      const playlists = await api.listPlaylists();
      set({ playlists, activePlaylist: name });
      await get().selectPlaylist(name);
    },

    renamePlaylist: async (name, newName) => {
      try {
        const renamed = await api.renamePlaylist(name, newName);
        const playlists = await api.listPlaylists();
        const { activePlaylist, expandedPlaylist, queue } = get();
        set({
          playlists,
          activePlaylist: activePlaylist === name ? renamed.name : activePlaylist,
          expandedPlaylist:
            expandedPlaylist === name ? renamed.name : expandedPlaylist,
          queue: queue.map((t) =>
            t.playlist === name ? { ...t, playlist: renamed.name } : t,
          ),
          status: `Renamed to “${renamed.name}”.`,
        });
        if (get().expandedPlaylist === renamed.name) {
          await get().selectPlaylist(renamed.name);
        }
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    deletePlaylist: async (name) => {
      try {
        await api.deletePlaylist(name);
        const playlists = await api.listPlaylists();
        const { activePlaylist, expandedPlaylist, queue, index } = get();
        const nextActive =
          activePlaylist === name
            ? (playlists.find((p) => p.kind === "normal")?.name ??
              playlists[0]?.name ??
              null)
            : activePlaylist;
        const nextExpanded = expandedPlaylist === name ? nextActive : expandedPlaylist;
        const nextQueue =
          activePlaylist === name
            ? []
            : queue.filter((t) => t.playlist !== name);
        set({
          playlists,
          activePlaylist: nextActive,
          expandedPlaylist: nextExpanded,
          tracks: [],
          queue: nextQueue,
          index: nextQueue.length ? Math.min(index, nextQueue.length - 1) : -1,
          status: `Deleted “${name}”.`,
        });
        if (nextExpanded) await get().selectPlaylist(nextExpanded);
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    deleteTrack: async (filePath) => {
      try {
        await api.deleteTrack(filePath);
        const { activePlaylist, queue, index } = get();
        const tracks = activePlaylist
          ? await api.listTracks(activePlaylist)
          : [];
        const playlists = await api.listPlaylists();
        const nextQueue = queue.filter(
          (t) => t.filePath.toLowerCase() !== filePath.toLowerCase(),
        );
        let nextIndex = index;
        if (queue[index]?.filePath.toLowerCase() === filePath.toLowerCase()) {
          nextIndex = nextQueue.length ? Math.min(index, nextQueue.length - 1) : -1;
          if (nextIndex >= 0) await get().playTrackAt(nextIndex, nextQueue);
          else {
            audioEngine.pause();
            set({ playing: false, currentTime: 0, duration: 0 });
          }
        }
        set({
          tracks,
          playlists,
          queue: nextQueue,
          index: nextIndex,
          status: "Track deleted.",
        });
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    moveTrack: async (filePath, playlist) => {
      try {
        await api.moveTrack(filePath, playlist);
        const { activePlaylist, queue, index } = get();
        const tracks = activePlaylist
          ? await api.listTracks(activePlaylist)
          : [];
        const playlists = await api.listPlaylists();
        const nextQueue = queue.filter(
          (t) => t.filePath.toLowerCase() !== filePath.toLowerCase(),
        );
        let nextIndex = index;
        if (queue[index]?.filePath.toLowerCase() === filePath.toLowerCase()) {
          nextIndex = nextQueue.length ? Math.min(index, nextQueue.length - 1) : -1;
        }
        set({
          tracks,
          playlists,
          queue: nextQueue,
          index: nextIndex,
          status: `Moved to “${playlist}”.`,
        });
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    download: async (playlist) => {
      const { downloadUrl, activePlaylist } = get();
      const target = playlist ?? activePlaylist;
      if (!downloadUrl.trim() || !target) return;
      set({ status: "Downloading…", activePlaylist: target, expandedPlaylist: target });
      try {
        await api.ensureTools();
        const tracks = await api.downloadTrack(downloadUrl.trim(), target);
        const playlists = await api.listPlaylists();
        set({
          tracks,
          playlists,
          downloadUrl: "",
          status: "Download complete.",
        });
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    importSources: async (playlist, sources) => {
      if (!sources.length) return;
      set({ status: "Importing…" });
      try {
        const tracks = await api.importMusicFiles(playlist, sources);
        const playlists = await api.listPlaylists();
        set({
          tracks,
          playlists,
          activePlaylist: playlist,
          expandedPlaylist: playlist,
          status: `Added ${tracks.length} track${tracks.length === 1 ? "" : "s"}.`,
        });
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    toggleLike: async () => {
      const { queue, index } = get();
      const track = queue[index];
      if (!track) return;
      await get().toggleLikePath(track.filePath);
    },

    toggleLikePath: async (filePath) => {
      const { liked, activePlaylist, expandedPlaylist } = get();
      const has = liked.some((p) => p.toLowerCase() === filePath.toLowerCase());
      const next = has
        ? liked.filter((p) => p.toLowerCase() !== filePath.toLowerCase())
        : [...liked, filePath];
      await api.setLiked(next);
      set({ liked: next });
      const viewingLiked = [activePlaylist, expandedPlaylist].some(
        (name) =>
          !!name &&
          (name === "__liked__" || name.toLowerCase() === "liked songs"),
      );
      if (viewingLiked) {
        const name = expandedPlaylist ?? activePlaylist;
        if (name) {
          const tracks = await api.listTracks(name);
          const playlists = await api.listPlaylists();
          set({ tracks, playlists });
        }
      } else {
        const playlists = await api.listPlaylists();
        set({ playlists });
      }
    },
  };
});
