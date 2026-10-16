/**
 * 管理员版「新增账号」整页表单的纯逻辑：校验、请求体拼装、两步创建流程。
 *
 * 初始密码：现有管理员创建接口（POST /Users）要求明文密码，**不支持「创建时不给密码并发设置密码邮件」**，
 * 所以本表单只提供「直接设置」（密码 + 确认）；邮件方式需要后端先支持。
 *
 * 创建分两步：第一步建号并带上角色与分店（普通关联）；第二步（仅当有「管理」分店）再调关联分店接口写入管理标记，
 * 店长角色由后端按「管理」分店派生。第一步成功后账号已经存在，无法回滚，因此第二步失败时：
 * 不重复创建，只重试第二步，并明确告知用户是哪一步失败。
 */
import { buildCreateNameBody, isPersonNameEmpty } from "./person-form";
import type { UserAccessStoreAssignment } from "@/modules/users/access-management-types";
import {
  hasPersonNameErrors,
  validateNewStaffEmail,
  validatePersonNameFields,
  validateUsername,
  type PersonNameErrors,
  type PersonNameInput,
} from "@/modules/users/validation";
import type { IdentityCreateUserInput, IdentityUserDetail } from "@/modules/identity-admin/types";

export const ADMIN_PASSWORD_MIN_LENGTH = 6;
export const ADMIN_PASSWORD_MAX_LENGTH = 100;

export interface AdminCreateFormValues {
  email: string;
  username: string;
  names: PersonNameInput;
  /** 立即启用：关闭则创建为停用账号。 */
  enabled: boolean;
  password: string;
  confirmPassword: string;
  roleGuids: string[];
  /** 分店关联：isPrimary=true 即「管理」，false 即「查看」。 */
  assignments: UserAccessStoreAssignment[];
}

export const EMPTY_ADMIN_CREATE_FORM: AdminCreateFormValues = {
  email: "",
  username: "",
  names: { familyName: "", givenName: "", middleName: "", nickname: "" },
  enabled: true,
  password: "",
  confirmPassword: "",
  roleGuids: [],
  assignments: [],
};

/** 管理员版默认账号 = 邮箱 @ 前的部分（小写）；超过 50 个字符则留空让用户自己填。 */
export function defaultUsernameFromEmail(email: string) {
  const local = email.trim().toLowerCase().split("@")[0] ?? "";
  return local.length <= 50 ? local : "";
}

export function isAdminCreateFormDirty(values: AdminCreateFormValues) {
  return (
    values.email.trim() !== ""
    || values.username.trim() !== ""
    || !isPersonNameEmpty(values.names)
    || values.password !== ""
    || values.confirmPassword !== ""
    || values.roleGuids.length > 0
    || values.assignments.length > 0
    || values.enabled !== EMPTY_ADMIN_CREATE_FORM.enabled
  );
}

export type AdminPasswordError = "required" | "tooShort" | "tooLong";

export interface AdminCreateErrors {
  email?: string;
  username?: string;
  password?: AdminPasswordError;
  confirmPassword?: "mismatch";
  names: PersonNameErrors;
}

export function hasAdminCreateErrors(errors: AdminCreateErrors) {
  return Boolean(errors.email || errors.username || errors.password || errors.confirmPassword)
    || hasPersonNameErrors(errors.names);
}

/** 逐字段校验；邮箱 / 账号的文案通过 `t`（userManagement 的 messages.* 键）翻译，密码与姓名返回错误码由界面翻译。 */
export function validateAdminCreateForm(values: AdminCreateFormValues, t: (key: string) => string): AdminCreateErrors {
  const errors: AdminCreateErrors = {
    email: validateNewStaffEmail(values.email, t) ?? undefined,
    username: validateUsername(values.username, t) ?? undefined,
    names: validatePersonNameFields(values.names, { requireNames: true }),
  };
  // 密码按原样校验（不去首尾空白），与提交给后端的值一致。
  if (!values.password) errors.password = "required";
  else if (values.password.length < ADMIN_PASSWORD_MIN_LENGTH) errors.password = "tooShort";
  else if (values.password.length > ADMIN_PASSWORD_MAX_LENGTH) errors.password = "tooLong";
  if (!errors.password && values.password !== values.confirmPassword) errors.confirmPassword = "mismatch";
  return errors;
}

/** 第一步（建号）的请求体：带角色与全部已选分店（普通关联），姓名段有值才带，fullName 为合成值。 */
export function buildAdminCreateInput(values: AdminCreateFormValues): IdentityCreateUserInput {
  return {
    username: values.username.trim(),
    email: values.email.trim(),
    password: values.password,
    passwordFormat: "raw",
    ...buildCreateNameBody(values.names),
    isActive: values.enabled,
    roleGuids: values.roleGuids,
    storeGuids: values.assignments.map((assignment) => assignment.storeGUID),
  };
}

/** 是否需要第二步：只有存在「管理」分店才要补写管理标记（并由后端派生店长角色）。 */
export function needsManageAssignment(assignments: UserAccessStoreAssignment[]) {
  return assignments.some((assignment) => assignment.isPrimary);
}

export function summarizeAssignments(assignments: UserAccessStoreAssignment[]) {
  const manageCount = assignments.filter((assignment) => assignment.isPrimary).length;
  return { total: assignments.length, manageCount };
}

// ───────────────────────── 两步创建流程 ─────────────────────────

export interface AdminCreateDeps {
  create(input: IdentityCreateUserInput): Promise<IdentityUserDetail>;
  assignStores(input: { userGuid: string; assignments: UserAccessStoreAssignment[] }): Promise<unknown>;
  /** 判断一次写请求是否「结果未知」（超时 / 5xx / 断网）。 */
  isUncertain(error: unknown): boolean;
}

export type AdminCreateOutcome =
  | { kind: "ok"; user: IdentityUserDetail }
  /** 建号请求被明确拒绝（校验 / 重名 / 无权限等）：账号肯定没建，可直接改完重试。 */
  | { kind: "createFailed"; error: unknown }
  /** 建号请求结果未知：可能已建成，必须冻结表单并让用户先核对，禁止直接重发（会重复创建）。 */
  | { kind: "createUncertain"; error: unknown }
  /** 账号已建成，但「管理」分店没写入：停在表单里提示是第二步失败，只重试第二步。 */
  | { kind: "storesFailed"; user: IdentityUserDetail; error: unknown };

export async function runAdminCreate(
  deps: AdminCreateDeps,
  input: IdentityCreateUserInput,
  assignments: UserAccessStoreAssignment[]
): Promise<AdminCreateOutcome> {
  let user: IdentityUserDetail;
  try {
    user = await deps.create(input);
  } catch (error) {
    return deps.isUncertain(error) ? { kind: "createUncertain", error } : { kind: "createFailed", error };
  }

  if (!needsManageAssignment(assignments)) return { kind: "ok", user };
  try {
    await deps.assignStores({ userGuid: user.userGUID, assignments });
    return { kind: "ok", user };
  } catch (error) {
    return { kind: "storesFailed", user, error };
  }
}

/** 重试第二步：只调关联分店接口（整体覆盖语义，重复调用安全），绝不再次创建账号。 */
export async function retryAdminStoreAssignment(
  deps: Pick<AdminCreateDeps, "assignStores">,
  user: IdentityUserDetail,
  assignments: UserAccessStoreAssignment[]
): Promise<{ kind: "ok" } | { kind: "storesFailed"; error: unknown }> {
  try {
    await deps.assignStores({ userGuid: user.userGUID, assignments });
    return { kind: "ok" };
  } catch (error) {
    return { kind: "storesFailed", error };
  }
}

/** 创建完成页「归属」一行用的摘要数据。 */
export function summarizeAdminAssignment(input: {
  roleNames: string[];
  assignments: UserAccessStoreAssignment[];
  storeNameByGuid: Record<string, string>;
}) {
  const stores = input.assignments.map((assignment) => ({
    name: input.storeNameByGuid[assignment.storeGUID] ?? "",
    manage: assignment.isPrimary,
  })).filter((store) => store.name);
  return { roleNames: input.roleNames, stores };
}
