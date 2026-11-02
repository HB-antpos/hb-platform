// 工资单接口调用（HTTP 客户端由外部注入：生产用 apiClient，测试用假客户端抓取请求）。
// 工资单属于敏感数据：所有请求带 no-store，避免系统 HTTP 缓存留下副本。
import type { AxiosRequestConfig } from "axios";
import { fromByteArray } from "base64-js";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { toDateOnly } from "./format";
import {
  normalizeCompanies,
  normalizeDetail,
  normalizeEmailResult,
  normalizeEmailTarget,
  normalizeHours,
  normalizeList,
} from "./normalize";
import type {
  PayslipCompany,
  PayslipDetail,
  PayslipEmailRequest,
  PayslipEmailResult,
  PayslipEmailTarget,
  PayslipHours,
  PayslipKey,
  PayslipListQuery,
  PayslipListResult,
} from "./types";

export const PAYSLIPS_BASE_PATH = "/react/v1/myob/payslips";

export interface PayslipsHttp {
  get(url: string, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
  post(url: string, body?: unknown, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
}

const NO_STORE_HEADERS = { "Cache-Control": "no-store", Pragma: "no-cache" } as const;

/** 定位一张工资单的查询参数（detail / hours / pdf / email-target 共用）。 */
export function buildKeyParams(key: PayslipKey): Record<string, string> {
  const params: Record<string, string> = {
    connectionGuid: key.connectionGuid,
    employeeUid: key.employeeUid,
    paymentDate: toDateOnly(key.paymentDate) || key.paymentDate,
  };
  const periodStart = key.periodStart ? toDateOnly(key.periodStart) || key.periodStart : "";
  if (periodStart) params.periodStart = periodStart;
  return params;
}

/** 列表查询参数：空筛选项不传，onlyUnassigned / refresh 只在为 true 时传。 */
export function buildListParams(query: PayslipListQuery): Record<string, string | boolean> {
  const params: Record<string, string | boolean> = { from: query.from, to: query.to };
  if (query.connectionGuid) params.connectionGuid = query.connectionGuid;
  if (query.storeCode) params.storeCode = query.storeCode;
  const keyword = query.keyword.trim();
  if (keyword) params.keyword = keyword;
  if (query.onlyUnassigned) params.onlyUnassigned = true;
  if (query.refresh) params.refresh = true;
  return params;
}

function toBytes(data: unknown): Uint8Array | null {
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  return null;
}

function isPdf(bytes: Uint8Array) {
  return bytes.length >= 5 && String.fromCharCode(bytes[0], bytes[1], bytes[2], bytes[3]) === "%PDF";
}

/** 文件名里不允许的字符统一去掉，空白折成短横线：ALEX MORGAN → ALEX-MORGAN。 */
export function buildPayslipFileName(employeeName: string, paymentDate: string): string {
  const safeName =
    employeeName
      .trim()
      .replace(/[\\/:*?"<>|\u0000-\u001f.]/g, "")
      .replace(/\s+/g, "-") || "Employee";
  const date = toDateOnly(paymentDate) || "date";
  return `Payslip_${safeName}_${date}.pdf`;
}

export function createPayslipsApi(http: PayslipsHttp) {
  return {
    async fetchCompanies(signal?: AbortSignal): Promise<PayslipCompany[]> {
      const response = await http.get(`${PAYSLIPS_BASE_PATH}/companies`, { signal, headers: NO_STORE_HEADERS });
      return normalizeCompanies(unwrapApiEnvelope<unknown>(response.data));
    },

    async fetchList(query: PayslipListQuery, signal?: AbortSignal): Promise<PayslipListResult> {
      const response = await http.get(PAYSLIPS_BASE_PATH, {
        params: buildListParams(query),
        signal,
        headers: NO_STORE_HEADERS,
      });
      return normalizeList(unwrapApiEnvelope<unknown>(response.data));
    },

    async fetchDetail(key: PayslipKey, signal?: AbortSignal): Promise<PayslipDetail> {
      const response = await http.get(`${PAYSLIPS_BASE_PATH}/detail`, {
        params: buildKeyParams(key),
        signal,
        headers: NO_STORE_HEADERS,
      });
      // 响应不带 employeeUid，用请求的 key 补齐，后续下载 / 发信都用它定位。
      return { ...normalizeDetail(unwrapApiEnvelope<unknown>(response.data)), employeeUid: key.employeeUid };
    },

    /** 「我方考勤工时」：点开才请求，只供参考，不进 PDF / 邮件。 */
    async fetchHours(key: PayslipKey, signal?: AbortSignal): Promise<PayslipHours> {
      const response = await http.get(`${PAYSLIPS_BASE_PATH}/hours`, {
        params: buildKeyParams(key),
        signal,
        headers: NO_STORE_HEADERS,
      });
      return normalizeHours(unwrapApiEnvelope<unknown>(response.data));
    },

    async fetchEmailTarget(key: PayslipKey, signal?: AbortSignal): Promise<PayslipEmailTarget> {
      const response = await http.get(`${PAYSLIPS_BASE_PATH}/email-target`, {
        params: buildKeyParams(key),
        signal,
        headers: NO_STORE_HEADERS,
      });
      return normalizeEmailTarget(unwrapApiEnvelope<unknown>(response.data));
    },

    async sendEmail(request: PayslipEmailRequest): Promise<PayslipEmailResult> {
      const response = await http.post(`${PAYSLIPS_BASE_PATH}/email`, request, { headers: NO_STORE_HEADERS });
      return normalizeEmailResult(unwrapApiEnvelope<unknown>(response.data));
    },

    /**
     * 下载 PDF（带登录态，不能用裸 fetch）。返回 base64，由调用方写缓存文件。
     * 无权限时后端回 JSON 错误（403 MYOB_PAYSLIP_FORBIDDEN），axios 抛出的错误里 response.data 是二进制，
     * 由 errors.ts 解码取错误码；200 却不是 PDF（例如被网关改写）按 PAYSLIP_PDF_INVALID 处理。
     */
    async fetchPdfBase64(key: PayslipKey, signal?: AbortSignal): Promise<string> {
      const response = await http.get(`${PAYSLIPS_BASE_PATH}/pdf`, {
        params: buildKeyParams(key),
        responseType: "arraybuffer",
        signal,
        headers: { ...NO_STORE_HEADERS, Accept: "application/pdf, application/json" },
      });
      const bytes = toBytes(response.data);
      if (!bytes || !isPdf(bytes)) {
        throw Object.assign(new Error("Payslip PDF invalid"), { code: "PAYSLIP_PDF_INVALID" });
      }
      return fromByteArray(bytes);
    },
  };
}

export type PayslipsApi = ReturnType<typeof createPayslipsApi>;
