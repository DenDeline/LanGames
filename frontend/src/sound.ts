import type { GameEventKind } from "./snapshot.js";

const SOUND_VOLUME_KEY = "lanpong-volume";
const SOUND_ENABLED_KEY = "lanpong-sound-enabled";

function clamp(value: unknown, min: number, max: number): number {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(max, Math.max(min, number)) : min;
}

export class SoundController {
  private audioContext: AudioContext | null = null;
  private masterGain: GainNode | null = null;
  private soundEnabled = true;
  private soundVolume = 0.45;

  constructor(
    private readonly soundToggle: HTMLButtonElement,
    private readonly volumeRange: HTMLInputElement,
  ) {}

  private updateControls(): void {
    this.soundToggle.setAttribute("aria-pressed", String(this.soundEnabled));
    const label = this.soundEnabled ? "Звук: вкл." : "Звук: выкл.";
    if (this.soundToggle.textContent !== label) this.soundToggle.textContent = label;
    this.volumeRange.value = String(Math.round(this.soundVolume * 100));
    this.volumeRange.disabled = !this.soundEnabled;
    if (this.audioContext && this.masterGain)
      this.masterGain.gain.setTargetAtTime(
        this.soundEnabled ? this.soundVolume : 0,
        this.audioContext.currentTime,
        0.015,
      );
  }

  private saveSettings(): void {
    try {
      window.localStorage.setItem(SOUND_VOLUME_KEY, String(Math.round(this.soundVolume * 100)));
      window.localStorage.setItem(SOUND_ENABLED_KEY, String(this.soundEnabled));
    } catch {
      // Private browsing can make localStorage unavailable.
    }
  }

  loadSettings(): void {
    try {
      const storedVolume = window.localStorage.getItem(SOUND_VOLUME_KEY);
      const storedEnabled = window.localStorage.getItem(SOUND_ENABLED_KEY);
      if (storedVolume !== null) this.soundVolume = clamp(storedVolume, 0, 100) / 100;
      if (storedEnabled !== null) this.soundEnabled = storedEnabled === "true";
    } catch {
      // The controls still work for this page when settings cannot be saved.
    }
    this.updateControls();
  }

  unlockAudio(): void {
    if (!this.soundEnabled || !window.AudioContext) return;
    try {
      if (!this.audioContext) {
        this.audioContext = new window.AudioContext();
        this.masterGain = this.audioContext.createGain();
        this.masterGain.gain.value = this.soundVolume;
        this.masterGain.connect(this.audioContext.destination);
      }
      if (this.audioContext.state !== "running") void this.audioContext.resume().catch(() => {});
    } catch {
      // Gameplay stays usable when the browser has no audio output.
    }
  }

  private playTone(
    frequency: number,
    endFrequency: number,
    delay: number,
    duration: number,
    gainLevel: number,
    wave: OscillatorType = "sine",
  ): void {
    if (!this.audioContext || !this.masterGain || this.audioContext.state !== "running") return;
    const start = this.audioContext.currentTime + delay;
    const oscillator = this.audioContext.createOscillator();
    const envelope = this.audioContext.createGain();
    oscillator.type = wave;
    oscillator.frequency.setValueAtTime(frequency, start);
    oscillator.frequency.exponentialRampToValueAtTime(Math.max(1, endFrequency), start + duration);
    envelope.gain.setValueAtTime(0.0001, start);
    envelope.gain.exponentialRampToValueAtTime(gainLevel, start + 0.008);
    envelope.gain.exponentialRampToValueAtTime(0.0001, start + duration);
    oscillator.connect(envelope);
    envelope.connect(this.masterGain);
    oscillator.onended = () => {
      oscillator.disconnect();
      envelope.disconnect();
    };
    oscillator.start(start);
    oscillator.stop(start + duration + 0.01);
  }

  playChallengeSound(): void {
    if (!this.soundEnabled || this.soundVolume <= 0) return;
    this.unlockAudio();
    this.playTone(660, 880, 0, 0.13, 0.1);
    this.playTone(880, 1175, 0.17, 0.18, 0.1);
  }

  playFeedbackSound(kind: GameEventKind, localScored = false, gameOver = false): void {
    if (!this.soundEnabled || this.soundVolume <= 0) return;
    this.unlockAudio();
    switch (kind) {
      case "paddle":
        this.playTone(480, 290, 0, 0.075, 0.16, "triangle");
        break;
      case "wall":
        this.playTone(310, 220, 0, 0.055, 0.09, "sine");
        break;
      case "serve":
        this.playTone(450, 580, 0, 0.12, 0.08);
        break;
      case "match":
        this.playTone(320, 420, 0, 0.12, 0.08);
        this.playTone(480, 580, 0.11, 0.13, 0.08);
        break;
      case "goal":
        if (localScored) {
          this.playTone(440, 520, 0, 0.14, 0.12, "triangle");
          this.playTone(660, gameOver ? 880 : 720, 0.14, 0.22, 0.12, "triangle");
        } else {
          this.playTone(440, 340, 0, 0.15, 0.1, "triangle");
          this.playTone(300, gameOver ? 180 : 250, 0.14, 0.2, 0.1, "triangle");
        }
        break;
    }
  }

  bindControls(): void {
    this.soundToggle.addEventListener("click", () => {
      this.soundEnabled = !this.soundEnabled;
      this.updateControls();
      this.saveSettings();
      if (this.soundEnabled) this.unlockAudio();
    });
    this.volumeRange.addEventListener("input", () => {
      this.soundVolume = clamp(this.volumeRange.value, 0, 100) / 100;
      this.updateControls();
      this.saveSettings();
    });
  }
}
