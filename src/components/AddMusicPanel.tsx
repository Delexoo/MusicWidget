import { useEffect, useState } from "react";
import { createPortal } from "react-dom";
import { AnimatePresence, motion } from "motion/react";
import { open } from "@tauri-apps/plugin-dialog";
import { useMusicStore } from "../stores/musicStore";
import { IconClose } from "./icons";

type AddMusicPanelProps = {
  open: boolean;
  playlistName: string | null;
  onClose: () => void;
};

type Mode = "chooser" | "download";

export function AddMusicPanel({ open: isOpen, playlistName, onClose }: AddMusicPanelProps) {
  const [mode, setMode] = useState<Mode>("chooser");
  const { downloadUrl, status, setDownloadUrl, download, importSources } = useMusicStore();

  useEffect(() => {
    if (!isOpen) return;
    setMode("chooser");
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [isOpen, onClose]);

  const pickFiles = async () => {
    if (!playlistName) return;
    try {
      const selected = await open({
        multiple: true,
        title: "Add music files",
        filters: [
          {
            name: "Audio",
            extensions: ["mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma", "aiff", "aif"],
          },
        ],
      });
      if (!selected) return;
      const paths = Array.isArray(selected) ? selected : [selected];
      if (!paths.length) return;
      await importSources(playlistName, paths);
      onClose();
    } catch (err) {
      useMusicStore.setState({
        status: err instanceof Error ? err.message : String(err),
      });
    }
  };

  const pickFolder = async () => {
    if (!playlistName) return;
    try {
      const selected = await open({
        directory: true,
        multiple: false,
        title: "Add music from folder",
      });
      if (!selected || Array.isArray(selected)) return;
      await importSources(playlistName, [selected]);
      onClose();
    } catch (err) {
      useMusicStore.setState({
        status: err instanceof Error ? err.message : String(err),
      });
    }
  };

  const runDownload = async () => {
    if (!playlistName) return;
    await download(playlistName);
    const { downloadUrl: remaining, status: nextStatus } = useMusicStore.getState();
    if (!remaining && nextStatus === "Download complete.") onClose();
  };

  return createPortal(
    <AnimatePresence>
      {isOpen ? (
        <motion.div
          className="app-modal-backdrop"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.16 }}
          onMouseDown={(e) => {
            if (e.target === e.currentTarget) onClose();
          }}
        >
          <motion.div
            role="dialog"
            aria-modal="true"
            aria-label="Add music"
            className="add-music-panel"
            initial={{ opacity: 0, y: 12, scale: 0.96 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 8, scale: 0.97 }}
            transition={{ type: "spring", stiffness: 420, damping: 32 }}
          >
            <div className="mb-4 flex items-start justify-between gap-3">
              <div>
                <div className="text-[16px] font-semibold tracking-tight">Add music</div>
                <div className="text-[11px] font-semibold text-[var(--muted)]">
                  {playlistName ? `Into “${playlistName}”` : "Choose a playlist first"}
                </div>
              </div>
              <button type="button" className="ghost-btn" title="Close" onClick={onClose}>
                <IconClose />
              </button>
            </div>

            {mode === "chooser" ? (
              <div className="flex flex-col gap-2">
                <button type="button" className="add-music-option" onClick={() => void pickFiles()}>
                  <span className="add-music-option-title">Select files</span>
                  <span className="add-music-option-sub">MP3, WAV, FLAC, M4A, and more</span>
                </button>
                <button type="button" className="add-music-option" onClick={() => void pickFolder()}>
                  <span className="add-music-option-title">Select folder</span>
                  <span className="add-music-option-sub">Import every audio file inside</span>
                </button>
                <button
                  type="button"
                  className="add-music-option"
                  onClick={() => setMode("download")}
                >
                  <span className="add-music-option-title">Download music</span>
                  <span className="add-music-option-sub">YouTube, Spotify, TikTok URL</span>
                </button>
              </div>
            ) : (
              <div>
                <button
                  type="button"
                  className="mb-3 text-[11px] font-semibold text-[var(--text-dim)] hover:text-[var(--text)]"
                  onClick={() => setMode("chooser")}
                >
                  ← Back
                </button>
                <label className="app-modal-label" htmlFor="add-music-url">
                  Paste URL
                </label>
                <input
                  id="add-music-url"
                  className="app-modal-input"
                  placeholder="YouTube / Spotify / TikTok URL"
                  value={downloadUrl}
                  autoFocus
                  onChange={(e) => setDownloadUrl(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") void runDownload();
                  }}
                />
                <div className="app-modal-actions">
                  <button type="button" className="app-modal-btn ghost" onClick={onClose}>
                    Cancel
                  </button>
                  <button
                    type="button"
                    className="app-modal-btn primary"
                    disabled={!downloadUrl.trim()}
                    onClick={() => void runDownload()}
                  >
                    Download
                  </button>
                </div>
              </div>
            )}

            {status ? (
              <div className="mt-3 text-[11px] text-[var(--text-dim)]">{status}</div>
            ) : null}
          </motion.div>
        </motion.div>
      ) : null}
    </AnimatePresence>,
    document.body,
  );
}
