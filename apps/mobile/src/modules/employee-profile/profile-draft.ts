import type { EmployeeProfile, UpdateEmployeeProfilePayload } from "./types";

export function toEmployeeProfileDraft(profile: EmployeeProfile): UpdateEmployeeProfilePayload {
  return {
    phone: profile.phone ?? "",
    email: profile.email ?? "",
    gender: profile.gender,
    employmentType: profile.employmentType ?? "",
    address: profile.address,
  };
}

export function syncEmployeeProfileDraft(
  currentDraft: UpdateEmployeeProfilePayload,
  profile: EmployeeProfile,
  hasInitialized: boolean
) {
  // 图片即时保存会刷新资料缓存，已编辑的银行和地址草稿不能被缓存更新覆盖。
  return hasInitialized ? currentDraft : toEmployeeProfileDraft(profile);
}
