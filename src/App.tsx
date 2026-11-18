import { useEffect } from "react";
import { animate, motion, useMotionValue } from "motion/react";
import {
  PhysicalPosition,
  currentMonitor,
  getCurrentWindow,
  LogicalSize,
} from "@tauri-apps/api/window";
import { listen } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { register, unregister } from "@tauri-apps/plugin-global-shortcut";
import { Dashboard } from "./components/Dashboard";
import { Pill } from "./components/Pill";
import { useMusicStore } from "./stores/musicStore";

const PILL_SIZE = { width: 360, height: 88 };
const DASH_SIZE = { width: 760, height: 820 };
/** Logical pixels from the top of the monitor. */
const TOP_MARGIN = 36;

async function placeTopCenter() {
  const win = getCurrentWindow();
  const monitor = await currentMonitor();
  if (!monitor) return;
  const outer = await win.outerSize();
  const x =
    monitor.position.x + Math.round((monitor.size.width - outer.width) / 2);
  const y = monitor.position.y + Math.round(TOP_MARGIN * monitor.scaleFactor);
  await win.setPosition(new PhysicalPosition(x, y));
}

async function sizeWindow(open: boolean, place = false) {
  try {
    const win = getCurrentWindow();
    const size = open ? DASH_SIZE : PILL_SIZE;
    await win.setSize(new LogicalSize(size.width, size.height));
    await win.setMinSize(new LogicalSize(open ? 420 : 280, open ? 440 : 72));
    if (place) await placeTopCenter();
  } catch {
    /* best-effort in browser preview */
  }
}

export default function App() {
  const dashHeight = useMotionValue(0);
  const dashOpacity = useMotionValue(0);
  const { ready, dashboardOpen, hydrate, setDashboardOpen } = useMusicStore();

  useEffect(() => {
    void hydrate();
  }, [hydrate]);

  useEffect(() => {
    void sizeWindow(dashboardOpen);
    const h = animate(dashHeight, dashboardOpen ? 720 : 0, {
      type: "spring",
      stiffness: 380,
      damping: 34,
    });
    const o = animate(dashOpacity, dashboardOpen ? 1 : 0, {
      type: "spring",
      stiffness: 380,
      damping: 34,
    });
    return () => {
      h.stop();
      o.stop();
    };
  }, [dashboardOpen, dashHeight, dashOpacity]);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    void listen("musicwidget://open-dashboard", () => {
      setDashboardOpen(true);
    }).then((fn) => {
      unlisten = fn;
    });
    return () => unlisten?.();
  }, [setDashboardOpen]);

  useEffect(() => {
    const hotkey = "Alt+\\";
    void register(hotkey, async () => {
      const win = getCurrentWindow();
      const visible = await win.isVisible();
      if (visible) await invoke("hide_main_window");
      else await invoke("show_main_window");
    }).catch(() => undefined);
    return () => {
      void unregister(hotkey).catch(() => undefined);
    };
  }, []);

  useEffect(() => {
    void sizeWindow(false, true); // pill size, horizontally centered near top
  }, []);

  useEffect(() => {
    const blockBrowserChrome = (e: Event) => e.preventDefault();
    document.addEventListener("contextmenu", blockBrowserChrome);
    return () => document.removeEventListener("contextmenu", blockBrowserChrome);
  }, []);

  if (!ready) {
    return (
      <div className="flex h-full items-start justify-center p-3 text-sm text-[var(--muted)]">
        Loading MusicWidget…
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col items-center gap-1.5 p-3">
      <Pill />
      <motion.div
        className="w-full max-w-[760px] overflow-hidden"
        style={{ height: dashHeight, opacity: dashOpacity }}
      >
        <div className="h-[720px]">
          <Dashboard />
        </div>
      </motion.div>
    </div>
  );
}
