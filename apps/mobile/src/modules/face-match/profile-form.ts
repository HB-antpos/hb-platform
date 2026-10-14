// 门店人脸比对档案（手机只编辑一小部分字段）的纯逻辑。
//
// 关键约束：PUT /face-match/profiles/{storeCode} 是「整体替换」——请求体里没有的合规记录字段（PIA、告示日期、律师确认、
// 跨境审查、培训日期、隐私联系人……）会被清空。所以请求体必须以服务端刚返回的档案为底，只覆盖手机可编辑的 7 个字段，
// 其余 17 个字段原样带回（包括 null），绝不预填、不改写、不清空。
import {
  ALLOWED_SUBJECTS_MAX_LENGTH,
  PROFILE_MODE_CAPTURE,
  PROFILE_MODE_MATCH,
  PROFILE_MODE_OFF,
  PROFILE_TIME_ZONES,
} from "./constants";
import { normalizeText, type ValidationIssue } from "./entry-form";
import type { FaceGateReport, FaceStoreProfile, FaceStoreProfileUpdateRequest } from "./types";

/** 手机上可编辑的字段（运行模式 + 四个开关 + 允许拍摄对象 + 时区）。 */
export interface ProfileEditValues {
  mode: number;
  evaluateMatch: boolean;
  staffExclusionEnabled: boolean;
  trafficDedupEnabled: boolean;
  unwelcomeListEnabled: boolean;
  /** 空串 = 未填写。 */
  allowedSubjects: string;
  /** 空串 = 自动判断。 */
  timeZoneId: string;
}

/** 手机可编辑的字段名；其余请求体字段一律原样带回。 */
export const PROFILE_EDITABLE_FIELDS = [
  "mode",
  "evaluateMatch",
  "staffExclusionEnabled",
  "trafficDedupEnabled",
  "unwelcomeListEnabled",
  "allowedSubjects",
  "timeZoneId",
] as const satisfies readonly (keyof FaceStoreProfileUpdateRequest)[];

export const PROFILE_MODE_OPTIONS = [
  { value: PROFILE_MODE_OFF, labelKey: "settings.profile.mode.off", hintKey: "settings.profile.mode.offHint" },
  { value: PROFILE_MODE_CAPTURE, labelKey: "settings.profile.mode.capture", hintKey: "settings.profile.mode.captureHint" },
  { value: PROFILE_MODE_MATCH, labelKey: "settings.profile.mode.match", hintKey: "settings.profile.mode.matchHint" },
] as const;

/** 时区选项：空 = 自动判断；其余只有后端 StoreTimeZonePolicy 允许的三个澳洲时区。 */
export const PROFILE_TIME_ZONE_OPTIONS: readonly { value: string; labelKey: string }[] = [
  { value: "", labelKey: "settings.profile.timeZone.auto" },
  ...PROFILE_TIME_ZONES.map((zone) => ({ value: zone, labelKey: `settings.profile.timeZone.${zone}` })),
];

export function profileToEditValues(profile: FaceStoreProfile): ProfileEditValues {
  return {
    mode: profile.mode ?? PROFILE_MODE_OFF,
    evaluateMatch: profile.evaluateMatch,
    staffExclusionEnabled: profile.staffExclusionEnabled,
    trafficDedupEnabled: profile.trafficDedupEnabled,
    unwelcomeListEnabled: profile.unwelcomeListEnabled,
    allowedSubjects: profile.allowedSubjects ?? "",
    timeZoneId: profile.timeZoneId ?? "",
  };
}

/** 请求体里的全部字段名（24 个），用于测试「没有漏带、没有多带」。 */
export const PROFILE_REQUEST_KEYS = [
  "mode",
  "evaluateMatch",
  "staffExclusionEnabled",
  "trafficDedupEnabled",
  "unwelcomeListEnabled",
  "unwelcomeAutoConfirmScore",
  "allowedSubjects",
  "piaReference",
  "signageInstalledOn",
  "staffNoticeDate",
  "legalSignoffBy",
  "legalSignoffAt",
  "dedupLegalSignoffBy",
  "dedupLegalSignoffAt",
  "crossBorderRegion",
  "crossBorderReviewRef",
  "crossBorderReviewedBy",
  "crossBorderReviewedAt",
  "timeZoneId",
  "unwelcomeAssessmentRef",
  "unwelcomeLegalSignoffBy",
  "unwelcomeLegalSignoffAt",
  "staffTrainingDate",
  "privacyContact",
] as const satisfies readonly (keyof FaceStoreProfileUpdateRequest)[];

/**
 * 构造 PUT 请求体：以服务端返回的档案为底，只覆盖手机可编辑的字段。
 * 逐个字段显式列出（而不是展开整个 profile 对象），保证不会把 storeCode、gates 等只读字段带进请求体，
 * 也保证后端以后给档案加了新的只读字段不会意外混入。
 */
export function buildProfileUpdateRequest(
  profile: FaceStoreProfile,
  edits: ProfileEditValues,
): FaceStoreProfileUpdateRequest {
  return {
    // —— 手机可编辑 ——
    mode: edits.mode,
    evaluateMatch: edits.evaluateMatch,
    staffExclusionEnabled: edits.staffExclusionEnabled,
    trafficDedupEnabled: edits.trafficDedupEnabled,
    unwelcomeListEnabled: edits.unwelcomeListEnabled,
    allowedSubjects: normalizeText(edits.allowedSubjects),
    timeZoneId: normalizeText(edits.timeZoneId),
    // —— 手机不编辑：原样带回服务端返回的值 ——
    unwelcomeAutoConfirmScore: profile.unwelcomeAutoConfirmScore,
    piaReference: profile.piaReference,
    signageInstalledOn: profile.signageInstalledOn,
    staffNoticeDate: profile.staffNoticeDate,
    legalSignoffBy: profile.legalSignoffBy,
    legalSignoffAt: profile.legalSignoffAt,
    dedupLegalSignoffBy: profile.dedupLegalSignoffBy,
    dedupLegalSignoffAt: profile.dedupLegalSignoffAt,
    crossBorderRegion: profile.crossBorderRegion,
    crossBorderReviewRef: profile.crossBorderReviewRef,
    crossBorderReviewedBy: profile.crossBorderReviewedBy,
    crossBorderReviewedAt: profile.crossBorderReviewedAt,
    unwelcomeAssessmentRef: profile.unwelcomeAssessmentRef,
    unwelcomeLegalSignoffBy: profile.unwelcomeLegalSignoffBy,
    unwelcomeLegalSignoffAt: profile.unwelcomeLegalSignoffAt,
    staffTrainingDate: profile.staffTrainingDate,
    privacyContact: profile.privacyContact,
  };
}

/** 是否有未保存的修改（与服务端当前值比较；允许拍摄对象按去空白后比较）。 */
export function isProfileDirty(profile: FaceStoreProfile, edits: ProfileEditValues): boolean {
  const original = profileToEditValues(profile);
  return (
    original.mode !== edits.mode ||
    original.evaluateMatch !== edits.evaluateMatch ||
    original.staffExclusionEnabled !== edits.staffExclusionEnabled ||
    original.trafficDedupEnabled !== edits.trafficDedupEnabled ||
    original.unwelcomeListEnabled !== edits.unwelcomeListEnabled ||
    (normalizeText(original.allowedSubjects) ?? "") !== (normalizeText(edits.allowedSubjects) ?? "") ||
    original.timeZoneId !== edits.timeZoneId
  );
}

export type ProfileEditErrors = Partial<Record<"allowedSubjects", ValidationIssue>>;

/**
 * 本地即时校验：只拦「允许拍摄对象最长 200」（与后端 400 字段校验一致）。
 * Capture 不填允许拍摄对象、评估比对非 Capture、开关在关闭模式下、员工排除没有有效登记，后端 2026-10-13 起一律只给警告
 * （「有问题只给提示」），不再拦截保存，所以这里也不拦；这些提示由保存响应 / 检查接口的闸门报告展示。
 */
export function validateProfileEdits(edits: ProfileEditValues): ProfileEditErrors {
  const errors: ProfileEditErrors = {};
  const subjects = edits.allowedSubjects.trim();
  if (subjects.length > ALLOWED_SUBJECTS_MAX_LENGTH) {
    errors.allowedSubjects = { key: "validation.textTooLong", params: { max: ALLOWED_SUBJECTS_MAX_LENGTH } };
  }
  return errors;
}

// ───────────── 闸门报告 ─────────────

/** Missing / Warnings 键 → 文案键（faceMatch 命名空间）。未登记的键显示键名本身，不隐藏缺项。 */
export const GATE_KEY_LABEL_KEYS: Record<string, string> = {
  mode: "settings.gate.keys.mode",
  allowedSubjects: "settings.gate.keys.allowedSubjects",
  piaReference: "settings.gate.keys.piaReference",
  signageInstalledOn: "settings.gate.keys.signageInstalledOn",
  staffNoticeDate: "settings.gate.keys.staffNoticeDate",
  legalSignoffBy: "settings.gate.keys.legalSignoffBy",
  legalSignoffAt: "settings.gate.keys.legalSignoffAt",
  dedupLegalSignoffBy: "settings.gate.keys.dedupLegalSignoffBy",
  dedupLegalSignoffAt: "settings.gate.keys.dedupLegalSignoffAt",
  crossBorderRegion: "settings.gate.keys.crossBorderRegion",
  crossBorderReviewRef: "settings.gate.keys.crossBorderReviewRef",
  crossBorderReviewedBy: "settings.gate.keys.crossBorderReviewedBy",
  crossBorderReviewedAt: "settings.gate.keys.crossBorderReviewedAt",
  timeZoneId: "settings.gate.keys.timeZoneId",
  unwelcomeAssessmentRef: "settings.gate.keys.unwelcomeAssessmentRef",
  unwelcomeLegalSignoffBy: "settings.gate.keys.unwelcomeLegalSignoffBy",
  unwelcomeLegalSignoffAt: "settings.gate.keys.unwelcomeLegalSignoffAt",
  staffTrainingDate: "settings.gate.keys.staffTrainingDate",
  privacyContact: "settings.gate.keys.privacyContact",
  activeEnrollment: "settings.gate.keys.activeEnrollment",
  regionMismatch: "settings.gate.keys.regionMismatch",
  staffNoticeTooRecent: "settings.gate.keys.staffNoticeTooRecent",
  modeOff: "settings.gate.keys.modeOff",
  evaluateMatchRequiresCapture: "settings.gate.keys.evaluateMatchRequiresCapture",
};

export function gateKeyLabelKey(key: string): string | null {
  return GATE_KEY_LABEL_KEYS[key] ?? null;
}

export const GATE_FACET_LABEL_KEYS: Record<string, string> = {
  capture: "settings.gate.facets.capture",
  match: "settings.gate.facets.match",
  staffExclusion: "settings.gate.facets.staffExclusion",
  trafficDedup: "settings.gate.facets.trafficDedup",
  unwelcome: "settings.gate.facets.unwelcome",
};

export interface GateRow {
  facet: string;
  facetLabelKey: string | null;
  /** 仍然阻塞保存的缺项。 */
  missing: string[];
  /** 不阻塞保存、但必须持续可见的合规记录缺口。 */
  warnings: string[];
}

/** 只列出「被请求启用」的闸门项；Requested=false 的项不展示。 */
export function summarizeGates(report: FaceGateReport | null | undefined): GateRow[] {
  if (!report) return [];
  return report.facets
    .filter((facet) => facet.requested)
    .map((facet) => ({
      facet: facet.facet,
      facetLabelKey: GATE_FACET_LABEL_KEYS[facet.facet] ?? null,
      missing: facet.missing,
      warnings: facet.warnings,
    }));
}

/** 仍然阻塞保存的缺项条数（不含警告）。 */
export function countMissing(rows: readonly GateRow[]): number {
  return rows.reduce((sum, row) => sum + row.missing.length, 0);
}

/** 合规记录警告条数（各 facet 之和，与服务端 gateWarningCount 同口径）。 */
export function countWarnings(rows: readonly GateRow[]): number {
  return rows.reduce((sum, row) => sum + row.warnings.length, 0);
}

/** 本次保存相对服务端当前档案的变化摘要（用于保存前的确认框）。 */
export interface ProfileChangeSummary {
  modeChanged: boolean;
  fromMode: number;
  toMode: number;
  switchesChanged: ("evaluateMatch" | "staffExclusionEnabled" | "trafficDedupEnabled" | "unwelcomeListEnabled")[];
  /** 切到 Match 或 Capture：会开始处理该店摄像机的真实抓拍，确认框要强调。 */
  startsProcessing: boolean;
}

export function summarizeProfileChange(profile: FaceStoreProfile, edits: ProfileEditValues): ProfileChangeSummary {
  const original = profileToEditValues(profile);
  const switches = ["evaluateMatch", "staffExclusionEnabled", "trafficDedupEnabled", "unwelcomeListEnabled"] as const;
  return {
    modeChanged: original.mode !== edits.mode,
    fromMode: original.mode,
    toMode: edits.mode,
    switchesChanged: switches.filter((key) => original[key] !== edits[key]),
    startsProcessing:
      original.mode !== edits.mode && (edits.mode === PROFILE_MODE_CAPTURE || edits.mode === PROFILE_MODE_MATCH),
  };
}
