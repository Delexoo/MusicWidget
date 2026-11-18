mod music;
mod windows;

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

#[tauri::command]
fn show_main_window(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window("main") {
        let _ = win.show();
        let _ = win.unminimize();
        let _ = win.set_focus();
    }
    Ok(())
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
