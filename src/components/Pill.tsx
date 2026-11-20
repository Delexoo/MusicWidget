import { useEffect, useRef } from "react";
import { animate, motion, useMotionValue, useTransform } from "motion/react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { trackCoverUrl } from "../services/music/musicApi";
import { useMusicStore } from "../stores/musicStore";
import {
  IconGear,
  IconLoop,
  IconMusic,
  IconNext,
  IconPause,
  IconPlay,
  IconPrev,
} from "./icons";

const HOVER_COL = 24;
const HOVER_GAP = 6;
const EXPAND = { type: "spring" as const, stiffness: 380, damping: 34 };

export function Pill() {
  const leaveTimer = useRef<number | null>(null);
  const {
    dashboardOpen,
    hoverExpanded,
    queue,
    index,
    playing,
    loop,
    setDashboardOpen,
    setHoverExpanded,
    togglePlay,
    next,
    prev,
    setLoop,
  } = useMusicStore();

  const track = index >= 0 ? queue[index] : null;
  const cover = trackCoverUrl(track?.coverPath);
  const expanded = hoverExpanded || dashboardOpen;
  // One progress value keeps the left column and the compensating shift in lockstep.
  // The pill is centered, so a wider pill would creep both directions; shifting left
  // by half the added width holds the body still and lets the controls grow left.
  const progress = useMotionValue(expanded ? 1 : 0);
  const hoverWidth = useTransform(progress, (v) => v * HOVER_COL);
  const hoverGap = useTransform(progress, (v) => v * HOVER_GAP);
  const hoverOpacity = useTransform(progress, (v) => v);
  const shiftX = useTransform(progress, (v) => v * -((HOVER_COL + HOVER_GAP) / 2));

  useEffect(() => {
    const anim = animate(progress, expanded ? 1 : 0, EXPAND);
    return () => anim.stop();
  }, [expanded, progress]);

  const onEnter = () => {
    if (leaveTimer.current) window.clearTimeout(leaveTimer.current);
    setHoverExpanded(true);
  };

  const onLeave = () => {
    if (dashboardOpen) return;
    leaveTimer.current = window.setTimeout(() => setHoverExpanded(false), 120);
  };

  return (
    <motion.div
      className="inline-flex max-w-full items-center overflow-hidden border border-[var(--border)] bg-[var(--surface)] px-2 py-2"
      style={{
        x: shiftX,
        borderRadius: "var(--radius-pill)",
        boxShadow: "0 4px 10px rgba(0,0,0,0.18), 0 2px 4px rgba(0,0,0,0.12)",
      }}
      onMouseEnter={onEnter}
      onMouseLeave={onLeave}
    >
      <motion.div
        className="flex shrink-0 flex-col items-center justify-center gap-1 overflow-hidden"
        style={{ width: hoverWidth, opacity: hoverOpacity, marginRight: hoverGap }}
      >
        <button
          type="button"
          className={`white-circle util ${loop ? "active-loop" : ""}`}
          title="Loop current"
          onClick={() => setLoop(!loop)}
        >
          <IconLoop size={10} />
        </button>
        <button
          type="button"
          className="white-circle util"
          title="Open dashboard"
          data-dashboard-toggle
          onClick={() => setDashboardOpen(!dashboardOpen)}
        >
          <IconGear size={11} />
        </button>
      </motion.div>

      <div className="flex shrink-0 items-center">
        <div
          className="mr-2 flex h-11 w-11 shrink-0 cursor-grab items-center justify-center overflow-hidden bg-[var(--pill-art)] active:cursor-grabbing"
          style={{ borderRadius: "var(--radius-art)" }}
          data-drag-handle
          onMouseDown={() => void getCurrentWindow().startDragging()}
          title="Drag to move"
        >
          {cover ? (
            <img
              src={cover}
              alt=""
              className="h-full w-full object-cover"
              draggable={false}
              style={{ borderRadius: 4 }}
            />
          ) : (
            <span className="text-[var(--pill-icon)]">
              <IconMusic size={18} />
            </span>
          )}
        </div>

        <div className="mr-3 w-[120px] shrink-0 overflow-hidden">
          <div className="truncate text-xs font-semibold leading-tight text-[var(--text)]">
            {track?.title ?? "Nothing playing"}
          </div>
          <div className="mt-0.5 truncate text-[10px] leading-tight text-[var(--text-dim)]">
            {track?.artist ?? "—"}
          </div>
        </div>

        <div className="flex w-[82px] shrink-0 items-center justify-end gap-1">
          <button type="button" className="white-circle sm" title="Previous" onClick={() => void prev()}>
            <IconPrev />
          </button>
          <button
            type="button"
            className="white-circle lg mx-1"
            title={playing ? "Pause" : "Play"}
            onClick={() => void togglePlay()}
          >
            {playing ? <IconPause /> : <IconPlay />}
          </button>
          <button type="button" className="white-circle sm" title="Next" onClick={() => void next()}>
            <IconNext />
          </button>
        </div>
      </div>
    </motion.div>
  );
}
