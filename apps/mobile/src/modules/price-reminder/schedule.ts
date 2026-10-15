/** 提醒间隔：每小时最多弹一次。 */
export const PRICE_REMINDER_INTERVAL_MS = 60 * 60 * 1000;
/** 前台期间每分钟检查一次是否到点；到点才会真的弹。 */
export const PRICE_REMINDER_TICK_MS = 60 * 1000;
/** 本机时钟被往回拨时，上次弹出时间会跑到「未来」；超过这个容差就当作已到点，避免一小时内永远不再提醒。 */
const CLOCK_SKEW_TOLERANCE_MS = 5 * 60 * 1000;

export type PriceReminderScope =
  | { kind: "account"; userGuid: string }
  | { kind: "device"; hardwareId: string; storeCode: string };

/**
 * 「上次弹出时间」的本机存储键。店长账号按账号记，PDA 按「设备 + 分店」记：
 * 同一台 PDA 换绑分店后是另一份待办，应当重新提醒；同一个账号在不同手机上互不影响。
 */
export function buildPriceReminderStorageKey(scope: PriceReminderScope): string {
  return scope.kind === "account"
    ? `priceReminder:lastShown:account:${scope.userGuid}`
    : `priceReminder:lastShown:device:${scope.hardwareId}:${scope.storeCode}`;
}

/** 存储里读出来的值不可信（被清理 / 写坏）：不是正数就当作「从没弹过」。 */
export function parseLastShownAt(raw: string | null | undefined): number | null {
  if (!raw) return null;
  const value = Number(raw);
  return Number.isFinite(value) && value > 0 ? value : null;
}

export interface PriceReminderGateInput {
  nowMs: number;
  lastShownAtMs: number | null;
  /** 汇总已拿到（没拿到就不提醒：接口故障时不能用旧数据或空数据弹窗）。 */
  overviewReady: boolean;
  /** 可访问分店的未完成合计；为 0 说明全部处理完了，不再提醒。 */
  totalPending: number;
  appActive: boolean;
  /** 已经停在「价格和标签」页：人就在处理，再弹窗是打扰。 */
  onPriceUpdatesScreen: boolean;
  /** iOS 审核模式等不应出现提醒的场合。 */
  suppressed: boolean;
  /** 正在打印标签或提交批量处理：不能被弹窗打断。 */
  busy: boolean;
}

export function shouldShowPriceReminder(input: PriceReminderGateInput): boolean {
  if (
    !input.overviewReady ||
    input.totalPending <= 0 ||
    !input.appActive ||
    input.onPriceUpdatesScreen ||
    input.suppressed ||
    input.busy
  ) {
    return false;
  }
  if (input.lastShownAtMs == null) return true;

  const elapsed = input.nowMs - input.lastShownAtMs;
  if (elapsed < -CLOCK_SKEW_TOLERANCE_MS) return true;
  return elapsed >= PRICE_REMINDER_INTERVAL_MS;
}
