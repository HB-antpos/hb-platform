// 详情页的纯展示逻辑：路由参数 → 工资单定位键、「我方考勤工时」展示行。
import { formatDate, formatHours, toDateOnly } from "./format";
import type { PayslipHours, PayslipKey } from "./types";

function firstParam(value: string | string[] | undefined): string {
  const raw = Array.isArray(value) ? value[0] : value;
  return typeof raw === "string" ? raw.trim() : "";
}

/** 路由参数 → 定位键；缺少必要参数（公司 / 员工 / 支付日）返回 null，页面显示「参数不完整」。 */
export function parsePayslipKeyParams(params: Record<string, string | string[] | undefined>): PayslipKey | null {
  const connectionGuid = firstParam(params.connectionGuid);
  const employeeUid = firstParam(params.employeeUid);
  const paymentDate = toDateOnly(firstParam(params.paymentDate));
  if (!connectionGuid || !employeeUid || !paymentDate) return null;
  return { connectionGuid, employeeUid, paymentDate, periodStart: toDateOnly(firstParam(params.periodStart)) || null };
}

export interface HoursRowView {
  key: string;
  label: string;
  failed: boolean;
  workText: string;
  leaveText: string;
}

export interface HoursView {
  periodFrom: string;
  periodTo: string;
  rows: HoursRowView[];
  totalWorkText: string;
  totalLeaveText: string;
}

/** 我方考勤工时 → 展示行；读取失败的分店标记 failed，不显示数字。 */
export function buildHoursView(hours: PayslipHours): HoursView {
  return {
    periodFrom: formatDate(hours.periodStart),
    periodTo: formatDate(hours.periodEnd),
    rows: hours.stores.map((store) => ({
      key: store.storeCode,
      label: store.storeLabel,
      failed: store.failed,
      workText: formatHours(store.workHours),
      leaveText: formatHours(store.leaveHours),
    })),
    totalWorkText: formatHours(hours.totalWorkHours),
    totalLeaveText: formatHours(hours.totalLeaveHours),
  };
}
