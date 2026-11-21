/**
 * BLE 扫码枪纯逻辑核心（不依赖 React Native / 蓝牙库）。
 *
 * 本文件在 iPad POS 与移动端各有一份**逐字相同**的拷贝：
 *   apps/pos-ipad/src/core/peripherals/scanner/ble/ble-scanner-core.ts
 *   apps/mobile/src/modules/scanner/ble/ble-scanner-core.ts
 * 移动端不在根 npm workspaces 里，没法引用 packages/*，所以用拷贝 + 一致性测试
 * （apps/mobile/src/modules/scanner/ble/ble-shared-sync.test.ts）代替共享包。
 * 修改时两份必须同步。
 */

/* ------------------------------------------------------------------ */
/* 型号档案                                                             */
/* ------------------------------------------------------------------ */

export type BleScannerSettingCode = Readonly<{
  id: string;
  /** Code128 原文，App 可直接在屏幕上画出来给影像式扫码枪扫。 */
  value: string;
  required?: boolean;
}>;

export type BleScannerProfile = Readonly<{
  id: string;
  serviceUuid: string;
  notifyUuid: string;
  /** 广播名包含这些片段（不区分大小写）时也认为是该型号。 */
  nameHints: readonly string[];
  /** 已知型号会在设置页显示「屏幕设置码」。 */
  settingCodes: readonly BleScannerSettingCode[];
}>;

const SIG_BASE_SUFFIX = "-0000-1000-8000-00805F9B34FB";

/** 16 / 32 位短 UUID 补成 128 位大写形式，便于不同平台返回值之间比较。 */
export function normalizeBleUuid(uuid: string): string {
  const trimmed = uuid.trim().toUpperCase();
  if (/^[0-9A-F]{4}$/.test(trimmed)) return `0000${trimmed}${SIG_BASE_SUFFIX}`;
  if (/^[0-9A-F]{8}$/.test(trimmed)) return `${trimmed}${SIG_BASE_SUFFIX}`;
  return trimmed;
}

export const BLE_BATTERY_SERVICE_UUID = normalizeBleUuid("180F");
export const BLE_BATTERY_LEVEL_UUID = normalizeBleUuid("2A19");

/**
 * 已知数据通道。`yichip-feea` 来自 2026-11-21 对门店背夹（S2DES3050）的 Mac 实测：
 * 广播服务 FEEA、notify 特征 2AA1，2AA2 是命令通道（本方案不写入）。
 */
export const BLE_SCANNER_PROFILES: readonly BleScannerProfile[] = Object.freeze([
  Object.freeze({
    id: "yichip-feea",
    serviceUuid: normalizeBleUuid("FEEA"),
    notifyUuid: normalizeBleUuid("2AA1"),
    nameHints: Object.freeze(["barcode scanner ble"]),
    settingCodes: Object.freeze([
      Object.freeze({ id: "ble-mode", value: "%%SpecCodeAC", required: true }),
      Object.freeze({ id: "suffix-cr", value: ">J606101<" }),
      Object.freeze({ id: "sleep-10min", value: "%%SpecCode34" }),
      Object.freeze({ id: "trigger-manual", value: ">RUNMOD0<" }),
    ]),
  }),
  Object.freeze({
    id: "nordic-uart",
    serviceUuid: "6E400001-B5A3-F393-E0A9-E50E24DCCA9E",
    notifyUuid: "6E400003-B5A3-F393-E0A9-E50E24DCCA9E",
    nameHints: Object.freeze([]),
    settingCodes: Object.freeze([]),
  }),
  Object.freeze({
    id: "hm10-ffe0",
    serviceUuid: normalizeBleUuid("FFE0"),
    notifyUuid: normalizeBleUuid("FFE1"),
    nameHints: Object.freeze([]),
    settingCodes: Object.freeze([]),
  }),
  Object.freeze({
    id: "issc-transparent",
    serviceUuid: "49535343-FE7D-4AE5-8FA9-9FAFD205E455",
    notifyUuid: "49535343-1E4D-4BD9-BA61-23C647249616",
    nameHints: Object.freeze([]),
    settingCodes: Object.freeze([]),
  }),
]);

export function findBleScannerProfile(id: string | null | undefined): BleScannerProfile | null {
  if (!id) return null;
  return BLE_SCANNER_PROFILES.find((profile) => profile.id === id) ?? null;
}

/** 按广播 / 发现到的服务 UUID 识别型号；都不匹配时再看广播名。 */
export function matchBleScannerProfile(
  serviceUuids: readonly string[],
  name: string | null | undefined,
): BleScannerProfile | null {
  const services = new Set(serviceUuids.map(normalizeBleUuid));
  const byService = BLE_SCANNER_PROFILES.find((profile) => services.has(profile.serviceUuid));
  if (byService) return byService;
  const lowerName = (name ?? "").toLowerCase();
  if (!lowerName) return null;
  return (
    BLE_SCANNER_PROFILES.find((profile) =>
      profile.nameHints.some((hint) => lowerName.includes(hint)),
    ) ?? null
  );
}

/** 搜索列表里「像扫码枪」的设备排前面并标「推荐」。 */
export function looksLikeBleScanner(
  serviceUuids: readonly string[],
  name: string | null | undefined,
): boolean {
  if (matchBleScannerProfile(serviceUuids, name)) return true;
  const lowerName = (name ?? "").toLowerCase();
  return /scan|barcode|扫码|扫描/.test(lowerName);
}

/** 同名设备（背夹广播名固定）靠设备 ID 尾号区分：iOS 是 UUID、Android 是 MAC。 */
export function bleDeviceSuffix(id: string): string {
  const hex = id.replace(/[^0-9a-fA-F]/g, "").toUpperCase();
  return hex.slice(-4);
}

/* ------------------------------------------------------------------ */
/* 字节工具                                                             */
/* ------------------------------------------------------------------ */

const BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/** 蓝牙库以 base64 交付特征值；Hermes 下不保证有 atob，这里自己解。 */
export function decodeBase64(value: string): Uint8Array {
  const clean = value.replace(/[^A-Za-z0-9+/]/g, "");
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const character of clean) {
    const index = BASE64_ALPHABET.indexOf(character);
    if (index < 0) continue;
    buffer = (buffer << 6) | index;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((buffer >> bits) & 0xff);
    }
  }
  return Uint8Array.from(bytes);
}

export function bytesToHex(bytes: ArrayLike<number>): string {
  return Array.from(bytes, (byte) => byte.toString(16).toUpperCase().padStart(2, "0")).join(" ");
}

/** UTF-8 解码；遇到非法序列整段退回 Latin-1，保证不抛错、不丢字节。 */
export function decodeScanBytes(bytes: ArrayLike<number>): string {
  let result = "";
  let index = 0;
  while (index < bytes.length) {
    const first = bytes[index] ?? 0;
    if (first < 0x80) {
      result += String.fromCharCode(first);
      index += 1;
      continue;
    }
    const length = first >= 0xf0 && first < 0xf8 ? 4 : first >= 0xe0 ? 3 : first >= 0xc2 && first < 0xe0 ? 2 : 0;
    if (length === 0 || index + length > bytes.length) return latin1(bytes);
    let codePoint = first & (0xff >> (length + 1));
    for (let offset = 1; offset < length; offset += 1) {
      const next = bytes[index + offset] ?? 0;
      if ((next & 0xc0) !== 0x80) return latin1(bytes);
      codePoint = (codePoint << 6) | (next & 0x3f);
    }
    result += String.fromCodePoint(codePoint);
    index += length;
  }
  return result;
}

function latin1(bytes: ArrayLike<number>): string {
  return Array.from(bytes, (byte) => String.fromCharCode(byte)).join("");
}

/** 电池服务 2A19 只有 1 字节 0–100；越界返回 null。 */
export function parseBatteryLevel(bytes: ArrayLike<number>): number | null {
  if (bytes.length < 1) return null;
  const level = bytes[0] ?? -1;
  return level >= 0 && level <= 100 ? level : null;
}

/* ------------------------------------------------------------------ */
/* 拼帧                                                                 */
/* ------------------------------------------------------------------ */

export type BleScanTerminator = "CR" | "LF" | "TAB" | "idle";

export type BleScanFrame = Readonly<{
  value: string;
  /** 本帧原始字节（含第一个结束符），测试页展开排查用。 */
  rawHex: string;
  packetCount: number;
  /** 首包到结束的耗时。 */
  durationMs: number;
  terminator: BleScanTerminator;
  receivedAt: number;
}>;

export type BleScanAssemblerDiagnostic = "max-length" | "stale-partial";

type TimerHandle = unknown;

export type BleScanFrameAssemblerOptions = Readonly<{
  onFrame: (frame: BleScanFrame) => void;
  onDiagnostic?: (diagnostic: BleScanAssemblerDiagnostic) => void;
  now?: () => number;
  /** 没见过结束符时，多久没有新包就当一个码结束。 */
  idleMs?: number;
  /** 见过结束符后，残码多久后清掉（只清不发）。 */
  staleMs?: number;
  maxLength?: number;
  setTimer?: (callback: () => void, ms: number) => TimerHandle;
  clearTimer?: (handle: TimerHandle) => void;
}>;

export const BLE_FRAME_IDLE_MS = 120;
export const BLE_FRAME_STALE_MS = 500;
export const BLE_FRAME_MAX_LENGTH = 256;

/**
 * BLE 每包最多 20 字节（MTU 23），长码会被拆成多包，包间实测最长 62ms。
 * 规则：
 * 1. CR / LF / Tab 结束一帧；连续结束符之间的空码丢弃（背夹结尾是 CR CR）。
 * 2. 没见过结束符的设备靠空闲超时（默认 120ms）结束。
 * 3. 一旦见过结束符，就只认结束符；残码 500ms 后清掉，避免低速传输把一个码切成两段。
 * 4. 超过最大长度整码丢弃，直到下一个结束符。
 */
export class BleScanFrameAssembler {
  private readonly onFrame: BleScanFrameAssemblerOptions["onFrame"];
  private readonly onDiagnostic?: BleScanFrameAssemblerOptions["onDiagnostic"];
  private readonly now: () => number;
  private readonly idleMs: number;
  private readonly staleMs: number;
  private readonly maxLength: number;
  private readonly setTimer: (callback: () => void, ms: number) => TimerHandle;
  private readonly clearTimer: (handle: TimerHandle) => void;
  private bytes: number[] = [];
  private raw: number[] = [];
  private packetCount = 0;
  private firstPacketAt: number | null = null;
  private overflowed = false;
  private seenTerminator = false;
  private timer: TimerHandle | null = null;

  constructor(options: BleScanFrameAssemblerOptions) {
    this.onFrame = options.onFrame;
    this.onDiagnostic = options.onDiagnostic;
    this.now = options.now ?? Date.now;
    this.idleMs = options.idleMs ?? BLE_FRAME_IDLE_MS;
    this.staleMs = options.staleMs ?? BLE_FRAME_STALE_MS;
    this.maxLength = options.maxLength ?? BLE_FRAME_MAX_LENGTH;
    this.setTimer = options.setTimer ?? ((callback, ms) => setTimeout(callback, ms));
    this.clearTimer =
      options.clearTimer ?? ((handle) => clearTimeout(handle as ReturnType<typeof setTimeout>));
  }

  /** 是否已经见过结束符（测试页据此提示「靠超时结束」）。 */
  hasSeenTerminator(): boolean {
    return this.seenTerminator;
  }

  push(packet: ArrayLike<number>, receivedAt = this.now()): void {
    this.cancelTimer();
    let packetCounted = false;
    for (let index = 0; index < packet.length; index += 1) {
      const byte = (packet[index] ?? 0) & 0xff;
      const terminator = terminatorFor(byte);
      if (terminator) {
        this.seenTerminator = true;
        if (this.bytes.length > 0 || this.overflowed) {
          this.raw.push(byte);
          if (!packetCounted) {
            this.packetCount += 1;
            packetCounted = true;
          }
          this.finish(terminator, receivedAt);
          packetCounted = false;
        }
        // 空码（CR CR 的第二个、CRLF 的 LF）直接丢弃。
        continue;
      }
      if (!packetCounted) {
        this.packetCount += 1;
        packetCounted = true;
      }
      if (this.firstPacketAt === null) this.firstPacketAt = receivedAt;
      this.raw.push(byte);
      if (this.overflowed) continue;
      // 去掉其余控制字符。
      if (byte < 0x20 || byte === 0x7f) continue;
      if (this.bytes.length >= this.maxLength * 4) {
        this.overflow();
        continue;
      }
      this.bytes.push(byte);
    }
    if (this.bytes.length > 0 || this.overflowed) {
      this.scheduleTimer();
    }
  }

  reset(): void {
    this.cancelTimer();
    this.clearPartial();
  }

  dispose(): void {
    this.reset();
  }

  private scheduleTimer(): void {
    const ms = this.seenTerminator ? this.staleMs : this.idleMs;
    this.timer = this.setTimer(() => {
      this.timer = null;
      if (this.seenTerminator) {
        if (this.bytes.length > 0) this.onDiagnostic?.("stale-partial");
        this.clearPartial();
        return;
      }
      this.finish("idle", this.now());
    }, ms);
  }

  private cancelTimer(): void {
    if (this.timer !== null) {
      this.clearTimer(this.timer);
      this.timer = null;
    }
  }

  private overflow(): void {
    if (!this.overflowed) this.onDiagnostic?.("max-length");
    this.overflowed = true;
    this.bytes = [];
  }

  private finish(terminator: BleScanTerminator, receivedAt: number): void {
    const overflowed = this.overflowed;
    const value = overflowed ? "" : decodeScanBytes(this.bytes).trim();
    const frame: BleScanFrame | null =
      !overflowed && value && value.length <= this.maxLength
        ? Object.freeze({
            value,
            rawHex: bytesToHex(this.raw),
            packetCount: Math.max(1, this.packetCount),
            durationMs: Math.max(0, receivedAt - (this.firstPacketAt ?? receivedAt)),
            terminator,
            receivedAt,
          })
        : null;
    if (!overflowed && value.length > this.maxLength) this.onDiagnostic?.("max-length");
    this.clearPartial();
    if (frame) this.onFrame(frame);
  }

  private clearPartial(): void {
    this.bytes = [];
    this.raw = [];
    this.packetCount = 0;
    this.firstPacketAt = null;
    this.overflowed = false;
  }
}

function terminatorFor(byte: number): BleScanTerminator | null {
  if (byte === 0x0d) return "CR";
  if (byte === 0x0a) return "LF";
  if (byte === 0x09) return "TAB";
  return null;
}

/* ------------------------------------------------------------------ */
/* 码分类与码型推测                                                       */
/* ------------------------------------------------------------------ */

export type BleScanKind = "barcode" | "setting-code" | "device-reply";

/**
 * 设置码正常由扫码枪自己处理，但不同型号不保证；设备回复（如版本信息）会被当数据发出来。
 * 这两类只在测试页显示，绝不进业务页面。
 */
export function classifyBleScanValue(value: string): BleScanKind {
  if (/^%%SpecCode[0-9A-Za-z]{1,4}$/.test(value)) return "setting-code";
  if (/^>[A-Za-z0-9]{2,16}<$/.test(value)) return "setting-code";
  if (/^HW:[^;]*;SW:/.test(value)) return "device-reply";
  return "barcode";
}

export type BarcodeSymbologyGuess = "EAN-13" | "UPC-A" | "EAN-8" | "other";

/** 只按长度和校验位推测，测试页展示用，不参与业务判断。 */
export function guessBarcodeSymbology(value: string): BarcodeSymbologyGuess {
  if (!/^\d+$/.test(value)) return "other";
  if (value.length === 13 && hasValidGtinCheckDigit(value)) return "EAN-13";
  if (value.length === 12 && hasValidGtinCheckDigit(value)) return "UPC-A";
  if (value.length === 8 && hasValidGtinCheckDigit(value)) return "EAN-8";
  return "other";
}

function hasValidGtinCheckDigit(value: string): boolean {
  const digits = Array.from(value, Number);
  const check = digits.pop() as number;
  let sum = 0;
  // 从右往左（不含校验位）奇数位乘 3。
  for (let index = digits.length - 1, position = 0; index >= 0; index -= 1, position += 1) {
    sum += (digits[index] ?? 0) * (position % 2 === 0 ? 3 : 1);
  }
  return (10 - (sum % 10)) % 10 === check;
}

/** 卡片 / 设置行只显示最近一次扫码的尾部 6 位。 */
export function scanValueTail(value: string, length = 6): string {
  return value.length <= length ? value : `…${value.slice(-length)}`;
}

/* ------------------------------------------------------------------ */
/* 跨来源去重                                                            */
/* ------------------------------------------------------------------ */

export const CROSS_SOURCE_DEDUPE_MS = 300;

/**
 * 同一个码 300ms 内从**不同来源**（hid / camera / ble）各来一次只算一次，
 * 例如同一把枪键盘模式和 BLE 模式都开着。同一来源的连续重复照常放行，
 * 不能误吞「连扫两件同款」。
 */
export class CrossSourceScanDeduper {
  private last: { value: string; source: string; at: number } | null = null;

  constructor(private readonly windowMs = CROSS_SOURCE_DEDUPE_MS) {}

  accept(value: string, source: string, at: number): boolean {
    const last = this.last;
    if (last && last.value === value && last.source !== source && at - last.at <= this.windowMs) {
      return false;
    }
    this.last = { value, source, at };
    return true;
  }

  reset(): void {
    this.last = null;
  }
}

/* ------------------------------------------------------------------ */
/* 已保存的扫码枪                                                        */
/* ------------------------------------------------------------------ */

export type SavedBleScanner = Readonly<{
  version: 1;
  peripheralId: string;
  /** 广播名，背夹固定为 BarCode Scanner BLE。 */
  name: string;
  /** 用户起的备注名，如「1 号背夹」。 */
  alias: string | null;
  profileId: string | null;
  serviceUuid: string;
  notifyUuid: string;
  autoReconnect: boolean;
  savedAt: string;
}>;

const MAX_TEXT = 80;

/** 存储里读出的内容不可信：字段白名单 + 长度限制，任何一项不合法整条作废。 */
export function parseSavedBleScanner(raw: unknown): SavedBleScanner | null {
  let value = raw;
  if (typeof value === "string") {
    try {
      value = JSON.parse(value);
    } catch {
      return null;
    }
  }
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  if (record.version !== 1) return null;
  const peripheralId = boundedText(record.peripheralId);
  const name = boundedText(record.name, true);
  const serviceUuid = boundedText(record.serviceUuid);
  const notifyUuid = boundedText(record.notifyUuid);
  const savedAt = boundedText(record.savedAt);
  if (!peripheralId || name === null || !serviceUuid || !notifyUuid || !savedAt) return null;
  const alias = record.alias === null || record.alias === undefined ? null : boundedText(record.alias);
  if (record.alias !== null && record.alias !== undefined && !alias) return null;
  const profileId =
    record.profileId === null || record.profileId === undefined ? null : boundedText(record.profileId);
  if (typeof record.autoReconnect !== "boolean") return null;
  return Object.freeze({
    version: 1,
    peripheralId,
    name,
    alias,
    profileId: profileId && findBleScannerProfile(profileId) ? profileId : null,
    serviceUuid: normalizeBleUuid(serviceUuid),
    notifyUuid: normalizeBleUuid(notifyUuid),
    autoReconnect: record.autoReconnect,
    savedAt,
  });
}

function boundedText(value: unknown, allowEmpty = false): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (!allowEmpty && !trimmed) return null;
  if (trimmed.length > MAX_TEXT) return null;
  return trimmed;
}

export function normalizeBleScannerAlias(value: string): string | null {
  const trimmed = value.trim().slice(0, 24);
  return trimmed ? trimmed : null;
}
