import type { PriceUpdateRunState } from "../price-updates/batch-runner";
import { buildPriceComparison, type PriceComparison } from "../price-updates/presentation";
import type { StorePriceUpdateTask } from "../price-updates/types";

/** 商品查询页「价格任务卡」的纯逻辑：选任务、算展示模型、把批处理状态折成三步进度。 */

export interface ProductTaskTarget {
  productCode: string | null | undefined;
}

function sameCode(a: string | null | undefined, b: string | null | undefined) {
  const left = a?.trim().toLowerCase();
  const right = b?.trim().toLowerCase();
  return Boolean(left) && left === right;
}

/**
 * 从关键字查询结果里挑出「这个商品、这个分店」的未完成任务。
 * 后端关键字是 Contains 模糊匹配，必须在前端按商品编码精确过滤；
 * 业务上同店同品至多一条未完成任务，万一有多条，优先「需改价」，再取最早发起的（最先超期）。
 */
export function pickProductTask(
  tasks: readonly StorePriceUpdateTask[],
  product: ProductTaskTarget,
  storeCode: string | null | undefined
): StorePriceUpdateTask | null {
  const matches = tasks.filter(
    (task) =>
      task.status === "Pending" &&
      sameCode(task.productCode, product.productCode) &&
      sameCode(task.storeCode, storeCode)
  );
  if (matches.length === 0) return null;
  return [...matches].sort((a, b) => {
    if (a.kind !== b.kind) return a.kind === "PriceUpdate" ? -1 : 1;
    return Date.parse(a.firstInitiatedAtUtc) - Date.parse(b.firstInitiatedAtUtc);
  })[0];
}

export type ProductTaskCardVariant = "priceUpdate" | "labelOnly";

export interface ProductTaskCardModel {
  variant: ProductTaskCardVariant;
  overdue: boolean;
  overdueByDays: number;
  waitingDays: number;
  comparison: PriceComparison;
}

export function buildProductTaskCardModel(task: StorePriceUpdateTask): ProductTaskCardModel {
  return {
    variant: task.kind === "PriceUpdate" ? "priceUpdate" : "labelOnly",
    overdue: task.isOverdue,
    overdueByDays: task.overdueByDays,
    waitingDays: task.waitingDays,
    comparison: buildPriceComparison(task),
  };
}

export type RunStepStatus = "waiting" | "active" | "done" | "failed" | "skipped";

export interface RunSteps {
  /** ① 更新价格（只有需改价任务才有这一步）。 */
  apply: RunStepStatus;
  /** ② 打印标签。 */
  print: RunStepStatus;
  /** ③ 回写任务完成状态（打印成功后立即回写，跟随打印结果）。 */
  complete: RunStepStatus;
}

/**
 * 把批处理状态折成三步进度。
 * 回写发生在打印成功之后且与打印同属一个条目，所以第三步只在打印成功后变为完成；
 * 打印失败时任务仍留在「待换标签」，第三步标记为 skipped。
 */
export function resolveRunSteps(state: PriceUpdateRunState | null, hasApplyStep: boolean): RunSteps {
  if (!state) {
    return {
      apply: hasApplyStep ? "waiting" : "skipped",
      print: "waiting",
      complete: "waiting",
    };
  }

  let apply: RunStepStatus;
  if (!hasApplyStep) {
    apply = "skipped";
  } else if (state.phase === "applying") {
    apply = "active";
  } else if (state.apply.success > 0) {
    apply = "done";
  } else {
    apply = "failed";
  }

  // 仅更新模式 / 标签机不可用：不会进入打印，后两步直接跳过。
  if (state.mode === "applyOnly" || state.printerUnavailable) {
    return { apply, print: "skipped", complete: "skipped" };
  }
  // 改价失败或仓库价格又变了：没有可打印的内容。
  if (hasApplyStep && apply === "failed") {
    return { apply, print: "skipped", complete: "skipped" };
  }
  if (state.phase === "applying") {
    return { apply, print: "waiting", complete: "waiting" };
  }

  const item = state.prints[0];
  if (!item) {
    // 改价后价格已一致，任务直接完成，无需打印。
    return { apply, print: "skipped", complete: state.phase === "done" ? "done" : "waiting" };
  }
  switch (item.status) {
    case "printing":
      return { apply, print: "active", complete: "waiting" };
    case "printed":
      return { apply, print: "done", complete: "done" };
    case "failed":
      // 已出纸但回写失败：打印算完成，回写失败，重试只补回写。
      return item.printedAwaitingConfirm
        ? { apply, print: "done", complete: "failed" }
        : { apply, print: "failed", complete: "skipped" };
    case "skipped":
      return { apply, print: "skipped", complete: "skipped" };
    default:
      return { apply, print: "waiting", complete: "waiting" };
  }
}

export type RunOutcome =
  | "running"
  /** 全部完成：改价成功且已打印（或无需改价、无需打印）。 */
  | "completed"
  /** 只改了价格，标签未打（仅更新 / 标签机不可用）：任务转为待换标签。 */
  | "priceUpdatedLabelPending"
  /** 仓库价格再次变化，需要重新确认。 */
  | "targetChanged"
  | "applyFailed"
  | "printFailed";

/** 汇总一次运行的结果，用来决定卡片下一步展示什么。 */
export function resolveRunOutcome(state: PriceUpdateRunState | null): RunOutcome {
  if (!state || state.phase !== "done") return "running";
  if (state.apply.targetChanged > 0) return "targetChanged";
  if (state.apply.total > 0 && state.apply.success === 0) return "applyFailed";
  if (state.prints.some((item) => item.status === "failed" || item.status === "skipped")) {
    return "printFailed";
  }
  if (state.mode === "applyOnly" || state.printerUnavailable) {
    return state.apply.success > 0 ? "priceUpdatedLabelPending" : "applyFailed";
  }
  return "completed";
}
