// 考勤新接口（带薪假、工时统计、审核看板）共用的宽松字段读取：后端 camelCase / PascalCase 都接受，缺省给安全值。
export type RawRecord = Record<string, unknown>;

export function isRecord(value: unknown): value is RawRecord {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

export function pick(raw: RawRecord, ...keys: string[]) {
  for (const key of keys) {
    if (raw[key] !== undefined && raw[key] !== null) return raw[key];
  }
  return undefined;
}

export function asString(value: unknown, fallback = ""): string {
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return fallback;
}

export function asOptionalString(value: unknown): string | undefined {
  const normalized = asString(value).trim();
  return normalized || undefined;
}

/** 日期统一截成 YYYY-MM-DD（后端 DateTime 会带 T00:00:00）。 */
export function asDateOnly(value: unknown): string {
  return asString(value).slice(0, 10);
}

export function asNumber(value: unknown, fallback = 0): number {
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) return parsed;
  }
  return fallback;
}

export function asOptionalNumber(value: unknown): number | undefined {
  const parsed = asNumber(value, Number.NaN);
  return Number.isNaN(parsed) ? undefined : parsed;
}

export function asBoolean(value: unknown, fallback = false): boolean {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") {
    const normalized = value.toLowerCase();
    if (normalized === "true") return true;
    if (normalized === "false") return false;
  }
  return fallback;
}

export function asRecordArray(value: unknown): RawRecord[] {
  return Array.isArray(value) ? value.filter(isRecord) : [];
}

/**
 * 字符串数组：兼容后端直接给数组，或旧字段存成 JSON 字符串（如 AttachmentUrls）。
 */
export function asStringArray(value: unknown): string[] {
  let candidate = value;
  if (typeof value === "string") {
    const trimmed = value.trim();
    if (!trimmed) return [];
    if (trimmed.startsWith("[")) {
      try {
        candidate = JSON.parse(trimmed);
      } catch {
        return [trimmed];
      }
    } else {
      return [trimmed];
    }
  }
  return Array.isArray(candidate)
    ? candidate.map((item) => asString(item).trim()).filter(Boolean)
    : [];
}
