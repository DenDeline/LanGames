import type { BotCatalogEntry, BotCatalogResponse } from "./botCatalog.js";

interface CatalogElements {
  dialog: HTMLDialogElement;
  opener: HTMLButtonElement;
  title: HTMLElement;
  summaryAvatar: HTMLElement;
  summaryName: HTMLElement;
  summaryDifficulty: HTMLElement;
  summaryAvailability: HTMLElement;
  summaryStatus: HTMLElement;
  fieldset: HTMLFieldSetElement;
  groups: HTMLElement;
  status: HTMLElement;
  retry: HTMLButtonElement;
  profile: HTMLElement;
  avatar: HTMLElement;
  name: HTMLElement;
  description: HTMLElement;
  difficulty: HTMLElement;
  style: HTMLElement;
  availability: HTMLElement;
  play: HTMLButtonElement;
}

interface BotCard {
  label: HTMLLabelElement;
  radio: HTMLInputElement;
  avatar: HTMLElement;
  name: HTMLElement;
  difficulty: HTMLElement;
  style: HTMLElement;
  availability: HTMLElement;
}

interface BotGroup {
  section: HTMLElement;
  heading: HTMLElement;
  cards: HTMLElement;
}

function text(element: HTMLElement, value: string): void {
  if (element.textContent !== value) element.textContent = value;
}

function span(className: string): HTMLSpanElement {
  const element = document.createElement("span");
  element.className = className;
  return element;
}

function avatar(bot: BotCatalogEntry): string {
  return (
    bot.glyph ??
    bot.name
      .trim()
      .split(/\s+/)
      .slice(0, 2)
      .map((word) => Array.from(word)[0])
      .join("")
      .toLocaleUpperCase("ru")
  );
}

/** Catalog nodes change only on fetch or native selection; simulation snapshots disable the fieldset. */
export class BotCatalogView {
  private catalog: BotCatalogResponse | null = null;
  private readonly cards = new Map<string, BotCard>();
  private readonly groups = new Map<string, BotGroup>();
  private selectedId: string | null = null;
  private rememberedId: string | null = null;
  private structureKey: string | null = null;
  private nextGroupId = 0;
  private disabled = false;

  constructor(private readonly elements: CatalogElements) {}

  getSelectedBotId(): string | null {
    return this.catalog?.bots.find((bot) => bot.id === this.selectedId && bot.canPlay)?.id ?? null;
  }

  setCatalog(catalog: BotCatalogResponse | null, error: string | null): void {
    const retryWasFocused = document.activeElement === this.elements.retry;
    const focusedId = [...this.cards].find(
      ([, card]) => card.radio === document.activeElement,
    )?.[0];
    this.catalog = catalog;
    this.elements.retry.hidden = error === null;
    if (catalog === null) {
      this.selectedId = null;
      this.structureKey = null;
      this.elements.groups.replaceChildren();
      this.elements.profile.hidden = true;
      this.elements.status.dataset.state = error === null ? "loading" : "error";
      this.elements.summaryStatus.dataset.state = this.elements.status.dataset.state;
      text(this.elements.status, error ?? "Загружаем список соперников…");
      text(this.elements.summaryStatus, this.elements.status.textContent ?? "");
      this.elements.summaryStatus.hidden = false;
      this.updateSummary(null);
      text(this.elements.play, "Играть с ботом");
      if (this.elements.dialog.open && !this.disabled && focusedId !== undefined && error !== null)
        this.elements.retry.focus();
      return;
    }

    const bots = [...catalog.bots].sort(
      (first, second) =>
        first.order - second.order || (first.id < second.id ? -1 : first.id > second.id ? 1 : 0),
    );
    const previous = bots.find((bot) => bot.id === this.rememberedId && bot.canPlay);
    const preferred = bots.find((bot) => bot.id === catalog.defaultBotId && bot.canPlay);
    this.selectedId = (previous ?? preferred ?? bots.find((bot) => bot.canPlay))?.id ?? null;
    if (this.selectedId !== null) this.rememberedId = this.selectedId;
    const grouped = new Map<string, BotCard[]>();
    for (const bot of bots) {
      let card = this.cards.get(bot.id);
      if (!card) {
        card = this.createCard(bot.id);
        this.cards.set(bot.id, card);
      }
      this.updateCard(card, bot);
      const categoryCards = grouped.get(bot.category) ?? [];
      categoryCards.push(card);
      grouped.set(bot.category, categoryCards);
    }
    const nextStructureKey = JSON.stringify(bots.map((bot) => [bot.id, bot.category]));
    if (nextStructureKey !== this.structureKey) {
      const sections: HTMLElement[] = [];
      for (const [category, cards] of grouped) {
        let group = this.groups.get(category);
        if (!group) {
          const section = document.createElement("section");
          section.className = "bot-category";
          const heading = document.createElement("h3");
          heading.id = `bot-category-${this.nextGroupId++}`;
          const cardList = document.createElement("div");
          cardList.className = "bot-card-list";
          section.setAttribute("aria-labelledby", heading.id);
          section.append(heading, cardList);
          group = { section, heading, cards: cardList };
          this.groups.set(category, group);
        }
        text(group.heading, category);
        group.cards.replaceChildren(...cards.map((card) => card.label));
        sections.push(group.section);
      }
      this.elements.groups.replaceChildren(...sections);
      this.structureKey = nextStructureKey;
    }
    const ids = new Set(bots.map((bot) => bot.id));
    for (const id of this.cards.keys()) if (!ids.has(id)) this.cards.delete(id);
    for (const category of this.groups.keys())
      if (!grouped.has(category)) this.groups.delete(category);
    this.elements.status.dataset.state = this.selectedId === null ? "empty" : "ready";
    this.elements.summaryStatus.dataset.state = this.elements.status.dataset.state;
    text(
      this.elements.status,
      bots.length === 0
        ? "В списке пока нет ботов. Вы можете сыграть с другом по сети."
        : this.selectedId === null
          ? "Сейчас все боты недоступны. Вы можете сыграть с другом по сети."
          : "Выберите соперника для игры.",
    );
    text(this.elements.summaryStatus, this.elements.status.textContent ?? "");
    this.elements.summaryStatus.hidden = this.selectedId !== null;
    this.elements.retry.hidden = this.selectedId !== null;
    this.applySelection();
    if (focusedId !== undefined && document.activeElement !== this.cards.get(focusedId)?.radio) {
      const previousFocus = this.cards.get(focusedId);
      const target =
        previousFocus && !previousFocus.radio.disabled
          ? previousFocus
          : this.selectedId === null
            ? null
            : this.cards.get(this.selectedId);
      if (target) this.restoreFocus(target.radio.value);
      else if (this.elements.dialog.open && !this.disabled) this.focusSelection();
    } else if (
      ((focusedId !== undefined && this.cards.get(focusedId)?.radio.disabled) || retryWasFocused) &&
      this.selectedId !== null
    ) {
      this.restoreFocus(this.selectedId);
    }
  }

  setDisabled(disabled: boolean): void {
    this.disabled = disabled;
    this.elements.fieldset.disabled = disabled;
    this.elements.opener.disabled = disabled;
    if (disabled) this.closePicker(false);
  }

  openPicker(): void {
    if (this.disabled || this.elements.dialog.open) return;
    this.elements.dialog.showModal();
    this.focusSelection();
  }

  closePicker(restoreFocus = true): void {
    if (!this.elements.dialog.open) return;
    this.elements.dialog.close();
    // Native close events are asynchronous. Restore only in this user action,
    // never from a later event that could override accepted match arena focus.
    if (restoreFocus && !this.disabled && !this.elements.opener.disabled)
      this.elements.opener.focus({ preventScroll: true });
  }

  private focusSelection(): void {
    if (!this.elements.dialog.open || this.disabled) return;
    const selected = this.selectedId === null ? null : this.cards.get(this.selectedId)?.radio;
    const target =
      selected && !selected.disabled
        ? selected
        : !this.elements.retry.hidden && !this.elements.retry.disabled
          ? this.elements.retry
          : this.elements.title;
    // Native focus scrolls a selected radio/retry into the bounded dialog body.
    target.focus();
  }

  private restoreFocus(id: string): void {
    const radio = this.cards.get(id)?.radio;
    if (
      !radio ||
      radio.disabled ||
      this.disabled ||
      this.elements.fieldset.disabled ||
      !this.elements.dialog.open
    )
      return;
    radio.focus();
  }

  updateSelection(): void {
    if (this.disabled) return;
    const checked = this.elements.fieldset.querySelector<HTMLInputElement>(
      'input[name="botId"]:checked',
    );
    const selected = this.catalog?.bots.find((bot) => bot.id === checked?.value && bot.canPlay);
    if (!selected) return;
    this.selectedId = selected.id;
    this.rememberedId = selected.id;
    this.applySelection();
  }

  private createCard(id: string): BotCard {
    const label = document.createElement("label");
    label.className = "bot-card";
    label.dataset.botId = id;
    const radio = document.createElement("input");
    radio.type = "radio";
    radio.name = "botId";
    radio.value = id;
    radio.id = `bot-choice-${id}`;
    radio.dataset.botId = id;
    label.htmlFor = radio.id;
    const avatarElement = span("bot-avatar");
    avatarElement.setAttribute("aria-hidden", "true");
    const copy = span("bot-card-copy");
    const name = span("bot-card-name");
    name.id = `bot-name-${id}`;
    const difficulty = span("bot-card-difficulty");
    difficulty.id = `bot-difficulty-${id}`;
    const style = span("bot-card-style");
    style.id = `bot-style-${id}`;
    const availability = span("bot-card-availability");
    availability.id = `bot-availability-${id}`;
    copy.append(name, difficulty, style, availability);
    radio.setAttribute("aria-labelledby", name.id);
    radio.setAttribute("aria-describedby", `${difficulty.id} ${style.id} ${availability.id}`);
    label.append(radio, avatarElement, copy);
    return { label, radio, avatar: avatarElement, name, difficulty, style, availability };
  }

  private updateCard(card: BotCard, bot: BotCatalogEntry): void {
    card.radio.disabled = !bot.canPlay;
    card.label.dataset.availability = bot.availability;
    card.label.dataset.playable = String(bot.canPlay);
    const visual = avatar(bot);
    card.avatar.dataset.dense = String(Array.from(visual).length > 3);
    text(card.avatar, visual);
    text(card.name, bot.name);
    text(card.difficulty, bot.difficulty);
    text(card.style, bot.style);
    text(card.availability, this.availabilityText(bot));
  }

  private fallbackTarget(bot: BotCatalogEntry): BotCatalogEntry | null {
    const visited = new Set<string>([bot.id]);
    let fallbackId = bot.fallbackBotId;
    while (fallbackId !== null && !visited.has(fallbackId)) {
      visited.add(fallbackId);
      const candidate = this.catalog?.bots.find((entry) => entry.id === fallbackId);
      if (!candidate) return null;
      if (
        candidate.enabled &&
        (candidate.availability === "ready" || candidate.availability === "notChecked")
      )
        return candidate;
      fallbackId = candidate.fallbackBotId;
    }
    return null;
  }

  private availabilityText(bot: BotCatalogEntry): string {
    if (!bot.enabled || bot.availability === "disabled") return "Пока недоступен для игры.";
    if (bot.availability === "notChecked") return "Готовность проверим при запуске матча.";
    if (bot.availability === "ready") return "Готов к игре.";
    const reason = bot.availabilityReason ?? "Этот соперник сейчас недоступен.";
    const fallback = bot.canPlay ? this.fallbackTarget(bot) : null;
    if (fallback?.availability === "notChecked")
      return `${reason} При запуске попробуем «${fallback.name}»; его готовность ещё не проверена.`;
    if (fallback) return `${reason} Вместо него будет играть «${fallback.name}».`;
    return reason;
  }

  private applySelection(): void {
    for (const [id, card] of this.cards) {
      card.radio.checked = id === this.selectedId;
      card.label.classList.toggle("is-selected", id === this.selectedId);
    }
    const selected = this.catalog?.bots.find((bot) => bot.id === this.selectedId);
    this.updateSummary(selected ?? null);
    this.elements.profile.hidden = !selected;
    if (!selected) {
      text(this.elements.play, "Играть с ботом");
      return;
    }
    const visual = avatar(selected);
    this.elements.avatar.dataset.dense = String(Array.from(visual).length > 3);
    text(this.elements.avatar, visual);
    text(this.elements.name, selected.name);
    text(this.elements.description, selected.description);
    text(this.elements.difficulty, selected.difficulty);
    text(this.elements.style, selected.style);
    text(this.elements.availability, this.availabilityText(selected));
    this.elements.availability.dataset.state = selected.availability;
    const substitute =
      selected.availability === "unavailable" ? this.fallbackTarget(selected) : null;
    text(
      this.elements.play,
      substitute
        ? substitute.availability === "notChecked"
          ? `Попробовать «${substitute.name}»`
          : `Играть с «${substitute.name}»`
        : `Играть с «${selected.name}»`,
    );
  }

  private updateSummary(selected: BotCatalogEntry | null): void {
    const visual = selected ? avatar(selected) : "?";
    this.elements.summaryAvatar.dataset.dense = String(Array.from(visual).length > 3);
    text(this.elements.summaryAvatar, visual);
    text(this.elements.summaryName, selected?.name ?? "Выберите соперника");
    text(this.elements.summaryDifficulty, selected?.difficulty ?? "");
    text(this.elements.summaryAvailability, selected ? this.availabilityText(selected) : "");
    this.elements.summaryAvailability.hidden = selected === null;
    this.elements.summaryAvailability.dataset.state = selected?.availability ?? "empty";
  }
}
