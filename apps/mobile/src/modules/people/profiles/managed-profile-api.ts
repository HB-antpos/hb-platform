// 员工个人信息维护接口（/api/employee-profiles/managed）。
// 响应统一包在 ApiResponse 里，apiClient 拦截器已解包；字段归一化与错误归类见 managed-profile-logic。
// 所有请求带 account 会话绑定头：账号切换期间发出的请求会被拒绝，不会把上一个账号的数据写到新账号下。
import { apiClient } from "@/shared/api/client";
import { accountBoundRequestConfig } from "@/modules/auth/account-bound-request";
import {
  buildManagedProfileListParams,
  normalizeManagedProfileDetail,
  normalizeManagedProfileFilterOptions,
  normalizeManagedProfilePage,
  type ManagedProfileEditField,
  type ManagedProfileFilters,
} from "./managed-profile-logic";

const BASE = "/employee-profiles/managed";

function detailPath(userGuid: string) {
  return `${BASE}/${encodeURIComponent(userGuid.trim())}`;
}

export async function fetchManagedProfilePage(
  filters: ManagedProfileFilters,
  page: number,
  actorGuid: string,
  signal?: AbortSignal,
) {
  const response = await apiClient.get(BASE, {
    ...accountBoundRequestConfig(actorGuid, signal),
    params: buildManagedProfileListParams(filters, page),
  });
  return normalizeManagedProfilePage(response.data);
}

export async function fetchManagedProfileFilterOptions(actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(`${BASE}/filter-options`, accountBoundRequestConfig(actorGuid, signal));
  return normalizeManagedProfileFilterOptions(response.data);
}

export async function fetchManagedProfileDetail(userGuid: string, actorGuid: string, signal?: AbortSignal) {
  const response = await apiClient.get(detailPath(userGuid), accountBoundRequestConfig(actorGuid, signal));
  return normalizeManagedProfileDetail(response.data);
}

/** 保存：body 只含改动过的 phone / gender / employmentType / address，返回最新详情。 */
export async function updateManagedProfile(
  userGuid: string,
  payload: Partial<Record<ManagedProfileEditField, string>>,
  actorGuid: string,
) {
  const response = await apiClient.put(detailPath(userGuid), payload, accountBoundRequestConfig(actorGuid));
  return normalizeManagedProfileDetail(response.data);
}
