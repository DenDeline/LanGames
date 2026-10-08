type TouchDirection = "up" | "down";

export class InputController {
  private readonly pressedKeys = new Set<string>();
  private readonly pressedTouch = new Set<TouchDirection>();
  private bound = false;

  constructor(
    private readonly moveUp: HTMLButtonElement,
    private readonly moveDown: HTMLButtonElement,
    private readonly onChange: () => void,
  ) {}

  get axis(): number {
    const up =
      this.pressedKeys.has("w") || this.pressedKeys.has("arrowup") || this.pressedTouch.has("up");
    const down =
      this.pressedKeys.has("s") ||
      this.pressedKeys.has("arrowdown") ||
      this.pressedTouch.has("down");
    return Number(down) - Number(up);
  }

  clear(): void {
    this.pressedKeys.clear();
    this.pressedTouch.clear();
    this.onChange();
  }

  bind(): void {
    if (this.bound) return;
    this.bound = true;

    window.addEventListener("keydown", (event) => {
      const key = event.key.toLowerCase();
      if (!["w", "s", "arrowup", "arrowdown"].includes(key)) return;
      if (
        event.target instanceof Element &&
        event.target.closest("input, textarea, select, [contenteditable], dialog[open]")
      )
        return;
      event.preventDefault();
      this.pressedKeys.add(key);
      this.onChange();
    });
    window.addEventListener("keyup", (event) => {
      const key = event.key.toLowerCase();
      if (!this.pressedKeys.has(key)) return;
      this.pressedKeys.delete(key);
      this.onChange();
    });
    window.addEventListener("blur", () => this.clear());
    document.addEventListener("visibilitychange", () => {
      if (document.hidden) this.clear();
    });
    document.addEventListener("focusin", (event) => {
      if (
        event.target instanceof Element &&
        event.target.closest("input, textarea, select, [contenteditable], dialog[open]")
      )
        this.clear();
    });

    this.bindTouch(this.moveUp, "up");
    this.bindTouch(this.moveDown, "down");
  }

  private bindTouch(button: HTMLButtonElement, direction: TouchDirection): void {
    button.addEventListener("pointerdown", (event) => {
      event.preventDefault();
      button.setPointerCapture(event.pointerId);
      this.pressedTouch.add(direction);
      this.onChange();
    });
    const release = (event: PointerEvent) => {
      event.preventDefault();
      this.pressedTouch.delete(direction);
      this.onChange();
    };
    button.addEventListener("pointerup", release);
    button.addEventListener("pointercancel", release);
    button.addEventListener("lostpointercapture", release);
  }
}
