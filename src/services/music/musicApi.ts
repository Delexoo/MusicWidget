import { convertFileSrc, invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type {
  DownloadProgress,
  MusicLibrarySnapshot,
  PlaylistInfo,
  ToolsStatus,
  TrackInfo,
} from "./types";

export async function getMusicSnapshot(): Promise<MusicLibrarySnapshot> {
  return invoke<MusicLibrarySnapshot>("music_get_snapshot");
}

export async function listPlaylists(): Promise<PlaylistInfo[]> {
  return invoke<PlaylistInfo[]>("music_list_playlists");
}

export async function createPlaylist(name: string): Promise<PlaylistInfo> {
  return invoke<PlaylistInfo>("music_create_playlist", { name });
}

export async function deletePlaylist(playlist: string): Promise<void> {
  return invoke("music_delete_playlist", { playlist });
}

export async function renamePlaylist(
  playlist: string,
  newName: string,
): Promise<PlaylistInfo> {
  return invoke<PlaylistInfo>("music_rename_playlist", { playlist, newName });
}

export async function deleteTrack(filePath: string): Promise<void> {
  return invoke("music_delete_track", { filePath });
}

export type MovedTrack = {
  track: TrackInfo;
  sourceRemoved: boolean;
  sourcePath: string;
};

export async function moveTrack(
  filePath: string,
  playlist: string,
): Promise<MovedTrack> {
  return invoke<MovedTrack>("music_move_track", { filePath, playlist });
}

export async function listTracks(playlist: string): Promise<TrackInfo[]> {
  return invoke<TrackInfo[]>("music_list_tracks", { playlist });
}

export async function getLiked(): Promise<string[]> {
  return invoke<string[]>("music_get_liked");
}

export async function setLiked(pathsList: string[]): Promise<void> {
  return invoke("music_set_liked", { pathsList });
}

export async function ensureTools(): Promise<ToolsStatus> {
  return invoke<ToolsStatus>("music_ensure_tools");
}

export async function downloadTrack(
  url: string,
  playlist: string,
): Promise<TrackInfo[]> {
  return invoke<TrackInfo[]>("music_download", { url, playlist });
}

export async function openLibraryFolder(): Promise<string> {
  return invoke<string>("music_open_library_folder");
}

export async function importMusicFiles(
  playlist: string,
  sources: string[],
): Promise<TrackInfo[]> {
  return invoke<TrackInfo[]>("music_import_files", { playlist, sources });
}

export function trackPlaybackUrl(filePath: string): string {
  try {
    return convertFileSrc(filePath);
  } catch {
    return filePath;
  }
}

export function trackCoverUrl(coverPath?: string | null): string | null {
  if (!coverPath) return null;
  try {
    return convertFileSrc(coverPath);
  } catch {
    return null;
  }
}

export async function onDownloadProgress(
  handler: (progress: DownloadProgress) => void,
): Promise<UnlistenFn> {
  return listen<DownloadProgress>("music://download-progress", (event) => {
    handler(event.payload);
  });
}

export type VocalToolsStatus = {
  ready: boolean;
  model: boolean;
  python: boolean;
  separator: boolean;
  toolsDir: string;
  message: string;
};

export async function getVocalStatus(): Promise<VocalToolsStatus> {
  return invoke<VocalToolsStatus>("music_vocal_status");
}

export async function ensureVocalTools(): Promise<VocalToolsStatus> {
  return invoke<VocalToolsStatus>("music_ensure_vocal_tools");
}

export async function removeVocals(filePath: string): Promise<string> {
  return invoke<string>("music_remove_vocals", { filePath });
}

export async function restoreVocals(filePath: string): Promise<string> {
  return invoke<string>("music_restore_vocals", { filePath });
}

export async function hasInstrumental(filePath: string): Promise<boolean> {
  return invoke<boolean>("music_has_instrumental", { filePath });
}
