import type { PongSnapshot } from "./wsProtocol.js";

interface MotionSample {
  tick: number;
  arrivedAt: number;
  x: number;
  y: number;
  vx: number;
  vy: number;
  leftY: number;
  rightY: number;
}

interface LocalPaddle {
  y: number;
  lastFrameTime: number;
  lastAxis: number;
  releaseUntil: number;
}

export interface MotionCorrection {
  x: number;
  y: number;
  startedAt: number;
}

const MILLISECONDS_PER_SECOND = 1000;
// Mirror the normalized gameplay geometry in GameConstants.cs for prediction and drawing.
const TICKS_PER_SECOND = 60;
export const LEFT_PADDLE_CENTER_X = 0.045;
export const RIGHT_PADDLE_CENTER_X = 0.955;
export const PADDLE_HALF_WIDTH = 0.009;
export const PADDLE_HALF_HEIGHT = 0.09;
const PADDLE_SPEED = 0.85;
export const BALL_RADIUS_Y = 0.012;
const BALL_RADIUS_X = (BALL_RADIUS_Y * 9) / 16;
export const MIN_PADDLE_Y = PADDLE_HALF_HEIGHT;
export const MAX_PADDLE_Y = 1 - PADDLE_HALF_HEIGHT;
const LEFT_CONTACT_X = LEFT_PADDLE_CENTER_X + PADDLE_HALF_WIDTH + BALL_RADIUS_X;
export const RIGHT_CONTACT_X = RIGHT_PADDLE_CENTER_X - PADDLE_HALF_WIDTH - BALL_RADIUS_X;
const TOP_CONTACT_Y = BALL_RADIUS_Y;
const BOTTOM_CONTACT_Y = 1 - BALL_RADIUS_Y;
// The peer publishes its current simulated tick at 60 Hz. The browser only bridges
// the time until the next tick, stopping at any collision it cannot simulate.
const MAX_EXTRAPOLATION_SECONDS = 2 / TICKS_PER_SECOND;
const MAX_CONTIGUOUS_TICK_GAP = 2;
const MOTION_CORRECTION_MS = 30;
const MAX_SMOOTH_CORRECTION = 0.015;
const MOTION_ERROR_EPSILON = 0.0001;
const VELOCITY_ERROR_EPSILON = 0.000001;
// The local paddle only predicts the short browser-to-peer input delivery time.
const MAX_LOCAL_FRAME_SECONDS = 0.05;
const LOCAL_PREDICTION_LEAD_Y = (2 * PADDLE_SPEED) / TICKS_PER_SECOND;
const LOCAL_RELEASE_GRACE_MS = (2 * MILLISECONDS_PER_SECOND) / TICKS_PER_SECOND;
const PADDLE_RECONCILIATION_SECONDS = 0.05;

function clamp(value: unknown, min: number, max: number): number {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : min;
}

export class MotionModel {
  private readonly motionSamples: MotionSample[] = [];
  private motionCorrection: MotionCorrection | null = null;
  private localPaddle: LocalPaddle | null = null;

  get sampleCount(): number {
    return this.motionSamples.length;
  }

  get correction(): Readonly<MotionCorrection> | null {
    return this.motionCorrection;
  }

  reset(): void {
    this.motionSamples.length = 0;
    this.motionCorrection = null;
    this.localPaddle = null;
  }

  record(next: PongSnapshot, now: number): void {
    const tick = Number(next.tick);
    if (
      next.connection !== "connected" ||
      (next.phase !== "playing" && next.phase !== "countdown") ||
      !Number.isFinite(tick)
    )
      return;

    const last = this.motionSamples.at(-1);
    const sample: MotionSample = {
      tick,
      arrivedAt: now,
      x: clamp(next.ballX, 0, 1),
      y: clamp(next.ballY, 0, 1),
      vx: next.ballVx,
      vy: next.ballVy,
      leftY: clamp(next.leftY, MIN_PADDLE_Y, MAX_PADDLE_Y),
      rightY: clamp(next.rightY, MIN_PADDLE_Y, MAX_PADDLE_Y),
    };
    const duplicate =
      last &&
      tick === last.tick &&
      sample.x === last.x &&
      sample.y === last.y &&
      sample.vx === last.vx &&
      sample.vy === last.vy &&
      sample.leftY === last.leftY &&
      sample.rightY === last.rightY;
    if (duplicate) return;

    const tickGap = last ? tick - last.tick : 0;
    const sameTrajectory =
      last &&
      tickGap >= 0 &&
      tickGap <= MAX_CONTIGUOUS_TICK_GAP &&
      Math.abs(last.vx - sample.vx) <= VELOCITY_ERROR_EPSILON &&
      Math.abs(last.vy - sample.vy) <= VELOCITY_ERROR_EPSILON;
    const stateError = sameTrajectory
      ? Math.hypot(
          sample.x - (last.x + (last.vx * tickGap) / TICKS_PER_SECOND),
          sample.y - (last.y + (last.vy * tickGap) / TICKS_PER_SECOND),
        )
      : 0;
    const previousDisplay =
      sameTrajectory && (stateError > MOTION_ERROR_EPSILON || this.motionCorrection !== null)
        ? this.displayedMotion(now, next)
        : null;
    if (tickGap > MAX_CONTIGUOUS_TICK_GAP) this.motionSamples.length = 0;
    if (last && tickGap === 0 && this.motionSamples.length > 0) this.motionSamples.pop();
    this.motionSamples.push(sample);
    if (this.motionSamples.length > 2) this.motionSamples.shift();
    this.motionCorrection = null;
    if (previousDisplay) {
      const x = previousDisplay.ballX - sample.x;
      const y = previousDisplay.ballY - sample.y;
      if (Math.hypot(x, y) <= MAX_SMOOTH_CORRECTION)
        this.motionCorrection = { x, y, startedAt: now };
    }
  }

  displayedMotion(
    now: number,
    snapshot: PongSnapshot,
  ): Pick<PongSnapshot, "ballX" | "ballY" | "leftY" | "rightY"> {
    if (this.motionSamples.length === 0) {
      return {
        ballX: snapshot.ballX,
        ballY: snapshot.ballY,
        leftY: snapshot.leftY,
        rightY: snapshot.rightY,
      };
    }
    const latest = this.motionSamples.at(-1)!;
    const frameSeconds = clamp(
      (now - latest.arrivedAt) / MILLISECONDS_PER_SECOND,
      0,
      MAX_EXTRAPOLATION_SECONDS,
    );
    let seconds = frameSeconds;
    // A future bounce is unknown; stop at the first wall or paddle contact.
    if (latest.vx > 0 && latest.x <= RIGHT_CONTACT_X) {
      seconds = Math.min(seconds, Math.max(0, (RIGHT_CONTACT_X - latest.x) / latest.vx));
    } else if (latest.vx < 0 && latest.x >= LEFT_CONTACT_X) {
      seconds = Math.min(seconds, Math.max(0, (LEFT_CONTACT_X - latest.x) / latest.vx));
    }
    if (latest.vy > 0 && latest.y <= BOTTOM_CONTACT_Y) {
      seconds = Math.min(seconds, Math.max(0, (BOTTOM_CONTACT_Y - latest.y) / latest.vy));
    } else if (latest.vy < 0 && latest.y >= TOP_CONTACT_Y) {
      seconds = Math.min(seconds, Math.max(0, (TOP_CONTACT_Y - latest.y) / latest.vy));
    }
    let ballX = latest.x + latest.vx * seconds;
    let ballY = latest.y + latest.vy * seconds;
    if (this.motionCorrection) {
      const remaining = 1 - (now - this.motionCorrection.startedAt) / MOTION_CORRECTION_MS;
      if (remaining > 0) {
        ballX += this.motionCorrection.x * remaining;
        ballY += this.motionCorrection.y * remaining;
      } else {
        this.motionCorrection = null;
      }
    }
    // The smoothing offset must obey the same contact bounds as extrapolation.
    if (latest.vx > 0 && latest.x <= RIGHT_CONTACT_X) ballX = Math.min(ballX, RIGHT_CONTACT_X);
    if (latest.vx < 0 && latest.x >= LEFT_CONTACT_X) ballX = Math.max(ballX, LEFT_CONTACT_X);
    if (latest.vy > 0 && latest.y <= BOTTOM_CONTACT_Y) ballY = Math.min(ballY, BOTTOM_CONTACT_Y);
    if (latest.vy < 0 && latest.y >= TOP_CONTACT_Y) ballY = Math.max(ballY, TOP_CONTACT_Y);

    const previous = this.motionSamples.length > 1 ? this.motionSamples[0] : null;
    const tickGap = previous ? latest.tick - previous.tick : 0;
    const paddleVelocity = (newY: number, oldY: number): number => {
      if (tickGap <= 0 || tickGap > MAX_CONTIGUOUS_TICK_GAP) return 0;
      const velocity = ((newY - oldY) * TICKS_PER_SECOND) / tickGap;
      return Math.abs(velocity) <= PADDLE_SPEED + VELOCITY_ERROR_EPSILON ? velocity : 0;
    };
    const leftVelocity = previous ? paddleVelocity(latest.leftY, previous.leftY) : 0;
    const rightVelocity = previous ? paddleVelocity(latest.rightY, previous.rightY) : 0;
    return {
      ballX: clamp(ballX, 0, 1),
      ballY: clamp(ballY, TOP_CONTACT_Y, BOTTOM_CONTACT_Y),
      leftY: clamp(latest.leftY + leftVelocity * frameSeconds, MIN_PADDLE_Y, MAX_PADDLE_Y),
      rightY: clamp(latest.rightY + rightVelocity * frameSeconds, MIN_PADDLE_Y, MAX_PADDLE_Y),
    };
  }

  displayedLocalPaddle(now: number, snapshot: PongSnapshot, axis: number): number | null {
    const isActive =
      snapshot.connection === "connected" &&
      (snapshot.phase === "countdown" || snapshot.phase === "playing") &&
      (snapshot.role === "host" || snapshot.role === "guest");
    if (!isActive) {
      this.localPaddle = null;
      return null;
    }

    const authoritativeY = clamp(
      snapshot.role === "host" ? snapshot.leftY : snapshot.rightY,
      MIN_PADDLE_Y,
      MAX_PADDLE_Y,
    );
    if (!this.localPaddle) {
      this.localPaddle = { y: authoritativeY, lastFrameTime: now, lastAxis: axis, releaseUntil: 0 };
      return this.localPaddle.y;
    }

    const seconds = clamp(
      (now - this.localPaddle.lastFrameTime) / MILLISECONDS_PER_SECOND,
      0,
      MAX_LOCAL_FRAME_SECONDS,
    );
    if (this.localPaddle.lastAxis !== 0 && axis === 0) {
      this.localPaddle.releaseUntil = now + LOCAL_RELEASE_GRACE_MS;
    }
    const predictedY = clamp(
      this.localPaddle.y + axis * PADDLE_SPEED * seconds,
      MIN_PADDLE_Y,
      MAX_PADDLE_Y,
    );
    if (axis > 0)
      this.localPaddle.y = Math.max(
        authoritativeY,
        Math.min(predictedY, authoritativeY + LOCAL_PREDICTION_LEAD_Y),
      );
    else if (axis < 0)
      this.localPaddle.y = Math.min(
        authoritativeY,
        Math.max(predictedY, authoritativeY - LOCAL_PREDICTION_LEAD_Y),
      );

    // The WebSocket snapshot can trail a just-pressed key by one or two ticks.
    const difference = authoritativeY - this.localPaddle.y;
    if (
      (axis === 0 && now >= this.localPaddle.releaseUntil) ||
      (axis !== 0 && difference * axis > 0)
    ) {
      const correction = 1 - Math.exp(-seconds / PADDLE_RECONCILIATION_SECONDS);
      this.localPaddle.y = clamp(
        this.localPaddle.y + difference * correction,
        MIN_PADDLE_Y,
        MAX_PADDLE_Y,
      );
    }
    this.localPaddle.lastAxis = axis;
    this.localPaddle.lastFrameTime = now;
    return this.localPaddle.y;
  }
}
