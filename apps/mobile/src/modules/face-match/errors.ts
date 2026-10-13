// 人脸比对 / 摄像头接口错误的提示解析。
// 注意：apiClient 的 preserveApiClientError 会把 error.message 改写成 response.data.details 里所有字符串拼起来的文本
// （原本为展示校验字典），而人脸比对的 409 闸门报告、400 字段错误的 details 是结构化对象，拼出来是一串字段键。
// 所以这里一律按「业务错误码 → HTTP 状态码 → 通用兜底」解析，不使用 error.message，也不把 details 原文展示给用户。
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { normalizeFaceGateReport } from "./normalize";
import type { FaceGateReport } from "./types";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

// ───────────── 响应体解码（图片接口的失败响应也是二进制） ─────────────

/**
 * 解码很短的 JSON 错误体：优先 TextDecoder；缺失时（Hermes 旧版本）按 ASCII 逐字节解码，非 ASCII 字节换成替换符。
 * 这里只会读取 errorCode / details.field 等纯 ASCII 标识符，中文 message 从不使用，所以兜底解码不影响结果；
 * 0x80 以上的字节不可能是 JSON 的引号或反斜杠，替换后字符串边界不会被破坏。
 */
function decodeBytes(bytes: Uint8Array): string {
  if (typeof TextDecoder !== "undefined") {
    try {
      return new TextDecoder("utf-8").decode(bytes);
    } catch {
      // 继续走 ASCII 兜底
    }
  }
  let result = "";
  for (let index = 0; index < bytes.length; index += 1) {
    result += bytes[index] < 0x80 ? String.fromCharCode(bytes[index]) : "�";
  }
  return result;
}

/**
 * responseType 为 arraybuffer 时，失败响应体也是二进制：解码成 JSON 对象，让错误码解析能读到 errorCode / details。
 * 已经是对象就原样返回；不是合法 JSON 对象返回 null。绝不会把图片字节当文本解析成功（图片不是合法 JSON）。
 */
export function decodeBinaryJsonBody(data: unknown): Record<string, unknown> | null {
  let text: string | null = null;
  if (data instanceof ArrayBuffer) {
    text = decodeBytes(new Uint8Array(data));
  } else if (ArrayBuffer.isView(data)) {
    text = decodeBytes(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
  } else if (typeof data === "string") {
    text = data;
  } else {
    return asRecord(data);
  }
  if (!text || !text.trim()) return null;
  try {
    return asRecord(JSON.parse(text));
  } catch {
    return null;
  }
}

// ───────────── 错误码提取 ─────────────

function responseBody(error: unknown): Record<string, unknown> | null {
  const response = asRecord(asRecord(error)?.response);
  if (!response) return null;
  return decodeBinaryJsonBody(response.data);
}

/**
 * 取业务错误码：
 * - HTTP 4xx/5xx 走 axios 错误，错误码在 response.data.errorCode（或 code）；
 * - success=false 的 200 响应由 unwrapApiEnvelope 抛出，错误码在 error.code。
 * 只读错误码，不读取响应体的其他内容。
 */
export function extractFaceMatchErrorCode(error: unknown): string | null {
  const body = responseBody(error);
  const fromBody = body?.errorCode ?? body?.ErrorCode ?? body?.code ?? body?.Code;
  if (typeof fromBody === "string" && fromBody.trim()) return fromBody.trim();
  const record = asRecord(error);
  const direct = record?.errorCode ?? record?.code;
  return typeof direct === "string" && direct.trim() ? direct.trim() : null;
}

export function extractHttpStatus(error: unknown): number | null {
  const status = asRecord(asRecord(error)?.response)?.status;
  return typeof status === "number" ? status : null;
}

function extractDetails(error: unknown): Record<string, unknown> | null {
  const details = responseBody(error)?.details ?? responseBody(error)?.Details;
  return asRecord(details);
}

// ───────────── 业务错误码 → 文案键（faceMatch 命名空间） ─────────────

/** 页面已知的业务错误码（FACE_MATCH_* / FACE_CAMERA_*）；后端新增的码不认识时按 HTTP 状态码给通用说明。 */
export const FACE_MATCH_ERROR_CODES = [
  "FACE_MATCH_DISABLED",
  "FACE_MATCH_STORE_NOT_CONFIGURED",
  "FACE_MATCH_FORBIDDEN",
  "FACE_MATCH_STORE_FORBIDDEN",
  "FACE_MATCH_STORE_INVALID",
  "FACE_MATCH_STORE_NOT_FOUND",
  "FACE_MATCH_FIELD_INVALID",
  "FACE_MATCH_NOT_FOUND",
  "FACE_MATCH_GATE_NOT_MET",
  "FACE_MATCH_PHOTO_UNUSABLE",
  "FACE_MATCH_TENCENT_AUTH",
  "FACE_MATCH_TENCENT_BUSY",
  "FACE_MATCH_TENCENT_LIMIT",
  "FACE_MATCH_TENCENT_CONFLICT",
  "FACE_MATCH_TENCENT_UNAVAILABLE",
  "FACE_MATCH_TENCENT_ERROR",
  "FACE_MATCH_ENTRY_NOT_PENDING",
  "FACE_MATCH_APPROVER_IS_SUBMITTER",
  "FACE_MATCH_ALREADY_APPROVED_BY_YOU",
  "FACE_MATCH_ENTRY_EXPIRED",
  "FACE_MATCH_UNWELCOME_NOT_ENABLED",
  "FACE_MATCH_ENTRY_CHANGED",
  "FACE_MATCH_TASK_CLOSED",
  "FACE_MATCH_SAME_REVIEWER",
  "FACE_MATCH_TASK_CHANGED",
  "FACE_MATCH_IMAGE_GONE",
  "FACE_MATCH_IMAGE_TOO_LARGE",
  "FACE_MATCH_IMAGE_UNSUPPORTED",
  "FACE_MATCH_STORE_DENIED",
  "FACE_MATCH_STORE_TIMEOUT",
  "FACE_MATCH_STORE_ERROR",
  "FACE_MATCH_AUDIT_FAILED",
  "FACE_MATCH_TIMEZONE_UNAVAILABLE",
  "FACE_CAMERA_ID_INVALID",
  "FACE_CAMERA_STORE_CODE_INVALID",
  "FACE_CAMERA_STORE_NOT_FOUND",
  "FACE_CAMERA_FIELD_INVALID",
  "FACE_CAMERA_NOT_FOUND",
] as const;

export type FaceMatchErrorCode = (typeof FACE_MATCH_ERROR_CODES)[number];

export function isKnownFaceMatchErrorCode(code: string | null): code is FaceMatchErrorCode {
  return code != null && (FACE_MATCH_ERROR_CODES as readonly string[]).includes(code);
}

/** 按 HTTP 状态码的兜底说明（错误码不认识时）。 */
export const STATUS_ERROR_CODES = [401, 403, 404, 409, 413, 503] as const;

export function faceMatchErrorCodeKey(code: FaceMatchErrorCode): string {
  return `errors.codes.${code}`;
}

/** 错误 → 文案键：已知错误码优先，其次 HTTP 状态码，最后通用文案。 */
export function classifyFaceMatchError(error: unknown): string {
  const code = extractFaceMatchErrorCode(error);
  if (isKnownFaceMatchErrorCode(code)) return faceMatchErrorCodeKey(code);
  const status = extractHttpStatus(error);
  if (status !== null && (STATUS_ERROR_CODES as readonly number[]).includes(status)) return `errors.status.${status}`;
  return "errors.generic";
}

/** 字段名只接受普通标识符（后端 details.field），别的内容一律不展示。 */
const FIELD_NAME_PATTERN = /^[A-Za-z][A-Za-z0-9_.]{0,63}$/;

/** 功能未开启的具体原因（details.reason）→ 文案键。 */
export const DISABLED_REASON_KEYS: Record<string, string> = {
  "region-missing": "disabled.reasons.regionMissing",
  "region-not-allowed": "disabled.reasons.regionNotAllowed",
  "credentials-missing": "disabled.reasons.credentialsMissing",
  "credentials-invalid": "disabled.reasons.credentialsInvalid",
};
export const DISABLED_REASON_FALLBACK_KEY = "disabled.reasons.unknown";

export function getDisabledReasonKey(reason: string | null | undefined): string {
  return (reason && DISABLED_REASON_KEYS[reason]) || DISABLED_REASON_FALLBACK_KEY;
}

/**
 * 错误 → 面向用户的文案（faceMatch 命名空间下的 t）：
 * - 已知业务错误码用对应文案；FACE_MATCH_DISABLED 追加具体原因，FIELD_INVALID 追加「字段：xxx」帮助定位；
 * - 网络、超时、5xx、登录失效等交给通用本地化解析；
 * - 后端的 message / details 文本从不原样展示。
 */
export function describeFaceMatchError(t: TranslateFn, language: string, error: unknown): string {
  const code = extractFaceMatchErrorCode(error);
  if (isKnownFaceMatchErrorCode(code)) {
    const base = t(faceMatchErrorCodeKey(code));
    const details = extractDetails(error);
    if (code === "FACE_MATCH_DISABLED") {
      const reason = details?.reason;
      const reasonKey = typeof reason === "string" ? DISABLED_REASON_KEYS[reason] : undefined;
      return reasonKey ? `${base} ${t(reasonKey)}` : base;
    }
    if (code === "FACE_MATCH_FIELD_INVALID") {
      const field = details?.field;
      return typeof field === "string" && FIELD_NAME_PATTERN.test(field)
        ? `${base}${t("errors.fieldSuffix", { field })}`
        : base;
    }
    return base;
  }
  const status = extractHttpStatus(error);
  if (status !== null && (STATUS_ERROR_CODES as readonly number[]).includes(status)) {
    return t(`errors.status.${status}`);
  }
  return resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "errors.generic", allowRawMessageInChinese: false });
}

/** 保存档案被闸门拒绝（409 FACE_MATCH_GATE_NOT_MET）时，取 details 里的闸门报告；否则 null。 */
export function getGateReportFromError(error: unknown): FaceGateReport | null {
  if (extractFaceMatchErrorCode(error) !== "FACE_MATCH_GATE_NOT_MET") return null;
  const details = extractDetails(error);
  return details ? normalizeFaceGateReport(details) : null;
}

export function isForbiddenError(error: unknown): boolean {
  return extractHttpStatus(error) === 403;
}

// ───────────── 复核图片读取失败的分类 ─────────────

/** 页面上区分展示的几类失败：未配置 / 无权限 / 登录过期 / 图片已清理 / 记录不存在 / 其余读取失败。 */
export type ReviewImageErrorKind = "notConfigured" | "forbidden" | "unauthorized" | "gone" | "missing" | "failed";

/** 只看错误码与 HTTP 状态码，不读取、不返回响应体的其他内容。 */
export function classifyReviewImageError(error: unknown): ReviewImageErrorKind {
  const code = extractFaceMatchErrorCode(error);
  if (code === "FACE_MATCH_STORE_NOT_CONFIGURED" || code === "FACE_MATCH_DISABLED") return "notConfigured";
  if (code === "FACE_MATCH_FORBIDDEN" || code === "FACE_MATCH_STORE_FORBIDDEN") return "forbidden";
  if (code === "FACE_MATCH_IMAGE_GONE") return "gone";
  if (code === "FACE_MATCH_NOT_FOUND") return "missing";
  const status = extractHttpStatus(error);
  if (status === 403) return "forbidden";
  if (status === 401) return "unauthorized";
  if (status === 410) return "gone";
  if (status === 404) return "missing";
  return "failed";
}

/** 这几类失败对同一任务的其余图片也必然成立，不必再逐张请求（单张被清理 / 不存在不算：其余图片可能还在）。 */
export function isSharedImageFailure(kind: ReviewImageErrorKind): boolean {
  return kind === "notConfigured" || kind === "forbidden" || kind === "unauthorized";
}
