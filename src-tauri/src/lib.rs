mod music;
mod windows;

use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::thread;
use std::time::Duration;

use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{Emitter, Manager};

use music::{
    music_create_playlist, music_delete_playlist, music_delete_track, music_download,
    music_ensure_tools, music_ensure_vocal_tools, music_get_liked, music_get_snapshot,
    music_has_instrumental, music_import_files, music_list_playlists, music_list_tracks,
    music_move_track, music_open_library_folder, music_remove_vocals, music_rename_playlist,
    music_restore_vocals, music_set_liked, music_vocal_status,
};

const PILL_LOGICAL_WIDTH: f64 = 360.0;
const TOP_MARGIN_LOGICAL: f64 = 36.0;

static POS_X: AtomicI32 = AtomicI32::new(0);
static POS_Y: AtomicI32 = AtomicI32::new(0);
static POS_DIRTY: AtomicBool = AtomicBool::new(false);
static POS_SEEN: AtomicBool = AtomicBool::new(false);

#[derive(serde::Serialize, serde::Deserialize)]
struct SavedPos {
    x: i32,
    y: i32,
}

#[tauri::command]
fn show_main_window(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window("main") {
        let _ = win.show();
        let _ = win.unminimize();
        let _ = win.set_focus();
    }
    Ok(())
}

fn position_path(app: &tauri::AppHandle) -> Option<std::path::PathBuf> {
    let dir = app.path().app_data_dir().ok()?;
    std::fs::create_dir_all(&dir).ok()?;
    Some(dir.join("window-position.json"))
}

fn write_saved_position(app: &tauri::AppHandle, x: i32, y: i32) {
    let Some(path) = position_path(app) else {
        return;
    };
    let Ok(json) = serde_json::to_vec(&SavedPos { x, y }) else {
        return;
    };
    let _ = std::fs::write(path, json);
}

/// Remember the last place the user left the window. Writes are debounced so
/// dragging does not rewrite the file on every pixel.
fn flush_window_position(app: &tauri::AppHandle) {
    if !POS_SEEN.load(Ordering::Acquire) {
        return;
    }
    write_saved_position(
        app,
        POS_X.load(Ordering::Relaxed),
        POS_Y.load(Ordering::Relaxed),
    );
}

fn save_window_position(app: &tauri::AppHandle, x: i32, y: i32) {
    POS_X.store(x, Ordering::Relaxed);
    POS_Y.store(y, Ordering::Relaxed);
    POS_SEEN.store(true, Ordering::Release);
    if POS_DIRTY.swap(true, Ordering::AcqRel) {
        return;
    }
    let app = app.clone();
    thread::spawn(move || {
        let mut x = POS_X.load(Ordering::Relaxed);
        let mut y = POS_Y.load(Ordering::Relaxed);
        loop {
            thread::sleep(Duration::from_millis(300));
            let nx = POS_X.load(Ordering::Relaxed);
            let ny = POS_Y.load(Ordering::Relaxed);
            if nx == x && ny == y {
                break;
            }
            x = nx;
            y = ny;
        }
        POS_DIRTY.store(false, Ordering::Release);
        write_saved_position(&app, x, y);
        let nx = POS_X.load(Ordering::Relaxed);
        let ny = POS_Y.load(Ordering::Relaxed);
        if nx != x || ny != y {
            save_window_position(&app, nx, ny);
        }
    });
}

fn position_visible(app: &tauri::AppHandle, x: i32, y: i32) -> bool {
    let Ok(monitors) = app.available_monitors() else {
        return true;
    };
    if monitors.is_empty() {
        return true;
    }
    // A slice of the pill has to land on a monitor so a saved spot on an
    // unplugged display falls back to the default instead of opening off-screen.
    let right = x + 120;
    let bottom = y + 40;
    monitors.iter().any(|monitor| {
        let origin = monitor.position();
        let size = monitor.size();
        let monitor_right = origin.x + size.width as i32;
        let monitor_bottom = origin.y + size.height as i32;
        x < monitor_right && right > origin.x && y < monitor_bottom && bottom > origin.y
    })
}

/// Horizontally center the pill near the top of the current monitor.
/// Used only the first time the widget opens, before the user has placed it.
fn place_main_window_top_center(app: &tauri::AppHandle) {
    let Some(win) = app.get_webview_window("main") else {
        return;
    };
    let Ok(Some(monitor)) = win.current_monitor() else {
        return;
    };
    let mon_size = monitor.size();
    let mon_pos = monitor.position();
    let scale = monitor.scale_factor();
    let measured = win.outer_size().map(|size| size.width as i32).unwrap_or(0);
    let width = if measured > 0 {
        measured
    } else {
        (PILL_LOGICAL_WIDTH * scale).round() as i32
    };
    let x = mon_pos.x + ((mon_size.width as i32 - width) / 2);
    let y = mon_pos.y + (TOP_MARGIN_LOGICAL * scale).round() as i32;
    let _ = win.set_position(tauri::PhysicalPosition::new(x, y));
}

fn restore_main_window_position(app: &tauri::AppHandle) {
    let restored = position_path(app).and_then(|path| {
        let text = std::fs::read_to_string(path).ok()?;
        let saved = serde_json::from_str::<SavedPos>(&text).ok()?;
        if !position_visible(app, saved.x, saved.y) {
            return None;
        }
        let win = app.get_webview_window("main")?;
        win.set_position(tauri::PhysicalPosition::new(saved.x, saved.y))
            .ok()?;
        Some(())
    });
    if restored.is_none() {
        place_main_window_top_center(app);
    }
}

#[tauri::command]
fn hide_main_window(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window("main") {
        let _ = win.hide();
    }
    Ok(())
}

#[tauri::command]
fn quit_app(app: tauri::AppHandle) {
    app.exit(0);
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            let _ = show_main_window(app.clone());
        }))
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_store::Builder::new().build())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_global_shortcut::Builder::new().build())
        .setup(|app| {
            restore_main_window_position(app.handle());
            if let Some(win) = app.get_webview_window("main") {
                let handle = app.handle().clone();
                win.on_window_event(move |event| match event {
                    tauri::WindowEvent::Moved(position) => {
                        save_window_position(&handle, position.x, position.y);
                    }
                    tauri::WindowEvent::CloseRequested { .. } => {
                        flush_window_position(&handle);
                    }
                    _ => {}
                });
            }

            let show_i = MenuItem::with_id(app, "show", "Show MusicWidget", true, None::<&str>)?;
            let dash_i = MenuItem::with_id(app, "dashboard", "Open Dashboard", true, None::<&str>)?;
            let quit_i = MenuItem::with_id(app, "quit", "Exit", true, None::<&str>)?;
            let menu = Menu::with_items(
                app,
                &[
                    &show_i,
                    &dash_i,
                    &PredefinedMenuItem::separator(app)?,
                    &quit_i,
                ],
            )?;

            let _tray = TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .menu(&menu)
                .tooltip("MusicWidget")
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "show" | "dashboard" => {
                        let _ = show_main_window(app.clone());
                        let _ = app.emit("musicwidget://open-dashboard", ());
                    }
                    "quit" => app.exit(0),
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        let app = tray.app_handle();
                        let _ = show_main_window(app.clone());
                    }
                })
                .build(app)?;

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            show_main_window,
            hide_main_window,
            quit_app,
            music_get_snapshot,
            music_list_playlists,
            music_create_playlist,
            music_delete_playlist,
            music_rename_playlist,
            music_delete_track,
            music_move_track,
            music_list_tracks,
            music_get_liked,
            music_set_liked,
            music_ensure_tools,
            music_download,
            music_import_files,
            music_open_library_folder,
            music_vocal_status,
            music_ensure_vocal_tools,
            music_remove_vocals,
            music_restore_vocals,
            music_has_instrumental,
        ])
        .run(tauri::generate_context!())
        .expect("error while running MusicWidget");
}
