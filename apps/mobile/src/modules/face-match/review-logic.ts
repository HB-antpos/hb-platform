// 命中复核的纯逻辑：相似度与档位展示、复核进度（待复核 / 已结束）、判断按钮可用性、参考照片切换。
// 约定：相似度只写「相似度 NN%（不等于是同一个人的概率）」；档位用服务端 band 字段，页面不自己用阈值计算。
// 规则：一位有复核权限的人判断一次即结束（match → 已确认；notMatch → 已驳回），不再区分先后复核人。
import type { FaceUnwelcomeBand, FaceUnwelcomeTask } from "./types";

/** 相似度百分比：以服务端整数 scorePercent 为准，夹在 0-100；绝不写成「概率」。 */
export function formatSimilarityPercent(task: Pick<FaceUnwelcomeTask, "scorePercent">): number {
  const value = Number.isFinite(task.scorePercent) ? task.scorePercent : 0;
  return Math.min(100, Math.max(0, Math.round(value)));
}

/** 档位文案键（faceMatch 命名空间）：由服务端 band 字段决定。 */
export const BAND_LABEL_KEYS: Record<FaceUnwelcomeBand, string> = {
  high: "review.band.high",
  normal: "review.band.normal",
};

export type ReviewStep = "pending" | "done";

/**
 * nextStep：first 与 second 一律是「待复核」，其余（none / 未知）视为已结束。
 * 新后端只会返回 first / none；second 是旧版遗留（已一审、待二审）任务，也要能被一个人判断结束，
 * 同时兼容 OTA 先于后端上线的时间窗，所以这里不区分。
 */
export function getReviewStep(task: Pick<FaceUnwelcomeTask, "nextStep">): ReviewStep {
  return task.nextStep === "first" || task.nextStep === "second" ? "pending" : "done";
}

export const REVIEW_STEP_HINT_KEYS: Record<ReviewStep, string> = {
  pending: "review.step.pending",
  done: "review.step.done",
};

type VerdictGate = Pick<FaceUnwelcomeTask, "canReview" | "imageAvailable" | "nextStep">;

/**
 * 是否可以提交判断：必须服务端 canReview、抓拍图还在、任务还没结束，并且功能没有处于只读状态。
 * App 不自己推断谁能复核（不看复核人姓名，也不比较是不是同一个人）。
 */
export function canSubmitVerdict(task: VerdictGate, writeBlocked: boolean): boolean {
  return !writeBlocked && task.canReview && task.imageAvailable && getReviewStep(task) !== "done";
}

/** 不能提交时给出原因文案键（按优先级）；可以提交返回 null。 */
export function getVerdictBlockReasonKey(task: VerdictGate, writeBlocked: boolean): string | null {
  if (getReviewStep(task) === "done") return "review.blocked.done";
  if (!task.imageAvailable) return "review.blocked.imageGone";
  if (writeBlocked) return "review.blocked.readOnly";
  if (!task.canReview) return "review.blocked.cannotReview";
  return null;
}

/** 参考照片切换项：0 .. referencePhotoCount-1。 */
export function referenceIndexes(task: Pick<FaceUnwelcomeTask, "referencePhotoCount">): number[] {
  return Array.from({ length: Math.max(0, task.referencePhotoCount) }, (_, index) => index);
}

/** 左右切换参考照片：到头不循环（避免用户误以为只有一张）。 */
export function stepReferenceIndex(current: number, count: number, delta: -1 | 1): number {
  if (count <= 0) return 0;
  return Math.min(count - 1, Math.max(0, current + delta));
}

export function canStepReference(current: number, count: number, delta: -1 | 1): boolean {
  return count > 1 && stepReferenceIndex(current, count, delta) !== current;
}
