import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { AnimatePresence, motion } from "motion/react";
import { open } from "@tauri-apps/plugin-dialog";
import { useMusicStore } from "../stores/musicStore";

export type AnchorRect = {
  top: number;
  left: number;
  bottom: number;
  right: number;
  width: number;
  height: number;
};

type AddMusicPanelProps = {
  open: boolean;
  playlistName: string | null;
  anchor: AnchorRect | null;
  onClose: () => void;
};

type Mode = "chooser" | "download";

export function AddMusicPanel({
  open: isOpen,
  playlistName,
  anchor,
  onClose,
}: AddMusicPanelProps) {
  const rootRef = useRef<HTMLDivElement>(null);
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
    const onPointer = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) onClose();
    };
    window.addEventListener("keydown", onKey);
    window.addEventListener("mousedown", onPointer);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("mousedown", onPointer);
    };
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

  const style =
    anchor == null
      ? undefined
      : ({
          position: "fixed" as const,
          top: Math.min(anchor.bottom + 8, window.innerHeight - 280),
          left: Math.min(Math.max(12, anchor.left), window.innerWidth - 292),
          zIndex: 40,
        });

  return createPortal(
    <AnimatePresence>
      {isOpen && anchor ? (
        <motion.div
          ref={rootRef}
          role="dialog"
          aria-label="Add music"
          className="anchor-popover add-music-panel"
          style={style}
          initial={{ opacity: 0, y: -6, scale: 0.96 }}
          animate={{ opacity: 1, y: 0, scale: 1 }}
          exit={{ opacity: 0, y: -4, scale: 0.97 }}
          transition={{ type: "spring", stiffness: 460, damping: 34 }}
        >
          {mode === "chooser" ? (
            <div className="flex flex-col gap-1.5">
              <button type="button" className="add-music-option" onClick={() => void pickFiles()}>
                <span className="add-music-option-title">Select files</span>
                <span className="add-music-option-sub">MP3, WAV, FLAC, M4A…</span>
              </button>
              <button type="button" className="add-music-option" onClick={() => void pickFolder()}>
                <span className="add-music-option-title">Select folder</span>
                <span className="add-music-option-sub">Import audio from a folder</span>
              </button>
              <button
                type="button"
                className="add-music-option"
                onClick={() => setMode("download")}
              >
                <span className="add-music-option-title">Download music</span>
                <span className="add-music-option-sub">YouTube / Spotify / TikTok</span>
              </button>
            </div>
          ) : (
            <div>
              <button
                type="button"
                className="mb-2 text-[11px] font-semibold text-[var(--text-dim)] hover:text-[var(--text)]"
                onClick={() => setMode("chooser")}
              >
                ← Back
              </button>
              <input
                className="app-modal-input"
                placeholder="Paste URL"
                value={downloadUrl}
                autoFocus
                onChange={(e) => setDownloadUrl(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") void runDownload();
                }}
              />
              <div className="mt-2 flex justify-end gap-2">
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
            <div className="mt-2 text-[11px] text-[var(--text-dim)]">{status}</div>
          ) : null}
        </motion.div>
      ) : null}
    </AnimatePresence>,
    document.body,
  );
}
