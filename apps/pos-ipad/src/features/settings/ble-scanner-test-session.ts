/**
 * 设置页「测试扫码」面板的纯逻辑：记录最近 20 条（只在内存）、敏感码屏蔽、
 * 检测「同一个码从键盘模式和 BLE 模式各来一次」（同一把枪两种模式都开着）。
 */
import type {
  BleScanFrame,
  BleScanKind,
  BleScanTerminator,
} from "@/core/peripherals/scanner/ble/ble-scanner-core";
import { isDeviceActivationScanValue } from "@/core/peripherals/scanner/hid-scanner";

export const SCANNER_TEST_HISTORY_LIMIT = 20;
/** 双模式重复的判定窗口：比路由去重（300ms）宽，测试时尽量把问题暴露出来。 */
export const SCANNER_TEST_DUPLICATE_WINDOW_MS = 1_000;

export type ScannerTestSource = "ble" | "hid" | "camera";

export type ScannerTestEntry = Readonly<{
  id: number;
  source: ScannerTestSource;
  /** 展示用的值：敏感码已屏蔽。 */
  value: string;
  masked: boolean;
  kind: BleScanKind;
  length: number;
  packetCount: number | null;
  durationMs: number | null;
  terminator: BleScanTerminator | null;
  /** 屏蔽的码不保留原始字节。 */
  rawHex: string | null;
  receivedAt: number;
  duplicate: boolean;
  /** 仅用于重复比对的内部键；敏感码为 null，不参与比对也不外露。 */
  matchKey: string | null;
}>;

export function createScannerTestEntry(
  input: Readonly<{
    id: number;
    source: ScannerTestSource;
    value: string;
    receivedAt: number;
    kind?: BleScanKind;
    frame?: BleScanFrame;
  }>,
): ScannerTestEntry {
  const masked = isDeviceActivationScanValue(input.value);
  return Object.freeze({
    id: input.id,
    source: input.source,
    value: masked ? "HBDEV1-••••••" : input.value,
    masked,
    kind: input.kind ?? "barcode",
    length: input.value.length,
    packetCount: input.frame?.packetCount ?? null,
    durationMs: input.frame?.durationMs ?? null,
    terminator: input.frame?.terminator ?? null,
    rawHex: masked ? null : (input.frame?.rawHex ?? null),
    receivedAt: input.receivedAt,
    duplicate: false,
    matchKey: masked ? null : input.value,
  });
}

/** 新记录放最前；与窗口内「不同来源的同码」互相标记为重复。 */
export function appendScannerTestEntry(
  entries: readonly ScannerTestEntry[],
  entry: ScannerTestEntry,
): Readonly<{ entries: readonly ScannerTestEntry[]; duplicate: boolean }> {
  const isPair = (other: ScannerTestEntry) =>
    entry.matchKey !== null &&
    other.matchKey === entry.matchKey &&
    other.source !== entry.source &&
    Math.abs(entry.receivedAt - other.receivedAt) <= SCANNER_TEST_DUPLICATE_WINDOW_MS;
  const duplicate = entries.some(isPair);
  const next = [
    duplicate ? Object.freeze({ ...entry, duplicate: true }) : entry,
    ...entries.map((other) =>
      duplicate && isPair(other) && !other.duplicate
        ? Object.freeze({ ...other, duplicate: true })
        : other,
    ),
  ].slice(0, SCANNER_TEST_HISTORY_LIMIT);
  return Object.freeze({ entries: Object.freeze(next), duplicate });
}
