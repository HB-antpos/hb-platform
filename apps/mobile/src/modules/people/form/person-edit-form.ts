/**
 * 「编辑资料」整页表单的纯逻辑：预填、改动计数、校验与两种数据源（店员接口 / 管理员接口）的请求体。
 *
 * 关键点：后端的更新接口都是「整体覆盖」语义（status 缺省按 1、fullName / phone 缺省会被清空），
 * 所以除了姓名四段按「只发有变化的段」处理，其余字段（用户名、状态、全名、电话、邮箱）必须始终带上当前值，
 * 否则保存一次资料就可能把停用账号重新启用或抹掉电话。
 */
import {
  buildPersonNameValues,
  diffNameParts,
  resolveUpdateFullName,
  type PersonNameRecord,
} from "./person-form";
import { getDisplayableEmail } from "@/modules/users/staff-email-username";
import { STORE_STAFF_ROLE, type StoreUserUpdatePayload } from "@/modules/users/types";
import {
  hasPersonNameErrors,
  validatePersonNameFields,
  type PersonNameErrors,
  type PersonNameInput,
} from "@/modules/users/validation";
import type { IdentityUpdateUserInput } from "@/modules/identity-admin/types";

export interface PersonEditValues {
  names: PersonNameInput;
  phone: string;
  email: string;
  /** 允许登录 App 和 Web（仅店员接口数据源有；管理员接口不支持）。 */
  loginEnabled: boolean;
}

export interface PersonEditSource extends PersonNameRecord {
  email?: string | null;
  phone?: string | null;
  /** 后端缺省（旧后端）视为允许。 */
  loginEnabled?: boolean;
}

export interface PersonEditBaseline {
  values: PersonEditValues;
  /** 姓名是由全名自动拆分的（存量账号），界面要给「请核对姓和名」的小提示。 */
  legacySplit: boolean;
}

/** 把详情变成表单预填值；占位邮箱（xxx@分店.store.local）一律当作未设置，不展示给用户。 */
export function buildPersonEditBaseline(source: PersonEditSource): PersonEditBaseline {
  const { values: names, legacySplit } = buildPersonNameValues(source);
  return {
    values: {
      names,
      phone: source.phone?.trim() ?? "",
      email: getDisplayableEmail(source.email),
      loginEnabled: source.loginEnabled !== false,
    },
    legacySplit,
  };
}

export type PersonEditChangeKey =
  | "familyName"
  | "givenName"
  | "middleName"
  | "nickname"
  | "phone"
  | "email"
  | "loginEnabled";

/**
 * 与基线相比有哪些字段改动。`loginAvailable=false`（管理员数据源）时登录开关与电话不参与计数——
 * 页面上本来就不展示它们。
 */
export function listPersonEditChanges(
  baseline: PersonEditValues,
  current: PersonEditValues,
  options: { phoneAvailable: boolean; loginAvailable: boolean }
): PersonEditChangeKey[] {
  const changes: PersonEditChangeKey[] = [...Object.keys(diffNameParts(baseline.names, current.names).parts)] as PersonEditChangeKey[];
  if (options.phoneAvailable && baseline.phone.trim() !== current.phone.trim()) changes.push("phone");
  // 后端会把邮箱统一成小写，仅大小写不同不算改动。
  if (baseline.email.trim().toLowerCase() !== current.email.trim().toLowerCase()) changes.push("email");
  if (options.loginAvailable && baseline.loginEnabled !== current.loginEnabled) changes.push("loginEnabled");
  return changes;
}

export interface PersonEditErrors {
  names: PersonNameErrors;
  /** 邮箱错误码：`invalid` 格式不对；`required` 必填（管理员接口邮箱必填）。 */
  email?: "invalid" | "required";
}

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function validatePersonEdit(
  baseline: PersonEditValues,
  current: PersonEditValues,
  options: { requireEmail: boolean }
): PersonEditErrors {
  const nameTouched = diffNameParts(baseline.names, current.names).nameChanged;
  const email = current.email.trim();
  let emailError: PersonEditErrors["email"];
  if (!email) {
    // 店员接口里邮箱留空表示沿用原邮箱，不报错；管理员接口邮箱必填。
    if (options.requireEmail) emailError = "required";
  } else if (!EMAIL_PATTERN.test(email)) {
    emailError = "invalid";
  }
  return {
    // 只有动过姓名区才要求姓、名必填：存量账号可能只有一个词的全名，不能因此挡住改电话、邮箱等保存。
    names: validatePersonNameFields(current.names, { requireNames: nameTouched }),
    ...(emailError ? { email: emailError } : {}),
  };
}

export function hasPersonEditErrors(errors: PersonEditErrors) {
  return Boolean(errors.email) || hasPersonNameErrors(errors.names);
}

/** 保存后是否要提示给员工发设置密码邮件：从「不能登录」改为「能登录」且操作者有重置密码权限。 */
export function shouldPromptPasswordSetup(input: {
  loginWasEnabled: boolean;
  loginNow: boolean;
  canResetPassword: boolean;
}) {
  return !input.loginWasEnabled && input.loginNow && input.canResetPassword;
}

/**
 * 店员接口更新请求体。
 * `current` 取最新详情（而不是打开时的快照）里的用户名 / 状态 / 全名，保证保存资料不会改动启停状态。
 * 登录开关只在与打开时不同才提交，避免保存资料时无意改动登录权限。
 */
export function buildStoreUserUpdatePayload(input: {
  userGuid: string;
  storeCode: string;
  current: { username: string; status: number; fullName?: string | null };
  baseline: PersonEditValues;
  values: PersonEditValues;
}): StoreUserUpdatePayload {
  const diff = diffNameParts(input.baseline.names, input.values.names);
  const fullName = resolveUpdateFullName({
    diff,
    currentFullName: input.current.fullName,
    baselineNames: input.baseline.names,
  });
  return {
    userGuid: input.userGuid,
    storeCode: input.storeCode,
    username: input.current.username,
    ...(fullName !== undefined ? { fullName } : {}),
    ...diff.parts,
    email: input.values.email.trim() || undefined,
    phone: input.values.phone.trim() || undefined,
    status: input.current.status,
    ...(input.values.loginEnabled !== input.baseline.loginEnabled ? { loginEnabled: input.values.loginEnabled } : {}),
    roleNames: [STORE_STAFF_ROLE],
  };
}

/** 管理员接口更新请求体：该接口整体覆盖 username / email / fullName / isActive，所以都带当前值。 */
export function buildIdentityUpdateInput(input: {
  current: { username: string; isActive: boolean; fullName?: string | null };
  baseline: PersonEditValues;
  values: PersonEditValues;
}): IdentityUpdateUserInput {
  const diff = diffNameParts(input.baseline.names, input.values.names);
  const fullName = resolveUpdateFullName({
    diff,
    currentFullName: input.current.fullName,
    baselineNames: input.baseline.names,
  });
  return {
    username: input.current.username,
    email: input.values.email.trim(),
    ...(fullName !== undefined ? { fullName } : {}),
    ...diff.parts,
    isActive: input.current.isActive,
  };
}

/** 编辑页的数据源：带 storeCode 走店员接口；否则管理员账号会话走管理员接口。 */
export type PersonEditSourceKind = "store" | "admin" | "none";

export function resolvePersonEditSource(input: {
  storeCode: string;
  adminSessionAllowed: boolean;
  /** 店长（非管理员、非仓库经理）不走管理员接口；深链没带 storeCode 时直接「无权编辑」，而不是越权读全局用户。 */
  isStoreLevelManager?: boolean;
}): PersonEditSourceKind {
  if (input.storeCode.trim()) return "store";
  return input.adminSessionAllowed && !input.isStoreLevelManager ? "admin" : "none";
}
