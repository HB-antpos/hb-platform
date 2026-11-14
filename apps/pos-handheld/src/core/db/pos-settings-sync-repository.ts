import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";
import type {
  PosSettingStoredValue,
  PosSettingsLocalState,
} from "@hb/pos-sync/features/pos-settings/pos-settings-service";

/** 总部「收银端设置下发」的本机同步快照（整份 JSON，原子替换）。 */
export const POS_SETTINGS_SYNC_KEY = "pos_settings_sync_v1";
/** cashMovement.enabled 的本机值；本期手持没有现金进出功能，只同步、存储与回执。 */
export const POS_CASH_MOVEMENT_ENABLED_KEY = "pos_cash_movement_enabled_v1";

/**
 * 严格校验并规整本机同步快照：形状不对一律返回 null（等同「从未同步过」），
 * 绝不信任落盘 JSON 的任何字段；未知字段被丢弃。
 */
export function parsePosSettingsLocalState(
  value: unknown,
): PosSettingsLocalState | null {
  if (!isRecord(value)) return null;
  const {
    storeCode,
    hash,
    values,
    syncedAtIso,
    overrides,
    offlineOverrideCount,
    ackedHash,
    ackedOverrideCount,
  } = value;
  if (
    typeof storeCode !== "string" ||
    typeof hash !== "string" ||
    typeof ackedHash !== "string" ||
    !(syncedAtIso === null || typeof syncedAtIso === "string") ||
    !isCount(offlineOverrideCount) ||
    !isCount(ackedOverrideCount) ||
    !isRecord(values) ||
    !isRecord(overrides)
  ) {
    return null;
  }
  const normalizedValues: Record<string, PosSettingStoredValue> = {};
  for (const [key, item] of Object.entries(values)) {
    if (
      !isRecord(item) ||
      typeof item.value !== "boolean" ||
      typeof item.source !== "string" ||
      item.source.trim() === ""
    ) {
      return null;
    }
    normalizedValues[key] = Object.freeze({
      value: item.value,
      source: item.source,
    });
  }
  const normalizedOverrides: Record<string, true> = {};
  for (const [key, flag] of Object.entries(overrides)) {
    if (flag !== true) return null;
    normalizedOverrides[key] = true;
  }
  return Object.freeze({
    storeCode,
    hash,
    values: Object.freeze(normalizedValues),
    syncedAtIso,
    overrides: Object.freeze(normalizedOverrides),
    offlineOverrideCount,
    ackedHash,
    ackedOverrideCount,
  });
}

/**
 * 总部设置下发的窄 facade：只能读写上面两个固定键，业务层拿不到任意 app_settings 访问能力。
 * 不涉及迁移：沿用既有 app_settings 键值表。
 */
export class PosSettingsSyncRepository {
  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  /** 缺失或损坏返回 null。 */
  public async load(): Promise<PosSettingsLocalState | null> {
    const raw = await this.readRaw(POS_SETTINGS_SYNC_KEY);
    if (raw === null) return null;
    try {
      return parsePosSettingsLocalState(JSON.parse(raw));
    } catch {
      return null;
    }
  }

  /** 写入前先校验形状；整份替换，不做字段级合并。 */
  public async save(state: PosSettingsLocalState): Promise<void> {
    const normalized = parsePosSettingsLocalState(state);
    if (!normalized) throw new Error("Invalid POS settings sync state.");
    await this.writeRaw(POS_SETTINGS_SYNC_KEY, JSON.stringify(normalized));
  }

  /** 只有字面量 true 算开启；缺失、损坏、其他类型一律关闭。 */
  public async readCashMovementEnabled(): Promise<boolean> {
    const raw = await this.readRaw(POS_CASH_MOVEMENT_ENABLED_KEY);
    if (raw === null) return false;
    try {
      return JSON.parse(raw) === true;
    } catch {
      return false;
    }
  }

  public async writeCashMovementEnabled(enabled: boolean): Promise<void> {
    if (typeof enabled !== "boolean") {
      throw new Error("Invalid cash movement setting.");
    }
    await this.writeRaw(POS_CASH_MOVEMENT_ENABLED_KEY, JSON.stringify(enabled));
  }

  private async readRaw(key: string): Promise<string | null> {
    const row = await this.db.getFirst<{ setting_value: unknown }>(
      "SELECT setting_value FROM app_settings WHERE setting_key = ?",
      [key],
    );
    return row && typeof row.setting_value === "string"
      ? row.setting_value
      : null;
  }

  private async writeRaw(key: string, payload: string): Promise<void> {
    await this.db.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
         VALUES (?, ?, ?)
         ON CONFLICT(setting_key) DO UPDATE SET
           setting_value = excluded.setting_value,
           updated_at_iso = excluded.updated_at_iso`,
        [key, payload, this.nowIso()],
      );
    });
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isCount(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
