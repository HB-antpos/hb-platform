import type { ContainerJob, CreateNewProductsRunResult } from "./types";

/**
 * 「创建新商品」「提交整柜」结果的展示模型（纯函数）：
 * 界面按 key 查 i18n 文案，tone 决定颜色。
 * 关键约束：创建成功永远不能因为「可选的 HQ 同步」失败而被报告成失败，
 * 所以 HQ 一侧的问题只会产生 warning 行，不会改写 creation 行。
 */

export type ResultTone = "success" | "warning" | "danger" | "neutral";

export interface ResultLine {
  tone: ResultTone;
  /** containerDetail 命名空间下的文案键 */
  key: string;
  params?: Record<string, string | number>;
  /** 后端原始说明（job.message / 错误信息），原样追加显示 */
  detail?: string;
}

export interface CreateNewProductsResultView {
  creation: ResultLine;
  /** 没有执行同步（含未勾选）时为 null 以外的说明行；blocked 时为 null */
  hq: ResultLine | null;
  extras: ResultLine[];
}

function firstText(...values: (string | undefined | null)[]) {
  return values.map((value) => value?.trim()).find((value): value is string => Boolean(value));
}

export function describeCreateNewProductsResult(result: CreateNewProductsRunResult): CreateNewProductsResultView {
  if (result.status === "blocked") {
    return {
      creation: {
        tone: "danger",
        key: result.blockedReason === "MISSING_RETAIL_PRICE" ? "createProducts.result.blockedMissingPrice" : "createProducts.result.blockedNoDetails",
      },
      hq: null,
      extras: [],
    };
  }

  const job = result.job;
  let creation: ResultLine;
  if (!job || job.status === "Failed") {
    creation = {
      tone: "danger",
      key: "createProducts.result.creationFailed",
      detail: firstText(job?.message, job?.result?.errors?.[0]?.message),
    };
  } else {
    const { createdCount, skippedCount, failedCount } = job.result;
    if (failedCount > 0 || skippedCount > 0) {
      creation = {
        tone: "warning",
        key: "createProducts.result.creationPartial",
        params: { created: createdCount, skipped: skippedCount, failed: failedCount },
        detail: firstText(job.message),
      };
    } else if (createdCount === 0) {
      creation = { tone: "neutral", key: "createProducts.result.creationNothing", detail: firstText(job.message) };
    } else {
      creation = { tone: "success", key: "createProducts.result.creationSuccess", params: { created: createdCount } };
    }
  }

  const extras: ResultLine[] = (result.plan?.warnings ?? []).flatMap<ResultLine>((warning) => {
    if (warning.code === "UNSENT_CREATED") {
      return [{ tone: "warning", key: "createProducts.result.hq.unsentCreated", params: { count: warning.count } }];
    }
    return [];
  });

  const push = result.push;
  let hq: ResultLine | null;
  switch (push.status) {
    case "skipped":
      switch (push.reason) {
        case "sync-disabled":
          hq = { tone: "neutral", key: "createProducts.result.hq.syncDisabled" };
          break;
        case "nothing-created":
          hq = { tone: "neutral", key: "createProducts.result.hq.nothingCreated" };
          break;
        case "no-candidates":
          hq = { tone: "warning", key: "createProducts.result.hq.noCandidates" };
          break;
        case "push-busy":
          hq = { tone: "warning", key: "createProducts.result.hq.pushBusy" };
          break;
        default:
          hq = null;
      }
      break;
    case "succeeded":
      hq = {
        tone: "success",
        key: "createProducts.result.hq.succeeded",
        params: { success: push.job.result?.successCount ?? 0, total: push.job.result?.totalCount ?? 0 },
      };
      break;
    case "failed":
      hq = {
        tone: "warning",
        key: "createProducts.result.hq.failed",
        detail: firstText(push.job.message, push.job.errors?.[0], push.job.result?.errors?.[0]),
      };
      break;
    case "error":
      hq = { tone: "warning", key: "createProducts.result.hq.error", detail: firstText(push.message) };
      break;
  }

  return { creation, hq, extras };
}

/** 提交整柜任务结果：Failed 状态按失败处理，成功但有失败明细按部分成功。 */
export function describeSubmitContainerResult(job: ContainerJob): ResultLine {
  if (job.status === "Failed") {
    return { tone: "danger", key: "submitContainer.result.failed", detail: firstText(job.message, job.result.errors[0]?.message) };
  }
  if (job.result.failedCount > 0) {
    return {
      tone: "warning",
      key: "submitContainer.result.partial",
      params: { failed: job.result.failedCount },
      detail: firstText(job.message),
    };
  }
  return { tone: "success", key: "submitContainer.result.success", detail: firstText(job.message) };
}
