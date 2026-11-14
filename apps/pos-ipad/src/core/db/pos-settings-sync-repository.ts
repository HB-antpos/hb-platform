import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";
import {
  type PosSettingsLocalState,
  type PosSettingsLocalStorePort,
  type PosSettingStoredValue,
} from "@hb/pos-sync/features/pos-settings/pos-settings-service";

/** 总部「收银端设置下发」的本机快照（整份 JSON，原子替换）。 */
const POS_SETTINGS_SYNC_KEY = "pos_settings_sync_v1";
/** 现金进出开关的本机值；只有字面量 true 算开，缺失 / 损坏 / 其他一律关。 */
const POS_CASH_MOVEMENT_ENABLED_KEY = "pos_cash_movement_enabled_v1";

type SettingsRow = Readonly<{ setting_value: unknown }>;

/**
 * 总部设置下发的本机存储窄 facade：只读写两个固定键，业务层拿不到任意 app_settings。
 * 只保存非敏感的布尔开关、来源标签、指纹与计数，不含任何凭据或顾客信息。
 */
export class PosSettingsSyncRepository implements PosSettingsLocalStorePort {
  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  /** 缺失、JSON 损坏或形状不合规都返回 null（等同「从未同步过」，由服务重新拉取）。 */
  public async load(): Promise<PosSettingsLocalState | null> {
    const row = await this.db.getFirst<SettingsRow>(
      "SELECT setting_value FROM app_settings WHERE setting_key = ?",
      [POS_SETTINGS_SYNC_KEY],
    );
    if (!row || typeof row.setting_value !== "string") return null;
    try {
      return normalizePosSettingsLocalState(JSON.parse(row.setting_value));
    } catch {
      return null;
    }
  }

  /** 写入前先校验形状；不合规直接抛错，绝不把脏数据落盘。 */
  public async save(state: PosSettingsLocalState): Promise<void> {
    const normalized = normalizePosSettingsLocalState(state);
    if (!normalized) throw new Error("Invalid POS settings sync state.");
    await this.upsert(POS_SETTINGS_SYNC_KEY, JSON.stringify(normalized));
  }

  /** 现金进出本机开关：本期没有界面消费方，仅供同步写穿与以后的现金进出功能读取。 */
  public async loadCashMovementEnabled(): Promise<boolean> {
    const row = await this.db.getFirst<SettingsRow>(
      "SELECT setting_value FROM app_settings WHERE setting_key = ?",
      [POS_CASH_MOVEMENT_ENABLED_KEY],
    );
    if (!row || typeof row.setting_value !== "string") return false;
    try {
      return JSON.parse(row.setting_value) === true;
    } catch {
      return false;
    }
  }

  public async saveCashMovementEnabled(enabled: boolean): Promise<void> {
    await this.upsert(POS_CASH_MOVEMENT_ENABLED_KEY, JSON.stringify(enabled === true));
  }

  private async upsert(key: string, value: string): Promise<void> {
    await this.db.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
         VALUES (?, ?, ?)
         ON CONFLICT(setting_key) DO UPDATE SET
           setting_value = excluded.setting_value,
           updated_at_iso = excluded.updated_at_iso`,
        [key, value, this.nowIso()],
      );
    });
  }
}

const STATE_KEYS: readonly string[] = [
  "storeCode",
  "hash",
  "values",
  "syncedAtIso",
  "overrides",
  "offlineOverrideCount",
  "ackedHash",
  "ackedOverrideCount",
];

/**
 * 严格规整本机快照：键集合必须恰好一致、类型必须正确，否则返回 null。
 * 空快照（从未同步）也是合法形状。
 */
export function normalizePosSettingsLocalState(value: unknown): PosSettingsLocalState | null {
  if (!isPlainRecord(value)) return null;
  const keys = Object.keys(value);
  if (keys.length !== STATE_KEYS.length || !STATE_KEYS.every((key) => key in value)) return null;
  if (
    typeof value.storeCode !== "string" ||
    typeof value.hash !== "string" ||
    typeof value.ackedHash !== "string" ||
    !isCount(value.offlineOverrideCount) ||
    !isCount(value.ackedOverrideCount)
  ) {
    return null;
  }
  const syncedAtIso = value.syncedAtIso;
  if (syncedAtIso !== null && (typeof syncedAtIso !== "string" || Number.isNaN(Date.parse(syncedAtIso)))) {
    return null;
  }
  if (!isPlainRecord(value.values) || !isPlainRecord(value.overrides)) return null;
  const values: Record<string, PosSettingStoredValue> = {};
  for (const [key, item] of Object.entries(value.values)) {
    if (
      !isPlainRecord(item) ||
      Object.keys(item).length !== 2 ||
      typeof item.value !== "boolean" ||
      typeof item.source !== "string"
    ) {
      return null;
    }
    values[key] = Object.freeze({ value: item.value, source: item.source });
  }
  const overrides: Record<string, true> = {};
  for (const [key, flag] of Object.entries(value.overrides)) {
    if (flag !== true) return null;
    overrides[key] = true;
  }
  return Object.freeze({
    storeCode: value.storeCode,
    hash: value.hash,
    values: Object.freeze(values),
    syncedAtIso,
    overrides: Object.freeze(overrides),
    offlineOverrideCount: value.offlineOverrideCount,
    ackedHash: value.ackedHash,
    ackedOverrideCount: value.ackedOverrideCount,
  });
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isCount(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}
