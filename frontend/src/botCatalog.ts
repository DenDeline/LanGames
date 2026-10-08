import { isRecord, validBotId, validText } from "./snapshot.js";

export interface BotCatalogEntry {
  id: string;
  name: string;
  description: string;
  style: string;
  difficulty: string;
  category: string;
  order: number;
  glyph: string | null;
  enabled: boolean;
  fallbackBotId: string | null;
  availability: "ready" | "notChecked" | "unavailable" | "disabled";
  availabilityReason: string | null;
  canPlay: boolean;
}

export interface BotCatalogResponse {
  version: 9;
  defaultBotId: string;
  bots: BotCatalogEntry[];
}

export function parseBotCatalog(data: unknown): BotCatalogResponse {
  if (!isRecord(data) || data.version !== 9)
    throw new RangeError("Unsupported bot catalog version");
  if (!validBotId(data.defaultBotId) || !Array.isArray(data.bots))
    throw new TypeError("Invalid bot catalog");
  const ids = new Set<string>();
  const bots = data.bots.map((entry): BotCatalogEntry => {
    if (
      !isRecord(entry) ||
      !validBotId(entry.id) ||
      ids.has(entry.id) ||
      !validText(entry.name, 64) ||
      !validText(entry.description, 512) ||
      !validText(entry.style, 128) ||
      !validText(entry.difficulty, 64) ||
      !validText(entry.category, 64) ||
      typeof entry.order !== "number" ||
      !Number.isSafeInteger(entry.order) ||
      entry.order < 0 ||
      (entry.glyph !== null && !validText(entry.glyph, 16)) ||
      typeof entry.enabled !== "boolean" ||
      (entry.fallbackBotId !== null && !validBotId(entry.fallbackBotId)) ||
      !["ready", "notChecked", "unavailable", "disabled"].includes(entry.availability as string) ||
      (entry.availabilityReason !== null && !validText(entry.availabilityReason, 512)) ||
      typeof entry.canPlay !== "boolean" ||
      (!entry.enabled && (entry.canPlay || entry.availability !== "disabled")) ||
      (entry.enabled && entry.availability === "disabled")
    )
      throw new TypeError("Invalid bot catalog entry");
    ids.add(entry.id);
    return { ...entry } as unknown as BotCatalogEntry;
  });
  if (bots.length > 0 && !ids.has(data.defaultBotId)) throw new TypeError("Missing default bot");
  return { version: 9, defaultBotId: data.defaultBotId, bots };
}
