import type { MusicEffects } from "./types";
import { trackPlaybackUrl } from "./musicApi";

type EngineHandlers = {
  onTime?: (current: number, duration: number) => void;
  onEnded?: () => void;
  onPlayState?: (playing: boolean) => void;
  onError?: (message: string) => void;
};

const BASS_GAIN_DB = [0, 7, 12, 16] as const;

function mimeForPath(filePath: string): string {
  const ext = filePath.split(".").pop()?.toLowerCase() ?? "";
  if (ext === "mp3") return "audio/mpeg";
  if (ext === "wav") return "audio/wav";
  if (ext === "flac") return "audio/flac";
  if (ext === "ogg" || ext === "oga") return "audio/ogg";
  if (ext === "m4a" || ext === "mp4" || ext === "aac") return "audio/mp4";
  if (ext === "webm") return "audio/webm";
  return "audio/*";
}

/**
 * HTMLAudioElement + Web Audio graph for bass / reverb.
 *
 * Tauri `asset://` / asset.localhost media is opaque to MediaElementSource in
 * WebView2, so we fetch the file into a blob: URL first, then wire effects.
 */
export class AudioEngine {
  private els = [new Audio(), new Audio()];
  private activeIndex = 0;
  private handlers: EngineHandlers = {};
  private raf = 0;
  private volume = 0.85;
  private rate = 1;
  private effects: MusicEffects = { bassBoost: 0, reverb: 0 };
  private blobUrls: (string | null)[] = [null, null];
  private slotGain: GainNode[] = [];
  private swapChain: Promise<void> = Promise.resolve();
  /** Native element loop (used for single-track playlist loop). */
  private loop = false;
  /** Guard so the near-end clock fallback only fires once per play-through. */
  private endedFallbackFired = false;
  /** File path last loaded into the audible element. */
  private sourcePath: string | null = null;
  /** Ignore ended/error events caused by swapping the file. */
  private switching = false;
  /** True only while pause() was requested, so a natural end can still advance. */
  private userPause = false;
  /** Drop a late `ended` from the file we just left. */
  private ignoreEndedUntil = 0;
  private lastTickAt = 0;
  private lastMediaTime = 0;
  private stalledForMs = 0;

  private ctx: AudioContext | null = null;
  private bass: BiquadFilterNode | null = null;
  private dry: GainNode | null = null;
  private wet: GainNode | null = null;
  private delay: DelayNode | null = null;
  private feedback: GainNode | null = null;
  private master: GainNode | null = null;

  constructor() {
    for (const el of this.els) this.bindElement(el);
  }

  private get audio() {
    return this.els[this.activeIndex];
  }

  private bindElement(el: HTMLAudioElement) {
    el.preload = "auto";
    // Do not set crossOrigin , asset:// / local file URLs fail CORS with anonymous.
    el.addEventListener("ended", () => {
      if (this.switching || !this.isActive(el)) return;
      this.finishTrack();
    });
    el.addEventListener("play", () => {
      if (!this.isActive(el)) return;
      this.handlers.onPlayState?.(true);
    });
    el.addEventListener("pause", () => {
      if (!this.isActive(el)) return;
      this.handlers.onPlayState?.(false);
      // WebView2 can pause at the end and skip `ended`. A user pause sets
      // userPause first, so this only advances a track that finished itself.
      if (this.switching || this.userPause || this.endedFallbackFired) return;
      const duration = Number.isFinite(el.duration) ? el.duration : 0;
      const atEnd =
        el.ended || (duration > 0 && el.currentTime >= duration - 0.35);
      if (!atEnd) return;
      this.finishTrack();
    });
    el.addEventListener("error", () => {
      if (this.switching || !this.isActive(el)) return;
      this.handlers.onError?.(
        "Could not play this file. Try another format or re-download.",
      );
    });
  }

  private isActive(el: HTMLAudioElement) {
    return el === this.els[this.activeIndex];
  }

  /** Seek to start and play again (single-track loop). */
  private restartLoop() {
    // Block RAF/ended re-entry until `play` clears the flag.
    this.endedFallbackFired = true;
    try {
      this.audio.currentTime = 0;
    } catch {
      // ignore
    }
    void this.audio
      .play()
      .then(() => {
        this.endedFallbackFired = false;
      })
      .catch(() => {
        this.endedFallbackFired = false;
        this.handlers.onError?.("Could not restart looped track.");
      });
    this.startClock();
  }

  setHandlers(handlers: EngineHandlers) {
    this.handlers = handlers;
  }

  /** Loop the current song, or move on when loop is off. */
  private finishTrack() {
    if (this.switching || this.endedFallbackFired) return;
    if (performance.now() < this.ignoreEndedUntil) return;
    if (this.loop || this.audio.loop) {
      this.restartLoop();
      return;
    }
    this.endedFallbackFired = true;
    this.userPause = true;
    this.stopClock();
    try {
      this.audio.pause();
    } catch {
      // ignore
    }
    this.handlers.onEnded?.();
  }

  private startClock() {
    cancelAnimationFrame(this.raf);
    this.lastTickAt = 0;
    this.stalledForMs = 0;
    const tick = (now: number) => {
      const current = this.audio.currentTime || 0;
      const duration = Number.isFinite(this.audio.duration)
        ? this.audio.duration
        : 0;
      this.handlers.onTime?.(current, duration);
      if (!this.switching && !this.endedFallbackFired && !this.userPause) {
        const atEnd =
          this.audio.ended ||
          (duration > 0 &&
            current >= duration - (this.audio.paused ? 0.35 : 0.08));
        let stalled = false;
        if (!this.audio.paused && duration > 0 && current > 0.25) {
          const delta = this.lastTickAt > 0 ? now - this.lastTickAt : 0;
          if (Math.abs(current - this.lastMediaTime) < 0.02) {
            this.stalledForMs += delta;
          } else {
            this.stalledForMs = 0;
          }
          stalled = this.stalledForMs >= 900 && duration - current <= 1.25;
        } else {
          this.stalledForMs = 0;
        }
        this.lastMediaTime = current;
        this.lastTickAt = now;
        // WebView2 + MediaElementSource can skip `ended` or pause early.
        if (atEnd || stalled) {
          this.finishTrack();
          return;
        }
      } else {
        this.lastMediaTime = current;
        this.lastTickAt = now;
      }
      this.raf = requestAnimationFrame(tick);
    };
    this.raf = requestAnimationFrame(tick);
  }

  private stopClock() {
    cancelAnimationFrame(this.raf);
  }

  private revokeSlot(index: number) {
    const url = this.blobUrls[index];
    if (!url) return;
    URL.revokeObjectURL(url);
    this.blobUrls[index] = null;
  }

  /** Load file bytes into a blob URL so Web Audio can process the stream. */
  private async resolvePlayableUrl(filePath: string, slot = this.activeIndex): Promise<string> {
    const assetUrl = trackPlaybackUrl(filePath);
    const toBlobUrl = (data: BlobPart) => {
      this.revokeSlot(slot);
      const blob = new Blob([data], { type: mimeForPath(filePath) });
      const url = URL.createObjectURL(blob);
      this.blobUrls[slot] = url;
      return url;
    };

    try {
      const res = await fetch(assetUrl);
      if (!res.ok) throw new Error(`fetch ${res.status}`);
      return toBlobUrl(await res.arrayBuffer());
    } catch {
      // asset:// fetch can fail; read via Tauri FS instead.
    }

    try {
      const { readFile } = await import("@tauri-apps/plugin-fs");
      return toBlobUrl(await readFile(filePath));
    } catch {
      // Last resort: direct asset URL (playback works; effects may be silent).
      this.revokeSlot(slot);
      return assetUrl;
    }
  }

  private ensureGraph() {
    if (this.ctx) return;

    const AudioCtx =
      window.AudioContext ||
      (window as unknown as { webkitAudioContext: typeof AudioContext })
        .webkitAudioContext;
    const ctx = new AudioCtx();
    this.ctx = ctx;

    const bass = ctx.createBiquadFilter();
    bass.type = "lowshelf";
    bass.frequency.value = 180;
    bass.Q.value = 0.7;
    this.bass = bass;

    const dry = ctx.createGain();
    this.dry = dry;

    const delay = ctx.createDelay(1.0);
    delay.delayTime.value = 0.26;
    this.delay = delay;

    const feedback = ctx.createGain();
    feedback.gain.value = 0.32;
    this.feedback = feedback;

    const wet = ctx.createGain();
    this.wet = wet;

    const master = ctx.createGain();
    this.master = master;

    for (let i = 0; i < this.els.length; i += 1) {
      const source = ctx.createMediaElementSource(this.els[i]);
      const gain = ctx.createGain();
      gain.gain.value = i === this.activeIndex ? 1 : 0;
      source.connect(gain);
      gain.connect(bass);
      this.slotGain[i] = gain;
    }

    bass.connect(dry);
    dry.connect(master);

    bass.connect(delay);
    delay.connect(feedback);
    feedback.connect(delay);
    delay.connect(wet);
    wet.connect(master);

    master.connect(ctx.destination);

    this.applyEffectsToGraph();
    this.applyVolume();
  }

  private applyEffectsToGraph() {
    if (!this.bass || !this.dry || !this.wet || !this.feedback) return;

    const level = Math.min(3, Math.max(0, Math.round(this.effects.bassBoost)));
    this.bass.gain.value = BASS_GAIN_DB[level] ?? 0;

    const amount = Math.min(1, Math.max(0, this.effects.reverb));
    if (amount > 0.001) {
      this.dry.gain.value = 1 - amount * 0.28;
      this.wet.gain.value = amount * 0.55;
      this.feedback.gain.value = 0.18 + amount * 0.28;
    } else {
      this.dry.gain.value = 1;
      this.wet.gain.value = 0;
      this.feedback.gain.value = 0;
    }
  }

  /** Repeat the current element when the loop button is on. */
  setLoop(enabled: boolean) {
    this.loop = enabled;
    for (const el of this.els) el.loop = enabled;
  }

  /**
   * Swap in another file at the playhead. The current element keeps sounding
   * until the new one is buffered, then the two crossfade.
   */
  replaceSource(filePath: string) {
    const run = this.swapChain.then(() => this.swapTo(filePath));
    this.swapChain = run.then(
      () => undefined,
      () => undefined,
    );
    return run;
  }

  private async swapTo(filePath: string) {
    this.ensureGraph();
    if (this.ctx?.state === "suspended") await this.ctx.resume();
    const from = this.activeIndex;
    const to = 1 - from;
    const incoming = this.els[to];
    const outgoing = this.els[from];
    const wasPlaying = !outgoing.paused && outgoing.duration > 0;

    try {
      const url = await this.resolvePlayableUrl(filePath, to);
      incoming.src = url;
      incoming.volume = 1;
      incoming.playbackRate = this.rate;
      incoming.loop = this.loop;
      incoming.load();
      await waitFor(incoming, "loadedmetadata", () => incoming.readyState >= 1);
      const place = () => {
        const at = outgoing.currentTime || 0;
        const duration = Number.isFinite(incoming.duration) ? incoming.duration : 0;
        return duration > 0.25
          ? Math.min(Math.max(0, at), duration - 0.05)
          : Math.max(0, at);
      };
      const target = place();
      if (target > 0) {
        incoming.currentTime = target;
        await waitFor(incoming, "seeked", () => Math.abs(incoming.currentTime - target) < 0.2);
      }
      if (incoming.readyState < HTMLMediaElement.HAVE_FUTURE_DATA) {
        await waitFor(incoming, "canplay", () => incoming.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA);
      }
      if (!wasPlaying) {
        this.activeIndex = to;
        this.setSlotGain(to, 1);
        this.setSlotGain(from, 0);
        outgoing.pause();
        this.revokeSlot(from);
        this.sourcePath = filePath;
        return;
      }
      this.setSlotGain(to, 0);
      await incoming.play();
      this.activeIndex = to;
      this.crossfade(to, from);
      await delay(90);
      if (this.activeIndex === to) {
        outgoing.pause();
        this.revokeSlot(from);
      }
      this.sourcePath = filePath;
      this.startClock();
    } catch (err) {
      incoming.pause();
      this.setSlotGain(to, 0);
      if (this.activeIndex === to) this.activeIndex = from;
      this.setSlotGain(from, 1);
      throw err;
    }
  }

  private setSlotGain(index: number, value: number) {
    const gain = this.slotGain[index];
    const ctx = this.ctx;
    if (!gain || !ctx) return;
    gain.gain.cancelScheduledValues(ctx.currentTime);
    gain.gain.setValueAtTime(value, ctx.currentTime);
  }

  private crossfade(to: number, from: number) {
    const ctx = this.ctx;
    const next = this.slotGain[to];
    const prev = this.slotGain[from];
    if (!ctx || !next || !prev) return;
    const now = ctx.currentTime;
    next.gain.cancelScheduledValues(now);
    prev.gain.cancelScheduledValues(now);
    next.gain.setValueAtTime(0, now);
    prev.gain.setValueAtTime(1, now);
    next.gain.linearRampToValueAtTime(1, now + 0.07);
    prev.gain.linearRampToValueAtTime(0, now + 0.07);
  }

  getSourcePath() {
    return this.sourcePath;
  }

  async load(filePath: string) {
    this.switching = true;
    this.endedFallbackFired = true;
    this.audio.pause();
    this.stopClock();
    const other = 1 - this.activeIndex;
    this.els[other].pause();
    this.setSlotGain(other, 0);
    this.setSlotGain(this.activeIndex, 1);
    let url: string;
    try {
      url = await this.resolvePlayableUrl(filePath, this.activeIndex);
    } catch (err) {
      this.switching = false;
      throw err;
    }
    this.revokeSlot(other);
    this.audio.src = url;
    // Element volume stays full; master gain owns loudness once the graph is up.
    this.audio.volume = 1;
    this.audio.playbackRate = this.rate;
    this.audio.loop = this.loop;
    this.audio.load();
    await new Promise<void>((resolve, reject) => {
      const onReady = () => {
        cleanup();
        this.sourcePath = filePath;
        this.switching = false;
        resolve();
      };
      const onError = () => {
        cleanup();
        this.switching = false;
        reject(new Error("Failed to load audio"));
      };
      const cleanup = () => {
        this.audio.removeEventListener("loadedmetadata", onReady);
        this.audio.removeEventListener("error", onError);
      };
      this.audio.addEventListener("loadedmetadata", onReady);
      this.audio.addEventListener("error", onError);
    });
  }

  async play() {
    this.userPause = false;
    this.ignoreEndedUntil = performance.now() + 800;
    this.ensureGraph();
    if (this.ctx?.state === "suspended") {
      await this.ctx.resume();
    }
    this.applyVolume();
    this.applyEffectsToGraph();
    this.audio.playbackRate = this.rate;
    await this.audio.play();
    this.endedFallbackFired = false;
    this.stalledForMs = 0;
    this.startClock();
  }

  pause() {
    this.userPause = true;
    this.audio.pause();
    this.stopClock();
  }

  async toggle() {
    if (this.audio.paused) await this.play();
    else this.pause();
  }

  seek(seconds: number) {
    if (!Number.isFinite(seconds)) return;
    this.audio.currentTime = Math.max(0, seconds);
  }

  setVolume(value: number) {
    this.volume = Math.min(1, Math.max(0, value));
    this.applyVolume();
  }

  setRate(value: number) {
    this.rate = Math.min(2, Math.max(0.5, value));
    for (const el of this.els) el.playbackRate = this.rate;
  }

  setEffects(effects: MusicEffects) {
    this.effects = {
      bassBoost: Math.min(3, Math.max(0, Math.round(effects.bassBoost))),
      reverb: Math.min(1, Math.max(0, effects.reverb)),
    };
    this.applyEffectsToGraph();
  }

  getEffects() {
    return this.effects;
  }

  private applyVolume() {
    this.audio.muted = false;
    if (this.master) {
      this.audio.volume = 1;
      this.master.gain.value = this.volume;
    } else {
      this.audio.volume = this.volume;
    }
  }

  get currentTime() {
    return this.audio.currentTime || 0;
  }

  get duration() {
    return Number.isFinite(this.audio.duration) ? this.audio.duration : 0;
  }

  get paused() {
    return this.audio.paused;
  }

  dispose() {
    this.pause();
    this.stopClock();
    try {
      for (const gain of this.slotGain) gain.disconnect();
      this.bass?.disconnect();
      this.dry?.disconnect();
      this.wet?.disconnect();
      this.delay?.disconnect();
      this.feedback?.disconnect();
      this.master?.disconnect();
    } catch {
      // Graph may already be torn down.
    }
    void this.ctx?.close();
    this.ctx = null;
    this.slotGain = [];
    this.bass = null;
    this.dry = null;
    this.wet = null;
    this.delay = null;
    this.feedback = null;
    this.master = null;
    for (const el of this.els) {
      el.removeAttribute("src");
      el.load();
    }
    this.revokeSlot(0);
    this.revokeSlot(1);
  }
}

function waitFor(el: HTMLAudioElement, event: string, ready: () => boolean) {
  if (ready()) return Promise.resolve();
  return new Promise<void>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      cleanup();
      resolve();
    }, 1500);
    const onReady = () => {
      cleanup();
      resolve();
    };
    const onError = () => {
      cleanup();
      reject(new Error("Failed to load audio"));
    };
    const cleanup = () => {
      window.clearTimeout(timer);
      el.removeEventListener(event, onReady);
      el.removeEventListener("error", onError);
    };
    el.addEventListener(event, onReady);
    el.addEventListener("error", onError);
  });
}

function delay(ms: number) {
  return new Promise<void>((resolve) => {
    window.setTimeout(resolve, ms);
  });
}

export const audioEngine = new AudioEngine();
