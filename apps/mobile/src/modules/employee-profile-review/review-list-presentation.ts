import type {
  EmployeeProfileReviewStatus,
  EmployeeProfileReviewSummary,
  EmployeeProfileSensitiveField,
} from "./types";

// 审核列表卡片的展示逻辑（纯函数，无 React 依赖，便于单测）。
// 只处理列表摘要里的非敏感字段：用户名、姓名、分店、变更字段标识、时间、状态。

/** 变更字段归类：9 个字段合并成 4 类，卡片上少几个胶囊。顺序即显示顺序（生日最先，因为它影响未成年判定）。 */
export type ReviewFieldGroup = "birthday" | "bank" | "superannuation" | "identity";

const FIELD_TO_GROUP: Record<EmployeeProfileSensitiveField, ReviewFieldGroup> = {
  birthday: "birthday",
  bankBsb: "bank",
  bankAccountNumber: "bank",
  superannuationCompanyName: "superannuation",
  superannuationCompanyCode: "superannuation",
  superannuationAccountNumber: "superannuation",
  identityType: "identity",
  identityId: "identity",
  identityPhotoUrl: "identity",
};

const GROUP_ORDER: readonly ReviewFieldGroup[] = ["birthday", "bank", "superannuation", "identity"];

export interface ReviewFieldGroupSummary {
  group: ReviewFieldGroup;
  /** 该类里改了几项。生日只有一项，界面不显示数字。 */
  count: number;
}

export function groupChangedFields(
  fields: readonly EmployeeProfileSensitiveField[]
): ReviewFieldGroupSummary[] {
  const counts = new Map<ReviewFieldGroup, number>();
  for (const field of new Set(fields)) {
    const group = FIELD_TO_GROUP[field];
    if (group) {
      counts.set(group, (counts.get(group) ?? 0) + 1);
    }
  }
  return GROUP_ORDER.filter((group) => counts.has(group)).map((group) => ({
    group,
    count: counts.get(group) ?? 0,
  }));
}

/** 改生日可能让员工跨过 / 退回未成年线，需要和未成年用工合规一起留意。 */
export function changesBirthday(fields: readonly EmployeeProfileSensitiveField[]) {
  return fields.includes("birthday");
}

/** 等待时长的展示等级：2 天内灰色，3～7 天橙色，8 天及以上红色。 */
export type ReviewWaitTone = "neutral" | "warning" | "danger";

export interface ReviewWait {
  /** 按本地日历日计算的整天数；今天 = 0，昨天 = 1。 */
  days: number;
  kind: "today" | "yesterday" | "days";
  tone: ReviewWaitTone;
}

const DAY_MS = 24 * 60 * 60 * 1000;

function startOfLocalDay(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
}

/** 提交时间无法解析或晚于现在（时钟偏差）时按「今天」处理，不显示负数天。 */
export function getReviewWait(submittedAt: string, now: Date = new Date()): ReviewWait {
  const submitted = new Date(submittedAt);
  if (Number.isNaN(submitted.getTime())) {
    return { days: 0, kind: "today", tone: "neutral" };
  }
  // 用本地日历日相减再四舍五入，夏令时切换日（23 / 25 小时）也不会差一天。
  const days = Math.max(0, Math.round((startOfLocalDay(now) - startOfLocalDay(submitted)) / DAY_MS));
  const tone: ReviewWaitTone = days >= 8 ? "danger" : days >= 3 ? "warning" : "neutral";
  return {
    days,
    kind: days === 0 ? "today" : days === 1 ? "yesterday" : "days",
    tone,
  };
}

export interface ReviewStoreSummary {
  /** 卡片上直接显示的分店文字：1～2 家全写（用 · 连接），3 家及以上只写第一家。 */
  label: string;
  total: number;
  /** 是否需要「等 N 家 ›」入口（3 家及以上）。 */
  hasMore: boolean;
}

export function summarizeStores(storeNames: readonly string[]): ReviewStoreSummary {
  const names = storeNames.map((name) => name.trim()).filter(Boolean);
  if (names.length <= 2) {
    return { label: names.join(" · "), total: names.length, hasMore: false };
  }
  return { label: names[0], total: names.length, hasMore: true };
}

/** 名字行：有姓名时姓名为主、用户名为辅；没有姓名回退用户名，再回退 userGuid。 */
export function getReviewPersonLabel(
  item: Pick<EmployeeProfileReviewSummary, "displayName" | "username" | "userGuid">
) {
  const username = item.username?.trim() ?? "";
  const displayName = item.displayName?.trim() ?? "";
  if (displayName && displayName.toLowerCase() !== username.toLowerCase()) {
    return { primary: displayName, secondary: username || undefined };
  }
  return { primary: username || item.userGuid, secondary: undefined };
}

/** 头像字母：取主名字的第一个可见字符（中文取第一个字），大写。 */
export function getReviewAvatarInitial(primary: string) {
  const first = Array.from(primary.trim())[0];
  return first ? first.toLocaleUpperCase() : "?";
}

const EN_MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"] as const;

/**
 * 日期时间格式：中文「7月16日 12:04」，英文（澳洲习惯）「16 Jul, 12:04」；withTime=false 时只要日期。
 * language 来自 i18n，非 en 前缀一律按中文格式。
 * 刻意手写拼接而不用 Intl：不同 ICU / Hermes 版本对 en-AU 的月份缩写和组合格式输出不一致。
 */
export function formatReviewDate(value: string, language: string, withTime = true) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  const time = `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
  if (language.toLowerCase().startsWith("en")) {
    const day = `${date.getDate()} ${EN_MONTHS[date.getMonth()]}`;
    return withTime ? `${day}, ${time}` : day;
  }
  const day = `${date.getMonth() + 1}月${date.getDate()}日`;
  return withTime ? `${day} ${time}` : day;
}

/** 已处理分段右上角那句「11月10日处理」用的日期：优先处理时间，缺省退回提交时间。 */
export function getReviewHandledAt(item: Pick<EmployeeProfileReviewSummary, "reviewedAt" | "submittedAt">) {
  return item.reviewedAt || item.submittedAt;
}

/** 已处理状态的色调：批准绿、拒绝红、撤回 / 失效灰。 */
export type ReviewResultTone = "success" | "danger" | "muted";

export function getReviewResultTone(status: EmployeeProfileReviewStatus): ReviewResultTone {
  if (status === "Approved") return "success";
  if (status === "Rejected") return "danger";
  return "muted";
}
