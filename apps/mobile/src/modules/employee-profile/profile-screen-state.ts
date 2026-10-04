import type {
  EmployeeProfile,
  SensitiveEmployeeProfilePayload,
  UpdateEmployeeProfilePayload,
} from "./types";
import {
  buildNonSensitiveProfilePayload,
  isSensitiveVersionConflict,
  normalizeSensitiveDraft,
} from "./sensitive-profile";
import { toEmployeeProfileDraft } from "./profile-draft";

// progress 为审核进度页（只读视图，返回时直接回到概览）；email 为绑定 / 更换邮箱（验证码流程，不进未保存草稿）。
export type EmployeeProfileView = "overview" | "basic" | "sensitive" | "progress" | "email";
export type SensitiveProfileSection = "personal" | "banking" | "superannuation" | "identity";

function areValuesEqual(
  left: object,
  right: object
) {
  const leftValues = left as Record<string, unknown>;
  const rightValues = right as Record<string, unknown>;
  const keys = new Set([...Object.keys(leftValues), ...Object.keys(rightValues)]);
  return [...keys].every((key) => leftValues[key] === rightValues[key]);
}

export function hasBasicProfileChanges(
  draft: UpdateEmployeeProfilePayload,
  profile: EmployeeProfile | null | undefined,
  options: { canEditPositionType?: boolean } = {}
) {
  if (!profile) return false;
  return !areValuesEqual(
    buildNonSensitiveProfilePayload(draft, options),
    buildNonSensitiveProfilePayload(toEmployeeProfileDraft(profile), options)
  );
}

export function hasSensitiveProfileChanges(
  draft: SensitiveEmployeeProfilePayload,
  initialDraft: SensitiveEmployeeProfilePayload
) {
  return !areValuesEqual(
    normalizeSensitiveDraft(draft),
    normalizeSensitiveDraft(initialDraft)
  );
}

export function buildSensitiveReviewPayload(
  draft: SensitiveEmployeeProfilePayload,
  expectedSensitiveRevision: number | undefined
) {
  // revision 必须取自进入编辑页时的正式资料，后台刷新不能悄悄改变并发基线。
  return normalizeSensitiveDraft({ ...draft, expectedSensitiveRevision });
}

export function getBackAction(input: {
  view: EmployeeProfileView;
  hasUnsavedChanges: boolean;
}) {
  if (input.view === "overview") return "navigate" as const;
  return input.hasUnsavedChanges ? "confirm-discard" as const : "show-overview" as const;
}

export function getSensitiveSectionOrder(selected: SensitiveProfileSection) {
  const sections: SensitiveProfileSection[] = ["personal", "banking", "superannuation", "identity"];
  return [selected, ...sections.filter((section) => section !== selected)];
}

export function getSensitiveSubmitFailureAction(error: unknown) {
  // revision 冲突代表当前并发基线已失效，不能保留编辑页让用户直接重试旧 revision。
  return isSensitiveVersionConflict(error) ? "discard-stale-edit" as const : "keep-draft" as const;
}

export function shouldUnlockSensitiveConflict(profileRefresh: { isError: boolean }) {
  return !profileRefresh.isError;
}

export function shouldApplyEmployeeProfileOperation(input: {
  submittedIdentity: string;
  currentIdentity: string;
  submittedScope: number;
  currentScope: number;
  isAuthenticated: boolean;
}) {
  return input.isAuthenticated
    && Boolean(input.submittedIdentity)
    && input.submittedIdentity === input.currentIdentity
    && input.submittedScope === input.currentScope;
}
