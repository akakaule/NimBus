// FlowAnimator — imperative SVG dot animation for the Live Flow page (spec 020).
//
// This is a TypeScript port of the animation technique in site/demo/index.html:
// a single requestAnimationFrame loop advances a *virtual clock* (vt), and every
// traveling dot derives its position from vt via path.getPointAtLength(). React
// owns the static scene (nodes, route paths); this class owns exactly one <g>
// element (the dot layer) and never triggers a React render — per NFR-002, all
// per-frame work touches only this imperative layer.
//
// WHY a virtual clock instead of wall time or CSS animations:
// - Pause must freeze dots mid-path; with vt, pausing is "stop accumulating dt"
//   and every dot stays exactly where it was, no per-dot bookkeeping.
// - Speed is a multiplier on the timeline (vt += dt * speed), so changing it
//   mid-flight smoothly retimes every dot and pending pulse at once.
// - Hiding the tab cancels the loop entirely (NFR-003 near-zero CPU); since vt
//   only advances inside the loop, in-flight dots resume from the same spot.
// - Scheduling (pulse expiry) rides the same clock as vt-deadline entries
//   processed in the frame loop — no setTimeout, so pause/speed/hidden-tab all
//   affect pending work consistently and dispose() has nothing extra to cancel.

import { MAX_INFLIGHT_DOTS } from "./types";

const SVG_NS = "http://www.w3.org/2000/svg";

/** CSS class lit on a route path while it carries dots or an edge pulse. */
const ROUTE_ON_CLASS = "flow-route-on";

/** Virtual-ms an edge pulse stays lit (reduced-motion / overflow fallback). */
const PULSE_MS = 600;

/**
 * Per-frame wall-clock delta clamp. Long gaps (debugger, jank, tab re-show
 * races) would otherwise teleport dots; clamping turns them into a brief slowdown.
 */
const MAX_FRAME_DT = 64;

const MIN_SPEED = 0.25;
const MAX_SPEED = 4;

/**
 * Travel time: never under 650 ms, otherwise 2.8 ms per path unit. Slower than
 * the demo's tuning (420 ms / 2.2) on purpose — real traffic is far sparser
 * than the demo's simulated firehose, so each dot lingers longer to keep the
 * canvas feeling alive between events.
 */
const MIN_TRAVEL_MS = 650;
const TRAVEL_MS_PER_UNIT = 2.8;

const DEFAULT_DOT_RADIUS = 6;

/** smoothstep — gentle ease-in-out so dots launch and land softly. */
function ease(t: number): number {
  return t * t * (3 - 2 * t);
}

export interface FlowAnimatorOptions {
  /** Imperative layer the page hands over via ref — the ONLY DOM the animator owns. */
  dotLayer: SVGGElement;
  /** Resolves a route id to its rendered <path>; null if filtered out/unmounted. */
  getPathEl: (routeId: string) => SVGPathElement | null;
  /** When true, spawn() pulses edges instead of creating traveling dots (prefers-reduced-motion). */
  reducedMotion?: boolean;
}

/**
 * A queued follow-up spawn that a finished dot hands off to, so one logical
 * message can ride several route segments back-to-back (producer → topic →
 * consumer). Each link carries its own color/badge and an optional deeper link;
 * the chain survives the pulse/cap/filtered fallbacks so later legs still show.
 */
export interface SpawnChain {
  routeId: string;
  color: string;
  multiplier?: number;
  radius?: number;
  next?: SpawnChain;
}

/** Options for spawn(): the ×N badge, dot radius, and an optional continuation. */
export interface SpawnOptions {
  multiplier?: number;
  radius?: number;
  next?: SpawnChain;
}

/** One dot (and its optional ×N label) currently traveling along a path. */
interface TravelingDot {
  /** Captured at spawn; if the path unmounts mid-flight the dot finishes early. */
  pathEl: SVGPathElement;
  /** Total path length, captured at spawn so a cache invalidation can't retime it. */
  len: number;
  circle: SVGCircleElement;
  label: SVGTextElement | null;
  /** Label offset from the dot center (rides above/right of the dot). */
  labelDx: number;
  labelDy: number;
  /** Virtual-clock spawn time. */
  start: number;
  /** Virtual-ms travel duration. */
  dur: number;
  /** Next segment to spawn when this dot lands — the journey's continuation. */
  next?: SpawnChain;
}

export class FlowAnimator {
  private readonly dotLayer: SVGGElement;
  private readonly getPathEl: (routeId: string) => SVGPathElement | null;
  private readonly reducedMotion: boolean;

  /** Virtual clock (ms). Only advances inside the frame loop, scaled by speed. */
  private vt = 0;
  /** Wall-clock anchor for dt; re-anchored on tab re-show so hidden time is free. */
  private last = 0;
  private speed = 1;
  private paused = false;

  private rafId: number | null = null;
  private started = false;
  private disposed = false;

  private readonly dots: TravelingDot[] = [];

  /** getTotalLength() per routeId — invalidated by the page after layout/filter changes. */
  private readonly pathLengths = new Map<string, number>();

  /**
   * Dots currently traveling per path element. The route stays lit
   * (ROUTE_ON_CLASS) while any dot rides it; the class comes off when the
   * LAST dot finishes — same per-edge `active` counter the demo uses.
   */
  private readonly activeCounts = new Map<SVGPathElement, number>();

  /**
   * Pending pulse-off deadlines (virtual time) per path. Overlapping pulses
   * EXTEND the single deadline instead of stacking removals, so a burst of
   * fallback pulses reads as one continuous glow.
   */
  private readonly pulseDeadlines = new Map<SVGPathElement, number>();

  constructor(opts: FlowAnimatorOptions) {
    this.dotLayer = opts.dotLayer;
    this.getPathEl = opts.getPathEl;
    this.reducedMotion = opts.reducedMotion ?? false;
  }

  /** Begins the frame loop and starts honoring tab visibility (FR-008/NFR-003). */
  start(): void {
    if (this.started || this.disposed) {
      return;
    }
    this.started = true;
    document.addEventListener("visibilitychange", this.onVisibilityChange);
    this.last = performance.now();
    if (document.visibilityState !== "hidden") {
      this.scheduleFrame();
    }
  }

  /** Cancels the loop, detaches the listener, removes all owned DOM. Idempotent. */
  dispose(): void {
    if (this.disposed) {
      return;
    }
    this.disposed = true;
    this.started = false;
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId);
      this.rafId = null;
    }
    document.removeEventListener("visibilitychange", this.onVisibilityChange);
    for (const dot of this.dots) {
      dot.circle.remove();
      dot.label?.remove();
    }
    this.dots.length = 0;
    // We added ROUTE_ON_CLASS to these paths; leave the scene as we found it.
    for (const path of this.activeCounts.keys()) {
      path.classList.remove(ROUTE_ON_CLASS);
    }
    for (const path of this.pulseDeadlines.keys()) {
      path.classList.remove(ROUTE_ON_CLASS);
    }
    this.activeCounts.clear();
    this.pulseDeadlines.clear();
    this.pathLengths.clear();
  }

  /** Timeline speed multiplier, clamped to 0.25–4 (FR-007 control range). */
  setSpeed(multiplier: number): void {
    this.speed = Math.min(MAX_SPEED, Math.max(MIN_SPEED, multiplier));
  }

  /** Freezes the virtual clock; dots hold position. The cheap loop keeps running. */
  setPaused(paused: boolean): void {
    this.paused = paused;
  }

  /** Number of dots currently traveling (drives the FR-009 cap and the gauge). */
  get inFlight(): number {
    return this.dots.length;
  }

  /**
   * Drops memoized path lengths. The page calls this after layout or filter
   * changes re-render the route paths; dots already in flight keep the length
   * they were spawned with (their captured element defines their geometry).
   */
  invalidatePaths(): void {
    this.pathLengths.clear();
  }

  /**
   * Animates one event along a route. Falls back to an edge pulse when the
   * in-flight cap is hit or reduced motion is requested (FR-008/FR-009); a
   * spawn for a filtered-out/unmounted route is a no-op (nothing to draw on).
   */
  spawn(routeId: string, color: string, opts?: SpawnOptions): void {
    if (this.disposed) {
      return;
    }
    const path = this.getPathEl(routeId);
    if (path === null) {
      // Segment not currently rendered (filtered/unmounted) — skip it but hand
      // off so later legs of the journey still animate.
      this.continueChain(opts?.next);
      return;
    }
    if (this.reducedMotion || this.dots.length >= MAX_INFLIGHT_DOTS) {
      this.pulse(path);
      this.continueChain(opts?.next);
      return;
    }

    const len = this.resolveLength(routeId, path);
    if (len <= 0) {
      // Degenerate path (zero-length d) — a pulse still signals activity.
      this.pulse(path);
      this.continueChain(opts?.next);
      return;
    }

    const radius = opts?.radius ?? DEFAULT_DOT_RADIUS;
    const startPoint = path.getPointAtLength(0);

    const circle = document.createElementNS(SVG_NS, "circle");
    circle.setAttribute("r", String(radius));
    circle.setAttribute("fill", color);
    circle.setAttribute("class", "flow-dot");
    // Position immediately so the dot never flashes at (0,0) before frame one.
    circle.setAttribute("cx", String(startPoint.x));
    circle.setAttribute("cy", String(startPoint.y));
    this.dotLayer.appendChild(circle);

    const multiplier = opts?.multiplier ?? 1;
    const labelDx = radius + 3;
    const labelDy = -(radius + 2);
    let label: SVGTextElement | null = null;
    if (multiplier > 1) {
      label = document.createElementNS(SVG_NS, "text");
      label.setAttribute("class", "flow-dot-label");
      label.textContent = `×${multiplier}`;
      label.setAttribute("x", String(startPoint.x + labelDx));
      label.setAttribute("y", String(startPoint.y + labelDy));
      this.dotLayer.appendChild(label);
    }

    this.dots.push({
      pathEl: path,
      len,
      circle,
      label,
      labelDx,
      labelDy,
      start: this.vt,
      dur: Math.max(MIN_TRAVEL_MS, len * TRAVEL_MS_PER_UNIT),
      next: opts?.next,
    });

    // Light the route for the duration of the ride (demo's edge.active++).
    this.activeCounts.set(path, (this.activeCounts.get(path) ?? 0) + 1);
    path.classList.add(ROUTE_ON_CLASS);
  }

  // -------------------------------------------------------------------------
  // Frame loop
  // -------------------------------------------------------------------------

  private scheduleFrame(): void {
    this.rafId = requestAnimationFrame(this.onFrame);
  }

  private readonly onFrame = (now: number): void => {
    this.rafId = null;
    if (this.disposed) {
      return;
    }
    const dt = Math.min(MAX_FRAME_DT, now - this.last);
    this.last = now;
    if (!this.paused) {
      this.vt += dt * this.speed;
      this.updateDots();
      this.expirePulses();
    }
    this.scheduleFrame();
  };

  /**
   * FR-008/NFR-003: hidden tab → cancel the loop outright (near-zero CPU).
   * On re-show, re-anchor `last` so the hidden interval contributes no dt —
   * the virtual clock never advanced, so dots resume exactly where they froze.
   */
  private readonly onVisibilityChange = (): void => {
    if (this.disposed || !this.started) {
      return;
    }
    if (document.visibilityState === "hidden") {
      if (this.rafId !== null) {
        cancelAnimationFrame(this.rafId);
        this.rafId = null;
      }
    } else {
      this.last = performance.now();
      if (this.rafId === null) {
        this.scheduleFrame();
      }
    }
  };

  private updateDots(): void {
    // Backwards so splice() during iteration is safe.
    for (let i = this.dots.length - 1; i >= 0; i--) {
      const dot = this.dots[i];
      const t = (this.vt - dot.start) / dot.dur;
      // A layout/filter change can unmount the captured path mid-flight;
      // finish such dots early instead of animating against a detached node.
      if (t >= 1 || !dot.pathEl.isConnected) {
        this.finishDot(i, dot);
        continue;
      }
      const point = dot.pathEl.getPointAtLength(ease(Math.max(0, t)) * dot.len);
      dot.circle.setAttribute("cx", String(point.x));
      dot.circle.setAttribute("cy", String(point.y));
      if (dot.label !== null) {
        dot.label.setAttribute("x", String(point.x + dot.labelDx));
        dot.label.setAttribute("y", String(point.y + dot.labelDy));
      }
    }
  }

  private finishDot(index: number, dot: TravelingDot): void {
    dot.circle.remove();
    dot.label?.remove();
    this.dots.splice(index, 1);
    const remaining = (this.activeCounts.get(dot.pathEl) ?? 1) - 1;
    if (remaining > 0) {
      this.activeCounts.set(dot.pathEl, remaining);
    } else {
      this.activeCounts.delete(dot.pathEl);
      // Last dot off this path — pulse it off, unless a pending edge pulse is
      // still holding the glow (its deadline will release the class instead).
      if (!this.pulseDeadlines.has(dot.pathEl)) {
        dot.pathEl.classList.remove(ROUTE_ON_CLASS);
      }
    }
    // Hand the journey to its next leg, so the dot reads as one message
    // continuing producer → topic → consumer. Appending mid-frame is safe: the
    // update loop iterates backwards, so the new dot animates from next frame.
    this.continueChain(dot.next);
  }

  /** Spawns the next leg of a journey, if any. */
  private continueChain(next: SpawnChain | undefined): void {
    if (next === undefined) {
      return;
    }
    this.spawn(next.routeId, next.color, {
      multiplier: next.multiplier,
      radius: next.radius,
      next: next.next,
    });
  }

  /** Edge pulse fallback: glow the path now, schedule the off on the virtual clock. */
  private pulse(path: SVGPathElement): void {
    path.classList.add(ROUTE_ON_CLASS);
    const deadline = this.vt + PULSE_MS;
    const existing = this.pulseDeadlines.get(path);
    this.pulseDeadlines.set(path, existing !== undefined ? Math.max(existing, deadline) : deadline);
  }

  private expirePulses(): void {
    if (this.pulseDeadlines.size === 0) {
      return;
    }
    for (const [path, deadline] of this.pulseDeadlines) {
      if (this.vt < deadline) {
        continue;
      }
      this.pulseDeadlines.delete(path);
      // Keep the glow if dots are still riding this path; the last dot's
      // completion owns the class removal in that case.
      if ((this.activeCounts.get(path) ?? 0) === 0) {
        path.classList.remove(ROUTE_ON_CLASS);
      }
    }
  }

  private resolveLength(routeId: string, path: SVGPathElement): number {
    const cached = this.pathLengths.get(routeId);
    if (cached !== undefined) {
      return cached;
    }
    const len = path.getTotalLength();
    this.pathLengths.set(routeId, len);
    return len;
  }
}
