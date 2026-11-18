import { useEffect } from "react";
import { createPortal } from "react-dom";
import { AnimatePresence, motion } from "motion/react";
import { useMusicStore } from "../stores/musicStore";
import { IconClose } from "./icons";

type EfxPanelProps = {
  open: boolean;
  onClose: () => void;
};

const BASS_TICKS = [
  { level: 0, label: "Off" },
  { level: 1, label: "1" },
  { level: 2, label: "2" },
  { level: 3, label: "3" },
] as const;

export function EfxPanel({ open, onClose }: EfxPanelProps) {
  const {
    queue,
    index,
    rate,
    effects,
    vocalsOffByPath,
    setBass,
    setReverb,
    setRate,
    toggleVocals,
  } = useMusicStore();

  const track = index >= 0 ? queue[index] : null;
  const vocalsOff = track ? Boolean(vocalsOffByPath[track.filePath]) : false;

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  return createPortal(
    <AnimatePresence>
      {open ? (
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
            aria-label="Effects"
            className="efx-panel"
            initial={{ opacity: 0, y: 12, scale: 0.96 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 8, scale: 0.97 }}
            transition={{ type: "spring", stiffness: 420, damping: 32 }}
          >
            <div className="mb-4 flex items-start justify-between gap-3">
              <div>
                <div className="text-[16px] font-semibold tracking-tight">EFX</div>
                <div className="text-[11px] font-semibold text-[var(--muted)]">
                  Bass · reverb · vocals · speed
                </div>
              </div>
              <button type="button" className="ghost-btn" title="Close" onClick={onClose}>
                <IconClose />
              </button>
            </div>

            <div className="efx-section">
              <div className="efx-section-label">Bass</div>
              <div className="bass-ticks" role="radiogroup" aria-label="Bass boost">
                {BASS_TICKS.map((tick) => {
                  const active = effects.bassBoost === tick.level;
                  return (
                    <button
                      key={tick.level}
                      type="button"
                      role="radio"
                      aria-checked={active}
                      className={`bass-tick ${active ? "is-on" : ""}`}
                      onClick={() => setBass(tick.level)}
                    >
                      <span className="bass-tick-dot" />
                      <span>{tick.label}</span>
                    </button>
                  );
                })}
              </div>
            </div>

            <div className="efx-section">
              <div className="efx-row-label">
                <span>Reverb</span>
                <span className="tabular-nums">{Math.round(effects.reverb * 100)}%</span>
              </div>
              <input
                className="slider"
                type="range"
                min={0}
                max={1}
                step={0.01}
                value={effects.reverb}
                onChange={(e) => setReverb(Number(e.target.value))}
              />
            </div>

            <div className="efx-section efx-toggle-row">
              <div>
                <div className="efx-section-label" style={{ marginBottom: 2 }}>
                  Vocals
                </div>
                <div className="text-[11px] text-[var(--text-dim)]">
                  {vocalsOff ? "Instrumental (MDX HQ)" : "Original mix"}
                </div>
              </div>
              <button
                type="button"
                role="switch"
                aria-checked={!vocalsOff}
                className={`toggle-switch ${vocalsOff ? "" : "is-on"}`}
                title={vocalsOff ? "Turn vocals on" : "Remove vocals"}
                onClick={() => void toggleVocals()}
              >
                <span className="toggle-knob" />
              </button>
            </div>

            <div className="efx-section" style={{ marginBottom: 0 }}>
              <div className="efx-row-label">
                <span>Speed</span>
                <span className="tabular-nums">{rate.toFixed(2)}x</span>
              </div>
              <input
                className="slider"
                type="range"
                min={0.5}
                max={2}
                step={0.01}
                value={rate}
                onChange={(e) => setRate(Number(e.target.value))}
              />
              <div className="efx-speed-marks">
                <span>0.5x</span>
                <button type="button" className="efx-reset" onClick={() => setRate(1)}>
                  1.0x
                </button>
                <span>2.0x</span>
              </div>
            </div>
          </motion.div>
        </motion.div>
      ) : null}
    </AnimatePresence>,
    document.body,
  );
}
