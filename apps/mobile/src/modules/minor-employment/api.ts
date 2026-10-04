import { apiClient } from "@/shared/api/client";
import {
  mapCandidate,
  mapInviteResult,
  mapProfile,
  mapRequest,
  mapSummary,
  toPayload,
} from "./contract";
import type {
  MinorEmploymentDraft,
  MinorEmploymentReviewDetail,
  MinorReturnField,
} from "./types";
const BASE = "/minor-employment";
export const minorEmploymentApi = {
  async getMine() {
    const r = await apiClient.get(`${BASE}/me`);
    return r.data == null ? null : mapProfile(r.data);
  },
  async saveDraft(
    d: MinorEmploymentDraft & { version?: number; revision?: number },
  ) {
    return mapProfile((await apiClient.put(`${BASE}/me`, toPayload(d))).data);
  },
  /**
   * 发起监护人签署。默认由后端直接发邮件到监护人邮箱（不回传链接）；
   * deliverByEmail=false 是备用方式，只返回链接供员工转发，监护人打开后仍须邮箱验证码。
   */
  async inviteParent(
    version: number,
    revision?: number,
    deliverByEmail = true,
  ) {
    return mapInviteResult(
      (
        await apiClient.post(`${BASE}/me/guardian-invite`, {
          version,
          revision: revision == null ? undefined : Number(revision),
          // 带回家给监护人处理，链接给 72 小时。
          expiryMinutes: 4320,
          deliverByEmail,
        })
      ).data,
    );
  },
  /** 店长发给我的未完成填写请求。 */
  async getMyRequests() {
    const data = (await apiClient.get(`${BASE}/me/requests`)).data;
    return Array.isArray(data) ? data.map(mapRequest) : [];
  },
  /** 店长：可管理门店里未满 18 岁的员工及其档案状态。 */
  async getManagerCandidates(storeCode?: string) {
    const data = (
      await apiClient.get(`${BASE}/manager/candidates`, {
        params: { storeCode: storeCode || undefined },
      })
    ).data;
    return Array.isArray(data) ? data.map(mapCandidate) : [];
  },
  async createRequest(userGUID: string, note?: string) {
    return mapRequest(
      (
        await apiClient.post(`${BASE}/manager/requests`, {
          userGUID,
          note: note?.trim() || null,
        })
      ).data,
    );
  },
  async cancelRequest(id: number) {
    return mapRequest(
      (await apiClient.post(`${BASE}/manager/requests/${id}/cancel`)).data,
    );
  },
  async submit(version: number) {
    return mapProfile(
      (
        await apiClient.post(`${BASE}/me/submit`, undefined, {
          params: { version },
        })
      ).data,
    );
  },
  async getReviews() {
    const r = (
      await apiClient.get(`${BASE}/hr`, { params: { page: 1, pageSize: 50 } })
    ).data;
    const x = (r?.data ?? r) as { items?: unknown[]; Items?: unknown[] };
    const items = Array.isArray(x.items)
      ? x.items
      : Array.isArray(x.Items)
        ? x.Items
        : [];
    return items.map(mapSummary);
  },
  async getReview(id: string) {
    return mapProfile(
      (await apiClient.get(`${BASE}/hr/${encodeURIComponent(id)}`)).data,
    ) as MinorEmploymentReviewDetail;
  },
  async approve(id: string, version: number, comment?: string) {
    return mapProfile(
      (
        await apiClient.post(`${BASE}/hr/${encodeURIComponent(id)}/approve`, {
          version,
          comment: comment || null,
          returnFields: [],
        })
      ).data,
    );
  },
  async returnForCorrection(
    id: string,
    version: number,
    fields: MinorReturnField[],
    comment: string,
  ) {
    return mapProfile(
      (
        await apiClient.post(`${BASE}/hr/${encodeURIComponent(id)}/return`, {
          version,
          comment,
          returnFields: fields,
        })
      ).data,
    );
  },
  async getDocument(id: string) {
    return (
      await apiClient.get(`${BASE}/hr/${encodeURIComponent(id)}/document`)
    ).data;
  },
  async getHistory(id: string) {
    const r = (
      await apiClient.get(`${BASE}/hr/${encodeURIComponent(id)}/history`)
    ).data;
    const x = r?.data ?? r;
    return Array.isArray(x)
      ? x
      : ((x as { items?: unknown[]; Items?: unknown[] }).items ??
          (x as { Items?: unknown[] }).Items ??
          []);
  },
};

export async function downloadMinorDocument(
  id: string,
): Promise<() => Promise<void>> {
  const response = await apiClient.get(
    `${BASE}/hr/${encodeURIComponent(id)}/document/download`,
    { responseType: "arraybuffer" },
  );
  const contentType = String(
    response.headers?.["content-type"] ?? "",
  ).toLowerCase();
  const bytes = new Uint8Array(response.data as ArrayBuffer);
  if (contentType && !contentType.includes("application/pdf"))
    throw new Error("DOCUMENT_NOT_PDF");
  if (
    bytes.length < 4 ||
    String.fromCharCode(...bytes.subarray(0, 4)) !== "%PDF"
  )
    throw new Error("DOCUMENT_INVALID");
  const FileSystem = await import("expo-file-system/legacy");
  const Sharing = await import("expo-sharing");
  if (!FileSystem.cacheDirectory) throw new Error("CACHE_UNAVAILABLE");
  const fileUri = `${FileSystem.cacheDirectory}minor-employment-${id}-${Date.now()}.pdf`;
  try {
    const { fromByteArray } = await import("base64-js");
    await FileSystem.writeAsStringAsync(fileUri, fromByteArray(bytes), {
      encoding: FileSystem.EncodingType.Base64,
    });
    if (!(await Sharing.isAvailableAsync()))
      throw new Error("SHARING_UNAVAILABLE");
    await Sharing.shareAsync(fileUri, {
      mimeType: "application/pdf",
      dialogTitle: "查看未成年用工原件",
    });
    return () => FileSystem.deleteAsync(fileUri, { idempotent: true });
  } catch (error) {
    await FileSystem.deleteAsync(fileUri, { idempotent: true }).catch(
      () => undefined,
    );
    throw error;
  }
}
