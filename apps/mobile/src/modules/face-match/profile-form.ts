// 门店人脸比对档案（手机只编辑一小部分字段）的纯逻辑。
//
// 关键约束：PUT /face-match/profiles/{storeCode} 是「整体替换」——请求体里没有的合规记录字段（PIA、告示日期、律师确认、
// 跨境审查、培训日期、隐私联系人……）会被清空。所以请求体必须以服务端刚返回的档案为底，只覆盖手机可编辑的 8 个字段，
// 其余 16 个字段原样带回（包括 null），绝不预填、不改写、不清空。
import {
  ALLOWED_SUBJECTS_MAX_LENGTH,
  AUTO_CONFIRM_SCORE_MAX,
  AUTO_CONFIRM_SCORE_MIN,
  AUTO_CONFIRM_SCORE_OFF,
  PROFILE_MODE_CAPTURE,
  PROFILE_MODE_MATCH,
  PROFILE_MODE_OFF,
  PROFILE_TIME_ZONES,
} from "./constants";
import { normalizeText, type ValidationIssue } from "./entry-form";
import type { FaceGateReport, FaceStoreProfile, FaceStoreProfileUpdateRequest } from "./types";

/** 手机上可编辑的字段（运行模式 + 四个开关 + 命中自动确认阈值 + 允许拍摄对象 + 时区）。 */
export interface ProfileEditValues {
  mode: number;
  evaluateMatch: boolean;
  staffExclusionEnabled: boolean;
  trafficDedupEnabled: boolean;
  unwelcomeListEnabled: boolean;
  /** 输入框里的文本（避免数字输入框吞掉中间状态）：「0」= 不自动确认，否则 50–100、最多两位小数；空串 = 未填写（校验不通过）。 */
  unwelcomeAutoConfirmScore: string;
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
  "unwelcomeAutoConfirmScore",
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

// ───────────── 命中自动确认阈值 ─────────────

/** 数字 → 输入框文本：90 → "90"，89.5 → "89.5"。 */
export function formatAutoConfirmScore(value: number): string {
  return String(value);
}

/**
 * 输入框文本 → 数字；不合法返回 null。只接受非负的十进制数、最多两位小数（逗号小数点按点处理，部分键盘会给逗号），
 * 不接受空串、负号、科学计数法、多个小数点。范围在 validateAutoConfirmScore 里另判。
 */
export function parseAutoConfirmScore(text: string): number | null {
  const normalized = text.trim().replace(",", ".");
  if (!/^\d{1,3}(\.\d{1,2})?$/.test(normalized)) return null;
  const value = Number(normalized);
  return Number.isFinite(value) ? value : null;
}

/** 校验：0（不自动确认）或 50–100 之间、最多 2 位小数；与后端 / Web 同一口径。 */
export function validateAutoConfirmScore(text: string): ValidationIssue | null {
  if (text.trim() === "") return { key: "validation.autoConfirmScoreRequired" };
  const value = parseAutoConfirmScore(text);
  if (value === null) return { key: "validation.autoConfirmScoreInvalid", params: { min: AUTO_CONFIRM_SCORE_MIN, max: AUTO_CONFIRM_SCORE_MAX } };
  if (value === AUTO_CONFIRM_SCORE_OFF) return null;
  if (value < AUTO_CONFIRM_SCORE_MIN || value > AUTO_CONFIRM_SCORE_MAX) {
    return { key: "validation.autoConfirmScoreInvalid", params: { min: AUTO_CONFIRM_SCORE_MIN, max: AUTO_CONFIRM_SCORE_MAX } };
  }
  return null;
}

export function profileToEditValues(profile: FaceStoreProfile): ProfileEditValues {
  return {
    mode: profile.mode ?? PROFILE_MODE_OFF,
    evaluateMatch: profile.evaluateMatch,
    staffExclusionEnabled: profile.staffExclusionEnabled,
    trafficDedupEnabled: profile.trafficDedupEnabled,
    unwelcomeListEnabled: profile.unwelcomeListEnabled,
    unwelcomeAutoConfirmScore: formatAutoConfirmScore(profile.unwelcomeAutoConfirmScore),
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
    // 保存前已校验；万一文本不合法就发 null（服务端按「保持原值」处理），绝不把非法输入改写成别的数字。
    unwelcomeAutoConfirmScore: parseAutoConfirmScore(edits.unwelcomeAutoConfirmScore),
    allowedSubjects: normalizeText(edits.allowedSubjects),
    timeZoneId: normalizeText(edits.timeZoneId),
    // —— 手机不编辑：原样带回服务端返回的值 ——
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
  // 阈值按数值比较（"90" 与 "90.0" 不算修改）；文本不合法时只要和原文不同就算修改。
  const nextScore = parseAutoConfirmScore(edits.unwelcomeAutoConfirmScore);
  const scoreChanged =
    nextScore === null
      ? edits.unwelcomeAutoConfirmScore.trim() !== original.unwelcomeAutoConfirmScore
      : nextScore !== profile.unwelcomeAutoConfirmScore;
  return (
    original.mode !== edits.mode ||
    original.evaluateMatch !== edits.evaluateMatch ||
    original.staffExclusionEnabled !== edits.staffExclusionEnabled ||
    original.trafficDedupEnabled !== edits.trafficDedupEnabled ||
    original.unwelcomeListEnabled !== edits.unwelcomeListEnabled ||
    scoreChanged ||
    (normalizeText(original.allowedSubjects) ?? "") !== (normalizeText(edits.allowedSubjects) ?? "") ||
    original.timeZoneId !== edits.timeZoneId
  );
}

export type ProfileEditErrors = Partial<Record<"allowedSubjects" | "unwelcomeAutoConfirmScore", ValidationIssue>>;

/**
 * 本地即时校验：只拦两处与后端 400 字段校验一致的格式问题——「允许拍摄对象最长 200」「命中自动确认阈值必须是 0 或 50–100」。
 * Capture 不填允许拍摄对象、评估比对非 Capture、开关在关闭模式下、员工排除没有有效登记，后端 2026-10-13 起一律只给警告
 * （「有问题只给提示」），不再拦截保存，所以这里也不拦；这些提示由保存响应 / 检查接口的闸门报告展示。
 */
export function validateProfileEdits(edits: ProfileEditValues): ProfileEditErrors {
  const errors: ProfileEditErrors = {};
  const subjects = edits.allowedSubjects.trim();
  if (subjects.length > ALLOWED_SUBJECTS_MAX_LENGTH) {
    errors.allowedSubjects = { key: "validation.textTooLong", params: { max: ALLOWED_SUBJECTS_MAX_LENGTH } };
  }
  const scoreIssue = validateAutoConfirmScore(edits.unwelcomeAutoConfirmScore);
  if (scoreIssue) errors.unwelcomeAutoConfirmScore = scoreIssue;
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
  /** 命中自动确认阈值有改动：它决定哪些命中不再有人复核，确认框要写明「原值 → 新值」。没改（或文本不合法）为 null。 */
  autoConfirmChange: { from: number; to: number } | null;
}

export function summarizeProfileChange(profile: FaceStoreProfile, edits: ProfileEditValues): ProfileChangeSummary {
  const original = profileToEditValues(profile);
  const switches = ["evaluateMatch", "staffExclusionEnabled", "trafficDedupEnabled", "unwelcomeListEnabled"] as const;
  const nextScore = parseAutoConfirmScore(edits.unwelcomeAutoConfirmScore);
  return {
    modeChanged: original.mode !== edits.mode,
    fromMode: original.mode,
    toMode: edits.mode,
    switchesChanged: switches.filter((key) => original[key] !== edits[key]),
    startsProcessing:
      original.mode !== edits.mode && (edits.mode === PROFILE_MODE_CAPTURE || edits.mode === PROFILE_MODE_MATCH),
    autoConfirmChange:
      nextScore !== null && nextScore !== profile.unwelcomeAutoConfirmScore
        ? { from: profile.unwelcomeAutoConfirmScore, to: nextScore }
        : null,
  };
}
