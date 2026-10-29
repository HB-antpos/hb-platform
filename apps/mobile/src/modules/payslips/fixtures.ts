// 虚构样例数据（与设计稿一致：ALEX MORGAN / HOT BARGAIN KAWANA PTY LTD / ABN 12 345 678 901），
// 只给单测和本地目测用，不是真实员工数据。响应形状 = 后端契约 ApiResponse<T>.data。
import type { PayslipDetail, PayslipListResult } from "./types";

export const FIXTURE_COMPANY_GUID = "11111111-1111-1111-1111-111111111111";

export const FIXTURE_COMPANIES = [
  { connectionGuid: FIXTURE_COMPANY_GUID, connectionName: "HOT BARGAIN KAWANA PTY LTD" },
  { connectionGuid: "22222222-2222-2222-2222-222222222222", connectionName: "HOT BARGAIN BANKSTOWN PTY LTD" },
];

const KAWANA = [{ storeCode: "1014", storeLabel: "1014 Kawana" }];

function row(name: string, displayId: string, employeeUid: string, gross: number, net: number, extra: Record<string, unknown> = {}) {
  return {
    connectionGuid: FIXTURE_COMPANY_GUID,
    companyName: "HOT BARGAIN KAWANA PTY LTD",
    employeeUid,
    employeeName: name,
    displayId,
    userGuid: null,
    paymentDate: "2026-10-29",
    periodStart: "2026-10-19",
    periodEnd: "2026-10-25",
    payFrequency: "Weekly",
    grossPay: gross,
    taxTotal: gross - net,
    netPay: net,
    placement: "Attendance",
    stores: KAWANA,
    ...extra,
  };
}

/** 列表响应（未经规整的原始 JSON 形状）。 */
export const FIXTURE_LIST_RESPONSE = {
  from: "2026-08-01",
  to: "2026-10-29",
  companies: [
    { connectionGuid: FIXTURE_COMPANY_GUID, connectionName: "HOT BARGAIN KAWANA PTY LTD", ok: true, count: 5, errorCode: null, errorMessage: null },
  ],
  rows: [
    row("ALEX MORGAN", "KAWANA07", "uid-1", 900, 762),
    row("JORDAN LEE", "KAWANA11", "uid-2", 812, 684.2),
    row("SAM TAYLOR", "KAWANA15", "uid-3", 229.46, 229.46, { payFrequency: "Fortnightly" }),
    row("CASEY WONG", "KAWANA21", "uid-4", 640, 540.1, { placement: "Unbound", stores: [] }),
    row("RILEY BROWN", "KAWANA26", "uid-5", 455, 390),
  ],
  totalCount: 5,
  employeeCount: 5,
  grossTotal: 3036.46,
  netTotal: 2605.76,
  unassignedCount: 1,
  canViewAllStores: true,
  fetchedAt: "2026-10-29T08:00:00Z",
};

/** 详情响应（未经规整的原始 JSON 形状）：MYOB 原始顺序的扁平明细。 */
export const FIXTURE_DETAIL_RESPONSE = {
  connectionGuid: FIXTURE_COMPANY_GUID,
  companyName: "HOT BARGAIN KAWANA PTY LTD",
  companyAbn: "12345678901",
  employeeName: "ALEX MORGAN",
  displayId: "KAWANA07",
  userGuid: null,
  paymentDate: "2026-10-29",
  periodStart: "2026-10-19",
  periodEnd: "2026-10-25",
  payFrequency: "Weekly",
  grossPay: 900,
  netPay: 762,
  superFundName: "SAMPLE SUPER",
  placement: "Attendance",
  stores: KAWANA,
  hourlyRate: 24,
  referenceNumber: "1263",
  lines: [
    { description: "Base Hourly", type: "Wages", typeLabel: "Wages", hours: 30, rate: 24, amount: 720, yearToDate: 11520 },
    { description: "SATURDAY (x1.25)", type: "Wages", typeLabel: "Wages", hours: 6, rate: 30, amount: 180, yearToDate: 2160 },
    { description: "PAYG Withholding", type: "Tax", typeLabel: "Tax", hours: null, rate: null, amount: -138, yearToDate: -2208 },
    { description: "Superannuation Guarantee", type: "Super", typeLabel: "Super", hours: null, rate: null, amount: 108, yearToDate: 1641.6 },
    { description: "Annual Leave Accrual", type: "Entitlement", typeLabel: "Entitlement", hours: 1.15, rate: null, amount: null, yearToDate: 18.45 },
  ],
  samePaymentDateCount: 1,
  canDownloadPdf: true,
  canSendEmail: true,
};

export const FIXTURE_HOURS_RESPONSE = {
  periodStart: "2026-10-19",
  periodEnd: "2026-10-25",
  stores: [{ storeCode: "1014", storeLabel: "1014 Kawana", workHours: 36, leaveHours: 0, failed: false }],
  totalWorkHours: 36,
  totalLeaveHours: 0,
};

export const FIXTURE_EMAIL_TARGET_RESPONSE = {
  email: "alex.morgan@example.com",
  maskedEmail: "a***@example.com",
  source: "profile",
  subject: "Payslip 19/10/2026 - 25/10/2026",
  filename: "Payslip_ALEX-MORGAN_2026-10-29.pdf",
  myEmail: "manager@example.com",
};

export type FixtureList = PayslipListResult;
export type FixtureDetail = PayslipDetail;
