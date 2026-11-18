use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};

const AUDIO_EXTENSIONS: &[&str] = &[
    "mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma", "aiff", "alac", "webm",
];

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryPaths {
    pub library_root: String,
    pub app_data: String,
    pub tools_dir: String,
    pub liked_file: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistInfo {
    pub id: String,
    pub name: String,
    pub folder_path: String,
    pub track_count: usize,
    pub kind: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackInfo {
    pub id: String,
    pub title: String,
    pub artist: String,
    pub file_path: String,
    pub file_name: String,
    pub playlist: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cover_path: Option<String>,
}

/// Default library lives under the Windows Music known folder: `Music\MusicWidget`.
fn music_documents_root() -> Result<PathBuf, String> {
    Ok(windows_music_folder()?.join("MusicWidget"))
}

fn app_data_root() -> Result<PathBuf, String> {
    let base = std::env::var("APPDATA").map_err(|_| "APPDATA not set".to_string())?;
    Ok(PathBuf::from(base).join("MusicWidget"))
}

fn windows_music_folder() -> Result<PathBuf, String> {
    #[cfg(windows)]
    {
        use std::ffi::OsString;
        use std::os::windows::ffi::OsStringExt;

        #[repr(C)]
        struct Guid {
            data1: u32,
            data2: u16,
            data3: u16,
            data4: [u8; 8],
        }

        // FOLDERID_Music
        const FOLDERID_MUSIC: Guid = Guid {
            data1: 0x4BD8D571,
            data2: 0x6D19,
            data3: 0x48D3,
            data4: [0xBE, 0x97, 0x42, 0x22, 0x20, 0x08, 0x0E, 0x43],
        };

        #[link(name = "shell32")]
        unsafe extern "system" {
            fn SHGetKnownFolderPath(
                rfid: *const Guid,
                dw_flags: u32,
                h_token: *mut core::ffi::c_void,
                ppsz_path: *mut *mut u16,
            ) -> i32;
        }

        #[link(name = "ole32")]
        unsafe extern "system" {
            fn CoTaskMemFree(pv: *mut core::ffi::c_void);
        }

        let mut path_ptr: *mut u16 = std::ptr::null_mut();
        let hr = unsafe {
            SHGetKnownFolderPath(
                &FOLDERID_MUSIC,
                0,
                std::ptr::null_mut(),
                &mut path_ptr,
            )
        };
        if hr < 0 || path_ptr.is_null() {
            return fallback_music_folder();
        }

        let mut len = 0usize;
        unsafe {
            while *path_ptr.add(len) != 0 {
                len += 1;
            }
        }
        let slice = unsafe { std::slice::from_raw_parts(path_ptr, len) };
        let os = OsString::from_wide(slice);
        unsafe { CoTaskMemFree(path_ptr.cast()) };
        return Ok(PathBuf::from(os));
    }

    #[cfg(not(windows))]
    {
        fallback_music_folder()
    }
}

fn fallback_music_folder() -> Result<PathBuf, String> {
    let home = std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .map(PathBuf::from)
        .ok_or_else(|| "home directory not found".to_string())?;
    Ok(home.join("Music"))
}

pub fn library_paths() -> Result<LibraryPaths, String> {
    let library_root = music_documents_root()?;
    let app_data = app_data_root()?;
    let tools_dir = app_data.join("tools");
    let liked_file = app_data.join("liked-songs.json");

    fs::create_dir_all(&library_root).map_err(|e| e.to_string())?;
    fs::create_dir_all(&tools_dir).map_err(|e| e.to_string())?;
    fs::create_dir_all(&app_data).map_err(|e| e.to_string())?;

    Ok(LibraryPaths {
        library_root: library_root.to_string_lossy().to_string(),
        app_data: app_data.to_string_lossy().to_string(),
        tools_dir: tools_dir.to_string_lossy().to_string(),
        liked_file: liked_file.to_string_lossy().to_string(),
    })
}

fn sanitize_playlist_name(name: &str) -> Result<String, String> {
    let clean: String = name
        .chars()
        .map(|c| match c {
            '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*' => '-',
            _ => c,
        })
        .collect::<String>()
        .trim()
        .trim_matches('.')
        .to_string();
    if clean.is_empty() {
        return Err("Playlist name cannot be empty".into());
    }
    Ok(clean)
}

fn is_audio(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .map(|e| AUDIO_EXTENSIONS.iter().any(|ok| e.eq_ignore_ascii_case(ok)))
        .unwrap_or(false)
}

fn beautify_title(stem: &str) -> String {
    stem.replace('_', " ")
        .replace("  ", " ")
        .trim()
        .to_string()
}

fn parse_artist_title(stem: &str) -> (String, String) {
    let pretty = beautify_title(stem);
    if let Some((artist, title)) = pretty.split_once(" - ") {
        let a = artist.trim();
        let t = title.trim();
        if !a.is_empty() && !t.is_empty() {
            return (a.to_string(), t.to_string());
        }
    }
    ("Unknown".into(), pretty)
}

const COVER_EXTENSIONS: &[&str] = &["jpg", "jpeg", "png", "webp", "img"];

fn resolve_cover_path(audio: &Path) -> Option<String> {
    let parent = audio.parent()?;
    let stem = audio.file_stem()?.to_string_lossy();

    for ext in COVER_EXTENSIONS {
        let candidate = parent.join(format!("{stem}.{ext}"));
        if candidate.is_file() {
            return Some(candidate.to_string_lossy().to_string());
        }
    }

    for name in ["cover", "folder", "album", "front"] {
        for ext in COVER_EXTENSIONS {
            let candidate = parent.join(format!("{name}.{ext}"));
            if candidate.is_file() {
                return Some(candidate.to_string_lossy().to_string());
            }
        }
    }

    None
}

fn track_from_path(path: &Path, playlist: &str) -> Option<TrackInfo> {
    if !is_audio(path) {
        return None;
    }
    let stem = path.file_stem()?.to_string_lossy();
    // Vocal-remover output sits beside the original and must not become its own track.
    if stem.ends_with(".instrumental") || stem == "mw_instrumental" {
        return None;
    }
    let (artist, title) = parse_artist_title(&stem);
    let file_path = path.to_string_lossy().to_string();
    Some(TrackInfo {
        id: file_path.clone(),
        title,
        artist,
        file_path,
        file_name: path.file_name()?.to_string_lossy().to_string(),
        playlist: playlist.to_string(),
        cover_path: resolve_cover_path(path),
    })
}

/// Create "My playlist" when the library has no user playlists yet.
fn ensure_default_playlist(paths: &LibraryPaths) -> Result<(), String> {
    let root = PathBuf::from(&paths.library_root);
    let has_normal = fs::read_dir(&root)
        .map(|rd| {
            rd.filter_map(|e| e.ok())
                .any(|e| e.file_type().map(|t| t.is_dir()).unwrap_or(false))
        })
        .unwrap_or(false);
    if has_normal {
        return Ok(());
    }
    let _ = create_playlist(paths, "My playlist")?;
    Ok(())
}

pub fn list_playlists(paths: &LibraryPaths) -> Result<Vec<PlaylistInfo>, String> {
    ensure_default_playlist(paths)?;

    let root = PathBuf::from(&paths.library_root);
    let mut out = Vec::new();

    let liked = load_liked(paths)?;
    out.push(PlaylistInfo {
        id: "__liked__".into(),
        name: "Liked Songs".into(),
        folder_path: paths.liked_file.clone(),
        track_count: liked.len(),
        kind: "liked".into(),
    });

    let entries = fs::read_dir(&root).map_err(|e| e.to_string())?;
    let mut dirs: Vec<_> = entries
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().map(|t| t.is_dir()).unwrap_or(false))
        .collect();
    dirs.sort_by_key(|e| e.file_name());

    for dir in dirs {
        let name = dir.file_name().to_string_lossy().to_string();
        let folder = dir.path();
        let track_count = fs::read_dir(&folder)
            .map(|rd| {
                rd.filter_map(|e| e.ok())
                    .filter(|e| is_audio(&e.path()))
                    .count()
            })
            .unwrap_or(0);
        out.push(PlaylistInfo {
            id: name.clone(),
            name: name.clone(),
            folder_path: folder.to_string_lossy().to_string(),
            track_count,
            kind: "normal".into(),
        });
    }

    Ok(out)
}

pub fn create_playlist(paths: &LibraryPaths, name: &str) -> Result<PlaylistInfo, String> {
    let clean = sanitize_playlist_name(name)?;
    let folder = PathBuf::from(&paths.library_root).join(&clean);
    if folder.exists() {
        return Err("A playlist with that name already exists".into());
    }
    fs::create_dir_all(&folder).map_err(|e| e.to_string())?;
    Ok(PlaylistInfo {
        id: clean.clone(),
        name: clean,
        folder_path: folder.to_string_lossy().to_string(),
        track_count: 0,
        kind: "normal".into(),
    })
}

pub fn list_tracks(paths: &LibraryPaths, playlist: &str) -> Result<Vec<TrackInfo>, String> {
    if playlist == "__liked__" || playlist.eq_ignore_ascii_case("Liked Songs") {
        let liked = load_liked(paths)?;
        let mut tracks = Vec::new();
        for path in liked {
            let p = PathBuf::from(&path);
            if let Some(track) = track_from_path(&p, "__liked__") {
                tracks.push(track);
            }
        }
        return Ok(tracks);
    }

    let folder = PathBuf::from(&paths.library_root).join(playlist);
    if !folder.is_dir() {
        return Err(format!("Playlist not found: {playlist}"));
    }

    let mut tracks = Vec::new();
    let entries = fs::read_dir(&folder).map_err(|e| e.to_string())?;
    let mut files: Vec<_> = entries.filter_map(|e| e.ok()).map(|e| e.path()).collect();
    files.sort();
    for path in files {
        if let Some(track) = track_from_path(&path, playlist) {
            tracks.push(track);
        }
    }
    Ok(tracks)
}

pub fn load_liked(paths: &LibraryPaths) -> Result<Vec<String>, String> {
    let file = Path::new(&paths.liked_file);
    if !file.exists() {
        return Ok(Vec::new());
    }
    let raw = fs::read_to_string(file).map_err(|e| e.to_string())?;
    let list: Vec<String> = serde_json::from_str(&raw).unwrap_or_default();
    Ok(list
        .into_iter()
        .filter(|p| Path::new(p).is_file())
        .collect())
}

pub fn save_liked(paths: &LibraryPaths, liked: &[String]) -> Result<(), String> {
    let file = Path::new(&paths.liked_file);
    if let Some(parent) = file.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let raw = serde_json::to_string_pretty(liked).map_err(|e| e.to_string())?;
    fs::write(file, raw).map_err(|e| e.to_string())
}

pub fn playlist_folder(paths: &LibraryPaths, playlist: &str) -> Result<PathBuf, String> {
    let clean = sanitize_playlist_name(playlist)?;
    let folder = PathBuf::from(&paths.library_root).join(&clean);
    fs::create_dir_all(&folder).map_err(|e| e.to_string())?;
    Ok(folder)
}

fn unique_dest(folder: &Path, file_name: &str) -> PathBuf {
    let candidate = folder.join(file_name);
    if !candidate.exists() {
        return candidate;
    }
    let path = Path::new(file_name);
    let stem = path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("track");
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .map(|e| format!(".{e}"))
        .unwrap_or_default();
    for i in 1..500 {
        let next = folder.join(format!("{stem} ({i}){ext}"));
        if !next.exists() {
            return next;
        }
    }
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    folder.join(format!("{stem}-{stamp}{ext}"))
}

fn collect_audio_sources(path: &Path, out: &mut Vec<PathBuf>) {
    if path.is_file() {
        if is_audio(path) {
            out.push(path.to_path_buf());
        }
        return;
    }
    if !path.is_dir() {
        return;
    }
    let Ok(entries) = fs::read_dir(path) else {
        return;
    };
    for entry in entries.flatten() {
        let child = entry.path();
        if child.is_file() && is_audio(&child) {
            out.push(child);
        } else if child.is_dir() {
            // One level of nested folders (album folders).
            if let Ok(nested) = fs::read_dir(&child) {
                for file in nested.flatten() {
                    let p = file.path();
                    if p.is_file() && is_audio(&p) {
                        out.push(p);
                    }
                }
            }
        }
    }
}

fn ensure_under_library(paths: &LibraryPaths, file: &Path) -> Result<PathBuf, String> {
    let root = PathBuf::from(&paths.library_root);
    let root_canon = root
        .canonicalize()
        .unwrap_or(root.clone());
    let file_canon = file
        .canonicalize()
        .map_err(|_| "Track file not found".to_string())?;
    if !file_canon.starts_with(&root_canon) {
        return Err("Can only delete tracks inside the MusicWidget music library".into());
    }
    Ok(file_canon)
}

/// Delete an audio file from the library and scrub it from Liked.
pub fn delete_track(paths: &LibraryPaths, file_path: &str) -> Result<(), String> {
    let path = PathBuf::from(file_path.trim());
    if path.as_os_str().is_empty() {
        return Err("Missing track path".into());
    }
    let file = ensure_under_library(paths, &path)?;
    if !file.is_file() {
        return Err("Track file not found".into());
    }

    for sidecar in stem_sidecars(&file) {
        let _ = fs::remove_file(sidecar);
    }

    fs::remove_file(&file).map_err(|e| format!("Could not delete track: {e}"))?;

    let mut liked = load_liked(paths)?;
    let before = liked.len();
    liked.retain(|p| {
        Path::new(p)
            .canonicalize()
            .map(|c| c != file)
            .unwrap_or(true)
            && p != file_path
    });
    if liked.len() != before {
        save_liked(paths, &liked)?;
    }
    Ok(())
}

/// Rename a normal playlist folder and rewrite Liked paths that lived under it.
pub fn rename_playlist(
    paths: &LibraryPaths,
    playlist: &str,
    new_name: &str,
) -> Result<PlaylistInfo, String> {
    if playlist == "__liked__" || playlist.eq_ignore_ascii_case("Liked Songs") {
        return Err("Liked Songs cannot be renamed".into());
    }
    let from = sanitize_playlist_name(playlist)?;
    let to = sanitize_playlist_name(new_name)?;
    if from.eq_ignore_ascii_case(&to) {
        let folder = PathBuf::from(&paths.library_root).join(&from);
        let track_count = fs::read_dir(&folder)
            .map(|rd| {
                rd.filter_map(|e| e.ok())
                    .filter(|e| is_audio(&e.path()))
                    .count()
            })
            .unwrap_or(0);
        return Ok(PlaylistInfo {
            id: from.clone(),
            name: from,
            folder_path: folder.to_string_lossy().to_string(),
            track_count,
            kind: "normal".into(),
        });
    }

    let root = PathBuf::from(&paths.library_root);
    let from_folder = root.join(&from);
    let to_folder = root.join(&to);
    if !from_folder.is_dir() {
        return Err(format!("Playlist not found: {from}"));
    }
    if to_folder.exists() {
        return Err("A playlist with that name already exists".into());
    }

    let from_canon = from_folder
        .canonicalize()
        .map_err(|e| format!("Could not rename playlist: {e}"))?;
    fs::rename(&from_folder, &to_folder).map_err(|e| format!("Could not rename playlist: {e}"))?;
    let to_canon = to_folder
        .canonicalize()
        .unwrap_or(to_folder.clone());

    let mut liked = load_liked(paths)?;
    let mut changed = false;
    for path in &mut liked {
        let Ok(canon) = Path::new(path).canonicalize() else {
            continue;
        };
        if canon.starts_with(&from_canon) {
            if let Ok(rel) = canon.strip_prefix(&from_canon) {
                *path = to_canon.join(rel).to_string_lossy().to_string();
                changed = true;
            }
        }
    }
    if changed {
        save_liked(paths, &liked)?;
    }

    let track_count = fs::read_dir(&to_folder)
        .map(|rd| {
            rd.filter_map(|e| e.ok())
                .filter(|e| is_audio(&e.path()))
                .count()
        })
        .unwrap_or(0);

    Ok(PlaylistInfo {
        id: to.clone(),
        name: to,
        folder_path: to_folder.to_string_lossy().to_string(),
        track_count,
        kind: "normal".into(),
    })
}

/// Delete a normal playlist folder and scrub its tracks from Liked.
pub fn delete_playlist(paths: &LibraryPaths, playlist: &str) -> Result<(), String> {
    if playlist == "__liked__" || playlist.eq_ignore_ascii_case("Liked Songs") {
        return Err("Liked Songs cannot be deleted".into());
    }
    let clean = sanitize_playlist_name(playlist)?;
    let folder = PathBuf::from(&paths.library_root).join(&clean);
    let root = PathBuf::from(&paths.library_root);
    let root_canon = root.canonicalize().unwrap_or(root.clone());
    let folder_canon = folder
        .canonicalize()
        .map_err(|_| format!("Playlist not found: {clean}"))?;
    if folder_canon == root_canon || !folder_canon.starts_with(&root_canon) {
        return Err("Invalid playlist folder".into());
    }
    if !folder_canon.is_dir() {
        return Err(format!("Playlist not found: {clean}"));
    }

    let prefix = folder_canon.to_string_lossy().to_string();
    fs::remove_dir_all(&folder_canon).map_err(|e| format!("Could not delete playlist: {e}"))?;

    let mut liked = load_liked(paths)?;
    let before = liked.len();
    liked.retain(|p| {
        Path::new(p)
            .canonicalize()
            .map(|c| !c.starts_with(&folder_canon))
            .unwrap_or(!p.replace('\\', "/").starts_with(&prefix.replace('\\', "/")))
    });
    if liked.len() != before {
        save_liked(paths, &liked)?;
    }
    Ok(())
}

/// Copy local audio files/folders into a playlist folder. Returns updated tracks.
pub fn import_into_playlist(
    paths: &LibraryPaths,
    playlist: &str,
    sources: &[String],
) -> Result<Vec<TrackInfo>, String> {
    if playlist == "__liked__" || playlist.eq_ignore_ascii_case("Liked Songs") {
        return Err("Add tracks to a normal playlist, then like them.".into());
    }
    let folder = playlist_folder(paths, playlist)?;
    let mut files = Vec::new();
    for src in sources {
        let path = PathBuf::from(src.trim());
        if path.as_os_str().is_empty() {
            continue;
        }
        collect_audio_sources(&path, &mut files);
    }
    files.sort();
    files.dedup();
    if files.is_empty() {
        return Err("No audio files found to add.".into());
    }

    for file in files {
        let name = file
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("track.mp3");
        let dest = unique_dest(&folder, name);
        fs::copy(&file, &dest).map_err(|e| format!("Could not copy {}: {e}", name))?;
    }

    list_tracks(paths, playlist)
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MovedTrack {
    pub track: TrackInfo,
    pub source_removed: bool,
    pub source_path: String,
}

fn stem_sidecars(audio: &Path) -> Vec<PathBuf> {
    let mut out = Vec::new();
    let Some(parent) = audio.parent() else {
        return out;
    };
    let Some(stem) = audio.file_stem().and_then(|s| s.to_str()) else {
        return out;
    };
    if stem.ends_with(".instrumental") || stem == "mw_instrumental" {
        return out;
    }
    for ext in COVER_EXTENSIONS {
        let candidate = parent.join(format!("{stem}.{ext}"));
        if candidate.is_file() {
            out.push(candidate);
        }
    }
    let instrumental = parent.join(format!("{stem}.instrumental.wav"));
    if instrumental.is_file() {
        out.push(instrumental);
    }
    out
}

/// Rename when the file is free, otherwise copy and leave the source if it is locked.
fn relocate_path(from: &Path, to: &Path) -> Result<bool, String> {
    if let Some(parent) = to.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    match fs::rename(from, to) {
        Ok(()) => Ok(true),
        Err(err) => {
            fs::copy(from, to).map_err(|e| {
                format!(
                    "Could not move {}: {err}; copy failed: {e}",
                    from.display()
                )
            })?;
            match fs::remove_file(from) {
                Ok(()) => Ok(true),
                Err(_) => Ok(false),
            }
        }
    }
}

fn sidecar_dest(source_audio: &Path, dest_audio: &Path, sidecar: &Path) -> Option<PathBuf> {
    let old_stem = source_audio.file_stem()?.to_str()?;
    let new_stem = dest_audio.file_stem()?.to_str()?;
    let name = sidecar.file_name()?.to_str()?;
    let prefix = format!("{old_stem}.");
    let rest = name.strip_prefix(&prefix)?;
    Some(dest_audio.parent()?.join(format!("{new_stem}.{rest}")))
}

/// Move a library song into another playlist, including its cover and instrumental.
pub fn move_track(
    paths: &LibraryPaths,
    file_path: &str,
    playlist: &str,
) -> Result<MovedTrack, String> {
    if playlist == "__liked__" || playlist.eq_ignore_ascii_case("Liked Songs") {
        return Err("Like a song to keep it in Liked Songs.".into());
    }
    let clean = sanitize_playlist_name(playlist)?;
    let folder = PathBuf::from(&paths.library_root).join(&clean);
    if !folder.is_dir() {
        return Err(format!("Playlist not found: {clean}"));
    }
    let raw = PathBuf::from(file_path.trim());
    if raw.as_os_str().is_empty() {
        return Err("Missing track path".into());
    }
    let file = ensure_under_library(paths, &raw)?;
    if !file.is_file() || !is_audio(&file) {
        return Err("Track file not found".into());
    }
    let stem = file.file_stem().and_then(|s| s.to_str()).unwrap_or("");
    if stem.ends_with(".instrumental") || stem == "mw_instrumental" {
        return Err("That file is part of another song.".into());
    }
    let folder_canon = folder
        .canonicalize()
        .map_err(|e| format!("Playlist not found: {e}"))?;
    if file.parent().and_then(|p| p.canonicalize().ok()).as_ref() == Some(&folder_canon) {
        return Err("Song is already in that playlist".into());
    }

    let file_name = file
        .file_name()
        .and_then(|n| n.to_str())
        .ok_or_else(|| "Track file name is not valid".to_string())?;
    let dest = unique_dest(&folder, file_name);
    let source_canon = file
        .canonicalize()
        .map_err(|_| "Track file not found".to_string())?;
    let source_display = file.to_string_lossy().to_string();
    let sidecars = stem_sidecars(&file);

    let mut liked = load_liked(paths)?;
    let liked_hits: Vec<usize> = liked
        .iter()
        .enumerate()
        .filter_map(|(i, path)| {
            Path::new(path)
                .canonicalize()
                .ok()
                .filter(|canon| canon == &source_canon)
                .map(|_| i)
        })
        .collect();

    let audio_removed = relocate_path(&file, &dest)?;
    let mut source_removed = audio_removed;
    for sidecar in &sidecars {
        if !sidecar.is_file() {
            continue;
        }
        let Some(target) = sidecar_dest(&file, &dest, sidecar) else {
            continue;
        };
        match relocate_path(sidecar, &target) {
            Ok(removed) => source_removed = source_removed && removed,
            Err(_) => source_removed = false,
        }
    }

    if !liked_hits.is_empty() {
        let to_str = dest.to_string_lossy().to_string();
        for index in liked_hits {
            if let Some(path) = liked.get_mut(index) {
                *path = to_str.clone();
            }
        }
        save_liked(paths, &liked)?;
    }

    let track = track_from_path(&dest, &clean)
        .ok_or_else(|| "Could not read moved song".to_string())?;
    Ok(MovedTrack {
        track,
        source_removed,
        source_path: source_display,
    })
}
