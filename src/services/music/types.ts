export type PlaylistKind = "liked" | "normal";

export type PlaylistInfo = {
  id: string;
  name: string;
  folderPath: string;
  trackCount: number;
  kind: PlaylistKind;
};

export type TrackInfo = {
  id: string;
  title: string;
  artist: string;
  filePath: string;
  fileName: string;
  playlist: string;
  coverPath?: string | null;
};

export type ToolsStatus = {
  ytDlp: boolean;
  ffmpeg: boolean;
  toolsDir: string;
};

export type LibraryPaths = {
  libraryRoot: string;
  appData: string;
  toolsDir: string;
  likedFile: string;
};

export type MusicLibrarySnapshot = {
  paths: LibraryPaths;
  playlists: PlaylistInfo[];
  liked: string[];
  tools: ToolsStatus;
};

export type DownloadProgress = {
  phase: string;
  percent: number | null;
  message: string;
};

export type MusicEffects = {
  bassBoost: number; // 0–3
  /** Wet amount 0–1 (0 = off). */
  reverb: number;
};
