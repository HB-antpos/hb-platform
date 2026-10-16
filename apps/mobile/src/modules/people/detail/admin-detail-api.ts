import { apiClient } from "@/shared/api/client";
import { accountBoundRequestConfig } from "@/modules/auth/account-bound-request";
import { fetchUserAccessPermissionState } from "@/modules/users/access-management-api";
import { fetchIdentityUserLoginRecords } from "@/modules/identity-admin/api";
import { summarizeActiveSessions, type SessionCountSummary } from "./detail-rules";

/**
 * 管理员版详情专用的几个请求。放在 people/detail 里而不是 identity-admin/api.ts，
 * 是因为后者由「新增 / 编辑」块维护；这里只补详情需要的状态切换与汇总读取。
 */

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** PUT /Users/guid/{guid}/status（Users.Edit）：只改启用 / 停用，不碰其他资料字段。 */
export async function updateIdentityUserStatus(userGuid: string, isActive: boolean, actorGuid: string) {
  const normalizedGuid = userGuid.trim();
  if (!normalizedGuid) throw new Error("User GUID is required");
  const response = await apiClient.put(
    `/Users/guid/${encodeURIComponent(normalizedGuid)}/status`,
    { isActive },
    accountBoundRequestConfig(actorGuid),
  );
  const payload = response.data;
  if (isRecord(payload) && (payload.success === false || payload.Success === false)) {
    const message = payload.message ?? payload.Message;
    // 收到完整业务响应说明服务端已明确拒绝；保留标记，调用方据此区分「确定失败」与「结果未知」。
    throw Object.assign(new Error(typeof message === "string" && message.trim() ? message : "Request failed"), { apiBusinessError: true });
  }
  return true;
}

const LOGIN_SUMMARY_PAGE_SIZE = 50;

/** 「有效会话 N 个」：读最近 50 条登录记录里仍有效的会话；超过 50 条时数量是下限。 */
export async function fetchActiveSessionSummary(
  userGuid: string,
  actorGuid: string,
  signal?: AbortSignal,
): Promise<SessionCountSummary> {
  const result = await fetchIdentityUserLoginRecords(userGuid, { page: 1, pageSize: LOGIN_SUMMARY_PAGE_SIZE }, actorGuid, signal);
  return summarizeActiveSessions(result.items, result.total);
}

/** 直接权限 / 有效权限数量（permissions/state，比 access-permissions 轻，不带权限目录）。 */
export async function fetchDirectPermissionSummary(userGuid: string, actorGuid: string, signal?: AbortSignal) {
  const state = await fetchUserAccessPermissionState(userGuid, accountBoundRequestConfig(actorGuid, signal));
  return {
    directCount: state.directPermissionCodes.length,
    effectiveCount: state.effectivePermissionCodes.length,
    implicitAll: state.implicitAllPermissions || state.isSuperAdmin,
  };
}
