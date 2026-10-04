import { apiClient } from "@/shared/api/client";

export interface MinorReminder {
  id: number;
  employeeName?: string;
  userGUID: string;
  storeCode: string;
  message: string;
  ruleCategory: string;
  sourceUrl?: string;
  workDate?: string;
  actualMinutes?: number;
  limitMinutes?: number;
  status: "open" | "acknowledged" | "escalated" | "resolved";
  revision: number;
  actionComment?: string;
  actionActor?: string;
}
export interface ReminderPage {
  items: MinorReminder[];
  total: number;
  page: number;
  pageSize: number;
}
const BASE = "/react/attendance/minor-reminders";
export async function getMinorReminders(
  status: string,
  page: number,
  storeCode?: string,
) {
  return (
    await apiClient.get<ReminderPage>(BASE, {
      params: { status: status || undefined, page, pageSize: 30, storeCode },
    })
  ).data;
}
export async function actOnMinorReminder(
  row: MinorReminder,
  action: "acknowledge" | "escalate",
  comment: string,
) {
  return (
    await apiClient.post<MinorReminder>(`${BASE}/${row.id}/action`, {
      expectedRevision: row.revision,
      action,
      comment: comment.trim() || null,
    })
  ).data;
}
