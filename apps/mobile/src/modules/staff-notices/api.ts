import { apiClient } from "@/shared/api/client";
import type {
  StaffNoticeBatchDetail,
  StaffNoticeBatchPage,
  StaffNoticePreflight,
  StaffNoticePreflightRequest,
  StaffNoticePreview,
  StaffNoticeSendRequest,
  StaffNoticeSendResult,
  StaffNoticeTemplate,
} from "./types";

const BASE = "/staff-notifications";

export async function fetchStaffNoticeTemplates(): Promise<StaffNoticeTemplate[]> {
  const response = await apiClient.get(`${BASE}/templates`);
  const templates = Array.isArray(response.data) ? (response.data as StaffNoticeTemplate[]) : [];
  // 停用的模板发不出去（后端会拒绝），手机上不列出来。
  return templates.filter((template) => template.isEnabled);
}

export async function fetchStaffNoticePreflight(request: StaffNoticePreflightRequest): Promise<StaffNoticePreflight> {
  const response = await apiClient.post(`${BASE}/preflight`, request);
  return response.data as StaffNoticePreflight;
}

export async function fetchStaffNoticePreview(templateKey: string, userGuid: string): Promise<StaffNoticePreview> {
  const response = await apiClient.post(`${BASE}/preview`, { templateKey, userGuid });
  return response.data as StaffNoticePreview;
}

export async function sendStaffNotice(request: StaffNoticeSendRequest): Promise<StaffNoticeSendResult> {
  const response = await apiClient.post(`${BASE}/send`, request);
  return response.data as StaffNoticeSendResult;
}

export async function fetchStaffNoticeBatches(page: number, pageSize: number): Promise<StaffNoticeBatchPage> {
  const response = await apiClient.get(`${BASE}/batches`, { params: { page, pageSize } });
  return response.data as StaffNoticeBatchPage;
}

export async function fetchStaffNoticeBatch(batchId: string): Promise<StaffNoticeBatchDetail> {
  const response = await apiClient.get(`${BASE}/batches/${encodeURIComponent(batchId)}`);
  return response.data as StaffNoticeBatchDetail;
}

export async function retryStaffNoticeBatch(batchId: string): Promise<StaffNoticeBatchDetail> {
  const response = await apiClient.post(`${BASE}/batches/${encodeURIComponent(batchId)}/retry`);
  return response.data as StaffNoticeBatchDetail;
}
