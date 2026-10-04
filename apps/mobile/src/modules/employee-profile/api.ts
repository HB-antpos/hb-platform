import { apiClient } from "@/shared/api/client";
import { reportExternalFetchFailure } from "@/shared/logging/external-fetch-log";
import { buildCashierBarcodePrintConfirmationRequest } from "@/modules/employee-profile/cashier-barcode";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { reviewAwareFetch } from "@/modules/ios-review/network";
import {
  createEmployeeProfileApi,
  normalizeEmployeeProfile,
} from "@/modules/employee-profile/api-contract";
import type {
  DirectUploadRequest,
  DirectUploadSignature,
  CashierBarcodeResponse,
  EmployeeProfile,
  EmployeeProfileImageKind,
  UpdateEmployeeProfilePayload,
  SensitiveEmployeeProfilePayload,
} from "@/modules/employee-profile/types";

type ApiRecord = Record<string, unknown>;

function toApiImageKind(kind: EmployeeProfileImageKind) {
  return kind === "identityPhoto" ? "identity" : "avatar";
}

function asString(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

function normalizeDirectUploadSignature(payload: unknown): DirectUploadSignature {
  const data = (payload && typeof payload === "object" ? payload : {}) as ApiRecord;
  const headersValue = data.headers ?? data.Headers;
  const headers =
    headersValue && typeof headersValue === "object"
      ? Object.fromEntries(
          Object.entries(headersValue as Record<string, unknown>).map(([key, value]) => [
            key,
            typeof value === "string" ? value : String(value ?? ""),
          ])
        )
      : {};

  return {
    url: asString(data.url ?? data.Url),
    objectKey: asString(data.objectKey ?? data.ObjectKey),
    headers,
  };
}

function normalizeCashierBarcode(payload: unknown): CashierBarcodeResponse {
  const data = (payload && typeof payload === "object" ? payload : {}) as ApiRecord;
  return {
    exists: Boolean(data.exists ?? data.Exists),
    barcode: asString(data.barcode ?? data.Barcode),
    format: asString(data.format ?? data.Format) || "EAN13",
    printCount: Number(data.printCount ?? data.PrintCount) || 0,
    createdAt: asString(data.createdAt ?? data.CreatedAt) || undefined,
    updatedAt: asString(data.updatedAt ?? data.UpdatedAt) || undefined,
  };
}

const employeeProfileApi = createEmployeeProfileApi(apiClient);

export async function getMyEmployeeProfileApi(): Promise<EmployeeProfile> {
  return employeeProfileApi.getMyEmployeeProfile();
}

export async function updateMyEmployeeProfileApi(
  payload: UpdateEmployeeProfilePayload
): Promise<EmployeeProfile> {
  return employeeProfileApi.updateMyEmployeeProfile(payload);
}

export async function getMySensitiveChangeRequestApi() {
  return employeeProfileApi.getMySensitiveChangeRequest();
}

export async function upsertMySensitiveChangeRequestApi(
  payload: SensitiveEmployeeProfilePayload
) {
  return employeeProfileApi.upsertMySensitiveChangeRequest(payload);
}

export async function withdrawMySensitiveChangeRequestApi(requestId?: number) {
  return employeeProfileApi.withdrawMySensitiveChangeRequest(requestId);
}

export async function getMySensitiveChangeHistoryApi(take = 20) {
  return employeeProfileApi.getMySensitiveChangeHistory(take);
}

export async function getEmployeeProfileImageUploadSignature(
  kind: EmployeeProfileImageKind,
  request: DirectUploadRequest
): Promise<DirectUploadSignature> {
  const response = await apiClient.post("/EmployeeProfiles/me/image-upload-signature", {
    ...request,
    kind: toApiImageKind(kind),
  });
  return normalizeDirectUploadSignature(response.data);
}

export async function uploadEmployeeProfileImageBlobToSignedUrl(
  blob: Blob,
  signature: DirectUploadSignature
) {
  if (isIosReviewSessionActive()) {
    return signature.objectKey;
  }
  let response: Response;
  try {
    response = await reviewAwareFetch(signature.url, {
      method: "PUT",
      headers: signature.headers,
      body: blob,
    });
  } catch (error) {
    reportExternalFetchFailure({
      message: "员工资料图片上传请求失败",
      sourceType: "employee-profile.upload",
      requestMethod: "PUT",
      requestUrl: signature.url,
      error,
      properties: {
        objectKey: signature.objectKey,
        uploadUrl: signature.url,
      },
    });
    throw error;
  }

  if (!response.ok) {
    reportExternalFetchFailure({
      message: "员工资料图片上传失败",
      sourceType: "employee-profile.upload",
      requestMethod: "PUT",
      requestUrl: signature.url,
      statusCode: response.status,
      properties: {
        objectKey: signature.objectKey,
        uploadUrl: signature.url,
      },
    });
    throw new Error(`Upload failed with status ${response.status}`);
  }

  return signature.objectKey;
}

export async function completeEmployeeProfileImageUploadApi(
  kind: EmployeeProfileImageKind,
  objectKey: string
): Promise<EmployeeProfile> {
  const response = await apiClient.post("/EmployeeProfiles/me/images/complete", {
    kind: toApiImageKind(kind),
    objectKey,
  });
  return normalizeEmployeeProfile(response.data);
}

export async function deleteEmployeeProfileImageApi(
  kind: EmployeeProfileImageKind
): Promise<EmployeeProfile> {
  const response = await apiClient.delete(`/EmployeeProfiles/me/images/${toApiImageKind(kind)}`);
  return normalizeEmployeeProfile(response.data);
}

export async function getMyCashierBarcodeApi(): Promise<CashierBarcodeResponse> {
  const response = await apiClient.get("/EmployeeProfiles/me/cashier-barcode");
  return normalizeCashierBarcode(response.data);
}

export async function refreshMyCashierBarcodeApi(): Promise<CashierBarcodeResponse> {
  const response = await apiClient.post("/EmployeeProfiles/me/cashier-barcode/refresh");
  return normalizeCashierBarcode(response.data);
}

export async function confirmMyCashierBarcodePrintApi(
  barcode: string,
  printAttemptId: string
): Promise<CashierBarcodeResponse> {
  const response = await apiClient.post(
    "/EmployeeProfiles/me/cashier-barcode/print-confirmation",
    buildCashierBarcodePrintConfirmationRequest(barcode, printAttemptId)
  );
  return normalizeCashierBarcode(response.data);
}
