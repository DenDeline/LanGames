import { FeedbackController } from "./feedback.js";
import { MotionModel } from "./motion.js";
import { defaultSnapshot, isRecord, parseSnapshot, type PongSnapshot } from "./snapshot.js";

export type SnapshotSource = "websocket" | "http";

export class GameSession {
  snapshot: PongSnapshot = { ...defaultSnapshot };

  constructor(
    readonly motion: MotionModel,
    readonly feedback: FeedbackController,
    private readonly onMotionReset: () => void,
    private readonly onSnapshot: () => void,
  ) {}

  apply(data: unknown, source: SnapshotSource = "http"): void {
    if (!isRecord(data)) return;
    const next = parseSnapshot(data);
    const previous = this.snapshot;

    const changedRound =
      next.role !== previous.role ||
      next.opponentMode !== previous.opponentMode ||
      next.requestedBotId !== previous.requestedBotId ||
      next.connection !== previous.connection ||
      next.phase !== previous.phase ||
      next.roundId !== previous.roundId ||
      next.leftScore !== previous.leftScore ||
      next.rightScore !== previous.rightScore;
    const tick = Number(next.tick);
    const previousTick = Number(previous.tick);
    const backwardsTick =
      !changedRound &&
      Number.isFinite(tick) &&
      Number.isFinite(previousTick) &&
      tick < previousTick;
    // WebSocket frames are ordered. A lower tick there is a legitimate guest
    // clock rebase; an older HTTP response must not replace a newer state.
    if (backwardsTick && source !== "websocket") return;

    if (changedRound) this.motion.reset();
    if (changedRound || backwardsTick) {
      this.feedback.clearPulse();
      this.onMotionReset();
    }
    this.snapshot = next;
    this.feedback.process(next, previous);
    this.motion.record(next, performance.now());
    this.onSnapshot();
  }
}
