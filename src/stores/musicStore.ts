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
  hydrate: () => Promise<void>;
  setDashboardOpen: (open: boolean) => void;
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
  download: () => Promise<void>;
  toggleLike: () => Promise<void>;
};

function resolvePlayPath(track: TrackInfo, vocalsOff: boolean) {
  if (!vocalsOff) return track.filePath;
  const stem = track.filePath.replace(/\.[^.]+$/, ".instrumental.wav");
  return stem;
}

let playbackGen = 0;

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

    download: async () => {
      const { downloadUrl, activePlaylist } = get();
      if (!downloadUrl.trim() || !activePlaylist) return;
      set({ status: "Downloading…" });
      try {
        await api.ensureTools();
        const tracks = await api.downloadTrack(downloadUrl.trim(), activePlaylist);
        set({ tracks, downloadUrl: "", status: "Download complete." });
      } catch (err) {
        set({
          status: err instanceof Error ? err.message : String(err),
        });
      }
    },

    toggleLike: async () => {
      const { queue, index, liked } = get();
      const track = queue[index];
      if (!track) return;
      const has = liked.some(
        (p) => p.toLowerCase() === track.filePath.toLowerCase(),
      );
      const next = has
        ? liked.filter((p) => p.toLowerCase() !== track.filePath.toLowerCase())
        : [...liked, track.filePath];
      await api.setLiked(next);
      set({ liked: next });
    },
  };
});
