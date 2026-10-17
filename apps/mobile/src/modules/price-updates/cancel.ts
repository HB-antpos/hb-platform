import type { PriceUpdateBatchResult, StorePriceUpdateTask } from "./types";

/** 撤销备注的最大长度，与后端 CancelStorePriceUpdateTasksRequestDto.MaxNoteLength 一致（超出会被后端截断）。 */
export const CANCEL_NOTE_MAX_LENGTH = 200;

/**
 * 是否显示撤销入口：必须是账号会话，且有撤销权限。
 * 撤销权限 = 专用码 Warehouse.CancelPriceUpdateTasks，或旧的 Warehouse.ManageProducts：
 * 后端上线新码前，cancel 接口还是由 ManageProducts 守卫；上线后别名让两者互相放行，
 * 所以同时认两个码，OTA 先发或后端先发都不会让人突然看不到入口。
 * 设备会话（PDA 绑定分店）没有账号权限，接口一定 401/403，所以不显示入口，避免点了才报错。
 */
export function canCancelPriceTasks(input: {
  isDeviceMode: boolean;
  hasCancelPermission: boolean;
  hasManageProducts: boolean;
}): boolean {
  return !input.isDeviceMode && (input.hasCancelPermission || input.hasManageProducts);
}

/** 备注去掉首尾空白并截到上限；空白视为没写（不发送）。 */
export function normalizeCancelNote(note: string): string | undefined {
  const trimmed = note.trim();
  return trimmed ? trimmed.slice(0, CANCEL_NOTE_MAX_LENGTH) : undefined;
}

/**
 * 撤销范围是「商品」而不是「当前分店的几条任务」：与 Web「按商品」撤销一致，
 * 一个商品的撤销会覆盖它在所有分店的未完成任务。这里把选中的任务折成不重复的商品编码（忽略大小写）。
 */
export function distinctProductCodes(tasks: readonly Pick<StorePriceUpdateTask, "productCode">[]): string[] {
  const seen = new Set<string>();
  const codes: string[] = [];
  for (const task of tasks) {
    const code = task.productCode?.trim();
    if (code && !seen.has(code.toLowerCase())) {
      seen.add(code.toLowerCase());
      codes.push(code);
    }
  }
  return codes;
}

export type CancelOutcomeKind =
  /** 全部撤销成功。 */
  | "success"
  /** 一部分撤销成功，其余已被别人处理或不存在（后端逐条回报并跳过）。 */
  | "partial"
  /** 一个都没撤销（都已被处理 / 不存在）。 */
  | "nothing"
  /** 后面的商品因网络 / 服务异常没提交，这些商品的任务仍留在未完成里，需要重试。 */
  | "interrupted";

export interface CancelOutcome {
  kind: CancelOutcomeKind;
  /** 实际撤销的任务数（跨所有分店）。 */
  cancelled: number;
  /** 已被处理或不存在而跳过的任务数。 */
  skipped: number;
  /** 因请求失败没提交的商品数。 */
  notSubmitted: number;
}

export function classifyCancelOutcome(counts: { cancelled: number; skipped: number; notSubmitted: number }): CancelOutcome {
  let kind: CancelOutcomeKind;
  if (counts.notSubmitted > 0) {
    kind = "interrupted";
  } else if (counts.cancelled === 0) {
    kind = "nothing";
  } else if (counts.skipped > 0) {
    kind = "partial";
  } else {
    kind = "success";
  }
  return { kind, ...counts };
}

/**
 * 按商品逐个撤销并汇总。后端每次请求只接受一个商品（productCode），所以多个商品要分次串行提交。
 * - 第一个商品就失败：原样抛出，和不分批时一致（整体失败，调用方显示错误）。
 * - 后面某个商品失败：已提交的不回滚；该商品及之后没提交的都算「未处理」，仍留在未完成里，用户可以稍后重试。
 */
export async function runCancelByProduct(options: {
  productCodes: readonly string[];
  cancelOne: (productCode: string) => Promise<PriceUpdateBatchResult>;
}): Promise<CancelOutcome> {
  let cancelled = 0;
  let skipped = 0;
  let notSubmitted = 0;
  for (let index = 0; index < options.productCodes.length; index += 1) {
    try {
      const result = await options.cancelOne(options.productCodes[index]);
      cancelled += result.successCount;
      skipped += result.failedCount;
    } catch (error) {
      if (index === 0) {
        throw error;
      }
      notSubmitted = options.productCodes.length - index;
      break;
    }
  }
  return classifyCancelOutcome({ cancelled, skipped, notSubmitted });
}
