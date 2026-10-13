// 命中复核的纯逻辑：相似度与档位展示、两人复核流程、判断按钮可用性、参考照片切换。
// 约定：相似度只写「相似度 NN%（不是同一个人的概率）」；档位用服务端 band 字段，页面不自己用阈值计算。
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

export type ReviewStep = "first" | "second" | "done";

/** nextStep：first 待第一复核人、second 待第二复核人、其余（none / 未知）视为已结束。 */
export function getReviewStep(task: Pick<FaceUnwelcomeTask, "nextStep">): ReviewStep {
  if (task.nextStep === "first") return "first";
  if (task.nextStep === "second") return "second";
  return "done";
}

export const REVIEW_STEP_HINT_KEYS: Record<ReviewStep, string> = {
  first: "review.step.first",
  second: "review.step.second",
  done: "review.step.done",
};

type VerdictGate = Pick<FaceUnwelcomeTask, "canReview" | "imageAvailable" | "nextStep">;

/**
 * 是否可以提交判断：必须服务端 canReview（含「第二复核人必须不同于第一复核人」）、抓拍图还在、任务还没结束，
 * 并且功能没有处于只读状态。App 不自己推断谁能复核。
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
