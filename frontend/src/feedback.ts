import type { GameEvent, GameEventKind, PongSnapshot } from "./snapshot.js";
import type { SoundController } from "./sound.js";

const MAX_SEEN_EVENTS = 128;

export interface FeedbackPulse {
  kind: GameEventKind;
  x: number;
  y: number;
  startedAt: number;
  scorer: "left" | "right" | null;
}

export class FeedbackController {
  private readonly seenEventIds = new Set<string>();
  private readonly seenEventOrder: string[] = [];
  private seenEventRound: number | null = null;
  private resyncFeedbackOnNextSnapshot = false;
  private feedbackPulse: FeedbackPulse | null = null;
  private scoreFlashTimer: number | undefined;

  constructor(
    private readonly leftScore: HTMLElement,
    private readonly rightScore: HTMLElement,
    private readonly sound: SoundController,
  ) {}

  get pulse(): FeedbackPulse | null {
    return this.feedbackPulse;
  }

  hasSeenEvent(id: string): boolean {
    return this.seenEventIds.has(id);
  }

  clearPulse(): void {
    this.feedbackPulse = null;
  }

  markReconnect(): void {
    this.resyncFeedbackOnNextSnapshot = true;
  }

  private flashScore(side: "left" | "right"): void {
    const scoreSide = (side === "left" ? this.leftScore : this.rightScore).parentElement;
    if (!scoreSide) return;
    scoreSide.classList.remove("is-scored");
    void scoreSide.offsetWidth;
    scoreSide.classList.add("is-scored");
    clearTimeout(this.scoreFlashTimer);
    this.scoreFlashTimer = setTimeout(() => scoreSide.classList.remove("is-scored"), 450);
  }

  private rememberEvent(id: string): void {
    if (this.seenEventIds.has(id)) return;
    this.seenEventIds.add(id);
    this.seenEventOrder.push(id);
    if (this.seenEventOrder.length > MAX_SEEN_EVENTS)
      this.seenEventIds.delete(this.seenEventOrder.shift()!);
  }

  private showFeedback(event: GameEvent, next: PongSnapshot): void {
    const scorer = event.kind === "goal" ? (event.x < 0.5 ? "right" : "left") : null;
    this.feedbackPulse = {
      kind: event.kind,
      x: event.x,
      y: event.y,
      startedAt: performance.now(),
      scorer,
    };
    if (scorer) this.flashScore(scorer);
    const localScored =
      scorer !== null &&
      ((scorer === "left" && next.role === "host") ||
        (scorer === "right" && next.role === "guest"));
    this.sound.playFeedbackSound(event.kind, localScored, next.phase === "gameover");
  }

  process(next: PongSnapshot, previous: PongSnapshot): void {
    if (next.connection !== "connected" || next.role === "none") {
      this.seenEventRound = null;
      this.seenEventIds.clear();
      this.seenEventOrder.length = 0;
      return;
    }
    const enteringSession =
      this.resyncFeedbackOnNextSnapshot ||
      this.seenEventRound === null ||
      previous.role !== next.role ||
      previous.connection !== "connected";
    this.resyncFeedbackOnNextSnapshot = false;
    if (enteringSession || next.roundId !== this.seenEventRound) {
      this.seenEventRound = next.roundId;
      this.seenEventIds.clear();
      this.seenEventOrder.length = 0;
      if (enteringSession) {
        for (const event of next.events) this.rememberEvent(event.id);
        return;
      }
    }
    for (const event of next.events) {
      if (this.seenEventIds.has(event.id)) continue;
      this.rememberEvent(event.id);
      this.showFeedback(event, next);
    }
  }
}
