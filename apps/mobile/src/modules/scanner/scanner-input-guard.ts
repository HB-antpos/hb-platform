import { extractVisibleBarcodeInput } from "./visible-barcode-input";

type TimerHandle = unknown;

export interface ScannerInputGuardOptions {
  /** 把文本写回字段（等同字段原本的 onChangeText，可在里面做清洗）。 */
  apply: (value: string) => void;
  /** 识别为扫码后调用；此时字段已恢复成扫码前的值。 */
  onScan: (barcode: string) => void;
  /** 当前写入是否可能来自扫码枪（通常是 Android 且字段聚焦）。 */
  isScannerCandidate: () => boolean;
  burstGapMs?: number;
  now?: () => number;
  setTimer?: (callback: () => void, ms: number) => TimerHandle;
  clearTimer?: (handle: TimerHandle) => void;
}

interface Burst {
  baseValue: string;
  changes: number;
  lastAt: number;
}

/**
 * 普通可见输入框（名称、价格、数量、时间…）的扫码防护。
 * Zebra DataWedge / HID 扫码枪在输入框聚焦时会把条码当键盘输入写进去：
 * - 一次 onChangeText 整段写入 8~18 位数字，或
 * - 间隔 ≤ burstGapMs 的连续写入最终增量为 8~18 位数字，
 * 都视为扫码：把字段恢复成扫码前的值，再把条码交给调用方路由（例如加入商品）。
 * 慢速手输、删除、粘贴非纯数字文本都原样写回字段。
 */
export function createScannerInputGuard({
  apply,
  onScan,
  isScannerCandidate,
  burstGapMs = 50,
  now = Date.now,
  setTimer = (callback, ms) => setTimeout(callback, ms),
  clearTimer = (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}: ScannerInputGuardOptions) {
  let value = "";
  let burst: Burst | null = null;
  let pendingBulk: { barcode: string; baseValue: string } | null = null;
  let timer: TimerHandle | null = null;

  const reset = () => {
    if (timer !== null) {
      clearTimer(timer);
      timer = null;
    }
    burst = null;
    pendingBulk = null;
  };

  const finish = (barcode: string, baseValue: string) => {
    reset();
    value = baseValue;
    apply(baseValue);
    onScan(barcode);
  };

  /** 停顿后判断这一串快速写入是否为条码。 */
  const resolveBurst = (): boolean => {
    const finished = burst;
    if (!finished || finished.changes < 2) return false;
    const barcode = extractVisibleBarcodeInput(finished.baseValue, value);
    if (!barcode) return false;
    finish(barcode, finished.baseValue);
    return true;
  };

  return {
    /** 受控值变化时同步（字段清洗后的值以外部为准）。 */
    syncValue(next: string) {
      value = next;
    },

    change(next: string) {
      const previous = value;
      if (next === previous) return;
      if (!isScannerCandidate()) {
        reset();
        value = next;
        apply(next);
        return;
      }

      // 先照常写入，恢复放到下一轮：同一事件里「写入再恢复」会被 React 合并成无变化，
      // 原生输入框就不会刷新，条码会残留在框里。
      const bulkBarcode = pendingBulk ? null : extractVisibleBarcodeInput(previous, next);
      value = next;
      apply(next);

      if (bulkBarcode) {
        reset();
        pendingBulk = { barcode: bulkBarcode, baseValue: previous };
        timer = setTimer(() => {
          timer = null;
          const pending = pendingBulk;
          if (pending) finish(pending.barcode, pending.baseValue);
        }, 0);
        return;
      }
      if (pendingBulk) return;

      const changedAt = now();
      burst =
        burst && changedAt - burst.lastAt <= burstGapMs
          ? { baseValue: burst.baseValue, changes: burst.changes + 1, lastAt: changedAt }
          : { baseValue: previous, changes: 1, lastAt: changedAt };
      if (timer !== null) clearTimer(timer);
      timer = setTimer(() => {
        timer = null;
        if (!resolveBurst()) burst = null;
      }, burstGapMs);
    },

    /**
     * 回车 / 提交时调用：扫码枪通常在条码后追加回车，此时 burst 计时器还没到点。
     * 返回 true 表示这次提交属于扫码，已路由条码，调用方应跳过原提交逻辑。
     */
    flush(): boolean {
      if (pendingBulk) {
        const pending = pendingBulk;
        finish(pending.barcode, pending.baseValue);
        return true;
      }
      if (resolveBurst()) return true;
      return false;
    },

    isPending() {
      return pendingBulk !== null || (burst !== null && burst.changes >= 2);
    },

    dispose() {
      reset();
    },
  };
}

export type ScannerInputGuard = ReturnType<typeof createScannerInputGuard>;
