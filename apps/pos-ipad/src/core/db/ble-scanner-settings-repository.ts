import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import type { BleScannerStorage } from "../peripherals/scanner/ble/ble-scanner-controller";
import {
  parseSavedBleScanner,
  type SavedBleScanner,
} from "../peripherals/scanner/ble/ble-scanner-core";

/** 本机保存的 BLE 扫码枪（一台 iPad 只存一把）。 */
const BLE_SCANNER_KEY = "ble_scanner_v1";

type SettingsRow = Readonly<{ setting_value: unknown }>;

/**
 * BLE 扫码枪配置的窄 facade：只读写 app_settings 的 ble_scanner_v1 一个固定键。
 * 只存设备 ID、广播名、备注名、数据通道 UUID 等非敏感信息；读出时用 parseSavedBleScanner
 * 做字段白名单 + 长度校验，损坏或被篡改的内容一律当「未配对」，绝不按猜测去连接。
 */
export class BleScannerSettingsRepository implements BleScannerStorage {
  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  public async load(): Promise<SavedBleScanner | null> {
    const row = await this.db.getFirst<SettingsRow>(
      "SELECT setting_value FROM app_settings WHERE setting_key = ?",
      [BLE_SCANNER_KEY],
    );
    if (!row || typeof row.setting_value !== "string") return null;
    return parseSavedBleScanner(row.setting_value);
  }

  /** 传 null =「忘记扫码枪」：只删除本键这一行。 */
  public async save(value: SavedBleScanner | null): Promise<void> {
    if (value === null) {
      await this.db.withExclusiveTransaction(async (transaction) => {
        await transaction.run(
          "DELETE FROM app_settings WHERE setting_key = ?",
          [BLE_SCANNER_KEY],
        );
      });
      return;
    }
    // 落盘前按读取规则再校验一遍，不合规直接抛错，避免存下下次读不出来的配置。
    const normalized = parseSavedBleScanner(value);
    if (!normalized) throw new Error("Invalid BLE scanner settings.");
    const payload = JSON.stringify({
      version: normalized.version,
      peripheralId: normalized.peripheralId,
      name: normalized.name,
      alias: normalized.alias,
      profileId: normalized.profileId,
      serviceUuid: normalized.serviceUuid,
      notifyUuid: normalized.notifyUuid,
      autoReconnect: normalized.autoReconnect,
      savedAt: normalized.savedAt,
    });
    await this.db.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
         VALUES (?, ?, ?)
         ON CONFLICT(setting_key) DO UPDATE SET
           setting_value = excluded.setting_value,
           updated_at_iso = excluded.updated_at_iso`,
        [BLE_SCANNER_KEY, payload, this.nowIso()],
      );
    });
  }
}
