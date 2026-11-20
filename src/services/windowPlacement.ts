import { openUrl } from "@tauri-apps/plugin-opener";
import {
  currentMonitor,
  getCurrentWindow,
  PhysicalPosition,
} from "@tauri-apps/api/window";

const TOP_MARGIN = 36;
const DONATION_URL = "https://buy.stripe.com/9B63cu3RE2ouaKgcHLcjS00";

/** Move the widget back to the top center of the monitor it's on. */
export async function recenterWindow() {
  try {
    const win = getCurrentWindow();
    const monitor = await currentMonitor();
    if (!monitor) return;
    const outer = await win.outerSize();
    const x =
      monitor.position.x + Math.round((monitor.size.width - outer.width) / 2);
    const y = monitor.position.y + Math.round(TOP_MARGIN * monitor.scaleFactor);
    await win.setPosition(new PhysicalPosition(x, y));
  } catch {
    /* browser preview has no window to move */
  }
}

export async function openDonationPage() {
  try {
    await openUrl(DONATION_URL);
  } catch {
    window.open(DONATION_URL, "_blank", "noopener");
  }
}
