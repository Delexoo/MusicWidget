use crate::music::library::{playlist_folder, LibraryPaths};
use crate::windows::cmd::HiddenConsole;
use serde::Serialize;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::thread;
use std::time::Duration;

const YT_DLP_URL: &str =
    "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolsStatus {
    pub yt_dlp: bool,
    pub ffmpeg: bool,
    pub tools_dir: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadProgress {
    pub phase: String,
    pub percent: Option<f32>,
    pub message: String,
}

fn yt_dlp_path(paths: &LibraryPaths) -> PathBuf {
    PathBuf::from(&paths.tools_dir).join("yt-dlp.exe")
}

fn ffmpeg_on_path() -> bool {
    let mut cmd = Command::new("where");
    cmd.arg("ffmpeg")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .hide_console();
    cmd.status()
        .map(|s| s.success())
        .unwrap_or(false)
}

pub fn find_ffmpeg(paths: &LibraryPaths) -> Option<PathBuf> {
    let local = PathBuf::from(&paths.tools_dir).join("ffmpeg.exe");
    if local.exists() {
        return Some(local);
    }
    if ffmpeg_on_path() {
        return Some(PathBuf::from("ffmpeg"));
    }
    None
}

pub fn download_file(url: &str, dest: &Path) -> Result<(), String> {
    let mut curl = Command::new("curl");
    curl.args([
        "-L",
        "--retry",
        "3",
        "--retry-delay",
        "1",
        "-o",
        dest.to_string_lossy().as_ref(),
        url,
    ])
    .stdout(Stdio::null())
    .stderr(Stdio::null())
    .hide_console();
    let status = curl
        .status()
        .map_err(|e| format!("curl failed to start: {e}"))?;

    if status.success() && dest.exists() && dest.metadata().map(|m| m.len() > 1024).unwrap_or(false)
    {
        return Ok(());
    }

    let ps = format!(
        "Invoke-WebRequest -Uri '{}' -OutFile '{}' -UseBasicParsing",
        url,
        dest.to_string_lossy().replace('\'', "''")
    );
    let mut powershell = Command::new("powershell");
    powershell
        .args(["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", &ps])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .hide_console();
    let status = powershell
        .status()
        .map_err(|e| format!("powershell download failed: {e}"))?;

    if !status.success() || !dest.exists() {
        return Err("Failed to download yt-dlp".into());
    }
    Ok(())
}

pub fn tools_status(paths: &LibraryPaths) -> ToolsStatus {
    ToolsStatus {
        yt_dlp: yt_dlp_path(paths).exists(),
        ffmpeg: find_ffmpeg(paths).is_some(),
        tools_dir: paths.tools_dir.clone(),
    }
}

pub fn ensure_tools(paths: &LibraryPaths, force_refresh_stale: bool) -> Result<ToolsStatus, String> {
    fs::create_dir_all(&paths.tools_dir).map_err(|e| e.to_string())?;
    let yt = yt_dlp_path(paths);

    let stale = yt
        .metadata()
        .ok()
        .and_then(|m| m.modified().ok())
        .and_then(|t| t.elapsed().ok())
        .is_some_and(|age| age > Duration::from_secs(60 * 60 * 24 * 7));

    let needs_yt = !yt.exists() || (force_refresh_stale && stale);

    if needs_yt {
        let tmp = yt.with_extension("exe.partial");
        let _ = fs::remove_file(&tmp);
        download_file(YT_DLP_URL, &tmp)?;
        let _ = fs::remove_file(&yt);
        fs::rename(&tmp, &yt).map_err(|e| e.to_string())?;
    }

    Ok(tools_status(paths))
}

struct SpotifySong {
    title: String,
    artist: String,
}

pub fn download_url(
    paths: &LibraryPaths,
    url: &str,
    playlist: &str,
    on_progress: impl Fn(DownloadProgress) + Send + 'static,
) -> Result<(), String> {
    let url = url.trim();
    if url.is_empty() {
        return Err("URL is empty".into());
    }

    let folder = playlist_folder(paths, playlist)?;
    let yt = yt_dlp_path(paths);
    if !yt.exists() {
        return Err("yt-dlp is not installed yet".into());
    }

    let ffmpeg = find_ffmpeg(paths);
    let source = resolve_spotify_link(url).unwrap_or_else(|| url.to_string());
    if let Some((kind, id)) = parse_spotify(&source) {
        let songs = spotify_songs(&kind, &id, &on_progress)?;
        return download_songs(&yt, ffmpeg.as_ref(), &folder, &songs, &on_progress);
    }

    on_progress(DownloadProgress {
        phase: "download".into(),
        percent: Some(0.0),
        message: "Starting download…".into(),
    });
    run_yt_dlp(&yt, &yt_dlp_args(ffmpeg.as_ref(), &folder, url), &on_progress, None)?;
    on_progress(DownloadProgress {
        phase: "done".into(),
        percent: Some(100.0),
        message: "Download complete".into(),
    });
    Ok(())
}

fn download_songs(
    yt: &Path,
    ffmpeg: Option<&PathBuf>,
    folder: &Path,
    songs: &[SpotifySong],
    on_progress: &impl Fn(DownloadProgress),
) -> Result<(), String> {
    if songs.is_empty() {
        return Err("That Spotify link has no songs.".into());
    }
    let total = songs.len();
    let mut saved = 0usize;
    let mut first_error = String::new();
    for (index, song) in songs.iter().enumerate() {
        let label = if song.artist.is_empty() {
            song.title.clone()
        } else {
            format!("{} — {}", song.artist, song.title)
        };
        let prefix = if total == 1 {
            String::new()
        } else {
            format!("{}/{} ", index + 1, total)
        };
        on_progress(DownloadProgress {
            phase: "download".into(),
            percent: Some((index as f32 / total as f32) * 100.0),
            message: format!("{prefix}Downloading {label}…"),
        });
        let query = if song.artist.is_empty() {
            format!("ytsearch1:{}", song.title)
        } else {
            format!("ytsearch1:{} - {}", song.artist, song.title)
        };
        let base = (index as f32 / total as f32) * 100.0;
        let span = 100.0 / total as f32;
        match run_yt_dlp(
            yt,
            &yt_dlp_args(ffmpeg, folder, &query),
            on_progress,
            Some((base, span, prefix.clone())),
        ) {
            Ok(()) => saved += 1,
            Err(err) => {
                if first_error.is_empty() {
                    first_error = err;
                }
            }
        }
    }
    if saved == 0 {
        return Err(if first_error.is_empty() {
            format!("Could not download \"{}\".", songs[0].title)
        } else {
            first_error
        });
    }
    let message = if saved == total {
        if total == 1 {
            "Download complete".to_string()
        } else {
            format!("Downloaded {saved} songs")
        }
    } else {
        format!("Downloaded {saved} of {total} songs")
    };
    on_progress(DownloadProgress {
        phase: "done".into(),
        percent: Some(100.0),
        message,
    });
    Ok(())
}

fn yt_dlp_args(ffmpeg: Option<&PathBuf>, folder: &Path, source: &str) -> Vec<String> {
    let out_tmpl = folder
        .join("%(title).180B.%(ext)s")
        .to_string_lossy()
        .to_string();
    let mut args = vec![
        "--no-playlist".into(),
        "--newline".into(),
        "-o".into(),
        out_tmpl,
        "--no-mtime".into(),
        "--restrict-filenames".into(),
        "--write-thumbnail".into(),
        "--convert-thumbnails".into(),
        "jpg".into(),
    ];
    if let Some(ff) = ffmpeg {
        if ff.exists() {
            args.push("--ffmpeg-location".into());
            args.push(ff.to_string_lossy().to_string());
        }
        args.extend([
            "-x".into(),
            "--audio-format".into(),
            "mp3".into(),
            "--audio-quality".into(),
            "0".into(),
        ]);
    } else {
        args.extend(["-f".into(), "ba[ext=m4a]/ba[ext=mp3]/ba".into()]);
    }
    args.push(source.to_string());
    args
}

fn run_yt_dlp(
    yt: &Path,
    args: &[String],
    on_progress: &impl Fn(DownloadProgress),
    scale: Option<(f32, f32, String)>,
) -> Result<(), String> {
    let mut command = Command::new(yt);
    command
        .args(args)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .hide_console();
    let mut child = command
        .spawn()
        .map_err(|e| format!("Could not start the downloader: {e}"))?;

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();
    let (tx, rx) = mpsc::channel::<String>();
    if let Some(out) = stdout {
        let txo = tx.clone();
        thread::spawn(move || pump_lines(out, txo));
    }
    if let Some(err) = stderr {
        thread::spawn(move || pump_lines(err, tx));
    } else {
        drop(tx);
    }

    let mut log = String::new();
    loop {
        match rx.recv_timeout(Duration::from_millis(120)) {
            Ok(line) => {
                log.push_str(&line);
                let (percent, message) = match &scale {
                    Some((base, span, prefix)) => {
                        let percent = parse_percent(&line)
                            .map(|pct| base + (pct / 100.0) * span)
                            .or(Some(*base));
                        let text = line.trim();
                        let message = if text.is_empty() {
                            prefix.trim().to_string()
                        } else {
                            format!("{prefix}{text}")
                        };
                        (percent, message)
                    }
                    None => (parse_percent(&line), line.trim().to_string()),
                };
                if !message.is_empty() {
                    on_progress(DownloadProgress {
                        phase: "download".into(),
                        percent,
                        message,
                    });
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if child.try_wait().ok().flatten().is_some() {
                    while let Ok(line) = rx.try_recv() {
                        log.push_str(&line);
                    }
                    break;
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }

    let status = match child.try_wait() {
        Ok(Some(status)) => status,
        _ => child.wait().map_err(|e| e.to_string())?,
    };
    if status.success() {
        return Ok(());
    }
    Err(download_failure(&log))
}

fn download_failure(log: &str) -> String {
    let detail = log
        .lines()
        .rev()
        .map(str::trim)
        .find(|line| {
            let lower = line.to_ascii_lowercase();
            !line.is_empty()
                && (lower.starts_with("error:")
                    || lower.contains("unsupported url")
                    || lower.contains("unable to"))
        })
        .unwrap_or("")
        .trim_start_matches("ERROR:")
        .trim();
    if detail.is_empty() {
        return "Download failed. Check the link and try again.".into();
    }
    let detail: String = detail.chars().take(180).collect();
    format!("Download failed. {detail}")
}

fn resolve_spotify_link(url: &str) -> Option<String> {
    let lower = url.to_ascii_lowercase();
    if !lower.contains("spotify.link/") {
        return None;
    }
    let tmp = std::env::temp_dir().join(format!("mw-spotify-{}.tmp", std::process::id()));
    let mut curl = Command::new("curl");
    curl.args([
        "-fsSL",
        "-A",
        "Mozilla/5.0",
        "--max-time",
        "20",
        "-o",
        &tmp.to_string_lossy(),
        "-w",
        "%{url_effective}",
        url,
    ])
    .stdin(Stdio::null())
    .stdout(Stdio::piped())
    .stderr(Stdio::null())
    .hide_console();
    let output = curl.output().ok()?;
    let _ = fs::remove_file(&tmp);
    if !output.status.success() {
        return None;
    }
    let final_url = String::from_utf8_lossy(&output.stdout).trim().to_string();
    final_url.starts_with("http").then_some(final_url)
}

fn parse_spotify(url: &str) -> Option<(String, String)> {
    let raw = url.trim();
    if let Some(rest) = raw.strip_prefix("spotify:") {
        let mut parts = rest.split(':');
        let kind = parts.next()?.to_ascii_lowercase();
        let id = parts.next()?.split('?').next()?.trim();
        if is_spotify_kind(&kind) && is_spotify_id(id) {
            return Some((kind, id.to_string()));
        }
        return None;
    }
    let no_hash = raw.split('#').next().unwrap_or(raw);
    let no_query = no_hash.split('?').next().unwrap_or(no_hash);
    let lower = no_query.to_ascii_lowercase();
    let host_at = ["open.spotify.com/", "spotify.com/"]
        .iter()
        .find_map(|host| lower.find(host).map(|index| index + host.len()))?;
    let path = &no_query[host_at..];
    let segs: Vec<&str> = path.split('/').filter(|seg| !seg.is_empty()).collect();
    let (kind, id) = if segs.first().is_some_and(|seg| seg.eq_ignore_ascii_case("embed"))
        && segs.len() >= 3
    {
        (segs[1], segs[2])
    } else if segs
        .first()
        .is_some_and(|seg| seg.to_ascii_lowercase().starts_with("intl-"))
        && segs.len() >= 3
    {
        (segs[1], segs[2])
    } else if segs.len() >= 2 {
        (segs[0], segs[1])
    } else {
        return None;
    };
    let kind = kind.to_ascii_lowercase();
    if !is_spotify_kind(&kind) || !is_spotify_id(id) {
        return None;
    }
    Some((kind, id.to_string()))
}

fn is_spotify_kind(kind: &str) -> bool {
    matches!(kind, "track" | "album" | "playlist")
}

fn is_spotify_id(id: &str) -> bool {
    (10..=40).contains(&id.len()) && id.chars().all(|c| c.is_ascii_alphanumeric())
}

fn spotify_songs(
    kind: &str,
    id: &str,
    on_progress: &impl Fn(DownloadProgress),
) -> Result<Vec<SpotifySong>, String> {
    on_progress(DownloadProgress {
        phase: "download".into(),
        percent: Some(0.0),
        message: "Reading Spotify link…".into(),
    });
    let page = http_get_text(&format!("https://open.spotify.com/embed/{kind}/{id}"))?;
    let json = next_data_json(&page)?;
    let value: serde_json::Value = serde_json::from_str(json)
        .map_err(|_| "Could not read that Spotify link.".to_string())?;
    let entity = value
        .pointer("/props/pageProps/state/data/entity")
        .ok_or_else(|| "Could not read that Spotify link.".to_string())?;
    let mut songs = songs_from_entity(entity);
    songs.truncate(100);
    if songs.is_empty() {
        return Err("That Spotify link has no songs.".into());
    }
    Ok(songs)
}

fn songs_from_entity(entity: &serde_json::Value) -> Vec<SpotifySong> {
    if let Some(list) = entity.get("trackList").and_then(|value| value.as_array()) {
        return list
            .iter()
            .filter(|track| track.get("isPlayable").and_then(|v| v.as_bool()).unwrap_or(true))
            .filter_map(|track| {
                let title = clean_label(track.get("title").and_then(|v| v.as_str()).unwrap_or(""));
                if title.is_empty() {
                    return None;
                }
                let artist =
                    clean_label(track.get("subtitle").and_then(|v| v.as_str()).unwrap_or(""));
                Some(SpotifySong { title, artist })
            })
            .collect();
    }
    let title = clean_label(
        entity
            .get("title")
            .or_else(|| entity.get("name"))
            .and_then(|v| v.as_str())
            .unwrap_or(""),
    );
    if title.is_empty() {
        return Vec::new();
    }
    let artist = entity
        .get("artists")
        .and_then(|value| value.as_array())
        .map(|artists| {
            artists
                .iter()
                .filter_map(|artist| artist.get("name").and_then(|name| name.as_str()))
                .map(clean_label)
                .filter(|name| !name.is_empty())
                .collect::<Vec<_>>()
                .join(", ")
        })
        .unwrap_or_default();
    vec![SpotifySong { title, artist }]
}

fn clean_label(value: &str) -> String {
    value
        .replace(['\u{00a0}', '\n', '\r', '\t'], " ")
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

fn next_data_json(html: &str) -> Result<&str, String> {
    let marker = html
        .find("__NEXT_DATA__")
        .ok_or_else(|| "Could not read that Spotify link.".to_string())?;
    let start = html[marker..]
        .find('>')
        .map(|offset| marker + offset + 1)
        .ok_or_else(|| "Could not read that Spotify link.".to_string())?;
    let end = html[start..]
        .find("</script>")
        .map(|offset| start + offset)
        .ok_or_else(|| "Could not read that Spotify link.".to_string())?;
    Ok(html[start..end].trim())
}

fn http_get_text(url: &str) -> Result<String, String> {
    let mut curl = Command::new("curl");
    curl.args([
        "-fsSL",
        "-A",
        "Mozilla/5.0",
        "--max-time",
        "25",
        url,
    ])
    .stdin(Stdio::null())
    .stdout(Stdio::piped())
    .stderr(Stdio::null())
    .hide_console();
    let output = curl
        .output()
        .map_err(|_| "Could not reach Spotify.".to_string())?;
    if !output.status.success() || output.stdout.is_empty() {
        return Err("Could not reach Spotify.".into());
    }
    String::from_utf8(output.stdout).map_err(|_| "Could not read that Spotify link.".into())
}

fn pump_lines<R: std::io::Read>(reader: R, tx: mpsc::Sender<String>) {
    let mut reader = std::io::BufReader::new(reader);
    let mut buf = String::new();
    loop {
        buf.clear();
        match std::io::BufRead::read_line(&mut reader, &mut buf) {
            Ok(0) => break,
            Ok(_) => {
                if tx.send(buf.clone()).is_err() {
                    break;
                }
            }
            Err(_) => break,
        }
    }
}

fn parse_percent(line: &str) -> Option<f32> {
    let line = line.trim();
    let idx = line.find('%')?;
    let start = line[..idx]
        .rfind(|c: char| !(c.is_ascii_digit() || c == '.'))
        .map(|i| i + 1)
        .unwrap_or(0);
    line[start..idx].parse().ok()
}
