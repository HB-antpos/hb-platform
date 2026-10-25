// 带薪假申请页用到的接口：读取请假规则与可选日（my/leave-options），以及对原有请假接口的薄包装。
// 原 api.ts 的函数不改。
import { apiClient } from "@/shared/api/client";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { reviewAwareFetch } from "@/modules/ios-review/network";
import { createLeaveRequest, getAttendanceLeaveAttachmentUploadSignature, getMyLeaveRequests } from "./api";
import { uploadAttendanceLeaveAttachmentToSignedUrl } from "./leave-attachment-upload";
import { localDateString, roundHours } from "./leave-rules";
import {
  DEFAULT_ATTENDANCE_LEAVE_RULES,
  type AttendanceLeaveRules,
  type AttendanceMyLeaveOptions,
  type AttendancePublicHolidayDate,
  type AttendanceSickLeaveCandidate,
} from "./leave-types";
import {
  asBoolean,
  asDateOnly,
  asNumber,
  asOptionalNumber,
  asOptionalString,
  asRecordArray,
  asString,
  asStringArray,
  isRecord,
  pick,
  type RawRecord,
} from "./raw-fields";
import type { AttendanceLeaveRequest, AttendanceLeaveRequestPayload } from "./types";

const ATTENDANCE_BASE = "/react/v1/attendance";

export const LEAVE_OPTIONS_QUERY_KEY = ["attendance", "my", "leave-options"] as const;
export const MY_LEAVE_REQUESTS_QUERY_KEY = ["attendance", "my", "leave-requests"] as const;

/** 规则参数：缺字段或非正数时用默认值（规格第 1 节），避免后端半成品把规则算成 0。 */
function normalizeRules(raw: unknown): AttendanceLeaveRules {
  const source: RawRecord = isRecord(raw) ? raw : {};
  const read = (key: keyof AttendanceLeaveRules) => {
    const pascal = key.charAt(0).toUpperCase() + key.slice(1);
    const value = asOptionalNumber(pick(source, key, pascal));
    return value !== undefined && value > 0 ? value : DEFAULT_ATTENDANCE_LEAVE_RULES[key];
  };
  return {
    sickLeaveSubmitDays: read("sickLeaveSubmitDays"),
    annualLeaveLeadWeeks: read("annualLeaveLeadWeeks"),
    annualLeaveMaxWeeks: read("annualLeaveMaxWeeks"),
    annualCashOutLeadDays: read("annualCashOutLeadDays"),
    annualCashOutMaxWeeks: read("annualCashOutMaxWeeks"),
    annualLeaveHoursPerWorkday: read("annualLeaveHoursPerWorkday"),
    maxCertificatePhotos: read("maxCertificatePhotos"),
  };
}

function normalizeHoliday(raw: unknown): AttendancePublicHolidayDate | null {
  // 兼容后端直接给日期字符串数组。
  if (typeof raw === "string") return raw ? { date: asDateOnly(raw) } : null;
  if (!isRecord(raw)) return null;
  const date = asDateOnly(pick(raw, "date", "Date", "holidayDate", "HolidayDate"));
  return date ? { date, name: asOptionalString(pick(raw, "name", "Name", "holidayName", "HolidayName")) } : null;
}

function normalizeCandidate(raw: RawRecord): AttendanceSickLeaveCandidate {
  return {
    scheduleGuid: asString(pick(raw, "scheduleGuid", "ScheduleGuid")),
    workDate: asDateOnly(pick(raw, "workDate", "WorkDate")),
    startTime: asString(pick(raw, "startTime", "StartTime")).slice(0, 5),
    endTime: asString(pick(raw, "endTime", "EndTime")).slice(0, 5),
    paidHours: roundHours(asNumber(pick(raw, "paidHours", "PaidHours"))),
    hasPunch: asBoolean(pick(raw, "hasPunch", "HasPunch")),
  };
}

/** 归一化 my/leave-options；兼容 camelCase / PascalCase，以及没拆信封的 { data: {...} }。 */
export function normalizeMyLeaveOptions(payload: unknown): AttendanceMyLeaveOptions {
  let raw: RawRecord = isRecord(payload) ? payload : {};
  const inner = pick(raw, "data", "Data");
  if (isRecord(inner) && pick(raw, "today", "Today") === undefined) raw = inner;
  const holidaysRaw = pick(raw, "publicHolidays", "PublicHolidays");
  return {
    today: asDateOnly(pick(raw, "today", "Today")) || localDateString(),
    storeCode: asOptionalString(pick(raw, "storeCode", "StoreCode")),
    storeName: asOptionalString(pick(raw, "storeName", "StoreName")),
    employeeName: asOptionalString(pick(raw, "employeeName", "EmployeeName", "fullName", "FullName")),
    employmentType: asOptionalString(pick(raw, "employmentType", "EmploymentType")),
    // 缺字段时按「不能申请」处理：宁可少显示带薪卡，也不让临时工误提交。
    paidLeaveEligible: asBoolean(pick(raw, "paidLeaveEligible", "PaidLeaveEligible")),
    rules: normalizeRules(pick(raw, "rules", "Rules")),
    publicHolidays: (Array.isArray(holidaysRaw) ? holidaysRaw : [])
      .map(normalizeHoliday)
      .filter((item): item is AttendancePublicHolidayDate => item !== null),
    publishedScheduleDates: asStringArray(pick(raw, "publishedScheduleDates", "PublishedScheduleDates")).map(asDateOnly),
    sickLeaveCandidates: asRecordArray(pick(raw, "sickLeaveCandidates", "SickLeaveCandidates"))
      .map(normalizeCandidate)
      .filter((candidate) => candidate.scheduleGuid && candidate.workDate),
  };
}

/** GET my/leave-options?storeCode：员工本人的请假规则、用工类型与病假可选排班。 */
export async function getMyLeaveOptions(storeCode?: string): Promise<AttendanceMyLeaveOptions> {
  const response = await apiClient.get(`${ATTENDANCE_BASE}/my/leave-options`, { params: { storeCode } });
  return normalizeMyLeaveOptions(response.data);
}

/** 本人请假记录（复用 api.ts）。 */
export async function getMyLeaveRequestsForLeaveHome(): Promise<AttendanceLeaveRequest[]> {
  return getMyLeaveRequests();
}

/** 提交请假（复用 api.ts）。 */
export async function submitMyLeaveRequest(payload: AttendanceLeaveRequestPayload): Promise<AttendanceLeaveRequest> {
  return createLeaveRequest(payload);
}

/**
 * 上传一张医嘱照片，返回对象地址（提交时放进 attachmentUrls）。
 * iOS 审核模式不申请签名，直接用本地 URI 当预览地址。
 */
export async function uploadLeaveCertificatePhoto(uri: string, mimeType?: string): Promise<string> {
  if (isIosReviewSessionActive()) return uri;
  const contentType = mimeType === "image/png" ? "image/png" : "image/jpeg";
  // 先读一次本地文件拿到大小，签名接口按大小校验。
  const fileResponse = await reviewAwareFetch(uri);
  const blob = await fileResponse.blob();
  if (!blob.size) throw new Error("empty file");
  const signature = await getAttendanceLeaveAttachmentUploadSignature({
    fileName: `leave-certificate-${Date.now()}.${contentType === "image/png" ? "png" : "jpg"}`,
    contentType,
    fileSize: blob.size,
  });
  const result = await uploadAttendanceLeaveAttachmentToSignedUrl(uri, signature);
  return result.downloadUrl;
}
