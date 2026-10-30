import { apiClient } from "@/shared/api/client";
import { getStoredApiHost } from "@/shared/api/config";
import {
  buildRevokePayload,
  buildSendPayload,
  mapSendResult,
  mapStaffDetail,
  mapStaffItem,
  type GuardianLinkForm,
  type MinorStaffComplianceItem,
} from "./staff-contract";

const BASE = "/minor-employment/staff";

/** 缓存键：按当前账号 + 目标员工隔离，换账号不串数据。 */
export function minorStaffQueryKey(actorKey: string, userGuid: string) {
  return ["minorEmployment", "staff", actorKey, userGuid] as const;
}

/** 后端回传的签字链接一般是完整地址；万一是相对路径，按当前接口域名补全，保证复制出去能直接打开。 */
async function absoluteSigningUrl(raw: string | undefined) {
  if (!raw) return undefined;
  if (/^https?:\/\//i.test(raw)) return raw;
  const host = await getStoredApiHost();
  return `https://${host}${raw.startsWith("/") ? raw : `/${raw}`}`;
}

/**
 * 店长 / HR 给未成年员工的家长发送签字链接（员工本人不再参与）。
 * 读：Attendance.Schedule.ViewStore；发送 / 作废：Attendance.Schedule.EditManagedStore（后端再按门店范围裁决）。
 */
export const minorEmploymentStaffApi = {
  async getStaff(userGuid: string, signal?: AbortSignal) {
    const response = await apiClient.get(`${BASE}/${encodeURIComponent(userGuid)}`, { signal });
    return mapStaffDetail(response.data);
  },
  async sendGuardianLink(
    userGuid: string,
    item: Pick<MinorStaffComplianceItem, "version" | "revision">,
    form: GuardianLinkForm,
  ) {
    const response = await apiClient.post(
      `${BASE}/${encodeURIComponent(userGuid)}/guardian-link`,
      buildSendPayload(item, form),
    );
    const result = mapSendResult(response.data);
    return { ...result, signingUrl: await absoluteSigningUrl(result.signingUrl) };
  },
  async revokeGuardianLink(userGuid: string, item: Pick<MinorStaffComplianceItem, "version" | "revision">) {
    const response = await apiClient.post(
      `${BASE}/${encodeURIComponent(userGuid)}/guardian-link/revoke`,
      buildRevokePayload(item),
    );
    return mapStaffItem(response.data);
  },
};
