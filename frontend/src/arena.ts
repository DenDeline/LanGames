import {
  BALL_RADIUS_Y,
  LEFT_PADDLE_CENTER_X,
  MAX_PADDLE_Y,
  MIN_PADDLE_Y,
  PADDLE_HALF_HEIGHT,
  PADDLE_HALF_WIDTH,
  RIGHT_PADDLE_CENTER_X,
  type MotionModel,
} from "./motion.js";
import type { FeedbackPulse } from "./feedback.js";
import type { PongSnapshot } from "./snapshot.js";

interface ArenaCache {
  width: number;
  height: number;
  dpr: number;
  pixelWidth: number;
  pixelHeight: number;
  backgroundCanvas: HTMLCanvasElement;
  vignetteCanvas: HTMLCanvasElement;
}

interface TrailPoint {
  x: number;
  y: number;
  at: number;
}

const MAX_CANVAS_DPR = 3;
const TRAIL_LIFETIME_MS = 150;
const TRAIL_SAMPLE_INTERVAL_MS = 16;

function clamp(value: unknown, min: number, max: number): number {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : min;
}

export class ArenaRenderer {
  private readonly ctx: CanvasRenderingContext2D | null;
  private arenaCache: ArenaCache | null = null;
  private readonly trail: TrailPoint[] = [];
  private lastTrailSampleAt = 0;

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private readonly motion: MotionModel,
    private readonly getSnapshot: () => PongSnapshot,
    private readonly getAxis: () => number,
    private readonly getPulse: () => FeedbackPulse | null,
  ) {
    this.ctx = canvas.getContext("2d");
  }

  resetTrail(): void {
    this.trail.length = 0;
    this.lastTrailSampleAt = 0;
  }

  private drawBallTrail(
    context: CanvasRenderingContext2D,
    motion: Pick<PongSnapshot, "ballX" | "ballY">,
    snapshot: PongSnapshot,
    now: number,
    width: number,
    height: number,
    ballRadius: number,
  ): void {
    if (snapshot.connection !== "connected" || snapshot.phase !== "playing") {
      this.trail.length = 0;
      return;
    }
    const last = this.trail.at(-1);
    if (last && Math.hypot(motion.ballX - last.x, motion.ballY - last.y) > 0.08)
      this.trail.length = 0;
    if (now - this.lastTrailSampleAt >= TRAIL_SAMPLE_INTERVAL_MS) {
      this.trail.push({ x: motion.ballX, y: motion.ballY, at: now });
      this.lastTrailSampleAt = now;
    }
    while (this.trail.length > 0 && now - this.trail[0].at > TRAIL_LIFETIME_MS) this.trail.shift();
    for (const point of this.trail) {
      const fade = 1 - (now - point.at) / TRAIL_LIFETIME_MS;
      context.fillStyle = `rgba(255, 247, 232, ${Math.max(0, fade * 0.2)})`;
      context.beginPath();
      context.arc(
        point.x * width,
        point.y * height,
        ballRadius * (0.45 + fade * 0.3),
        0,
        Math.PI * 2,
      );
      context.fill();
    }
  }

  private drawFeedbackBackground(
    context: CanvasRenderingContext2D,
    now: number,
    width: number,
    height: number,
  ): void {
    const pulse = this.getPulse();
    if (!pulse || pulse.kind !== "goal" || !pulse.scorer) return;
    const elapsed = now - pulse.startedAt;
    if (elapsed >= 500) return;
    const opacity = (1 - elapsed / 500) * 0.16;
    context.fillStyle =
      pulse.scorer === "left"
        ? `rgba(102, 232, 223, ${opacity})`
        : `rgba(255, 159, 145, ${opacity})`;
    context.fillRect(0, 0, width, height);
  }

  private drawFeedbackRing(
    context: CanvasRenderingContext2D,
    now: number,
    width: number,
    height: number,
  ): void {
    const pulse = this.getPulse();
    if (!pulse || pulse.kind === "goal") return;
    const elapsed = now - pulse.startedAt;
    if (elapsed >= 260) return;
    const progress = elapsed / 260;
    context.strokeStyle = `rgba(255, 247, 232, ${(1 - progress) * 0.55})`;
    context.lineWidth = Math.max(1, height * 0.004 * (1 - progress));
    context.beginPath();
    context.arc(
      pulse.x * width,
      pulse.y * height,
      height * (0.016 + progress * 0.045),
      0,
      Math.PI * 2,
    );
    context.stroke();
  }

  resize(
    width: number,
    height: number,
    dpr: number = Math.min(window.devicePixelRatio || 1, MAX_CANVAS_DPR),
  ): void {
    if (!this.ctx || width <= 0 || height <= 0) return;
    const pixelWidth = Math.round(width * dpr);
    const pixelHeight = Math.round(height * dpr);
    if (
      this.arenaCache &&
      this.arenaCache.width === width &&
      this.arenaCache.height === height &&
      this.arenaCache.dpr === dpr
    )
      return;
    this.canvas.width = pixelWidth;
    this.canvas.height = pixelHeight;

    const backgroundCanvas = document.createElement("canvas");
    backgroundCanvas.width = pixelWidth;
    backgroundCanvas.height = pixelHeight;
    const backgroundCtx = backgroundCanvas.getContext("2d");
    const vignetteCanvas = document.createElement("canvas");
    vignetteCanvas.width = pixelWidth;
    vignetteCanvas.height = pixelHeight;
    const vignetteCtx = vignetteCanvas.getContext("2d");
    if (!backgroundCtx || !vignetteCtx) return;
    backgroundCtx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const background = backgroundCtx.createLinearGradient(0, 0, width, height);
    background.addColorStop(0, "#122639");
    background.addColorStop(0.5, "#0c1a2c");
    background.addColorStop(1, "#172438");
    backgroundCtx.fillStyle = background;
    backgroundCtx.fillRect(0, 0, width, height);

    backgroundCtx.strokeStyle = "rgba(126, 194, 203, 0.055)";
    backgroundCtx.lineWidth = 1;
    const step = Math.max(24, width / 28);
    backgroundCtx.beginPath();
    for (let x = step; x < width; x += step) {
      backgroundCtx.moveTo(x, 0);
      backgroundCtx.lineTo(x, height);
    }
    for (let y = step; y < height; y += step) {
      backgroundCtx.moveTo(0, y);
      backgroundCtx.lineTo(width, y);
    }
    backgroundCtx.stroke();

    backgroundCtx.strokeStyle = "rgba(189, 225, 232, 0.26)";
    backgroundCtx.lineWidth = Math.max(1, width * 0.0015);
    backgroundCtx.setLineDash([Math.max(7, height * 0.022), Math.max(7, height * 0.022)]);
    backgroundCtx.beginPath();
    backgroundCtx.moveTo(width / 2, 0);
    backgroundCtx.lineTo(width / 2, height);
    backgroundCtx.stroke();
    backgroundCtx.setLineDash([]);
    backgroundCtx.beginPath();
    backgroundCtx.arc(width / 2, height / 2, height * 0.105, 0, Math.PI * 2);
    backgroundCtx.stroke();

    vignetteCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const vignette = vignetteCtx.createRadialGradient(
      width / 2,
      height / 2,
      height * 0.2,
      width / 2,
      height / 2,
      width * 0.75,
    );
    vignette.addColorStop(0, "rgba(0, 0, 0, 0)");
    vignette.addColorStop(1, "rgba(2, 7, 15, 0.34)");
    vignetteCtx.fillStyle = vignette;
    vignetteCtx.fillRect(0, 0, width, height);
    this.arenaCache = {
      width,
      height,
      dpr,
      pixelWidth,
      pixelHeight,
      backgroundCanvas,
      vignetteCanvas,
    };
  }

  draw(now: number = performance.now()): void {
    const ctx = this.ctx;
    if (!ctx) return;
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_CANVAS_DPR);
    if (this.arenaCache && dpr !== this.arenaCache.dpr)
      this.resize(this.canvas.clientWidth, this.canvas.clientHeight, dpr);
    if (!this.arenaCache) return;
    const { width, height, pixelWidth, pixelHeight, backgroundCanvas, vignetteCanvas } =
      this.arenaCache;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.drawImage(backgroundCanvas, 0, 0, pixelWidth, pixelHeight);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const reducedMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;
    if (!reducedMotion) this.drawFeedbackBackground(ctx, now, width, height);

    const snapshot = this.getSnapshot();
    const paddleWidth = width * PADDLE_HALF_WIDTH * 2;
    const paddleHeight = height * PADDLE_HALF_HEIGHT * 2;
    const motion = this.motion.displayedMotion(now, snapshot);
    const localY = this.motion.displayedLocalPaddle(now, snapshot, this.getAxis());
    const leftY = snapshot.role === "host" && localY !== null ? localY : motion.leftY;
    const rightY = snapshot.role === "guest" && localY !== null ? localY : motion.rightY;
    this.drawPaddle(
      ctx,
      width * LEFT_PADDLE_CENTER_X - paddleWidth / 2,
      clamp(leftY, MIN_PADDLE_Y, MAX_PADDLE_Y) * height - paddleHeight / 2,
      paddleWidth,
      paddleHeight,
      "#66e8df",
    );
    this.drawPaddle(
      ctx,
      width * RIGHT_PADDLE_CENTER_X - paddleWidth / 2,
      clamp(rightY, MIN_PADDLE_Y, MAX_PADDLE_Y) * height - paddleHeight / 2,
      paddleWidth,
      paddleHeight,
      "#ff9f91",
    );

    const ballX = clamp(motion.ballX, 0, 1) * width;
    const ballY = clamp(motion.ballY, 0, 1) * height;
    const ballRadius = Math.max(4, height * BALL_RADIUS_Y);
    if (!reducedMotion) this.drawBallTrail(ctx, motion, snapshot, now, width, height, ballRadius);
    ctx.save();
    ctx.shadowColor = "#fff6df";
    ctx.shadowBlur = ballRadius * 3;
    ctx.fillStyle = "#fff7e8";
    ctx.beginPath();
    ctx.arc(ballX, ballY, ballRadius, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
    if (!reducedMotion) this.drawFeedbackRing(ctx, now, width, height);

    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.drawImage(vignetteCanvas, 0, 0, pixelWidth, pixelHeight);
  }

  private drawPaddle(
    context: CanvasRenderingContext2D,
    x: number,
    y: number,
    width: number,
    height: number,
    color: string,
  ): void {
    context.save();
    context.shadowColor = color;
    context.shadowBlur = Math.max(10, width * 1.4);
    context.fillStyle = color;
    context.beginPath();
    context.roundRect(x, y, width, height, Math.min(width / 2, 7));
    context.fill();
    context.restore();
  }
}
