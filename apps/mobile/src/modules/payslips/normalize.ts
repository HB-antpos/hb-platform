// 工资单接口响应的规整：字段缺失 / 类型不对时给安全默认值，页面与测试都只面对规整后的类型。
import { toDateOnly } from "./format";
import type {
  PayslipCompany,
  PayslipCompanyStatus,
  PayslipDetail,
  PayslipEmailResult,
  PayslipEmailTarget,
  PayslipHours,
  PayslipLine,
  PayslipListResult,
  PayslipListRow,
  PayslipPlacement,
  PayslipStoreRef,
} from "./types";

type Rec = Record<string, unknown>;

function rec(value: unknown): Rec | null {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? (value as Rec) : null;
}

function str(value: unknown): string {
  return typeof value === "string" ? value.trim() : typeof value === "number" ? String(value) : "";
}

function strOrNull(value: unknown): string | null {
  const text = str(value);
  return text ? text : null;
}

function num(value: unknown): number | null {
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  if (typeof value === "string" && value.trim() !== "") {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function arr(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

const PLACEMENTS: readonly PayslipPlacement[] = ["Attendance", "Inferred", "Unknown", "Unbound"];

export function normalizePlacement(value: unknown): PayslipPlacement {
  return PLACEMENTS.find((item) => item === value) ?? "Unknown";
}

function normalizeStores(value: unknown): PayslipStoreRef[] {
  return arr(value).flatMap((item) => {
    const record = rec(item);
    const storeCode = str(record?.storeCode);
    if (!record || !storeCode) return [];
    return [{ storeCode, storeLabel: str(record.storeLabel) || storeCode }];
  });
}

export function normalizeCompanies(data: unknown): PayslipCompany[] {
  return arr(data).flatMap((item) => {
    const record = rec(item);
    const connectionGuid = str(record?.connectionGuid);
    if (!record || !connectionGuid) return [];
    return [{ connectionGuid, connectionName: str(record.connectionName) || connectionGuid }];
  });
}

function normalizeRow(item: unknown): PayslipListRow | null {
  const record = rec(item);
  if (!record) return null;
  const connectionGuid = str(record.connectionGuid);
  const employeeUid = str(record.employeeUid);
  const paymentDate = toDateOnly(record.paymentDate);
  // 没有这三项无法定位工资单（点进详情会请求失败），整行丢弃。
  if (!connectionGuid || !employeeUid || !paymentDate) return null;
  return {
    connectionGuid,
    employeeUid,
    paymentDate,
    periodStart: toDateOnly(record.periodStart) || null,
    periodEnd: toDateOnly(record.periodEnd) || null,
    companyName: str(record.companyName),
    employeeName: str(record.employeeName),
    displayId: str(record.displayId),
    userGuid: strOrNull(record.userGuid),
    payFrequency: str(record.payFrequency),
    grossPay: num(record.grossPay),
    taxTotal: num(record.taxTotal),
    netPay: num(record.netPay),
    placement: normalizePlacement(record.placement),
    stores: normalizeStores(record.stores),
  };
}

function normalizeCompanyStatus(item: unknown): PayslipCompanyStatus | null {
  const record = rec(item);
  const connectionGuid = str(record?.connectionGuid);
  if (!record || !connectionGuid) return null;
  return {
    connectionGuid,
    connectionName: str(record.connectionName) || connectionGuid,
    ok: record.ok !== false,
    count: num(record.count) ?? 0,
    errorCode: strOrNull(record.errorCode),
    errorMessage: strOrNull(record.errorMessage),
  };
}

export function normalizeList(data: unknown): PayslipListResult {
  const record = rec(data);
  if (!record) throw Object.assign(new Error("Payslip list response invalid"), { code: "PAYSLIP_INVALID_RESPONSE" });
  const rows = arr(record.rows).flatMap((item) => {
    const row = normalizeRow(item);
    return row ? [row] : [];
  });
  return {
    from: toDateOnly(record.from),
    to: toDateOnly(record.to),
    companies: arr(record.companies).flatMap((item) => {
      const status = normalizeCompanyStatus(item);
      return status ? [status] : [];
    }),
    rows,
    totalCount: num(record.totalCount) ?? rows.length,
    employeeCount: num(record.employeeCount) ?? 0,
    grossTotal: num(record.grossTotal) ?? 0,
    netTotal: num(record.netTotal) ?? 0,
    unassignedCount: num(record.unassignedCount) ?? 0,
    canViewAllStores: record.canViewAllStores === true,
    fetchedAt: strOrNull(record.fetchedAt),
  };
}

function normalizeLine(item: unknown): PayslipLine | null {
  const record = rec(item);
  if (!record) return null;
  const type = str(record.type);
  return {
    description: str(record.description),
    type,
    typeLabel: str(record.typeLabel) || type,
    hours: num(record.hours),
    rate: num(record.rate),
    amount: num(record.amount),
    yearToDate: num(record.yearToDate),
  };
}

export function normalizeDetail(data: unknown): PayslipDetail {
  const record = rec(data);
  if (!record || !Array.isArray(record.lines)) {
    throw Object.assign(new Error("Payslip detail response invalid"), { code: "PAYSLIP_INVALID_RESPONSE" });
  }
  return {
    connectionGuid: str(record.connectionGuid),
    // 详情响应不带 employeeUid，由页面用路由参数补上；这里留空占位。
    employeeUid: str(record.employeeUid),
    paymentDate: toDateOnly(record.paymentDate),
    periodStart: toDateOnly(record.periodStart) || null,
    periodEnd: toDateOnly(record.periodEnd) || null,
    companyName: str(record.companyName),
    companyAbn: str(record.companyAbn),
    employeeName: str(record.employeeName),
    displayId: str(record.displayId),
    userGuid: strOrNull(record.userGuid),
    payFrequency: str(record.payFrequency),
    grossPay: num(record.grossPay),
    netPay: num(record.netPay),
    superFundName: str(record.superFundName),
    placement: normalizePlacement(record.placement),
    stores: normalizeStores(record.stores),
    hourlyRate: num(record.hourlyRate),
    referenceNumber: strOrNull(record.referenceNumber),
    lines: arr(record.lines).flatMap((item) => {
      const line = normalizeLine(item);
      return line ? [line] : [];
    }),
    samePaymentDateCount: Math.max(1, num(record.samePaymentDateCount) ?? 1),
    // 没有明确为 true 就不给按钮：宁可少一个入口，也不让无权限的人点了才被拒。
    canDownloadPdf: record.canDownloadPdf === true,
    canSendEmail: record.canSendEmail === true,
  };
}

export function normalizeHours(data: unknown): PayslipHours {
  const record = rec(data);
  if (!record) throw Object.assign(new Error("Payslip hours response invalid"), { code: "PAYSLIP_INVALID_RESPONSE" });
  return {
    periodStart: toDateOnly(record.periodStart) || null,
    periodEnd: toDateOnly(record.periodEnd) || null,
    stores: arr(record.stores).flatMap((item) => {
      const store = rec(item);
      const storeCode = str(store?.storeCode);
      if (!store || !storeCode) return [];
      return [
        {
          storeCode,
          storeLabel: str(store.storeLabel) || storeCode,
          workHours: num(store.workHours),
          leaveHours: num(store.leaveHours),
          failed: store.failed === true,
        },
      ];
    }),
    totalWorkHours: num(record.totalWorkHours),
    totalLeaveHours: num(record.totalLeaveHours),
  };
}

export function normalizeEmailTarget(data: unknown): PayslipEmailTarget {
  const record = rec(data);
  if (!record) throw Object.assign(new Error("Payslip email target invalid"), { code: "PAYSLIP_INVALID_RESPONSE" });
  const email = strOrNull(record.email);
  return {
    email,
    maskedEmail: strOrNull(record.maskedEmail),
    source: record.source === "profile" ? "profile" : "none",
    subject: str(record.subject),
    filename: str(record.filename),
    myEmail: strOrNull(record.myEmail),
  };
}

export function normalizeEmailResult(data: unknown): PayslipEmailResult {
  const record = rec(data);
  return { sentTo: str(record?.sentTo), copiedToMe: record?.copiedToMe === true };
}
