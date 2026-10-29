// 「员工个人信息维护」（人员管理 > 资料页签 / 个人信息编辑页）的纯逻辑：
// 接口响应归一化、列表查询参数、分页累加、「待补全」判断、编辑表单差异与保存载荷、错误归类。
// 不依赖 React Native，可在 Node 下测试。
//
// 后端接口（前缀 /api/employee-profiles/managed，响应统一包在 ApiResponse 里，拦截器已解包）：
//   GET /managed、GET /managed/filter-options、GET /managed/{userGuid}、PUT /managed/{userGuid}。
// 数据范围（管理员全部分店 / 店长限管理分店、排除本人与受保护角色）全部由服务端强制，客户端只负责显示。
import { normalizeBirthday } from "../../employee-profile/birthday";
import { formatPhoneForList, pickPrimaryRole } from "../person-list-item";
import { resolvePersonName } from "../person-name";
import type { PeopleTone } from "../ui";

export const MANAGED_PROFILE_PAGE_SIZE = 20;

export const MANAGED_PROFILE_GENDERS = ["unknown", "male", "female", "other"] as const;
export type ManagedProfileGender = (typeof MANAGED_PROFILE_GENDERS)[number];

export const MANAGED_PROFILE_EMPLOYMENT_TYPES = ["fullTime", "partTime", "casual"] as const;
export type ManagedProfileEmploymentType = (typeof MANAGED_PROFILE_EMPLOYMENT_TYPES)[number];

/** 服务端字段长度上限（EmployeeProfileManagedUpsertDto）。 */
export const MANAGED_PROFILE_PHONE_MAX = 50;
export const MANAGED_PROFILE_ADDRESS_MAX = 500;

// ───────────────────────── 类型 ─────────────────────────

export interface ManagedProfileListItem {
  employeeInfoId: number | null;
  userGUID: string;
  username: string;
  email: string;
  fullName: string;
  hasProfile: boolean;
  phone: string;
  /** 以下银行 / 公积金字段服务端已脱敏（****1234），只用于展示与「待补全」判断。 */
  bankBsb: string;
  bankAccountNumber: string;
  superannuationCompanyName: string;
  superannuationCompanyCode: string;
  superannuationAccountNumber: string;
  gender: string;
  employeeType: string;
  birthday: string;
  avatarUrl: string;
  updatedAt: string;
  storeNames: string[];
  roleNames: string[];
}

export interface ManagedProfilePage {
  items: ManagedProfileListItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ManagedProfileFilterStore {
  storeGuid: string;
  storeCode: string;
  storeName: string;
}

export interface ManagedProfileFilterRole {
  roleGuid: string;
  roleName: string;
}

export interface ManagedProfileFilterOptions {
  stores: ManagedProfileFilterStore[];
  roles: ManagedProfileFilterRole[];
}

export interface ManagedProfileDetail {
  employeeInfoId: number | null;
  userGUID: string;
  username: string;
  email: string;
  fullName: string;
  hasProfile: boolean;
  storeNames: string[];
  roleNames: string[];
  phone: string;
  gender: string;
  employmentType: string;
  address: string;
  avatarUrl: string;
  /** 敏感资料脱敏摘要：只读展示，不可回写。 */
  bankBsb: string;
  bankAccountNumber: string;
  superannuationCompanyName: string;
  superannuationCompanyCode: string;
  superannuationAccountNumber: string;
  identityType: string;
  identityId: string;
  hasBirthday: boolean;
  /** 调用者是否持有「修改职位类型」权限；没有时员工类型置灰带锁。 */
  canEditEmploymentType: boolean;
  updatedAt: string;
  updatedBy: string;
}

export interface ManagedProfileFilters {
  search: string;
  /** 空串 = 全部（我管理的）分店。 */
  storeGuid: string;
  /** 空串 = 全部角色。 */
  roleGuid: string;
  /** 「待补全」chip：服务端没有该筛选，只在已加载的行里本地过滤。 */
  pendingOnly: boolean;
}

export const DEFAULT_MANAGED_PROFILE_FILTERS: ManagedProfileFilters = {
  search: "",
  storeGuid: "",
  roleGuid: "",
  pendingOnly: false,
};

// ───────────────────────── 响应归一化 ─────────────────────────

type ApiRecord = Record<string, unknown>;

function isRecord(value: unknown): value is ApiRecord {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** 同时认 camelCase 与 PascalCase（拦截器只解包信封，不改字段大小写）。 */
function pick(source: ApiRecord, key: string): unknown {
  if (Object.prototype.hasOwnProperty.call(source, key)) return source[key];
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  return source[pascal];
}

function str(source: ApiRecord, key: string): string {
  const value = pick(source, key);
  return typeof value === "string" ? value.trim() : "";
}

function bool(source: ApiRecord, key: string): boolean {
  return pick(source, key) === true;
}

function strList(source: ApiRecord, key: string): string[] {
  const value = pick(source, key);
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string").map((item) => item.trim()).filter(Boolean)
    : [];
}

function num(source: ApiRecord, key: string): number | null {
  const value = pick(source, key);
  const numeric = typeof value === "string" && value.trim() ? Number(value) : value;
  return typeof numeric === "number" && Number.isFinite(numeric) ? numeric : null;
}

export const MANAGED_PROFILE_INVALID_RESPONSE = "MANAGED_PROFILE_RESPONSE_INVALID";

export function normalizeManagedProfileListItem(payload: unknown): ManagedProfileListItem {
  if (!isRecord(payload)) throw new Error(MANAGED_PROFILE_INVALID_RESPONSE);
  const userGUID = str(payload, "userGUID") || str(payload, "userGuid");
  if (!userGUID) throw new Error(MANAGED_PROFILE_INVALID_RESPONSE);
  return {
    employeeInfoId: num(payload, "employeeInfoId"),
    userGUID,
    username: str(payload, "username"),
    email: str(payload, "email"),
    fullName: str(payload, "fullName"),
    hasProfile: bool(payload, "hasProfile"),
    phone: str(payload, "phone"),
    bankBsb: str(payload, "bankBsb"),
    bankAccountNumber: str(payload, "bankAccountNumber"),
    superannuationCompanyName: str(payload, "superannuationCompanyName"),
    superannuationCompanyCode: str(payload, "superannuationCompanyCode"),
    superannuationAccountNumber: str(payload, "superannuationAccountNumber"),
    gender: str(payload, "gender"),
    employeeType: str(payload, "employeeType"),
    birthday: str(payload, "birthday"),
    avatarUrl: str(payload, "avatarUrl"),
    updatedAt: str(payload, "updatedAt"),
    storeNames: strList(payload, "storeNames"),
    roleNames: strList(payload, "roleNames"),
  };
}

export function normalizeManagedProfilePage(payload: unknown): ManagedProfilePage {
  if (!isRecord(payload)) throw new Error(MANAGED_PROFILE_INVALID_RESPONSE);
  const rawItems = pick(payload, "items");
  const items = Array.isArray(rawItems) ? rawItems.map(normalizeManagedProfileListItem) : [];
  return {
    items,
    total: num(payload, "total") ?? items.length,
    page: num(payload, "page") ?? 1,
    pageSize: num(payload, "pageSize") ?? MANAGED_PROFILE_PAGE_SIZE,
  };
}

export function normalizeManagedProfileFilterOptions(payload: unknown): ManagedProfileFilterOptions {
  if (!isRecord(payload)) throw new Error(MANAGED_PROFILE_INVALID_RESPONSE);
  const rawStores = pick(payload, "stores");
  const rawRoles = pick(payload, "roles");
  const stores = (Array.isArray(rawStores) ? rawStores : []).flatMap((item) => {
    if (!isRecord(item)) return [];
    const storeGuid = str(item, "storeGuid") || str(item, "storeGUID");
    return storeGuid ? [{ storeGuid, storeCode: str(item, "storeCode"), storeName: str(item, "storeName") }] : [];
  });
  const roles = (Array.isArray(rawRoles) ? rawRoles : []).flatMap((item) => {
    if (!isRecord(item)) return [];
    const roleGuid = str(item, "roleGuid") || str(item, "roleGUID");
    return roleGuid ? [{ roleGuid, roleName: str(item, "roleName") }] : [];
  });
  return { stores, roles };
}

export function normalizeManagedProfileDetail(payload: unknown): ManagedProfileDetail {
  if (!isRecord(payload)) throw new Error(MANAGED_PROFILE_INVALID_RESPONSE);
  const userGUID = str(payload, "userGUID") || str(payload, "userGuid");
  if (!userGUID) throw new Error(MANAGED_PROFILE_INVALID_RESPONSE);
  return {
    employeeInfoId: num(payload, "employeeInfoId"),
    userGUID,
    username: str(payload, "username"),
    email: str(payload, "email"),
    fullName: str(payload, "fullName"),
    hasProfile: bool(payload, "hasProfile"),
    storeNames: strList(payload, "storeNames"),
    roleNames: strList(payload, "roleNames"),
    phone: str(payload, "phone"),
    gender: str(payload, "gender"),
    employmentType: str(payload, "employmentType"),
    address: str(payload, "address"),
    avatarUrl: str(payload, "avatarUrl"),
    bankBsb: str(payload, "bankBsb"),
    bankAccountNumber: str(payload, "bankAccountNumber"),
    superannuationCompanyName: str(payload, "superannuationCompanyName"),
    superannuationCompanyCode: str(payload, "superannuationCompanyCode"),
    superannuationAccountNumber: str(payload, "superannuationAccountNumber"),
    identityType: str(payload, "identityType"),
    identityId: str(payload, "identityId"),
    hasBirthday: bool(payload, "hasBirthday"),
    // 缺省按「不可改」处理：宁可置灰也不要让没权限的人误以为能改。
    canEditEmploymentType: bool(payload, "canEditEmploymentType"),
    updatedAt: str(payload, "updatedAt"),
    updatedBy: str(payload, "updatedBy"),
  };
}

// ───────────────────────── 列表查询与分页 ─────────────────────────

/** 筛选条件 → GET /managed 的查询参数（空值不带；待补全是本地过滤，不进查询）。 */
export function buildManagedProfileListParams(
  filters: ManagedProfileFilters,
  page: number,
  pageSize = MANAGED_PROFILE_PAGE_SIZE,
): Record<string, string | number> {
  const search = filters.search.trim();
  return {
    page,
    pageSize,
    ...(search ? { search } : {}),
    ...(filters.storeGuid ? { storeGuid: filters.storeGuid } : {}),
    ...(filters.roleGuid ? { roleGuid: filters.roleGuid } : {}),
  };
}

/** react-query 的 key 片段：不含 pendingOnly（本地过滤不应触发重新取数）。 */
export function buildManagedProfileFilterKey(filters: ManagedProfileFilters): string[] {
  return [filters.search.trim(), filters.storeGuid, filters.roleGuid];
}

/** 下一页页码；已取完返回 undefined。 */
export function resolveManagedProfileNextPage(last: ManagedProfilePage): number | undefined {
  const loaded = last.page * last.pageSize;
  return last.items.length > 0 && loaded < last.total ? last.page + 1 : undefined;
}

/** 累加各页并按 userGUID 去重（翻页期间数据变动可能让同一人出现在相邻两页）。 */
export function flattenManagedProfilePages(pages: readonly ManagedProfilePage[]): ManagedProfileListItem[] {
  const seen = new Set<string>();
  const result: ManagedProfileListItem[] = [];
  for (const page of pages) {
    for (const item of page.items) {
      const key = item.userGUID.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      result.push(item);
    }
  }
  return result;
}

// ───────────────────────── 待补全 ─────────────────────────

/**
 * 列表里「缺什么」的检查项，顺序即显示顺序。
 * 只看工资发放必需、且列表接口已返回的字段（与 employee-profile/profile-completeness 的判断口径一致）：
 * 银行 / 公积金要「机构 + 账号」齐全才算完成，只填一半发不了工资。
 */
export const MANAGED_PROFILE_MISSING_ORDER = ["banking", "superannuation", "phone", "birthday"] as const;
export type ManagedProfileMissingKey = (typeof MANAGED_PROFILE_MISSING_ORDER)[number];

export interface ManagedProfileCompletenessFacts {
  phone: string;
  bankBsb: string;
  bankAccountNumber: string;
  superannuationCompanyName: string;
  superannuationAccountNumber: string;
  hasBirthday: boolean;
}

function filled(value: string | null | undefined) {
  return Boolean(value?.trim());
}

export function resolveManagedProfileMissing(facts: ManagedProfileCompletenessFacts): ManagedProfileMissingKey[] {
  const checks: Record<ManagedProfileMissingKey, boolean> = {
    banking: filled(facts.bankBsb) && filled(facts.bankAccountNumber),
    superannuation: filled(facts.superannuationCompanyName) && filled(facts.superannuationAccountNumber),
    phone: filled(facts.phone),
    birthday: facts.hasBirthday,
  };
  return MANAGED_PROFILE_MISSING_ORDER.filter((key) => !checks[key]);
}

/** 列表行：生日在列表里是日期串，归一化成功才算「已填」。 */
export function resolveListItemMissing(item: ManagedProfileListItem): ManagedProfileMissingKey[] {
  return resolveManagedProfileMissing({ ...item, hasBirthday: Boolean(normalizeBirthday(item.birthday)) });
}

export function resolveDetailMissing(detail: ManagedProfileDetail): ManagedProfileMissingKey[] {
  return resolveManagedProfileMissing(detail);
}

export function isListItemPending(item: ManagedProfileListItem): boolean {
  return resolveListItemMissing(item).length > 0;
}

/** 「待补全」chip 的本地过滤。 */
export function filterPendingItems(items: readonly ManagedProfileListItem[], pendingOnly: boolean): ManagedProfileListItem[] {
  return pendingOnly ? items.filter(isListItemPending) : [...items];
}

/**
 * 「待补全」chip 打开时，已加载的行里命中的不多、但后面还有页：要继续自动取下一页，
 * 否则用户会看到空列表却不知道后面还有人（本地过滤只能看见已加载的行）。
 */
export function shouldAutoLoadMoreForPending(input: {
  pendingOnly: boolean;
  visibleCount: number;
  hasNextPage: boolean;
  isFetching: boolean;
  /** 至少凑够这么多条命中就不再自动翻页。 */
  minVisible?: number;
}): boolean {
  if (!input.pendingOnly || !input.hasNextPage || input.isFetching) return false;
  return input.visibleCount < (input.minVisible ?? 8);
}

// ───────────────────────── 列表行模型 ─────────────────────────

export interface ManagedProfileRowModel {
  key: string;
  name: string;
  initials: string;
  /** 名字旁只放权限最高的角色（与人员列表同一规则）。 */
  role: { roleName: string; tone: PeopleTone } | null;
  /** 分店名（多家用「、」之外的拼接由界面处理，这里给数组）。 */
  storeNames: string[];
  phone: string;
  missing: ManagedProfileMissingKey[];
  /** 还没有任何资料记录（employeeInfo 不存在）。 */
  noProfile: boolean;
  pending: boolean;
}

export function buildManagedProfileRow(item: ManagedProfileListItem): ManagedProfileRowModel {
  const name = resolvePersonName({ fullName: item.fullName, username: item.username });
  const missing = resolveListItemMissing(item);
  return {
    key: item.userGUID,
    name: name.primary || item.username,
    initials: name.initials,
    role: pickPrimaryRole(item.roleNames),
    storeNames: item.storeNames,
    phone: formatPhoneForList(item.phone),
    missing,
    noProfile: !item.hasProfile,
    pending: missing.length > 0,
  };
}

// ───────────────────────── 分店 chip（单店锁定） ─────────────────────────

/**
 * 分店 chip 的当前值与是否锁定：
 * - 只有一家可选分店：默认选中并锁定（不可清除）；
 * - 否则用用户选的值，但选项里已不存在的值（范围变化）回落到「全部」。
 */
export function resolveStoreChip(
  stores: readonly ManagedProfileFilterStore[],
  selectedStoreGuid: string,
): { storeGuid: string; locked: boolean } {
  if (stores.length === 1) return { storeGuid: stores[0].storeGuid, locked: true };
  const exists = stores.some((store) => store.storeGuid.toLowerCase() === selectedStoreGuid.toLowerCase());
  return { storeGuid: exists ? selectedStoreGuid : "", locked: false };
}

// ───────────────────────── 编辑表单 ─────────────────────────

export interface ManagedProfileEditValues {
  phone: string;
  gender: ManagedProfileGender;
  /** 空串 = 尚未设置。 */
  employmentType: ManagedProfileEmploymentType | "";
  address: string;
}

export function normalizeGender(value: string | null | undefined): ManagedProfileGender {
  const lower = (value ?? "").trim().toLowerCase();
  return (MANAGED_PROFILE_GENDERS as readonly string[]).includes(lower) ? (lower as ManagedProfileGender) : "unknown";
}

export function normalizeEmploymentType(value: string | null | undefined): ManagedProfileEmploymentType | "" {
  const text = (value ?? "").trim();
  return (MANAGED_PROFILE_EMPLOYMENT_TYPES as readonly string[]).includes(text)
    ? (text as ManagedProfileEmploymentType)
    : "";
}

export function buildManagedProfileEditBaseline(detail: ManagedProfileDetail): ManagedProfileEditValues {
  return {
    phone: detail.phone,
    gender: normalizeGender(detail.gender),
    employmentType: normalizeEmploymentType(detail.employmentType),
    address: detail.address,
  };
}

export type ManagedProfileEditField = keyof ManagedProfileEditValues;

export interface ManagedProfileEditAccess {
  /** 持有 EditManagedStore：没有则整页只读、不显示保存按钮。 */
  canEdit: boolean;
  /** 详情返回的 canEditEmploymentType：false 时员工类型置灰带锁图标。 */
  canEditEmploymentType: boolean;
}

/** 本次允许提交的字段：员工类型另需 canEditEmploymentType。 */
export function resolveEditableFields(access: ManagedProfileEditAccess): ManagedProfileEditField[] {
  if (!access.canEdit) return [];
  const fields: ManagedProfileEditField[] = ["phone", "gender", "address"];
  if (access.canEditEmploymentType) fields.push("employmentType");
  return fields;
}

/** 与基线对比，列出改动过的字段（比较时去掉首尾空白；员工类型不可改时不算改动）。 */
export function listManagedProfileChanges(
  baseline: ManagedProfileEditValues,
  values: ManagedProfileEditValues,
  access: ManagedProfileEditAccess,
): ManagedProfileEditField[] {
  return resolveEditableFields(access).filter((field) => values[field].trim() !== baseline[field].trim());
}

/**
 * 保存载荷：只带改动过的键。后端用「请求里带没带」区分未改与清空——
 * 清空地址 / 手机号要显式传空字符串；员工类型没选（空串）不提交（不能清空职位类型）。
 */
export function buildManagedProfileUpdatePayload(
  baseline: ManagedProfileEditValues,
  values: ManagedProfileEditValues,
  access: ManagedProfileEditAccess,
): Partial<Record<ManagedProfileEditField, string>> {
  const payload: Partial<Record<ManagedProfileEditField, string>> = {};
  for (const field of listManagedProfileChanges(baseline, values, access)) {
    if (field === "employmentType" && !values.employmentType) continue;
    payload[field] = values[field].trim();
  }
  return payload;
}

export type ManagedProfileEditErrors = Partial<Record<"phone" | "address", "tooLong">>;

export function validateManagedProfileEdit(values: ManagedProfileEditValues): ManagedProfileEditErrors {
  const errors: ManagedProfileEditErrors = {};
  if (values.phone.trim().length > MANAGED_PROFILE_PHONE_MAX) errors.phone = "tooLong";
  if (values.address.trim().length > MANAGED_PROFILE_ADDRESS_MAX) errors.address = "tooLong";
  return errors;
}

// ───────────────────────── 错误归类 ─────────────────────────

export type ManagedProfileFailureKey = "scope" | "positionType" | "notFound" | "forbidden" | "generic";

export interface ManagedProfileErrorMeta {
  status?: number;
  code?: string;
}

/**
 * 从 axios 错误（4xx 带 response.data.errorCode）或拦截器抛出的业务错误（200 + success=false，带 error.code）里取状态码与业务码。
 * 不依赖 axios，按结构读取，便于单测。
 */
export function readManagedProfileErrorMeta(error: unknown): ManagedProfileErrorMeta {
  if (!isRecord(error)) return {};
  const response = isRecord(error.response) ? error.response : null;
  const data = response && isRecord(response.data) ? response.data : null;
  const rawCode = (data ? pick(data, "errorCode") ?? pick(data, "code") : undefined) ?? error.code;
  const status = response && typeof response.status === "number" ? response.status : undefined;
  return {
    ...(status !== undefined ? { status } : {}),
    ...(typeof rawCode === "string" && rawCode.trim() ? { code: rawCode.trim() } : {}),
  };
}

/** 保存 / 读取失败的固定文案键：只显示我们自己的中英文文案，不展示服务端原文。 */
export function classifyManagedProfileFailure(meta: ManagedProfileErrorMeta): ManagedProfileFailureKey {
  switch ((meta.code ?? "").toUpperCase()) {
    case "EMPLOYEE_PROFILE_MANAGED_SCOPE_FORBIDDEN":
      return "scope";
    case "EMPLOYEE_PROFILE_POSITION_TYPE_FORBIDDEN":
      return "positionType";
    case "USER_NOT_FOUND":
      return "notFound";
    default:
      break;
  }
  if (meta.status === 404) return "notFound";
  if (meta.status === 403) return "forbidden";
  return "generic";
}

// ───────────────────────── 敏感资料只读摘要 ─────────────────────────

export interface SensitiveSummaryRow {
  key: "bank" | "super" | "birthdayIdentity";
  /** null = 未填写（界面显示「未填写」）。 */
  value: string | null;
}

/**
 * 「敏感资料（只读）」三行：只展示服务端返回的脱敏摘要，从不拼接 / 还原任何明文。
 * 生日只有「是否已填」，证件号是脱敏串；两者合成一行。
 */
export function buildSensitiveSummary(detail: ManagedProfileDetail): SensitiveSummaryRow[] {
  const join = (parts: string[]) => parts.filter(Boolean).join(" ");
  const bank = join([detail.bankBsb, detail.bankAccountNumber]);
  const superValue = join([detail.superannuationCompanyName, detail.superannuationAccountNumber]);
  const birthdayIdentity = join([detail.hasBirthday ? "****" : "", detail.identityId]);
  return [
    { key: "bank", value: bank || null },
    { key: "super", value: superValue || null },
    { key: "birthdayIdentity", value: birthdayIdentity || null },
  ];
}
