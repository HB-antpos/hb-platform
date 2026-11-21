/**
 * BLE 扫码枪的码分发中心（纯 TS，不依赖 React Native，便于 tsx 直接测）。
 *
 * 移动端没有全局扫码总线：各页面自己调用 useHidBarcodeScanner，用 enabled 表示「我在接收扫码」。
 * BLE 码不走键盘，必须有人决定投给谁，规则是：
 * 1. 租约栈：每个 enabled 的 useHidBarcodeScanner 持有一份租约，BLE 码**只投给最后一个**仍持有租约的
 *    handler（最后启用的通常是最上层的弹层或当前聚焦页面）；它释放后自动回到前一个。
 * 2. 测试独占：设置页「测试扫码」打开时设置拦截器，所有 BLE 码只给它，不触发任何业务。
 * 3. 跨来源去重：同一个码 300ms 内从不同来源（ble / hid）各来一次只算一次
 *    （同一把枪键盘模式和 BLE 模式都开着时）；同一来源的连续重复照常放行。
 */
import { CrossSourceScanDeduper } from "./ble-scanner-core";

export type BleScanLeaseHandler = (value: string) => boolean | void;

export type BleScanLease = Readonly<{ release: () => void }>;

export type ScanHubSource = "ble" | "hid" | "camera";

export type CrossSourceDuplicate = Readonly<{
  value: string;
  /** 被丢弃的那一次的来源。 */
  droppedSource: ScanHubSource;
  at: number;
}>;

type LeaseEntry = { id: number; handler: BleScanLeaseHandler };

const leases: LeaseEntry[] = [];
let nextLeaseId = 1;
let interceptor: ((value: string) => void) | null = null;
let deduper = new CrossSourceScanDeduper();
let now: () => number = () => Date.now();
const duplicateListeners = new Set<(duplicate: CrossSourceDuplicate) => void>();

/** 申请一份接收 BLE 码的租约；release 可重复调用（React effect 清理可能执行多次）。 */
export function acquireBleScanLease(handler: BleScanLeaseHandler): BleScanLease {
  const entry: LeaseEntry = { id: nextLeaseId++, handler };
  leases.push(entry);
  let released = false;
  return Object.freeze({
    release() {
      if (released) return;
      released = true;
      const index = leases.findIndex((item) => item.id === entry.id);
      if (index >= 0) leases.splice(index, 1);
    },
  });
}

/** 测试面板打开时独占所有 BLE 码；传 null 恢复正常分发。 */
export function setBleScanInterceptor(next: ((value: string) => void) | null): void {
  interceptor = next;
}

export function hasBleScanInterceptor(): boolean {
  return interceptor !== null;
}

/**
 * 投递一个 BLE 业务码。返回 true 表示有人处理（含被去重吞掉、被测试面板拦截），
 * false 表示当前没有页面接收扫码（调用方据此轻提示）。
 */
export function deliverBleScan(value: string, at: number = now()): boolean {
  if (!acceptOrReportDuplicate(value, "ble", at)) return true;
  if (interceptor) {
    interceptor(value);
    return true;
  }
  const top = leases[leases.length - 1];
  if (!top) return false;
  return top.handler(value) !== false;
}

/**
 * 键盘模式（HID）/ 相机路径在调用页面 onScan 前登记来源。
 * 返回 false 表示同码 300ms 内已经从别的来源（如 BLE）来过，本次应跳过。
 */
export function noteScanSource(value: string, source: Exclude<ScanHubSource, "ble">, at: number = now()): boolean {
  return acceptOrReportDuplicate(value, source, at);
}

/** 测试页用：订阅「被跨来源去重丢掉的码」，用来提示「同一把枪键盘模式也开着」。 */
export function subscribeCrossSourceDuplicates(listener: (duplicate: CrossSourceDuplicate) => void): () => void {
  duplicateListeners.add(listener);
  return () => {
    duplicateListeners.delete(listener);
  };
}

function acceptOrReportDuplicate(value: string, source: ScanHubSource, at: number): boolean {
  if (deduper.accept(value, source, at)) return true;
  const duplicate: CrossSourceDuplicate = Object.freeze({ value, droppedSource: source, at });
  duplicateListeners.forEach((listener) => {
    try {
      listener(duplicate);
    } catch {
      // 诊断监听出错不影响分发。
    }
  });
  return false;
}

/** 仅测试用：清空租约、拦截器、去重状态，并可注入时钟。 */
export function __resetBleScanHubForTests(clock?: () => number): void {
  leases.splice(0);
  nextLeaseId = 1;
  interceptor = null;
  deduper = new CrossSourceScanDeduper();
  duplicateListeners.clear();
  now = clock ?? (() => Date.now());
}

export function __getBleScanLeaseCountForTests(): number {
  return leases.length;
}
