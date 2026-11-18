pub mod download;
pub mod library;
pub mod vocal;

use download::{download_url, ensure_tools, tools_status, DownloadProgress, ToolsStatus};
use vocal::{
    ensure_vocal_tools, has_instrumental, remove_vocals, restore_original_vocals,
    vocal_tools_status, VocalToolsStatus,
};
use library::{
    create_playlist, delete_playlist, delete_track, import_into_playlist, library_paths,
    list_playlists, list_tracks, load_liked, move_track, rename_playlist, save_liked,
    LibraryPaths, PlaylistInfo, TrackInfo,
};
use serde::Serialize;
use std::process::Command;
use tauri::{AppHandle, Emitter};
use crate::windows::cmd::HiddenConsole;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicLibrarySnapshot {
    pub paths: LibraryPaths,
    pub playlists: Vec<PlaylistInfo>,
    pub liked: Vec<String>,
    pub tools: ToolsStatus,
}

#[tauri::command]
pub fn music_get_snapshot() -> Result<MusicLibrarySnapshot, String> {
    let paths = library_paths()?;
    let playlists = list_playlists(&paths)?;
    let liked = load_liked(&paths)?;
    // Probe only , never download tools during hydrate (that blocked Music open).
    let tools = tools_status(&paths);
    Ok(MusicLibrarySnapshot {
        paths,
        playlists,
        liked,
        tools,
    })
}

#[tauri::command]
pub fn music_list_playlists() -> Result<Vec<PlaylistInfo>, String> {
    let paths = library_paths()?;
    list_playlists(&paths)
}

#[tauri::command]
pub fn music_create_playlist(name: String) -> Result<PlaylistInfo, String> {
    let paths = library_paths()?;
    create_playlist(&paths, &name)
}

#[tauri::command]
pub fn music_delete_playlist(playlist: String) -> Result<(), String> {
    let paths = library_paths()?;
    delete_playlist(&paths, &playlist)
}

#[tauri::command]
pub fn music_rename_playlist(playlist: String, new_name: String) -> Result<PlaylistInfo, String> {
    let paths = library_paths()?;
    rename_playlist(&paths, &playlist, &new_name)
}

#[tauri::command]
pub fn music_delete_track(file_path: String) -> Result<(), String> {
    let paths = library_paths()?;
    delete_track(&paths, &file_path)
}

#[tauri::command]
pub fn music_move_track(file_path: String, playlist: String) -> Result<library::MovedTrack, String> {
    let paths = library_paths()?;
    move_track(&paths, &file_path, &playlist)
}

#[tauri::command]
pub fn music_list_tracks(playlist: String) -> Result<Vec<TrackInfo>, String> {
    let paths = library_paths()?;
    list_tracks(&paths, &playlist)
}

#[tauri::command]
pub fn music_get_liked() -> Result<Vec<String>, String> {
    let paths = library_paths()?;
    load_liked(&paths)
}

#[tauri::command]
pub fn music_set_liked(paths_list: Vec<String>) -> Result<(), String> {
    let paths = library_paths()?;
    save_liked(&paths, &paths_list)
}

#[tauri::command]
pub async fn music_ensure_tools() -> Result<ToolsStatus, String> {
    let paths = library_paths()?;
    tauri::async_runtime::spawn_blocking(move || ensure_tools(&paths, true))
        .await
        .map_err(|e| format!("Tool install task failed: {e}"))?
}

#[tauri::command]
pub async fn music_download(
    app: AppHandle,
    url: String,
    playlist: String,
) -> Result<Vec<TrackInfo>, String> {
    let paths = library_paths()?;
    let progress_app = app.clone();
    tauri::async_runtime::spawn_blocking(move || {
        // Keep tool install + yt-dlp off the UI/async runtime so the shell
        // stays interactive while a download runs.
        ensure_tools(&paths, true)?;
        let emit_app = progress_app.clone();
        download_url(&paths, &url, &playlist, move |progress: DownloadProgress| {
            let _ = emit_app.emit("music://download-progress", progress);
        })?;
        list_tracks(&paths, &playlist)
    })
    .await
    .map_err(|e| format!("Download task failed: {e}"))?
}

#[tauri::command]
pub fn music_import_files(
    playlist: String,
    sources: Vec<String>,
) -> Result<Vec<TrackInfo>, String> {
    let paths = library_paths()?;
    import_into_playlist(&paths, &playlist, &sources)
}

#[tauri::command]
pub fn music_open_library_folder() -> Result<String, String> {
    let paths = library_paths()?;
    let root = paths.library_root.clone();
    #[cfg(windows)]
    {
        Command::new("explorer")
            .arg(&root)
            .hide_console()
            .spawn()
            .map_err(|e| format!("Could not open library folder: {e}"))?;
    }
    #[cfg(not(windows))]
    {
        Command::new("xdg-open")
            .arg(&root)
            .spawn()
            .map_err(|e| format!("Could not open library folder: {e}"))?;
    }
    Ok(root)
}

#[tauri::command]
pub fn music_vocal_status() -> Result<VocalToolsStatus, String> {
    let paths = library_paths()?;
    Ok(vocal_tools_status(&paths))
}

#[tauri::command]
pub async fn music_ensure_vocal_tools(app: AppHandle) -> Result<VocalToolsStatus, String> {
    let paths = library_paths()?;
    let progress_app = app.clone();
    tauri::async_runtime::spawn_blocking(move || {
        ensure_vocal_tools(&paths, &|phase, percent, message| {
            let _ = progress_app.emit(
                "music://download-progress",
                DownloadProgress {
                    phase,
                    percent,
                    message,
                },
            );
        })
    })
    .await
    .map_err(|e| format!("Vocal tool install failed: {e}"))?
}

#[tauri::command]
pub async fn music_remove_vocals(app: AppHandle, file_path: String) -> Result<String, String> {
    let paths = library_paths()?;
    let progress_app = app.clone();
    tauri::async_runtime::spawn_blocking(move || {
        remove_vocals(&paths, &file_path, &|phase, percent, message| {
            let _ = progress_app.emit(
                "music://download-progress",
                DownloadProgress {
                    phase,
                    percent,
                    message,
                },
            );
        })
    })
    .await
    .map_err(|e| format!("Vocal remove task failed: {e}"))?
}

#[tauri::command]
pub fn music_restore_vocals(file_path: String) -> Result<String, String> {
    restore_original_vocals(&file_path)
}

#[tauri::command]
pub fn music_has_instrumental(file_path: String) -> Result<bool, String> {
    Ok(has_instrumental(&file_path))
}
