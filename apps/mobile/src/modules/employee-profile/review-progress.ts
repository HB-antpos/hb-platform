import type { EmployeeProfileSensitiveChangeStatus } from "./types";
import type { SensitiveProfileSection } from "./profile-screen-state";

// 审核进度页（B5）的纯逻辑：时间线、可执行操作与重提时定位的分组。

export type ReviewTimelineStepKey = "submitted" | "reviewing" | "result";
/** done=已完成，current=进行中，upcoming=未开始，failed=被驳回，stopped=流程中止（撤回/失效）。 */
export type ReviewTimelineStepState = "done" | "current" | "upcoming" | "failed" | "stopped";

export interface ReviewTimelineStep {
  key: ReviewTimelineStepKey;
  state: ReviewTimelineStepState;
  /** employeeProfile 命名空间下的文案键。 */
  labelKey: string;
  time?: string;
}

interface ProgressRequest {
  requestId: number;
  status: EmployeeProfileSensitiveChangeStatus;
  submittedAt: string;
  reviewedAt?: string;
}

export function buildSensitiveReviewTimeline(request: ProgressRequest): ReviewTimelineStep[] {
  const submitted: ReviewTimelineStep = {
    key: "submitted",
    state: "done",
    labelKey: "progress.timeline.submitted",
    time: request.submittedAt || undefined,
  };
  const reviewedAt = request.reviewedAt || undefined;
  switch (request.status) {
    case "Pending":
      return [
        submitted,
        { key: "reviewing", state: "current", labelKey: "progress.timeline.reviewing" },
        { key: "result", state: "upcoming", labelKey: "progress.timeline.result" },
      ];
    case "Approved":
      return [
        submitted,
        { key: "reviewing", state: "done", labelKey: "progress.timeline.reviewed" },
        { key: "result", state: "done", labelKey: "progress.timeline.approved", time: reviewedAt },
      ];
    case "Rejected":
      return [
        submitted,
        { key: "reviewing", state: "done", labelKey: "progress.timeline.reviewed" },
        { key: "result", state: "failed", labelKey: "progress.timeline.rejected", time: reviewedAt },
      ];
    case "Withdrawn":
      // 撤回发生在审核之前，审核步骤视为中止而不是已完成。
      return [
        submitted,
        { key: "reviewing", state: "stopped", labelKey: "progress.timeline.notReviewed" },
        { key: "result", state: "stopped", labelKey: "progress.timeline.withdrawn", time: reviewedAt },
      ];
    default:
      return [
        submitted,
        { key: "reviewing", state: "stopped", labelKey: "progress.timeline.notReviewed" },
        { key: "result", state: "stopped", labelKey: "progress.timeline.superseded", time: reviewedAt },
      ];
  }
}

export function canWithdrawSensitiveRequest(request: ProgressRequest | null | undefined) {
  return request?.status === "Pending" && request.requestId > 0;
}

/** 驳回、撤回、失效后都可以重新填报；待审中也可以直接修改，提交后覆盖旧申请。 */
export function canResubmitSensitiveRequest(request: ProgressRequest | null | undefined) {
  return Boolean(request) && request?.status !== "Approved";
}

const FIELD_SECTIONS: Record<string, SensitiveProfileSection> = {
  birthday: "personal",
  bankBsb: "banking",
  bankAccountNumber: "banking",
  superannuationCompanyName: "superannuation",
  superannuationCompanyCode: "superannuation",
  superannuationAccountNumber: "superannuation",
  identityType: "identity",
  identityId: "identity",
  identityPhotoUrl: "identity",
};

/** 「修改后重提」时把编辑页定位到本次变更的第一个分组。 */
export function getResubmitSection(changedFields: readonly string[]): SensitiveProfileSection {
  for (const field of changedFields) {
    const section = FIELD_SECTIONS[field];
    if (section) return section;
  }
  return "personal";
}

/** 历史列表排除状态卡片已展示的那一条，避免重复。 */
export function getEarlierSensitiveRequests<T extends { requestId: number }>(
  history: readonly T[],
  currentRequestId: number | undefined
) {
  return history.filter((item) => item.requestId !== currentRequestId);
}
