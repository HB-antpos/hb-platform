/**
 * 店长版「新增员工」整页表单的纯逻辑：请求体拼装与校验。
 *
 * 业务规则（保持不变）：账号固定为本店店员（StoreStaff）、员工类型 casual、与本店关系「仅查看」；
 * 本页绝不提交任何密码——允许登录时由系统生成初始密码发到员工邮箱，店长全程不经手密码。
 * 新增（与后端 NewStaffProfileRules 一致）：电话、生日必填；未满 18 岁时监护人四项必填，规则见 new-staff-profile.ts。
 */
import { isPersonNameEmpty, buildCreateNameBody } from "./person-form";
import {
  buildNewStaffProfileBody,
  EMPTY_NEW_STAFF_GUARDIAN,
  hasNewStaffProfileErrors,
  isNewStaffProfileDirty,
  sydneyToday,
  validateNewStaffProfile,
  type NewStaffGuardianInput,
  type NewStaffProfileErrors,
} from "./new-staff-profile";
import { STORE_STAFF_ROLE, type StoreUserCreatePayload } from "@/modules/users/types";
import {
  hasPersonNameErrors,
  validateNewStaffEmail,
  validatePersonNameFields,
  validateUsername,
  type PersonNameErrors,
  type PersonNameInput,
} from "@/modules/users/validation";

export interface StaffCreateFormValues {
  email: string;
  username: string;
  names: PersonNameInput;
  /** 必填；澳洲手机号（04 开头）。 */
  phone: string;
  /** 必填；YYYY-MM-DD，未选为空串。 */
  birthday: string;
  /** 只在生日未满 18 岁时显示与提交；改回成年后保留已填内容但不提交。 */
  guardian: NewStaffGuardianInput;
  /** 立即启用：关闭则创建为停用账号。 */
  enabled: boolean;
  /** 允许登录 App 和 Web：开启才发初始密码邮件。 */
  loginEnabled: boolean;
}

export const EMPTY_STAFF_CREATE_FORM: StaffCreateFormValues = {
  email: "",
  username: "",
  names: { familyName: "", givenName: "", middleName: "", nickname: "" },
  phone: "",
  birthday: "",
  guardian: EMPTY_NEW_STAFF_GUARDIAN,
  enabled: true,
  loginEnabled: false,
};

/** 是否已经填过内容（决定返回时要不要弹「放弃修改？」）。 */
export function isStaffCreateFormDirty(values: StaffCreateFormValues) {
  return (
    values.email.trim() !== ""
    || values.username.trim() !== ""
    || isNewStaffProfileDirty(values)
    || !isPersonNameEmpty(values.names)
    || values.enabled !== EMPTY_STAFF_CREATE_FORM.enabled
    || values.loginEnabled !== EMPTY_STAFF_CREATE_FORM.loginEnabled
  );
}

/**
 * 创建请求体。姓 / 名 / 中间名 / 昵称有值的才带，`fullName` 始终是合成值（旧后端只认它）。
 * 关键逻辑：不带任何密码；允许登录时 `sendPasswordSetupEmail: true`，由后端生成初始密码发到员工邮箱。
 * 电话、生日始终带；监护人只在未成年时带（按悉尼当天算年龄）。
 */
export function buildStaffCreatePayload(
  values: StaffCreateFormValues,
  storeCode: string,
  today: Date = sydneyToday()
): StoreUserCreatePayload {
  return {
    username: values.username.trim(),
    ...buildCreateNameBody(values.names),
    email: values.email.trim(),
    ...buildNewStaffProfileBody(values, today),
    status: values.enabled ? 1 : 0,
    storeCode,
    roleNames: [STORE_STAFF_ROLE],
    passwordFormat: "raw",
    employmentType: "casual",
    loginEnabled: values.loginEnabled,
    ...(values.loginEnabled ? { sendPasswordSetupEmail: true } : {}),
  };
}

export interface StaffCreateErrors extends NewStaffProfileErrors {
  email?: string;
  username?: string;
  names: PersonNameErrors;
}

export function hasStaffCreateErrors(errors: StaffCreateErrors) {
  return Boolean(errors.email || errors.username) || hasPersonNameErrors(errors.names) || hasNewStaffProfileErrors(errors);
}

/**
 * 逐字段校验；`t` 只负责把 `messages.*` 键翻成文案（邮箱 / 账号）。
 * 姓名、电话、生日、监护人返回错误码，由界面按字段翻译。
 */
export function validateStaffCreateForm(
  values: StaffCreateFormValues,
  t: (key: string) => string,
  today: Date = sydneyToday()
): StaffCreateErrors {
  return {
    email: validateNewStaffEmail(values.email, t) ?? undefined,
    username: validateUsername(values.username, t) ?? undefined,
    names: validatePersonNameFields(values.names, { requireNames: true }),
    // 电话必填 + 澳洲手机号（validateNewStaffPhone）、生日、未成年监护人，规则见 new-staff-profile.ts。
    ...validateNewStaffProfile(values, values.email, today),
  };
}
