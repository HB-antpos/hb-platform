import type { EmployeeProfile } from "./types";
import { normalizeBirthday } from "./birthday";

// 资料完整度只看已确认的正式资料：待审申请未批准前不算已填写，避免员工误以为资料已生效。
export const PROFILE_COMPLETENESS_ITEMS = [
  "phone",
  "email",
  "gender",
  "address",
  "birthday",
  "banking",
  "superannuation",
  "identity",
  "identityPhoto",
] as const;

export type ProfileCompletenessItem = (typeof PROFILE_COMPLETENESS_ITEMS)[number];

/** 每一项对应的 employeeProfile 命名空间文案键，复用字段/分组标题。 */
export const PROFILE_COMPLETENESS_LABEL_KEYS: Record<ProfileCompletenessItem, string> = {
  phone: "fields.phone",
  email: "fields.email",
  gender: "fields.gender",
  address: "fields.address",
  birthday: "fields.birthday",
  banking: "sections.banking",
  superannuation: "sections.superannuation",
  identity: "fields.identityId",
  identityPhoto: "fields.identityPhotoUrl",
};

type CompletenessSource = Pick<
  EmployeeProfile,
  | "phone"
  | "email"
  | "gender"
  | "address"
  | "birthday"
  | "bankBsb"
  | "bankAccountNumber"
  | "superannuationCompanyName"
  | "superannuationAccountNumber"
  | "identityId"
  | "identityPhotoUrl"
>;

function hasValue(value: string | null | undefined) {
  return Boolean(value?.trim());
}

export function getProfileCompleteness(profile: Partial<CompletenessSource> | null | undefined) {
  const checks: Record<ProfileCompletenessItem, boolean> = {
    phone: hasValue(profile?.phone),
    email: hasValue(profile?.email),
    gender: hasValue(profile?.gender),
    address: hasValue(profile?.address),
    birthday: Boolean(normalizeBirthday(profile?.birthday)),
    // 银行和养老金都要「机构 + 账号」齐全才算完成，只填一半发不了工资。
    banking: hasValue(profile?.bankBsb) && hasValue(profile?.bankAccountNumber),
    superannuation: hasValue(profile?.superannuationCompanyName) && hasValue(profile?.superannuationAccountNumber),
    identity: hasValue(profile?.identityId),
    identityPhoto: hasValue(profile?.identityPhotoUrl),
  };
  const missing = PROFILE_COMPLETENESS_ITEMS.filter((item) => !checks[item]);
  const total = PROFILE_COMPLETENESS_ITEMS.length;
  const filled = total - missing.length;
  return {
    filled,
    total,
    missing,
    percent: Math.round((filled / total) * 100),
    isComplete: missing.length === 0,
  };
}
