import { RECEIPT_TERMS_TEXT_MAX_LENGTH } from "@hb/pos-receipt-core/features/receipts/receipt-terms-text";

import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

const RECEIPT_PRINTER_KEY = "receipt_printer_v1";
const SENSITIVE_KEY = /token|authorization|voucher|card/i;
/**
 * voucherTerms 是印在退款券券面底部的公开使用说明文案，不是券码或任何凭据，但键名含 "voucher"，
 * 必须显式豁免敏感键拦截；其余含 voucher 的键（如 voucherCode）仍按敏感字段拒绝。
 */
const NON_SENSITIVE_KEYS: ReadonlySet<string> = new Set(["voucherTerms"]);
const PERIPHERAL_ID = /^[A-Za-z0-9._:-]{1,128}$/;
const PAPER_VALUES = new Set(["58mm", "80mm"]);
const LOCALE_VALUES = new Set(["en", "zh-CN"]);

export type ReceiptPrinterSettings = Readonly<{
  printEnabled: boolean;
  drawerEnabled: boolean;
  peripheralId: string | null;
  paper: "58mm" | "80mm";
  locale: "en" | "zh-CN";
  brandName: string;
  storeName: string;
  address: string;
  phone: string;
  abn: string;
  returnPolicy: string;
  /**
   * 退款代金券券面底部「VOUCHER TERMS」的自定义正文（多行纯文本，一行一条，标题仍由收银端固定打印）。
   * 空串 / 纯空白 = 未定制，按内置默认文案打印。上限 RECEIPT_TERMS_TEXT_MAX_LENGTH，规则同退货政策。
   * 旧数据缺省视为空串。
   */
  voucherTerms: string;
  /** 进行中分期小票底部「INSTALLMENT TERMS」的自定义正文；规则同 voucherTerms。 */
  installmentTerms: string;
  profileStoreCode: string;
  /**
   * 已应用的总部下发版本；0 = 从未应用过下发（资料由本机手工维护，行为与旧版完全一致）。
   * 大于 0 时八项门店资料（含券使用说明、分期条款）只读，由总部下发覆盖。旧数据缺省视为 0，无需迁移。
   */
  profileVersion: number;
  /** 已成功回执给服务端的下发版本；小于 profileVersion 表示还有回执待补发。 */
  profileAckedVersion: number;
}>;

export const DEFAULT_RECEIPT_PRINTER_SETTINGS: ReceiptPrinterSettings = {
  printEnabled: false,
  drawerEnabled: false,
  peripheralId: null,
  paper: "80mm",
  locale: "en",
  brandName: "",
  storeName: "",
  address: "",
  phone: "",
  abn: "",
  returnPolicy: "",
  voucherTerms: "",
  installmentTerms: "",
  profileStoreCode: "",
  profileVersion: 0,
  profileAckedVersion: 0,
};

type SettingsRow = Readonly<{ setting_value: unknown }>;

/**
 * 仅管理 receipt_printer_v1；任何损坏或未知字段都回退到禁用打印/钱箱的安全默认值。
 * 设备票据、支付引用等敏感数据不得借 app_settings 成为普通 JSON 配置。
 */
export class PosSettingsRepository {
  public constructor(
    private readonly db: SqliteConnectionPort,
    private readonly nowIso: () => string,
  ) {}

  public async getReceiptPrinterSettings(): Promise<ReceiptPrinterSettings> {
    return readReceiptPrinterSettings(this.db);
  }

  public async saveReceiptPrinterSettings(
    input: ReceiptPrinterSettings,
  ): Promise<ReceiptPrinterSettings> {
    const settings = validateReceiptPrinterSettings(input);
    const payload = serializeReceiptPrinterSettings(settings);
    await this.db.withExclusiveTransaction(async (transaction) => {
      await transaction.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
         VALUES (?, ?, ?)
         ON CONFLICT(setting_key) DO UPDATE SET
           setting_value = excluded.setting_value,
           updated_at_iso = excluded.updated_at_iso`,
        [RECEIPT_PRINTER_KEY, payload, this.nowIso()],
      );
    });
    return settings;
  }

  /**
   * 设置页等「用户保存」入口专用：在同一个独占事务里重读已落盘设置，再合并后写入。
   * - 下发版本两个字段永远以已落盘值为准，草稿里的旧值不能覆盖后台刚写入的版本；
   * - 已应用总部下发（profileVersion > 0）时，八项资料（含券使用说明、分期条款）与绑定门店也以已落盘值为准，
   *   避免过期草稿把后台刚同步的新资料盖回旧值。
   * 换店清空等需要重置这些字段的内部路径仍用 saveReceiptPrinterSettings 整体覆盖。
   */
  public async saveReceiptPrinterSettingsPreservingProfile(
    input: ReceiptPrinterSettings,
  ): Promise<ReceiptPrinterSettings> {
    const requested = validateReceiptPrinterSettings(input);
    return this.db.withExclusiveTransaction(async (transaction) => {
      const current = await readReceiptPrinterSettings(transaction);
      const managed = current.profileVersion > 0;
      const merged: ReceiptPrinterSettings = Object.freeze({
        ...requested,
        ...(managed
          ? {
              brandName: current.brandName,
              storeName: current.storeName,
              address: current.address,
              phone: current.phone,
              abn: current.abn,
              returnPolicy: current.returnPolicy,
              voucherTerms: current.voucherTerms,
              installmentTerms: current.installmentTerms,
              profileStoreCode: current.profileStoreCode,
            }
          : {}),
        profileVersion: current.profileVersion,
        profileAckedVersion: current.profileAckedVersion,
      });
      await upsertReceiptPrinterSettings(transaction, merged, this.nowIso());
      return merged;
    });
  }

  /**
   * 原子写入总部下发的一版资料：八项资料（六项门店资料 + 券使用说明 + 分期条款）+ 下发版本 + 绑定门店代码
   * 在同一个事务、同一条 UPSERT 里落盘。
   * 校验与「现有保存」同口径（长度上限、控制字符规则，另要求门店名非空），任何一项不通过
   * 抛 ReceiptProfileRejectedError 且不写入任何内容；存储层 I/O 故障则以原异常抛出（调用方下一轮重试）。
   * 打印机型号、纸宽、钱箱等硬件设置保持落盘值不变。
   */
  public async applyReceiptProfile(
    profile: ReceiptProfileApplyInput,
  ): Promise<ReceiptPrinterSettings> {
    if (!Number.isSafeInteger(profile.version) || profile.version <= 0) {
      throw new ReceiptProfileRejectedError("Receipt profile version is invalid.");
    }
    // 先用安全默认值做一次纯校验：不通过就不开事务，更不会写入半截数据。
    const candidate = (base: ReceiptPrinterSettings): ReceiptPrinterSettings =>
      validateReceiptPrinterSettings({
        ...base,
        brandName: profile.brandName,
        storeName: profile.storeName,
        address: profile.address,
        phone: profile.phone,
        abn: profile.abn,
        returnPolicy: profile.returnPolicy,
        voucherTerms: profile.voucherTerms,
        installmentTerms: profile.installmentTerms,
        profileStoreCode: profile.storeCode,
        profileVersion: profile.version,
        profileAckedVersion: 0,
      });
    const normalize = (settings: ReceiptPrinterSettings): ReceiptPrinterSettings => {
      // 与设置页载入资料同口径：单行字段去首尾空白，地址/退货政策保留换行排版。
      const normalized = Object.freeze({
        ...settings,
        brandName: settings.brandName.trim(),
        storeName: settings.storeName.trim(),
        phone: settings.phone.trim(),
        abn: settings.abn.trim(),
        profileStoreCode: settings.profileStoreCode.trim(),
      });
      if (normalized.storeName === "") {
        throw new ReceiptProfileRejectedError("Receipt profile store name is required.");
      }
      return normalized;
    };
    try {
      normalize(candidate(DEFAULT_RECEIPT_PRINTER_SETTINGS));
    } catch (error) {
      if (error instanceof ReceiptProfileRejectedError) throw error;
      throw new ReceiptProfileRejectedError("Receipt profile failed validation.");
    }
    return this.db.withExclusiveTransaction(async (transaction) => {
      const current = await readReceiptPrinterSettings(transaction);
      const next = normalize(candidate(current));
      // 新版本尚未回执：已回执版本不得超过 version - 1（服务端回退版本号时也能重新回执）。
      const applied: ReceiptPrinterSettings = Object.freeze({
        ...next,
        profileAckedVersion: Math.min(current.profileAckedVersion, profile.version - 1),
      });
      await upsertReceiptPrinterSettings(transaction, applied, this.nowIso());
      return applied;
    });
  }

  /**
   * 记录「已成功回执」的下发版本。只有本机当前版本仍等于 version 时才写入（比较并设置），
   * 回执在途期间若已应用更新的版本，则不能把旧版本的回执记到新版本头上。
   */
  public async markReceiptProfileAcked(version: number): Promise<boolean> {
    if (!Number.isSafeInteger(version) || version <= 0) return false;
    return this.db.withExclusiveTransaction(async (transaction) => {
      const current = await readReceiptPrinterSettings(transaction);
      if (current.profileVersion !== version || current.profileAckedVersion >= version) {
        return false;
      }
      await upsertReceiptPrinterSettings(
        transaction,
        validateReceiptPrinterSettings({ ...current, profileAckedVersion: version }),
        this.nowIso(),
      );
      return true;
    });
  }
}

/** 总部下发资料没通过本机校验：未写入任何内容，同一版本重试也不会通过。 */
export class ReceiptProfileRejectedError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "ReceiptProfileRejectedError";
  }
}

export type ReceiptProfileApplyInput = Readonly<{
  version: number;
  storeCode: string;
  storeName: string;
  brandName: string;
  address: string;
  phone: string;
  abn: string;
  returnPolicy: string;
  /** 券使用说明自定义正文；空串 = 未定制（服务端 null 已由 API 适配器归一为空串）。 */
  voucherTerms: string;
  /** 分期条款自定义正文；空串 = 未定制。 */
  installmentTerms: string;
}>;

async function readReceiptPrinterSettings(
  db: SqliteConnectionPort,
): Promise<ReceiptPrinterSettings> {
  const row = await db.getFirst<SettingsRow>(
    "SELECT setting_value FROM app_settings WHERE setting_key = ?",
    [RECEIPT_PRINTER_KEY],
  );
  if (!row) return DEFAULT_RECEIPT_PRINTER_SETTINGS;
  try {
    return parseReceiptPrinterSettings(row.setting_value);
  } catch {
    // 配置损坏时绝不以旧值猜测开钱箱或打印，必须由设置页重新明确保存。
    return DEFAULT_RECEIPT_PRINTER_SETTINGS;
  }
}

async function upsertReceiptPrinterSettings(
  db: SqliteConnectionPort,
  settings: ReceiptPrinterSettings,
  updatedAtIso: string,
): Promise<void> {
  await db.run(
    `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
     VALUES (?, ?, ?)
     ON CONFLICT(setting_key) DO UPDATE SET
       setting_value = excluded.setting_value,
       updated_at_iso = excluded.updated_at_iso`,
    [RECEIPT_PRINTER_KEY, serializeReceiptPrinterSettings(settings), updatedAtIso],
  );
}

/**
 * 落盘序列化：下发版本为 0（从未应用过总部下发）时不写两个版本字段；券使用说明 / 分期条款
 * 为空串（未定制）时也不写对应键。这样没被下发、没定制过的设备，其 receipt_printer_v1 与旧版
 * 格式逐字段一致；旧代码的校验会拒绝未知键并把整份设置判为损坏（回落为关闭打印/钱箱的默认值），
 * 因此只有真正应用过下发或填写过条款的设备才写入新键，缩小 OTA 回滚时的影响面。读取时缺省按 0 / 空串。
 */
function serializeReceiptPrinterSettings(settings: ReceiptPrinterSettings): string {
  const { profileVersion, profileAckedVersion, voucherTerms, installmentTerms, ...rest } = settings;
  return JSON.stringify({
    ...rest,
    ...(voucherTerms !== "" ? { voucherTerms } : {}),
    ...(installmentTerms !== "" ? { installmentTerms } : {}),
    ...(profileVersion > 0 ? { profileVersion, profileAckedVersion } : {}),
  });
}

function parseReceiptPrinterSettings(value: unknown): ReceiptPrinterSettings {
  if (typeof value !== "string") throw new Error("Invalid receipt printer settings JSON.");
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error("Invalid receipt printer settings JSON.");
  }
  return validateReceiptPrinterSettings(parsed);
}

function validateReceiptPrinterSettings(value: unknown): ReceiptPrinterSettings {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("Receipt printer settings must be an object.");
  }
  const record = value as Record<string, unknown>;
  const allowed = new Set([
    "printEnabled", "drawerEnabled", "peripheralId", "paper", "locale",
    "brandName", "storeName", "address", "phone", "abn",
    "returnPolicy", "voucherTerms", "installmentTerms",
    "profileStoreCode", "profileVersion", "profileAckedVersion",
  ]);
  for (const key of Object.keys(record)) {
    if ((SENSITIVE_KEY.test(key) && !NON_SENSITIVE_KEYS.has(key)) || !allowed.has(key)) {
      throw new Error("Receipt printer settings contain an unsupported or sensitive field.");
    }
  }

  const peripheralId = nullablePeripheral(record.peripheralId);
  const printEnabled = boolean(record.printEnabled, "printEnabled");
  const drawerEnabled = boolean(record.drawerEnabled, "drawerEnabled");
  if (drawerEnabled && peripheralId === null) {
    throw new Error("Drawer can only be enabled for a valid receipt printer peripheral.");
  }
  return {
    printEnabled,
    drawerEnabled,
    peripheralId,
    paper: enumeration(record.paper, PAPER_VALUES, "paper") as "58mm" | "80mm",
    locale: enumeration(record.locale, LOCALE_VALUES, "locale") as "en" | "zh-CN",
    brandName: boundedText(record.brandName, 120, "brandName"),
    storeName: boundedText(record.storeName, 120, "storeName"),
    address: multilineText(record.address, 240, "address"),
    phone: boundedText(record.phone, 60, "phone"),
    abn: boundedText(record.abn, 32, "abn"),
    returnPolicy: optionalMultilineText(record.returnPolicy, 500, "returnPolicy"),
    // 券使用说明 / 分期条款与退货政策同口径（仅放行 CR/LF/TAB），上限与打印层、设置页共用同一个常量。
    voucherTerms: optionalMultilineText(
      record.voucherTerms,
      RECEIPT_TERMS_TEXT_MAX_LENGTH,
      "voucherTerms",
    ),
    installmentTerms: optionalMultilineText(
      record.installmentTerms,
      RECEIPT_TERMS_TEXT_MAX_LENGTH,
      "installmentTerms",
    ),
    profileStoreCode: optionalText(record.profileStoreCode, 128, "profileStoreCode"),
    profileVersion: lenientVersion(record.profileVersion),
    profileAckedVersion: lenientVersion(record.profileAckedVersion),
  };
}

/**
 * 下发版本号：缺失（旧数据）、非整数、负数、超范围等脏值一律回落为 0，
 * 而不是让整份设置校验失败——否则一个脏版本号会把打印/钱箱设置整体重置为默认值。
 */
function lenientVersion(value: unknown): number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : 0;
}

function boolean(value: unknown, field: string): boolean {
  if (typeof value !== "boolean") throw new Error(`Receipt printer ${field} must be boolean.`);
  return value;
}

function nullablePeripheral(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== "string" || !PERIPHERAL_ID.test(value)) {
    throw new Error("Receipt printer peripheralId is invalid.");
  }
  return value;
}

function enumeration(value: unknown, allowed: ReadonlySet<string>, field: string): string {
  if (typeof value !== "string" || !allowed.has(value)) {
    throw new Error(`Receipt printer ${field} is invalid.`);
  }
  return value;
}

function boundedText(value: unknown, maxLength: number, field: string): string {
  if (typeof value !== "string" || value.length > maxLength || /[\u0000-\u001F\u007F-\u009F]/.test(value)) {
    throw new Error(`Receipt printer ${field} is invalid.`);
  }
  return value;
}

/** 地址与退货政策需要换行排版，仅放行 CR/LF/TAB，其余控制字符（含 C1）仍拒绝。 */
function multilineText(value: unknown, maxLength: number, field: string): string {
  if (
    typeof value !== "string" ||
    value.length > maxLength ||
    /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/.test(value)
  ) {
    throw new Error(`Receipt printer ${field} is invalid.`);
  }
  return value;
}

/** 旧 receipt_printer_v1 没有新资料字段，首次读取补默认空值且不清空旧硬件。 */
function optionalText(value: unknown, maxLength: number, field: string): string {
  if (value === undefined || value === null) return "";
  return boundedText(value, maxLength, field);
}

function optionalMultilineText(value: unknown, maxLength: number, field: string): string {
  if (value === undefined || value === null) return "";
  return multilineText(value, maxLength, field);
}
