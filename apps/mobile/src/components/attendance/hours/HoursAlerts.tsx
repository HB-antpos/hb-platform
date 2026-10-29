// 列表页顶部提醒条：未成年生日核对时薪 / 生日（粉）、待审没算进去（黄，去审核）、带薪公假待确认（黄，去审核）、
// 其他分店有打卡（蓝，合并查看开关；合并只是查看参数，不改数据）。
// 移动端没有时薪表页，生日提醒只显示文字，不放跳转。
import type { ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { formatShortDate } from "@/modules/attendance/payroll-hours";
import type { PayrollHoursAlert, PayrollHoursEmployee } from "@/modules/attendance/payroll-hours-types";
import { listSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HoursAlertBar } from "./hours-ui";

export function HoursAlerts({
  alerts,
  employees,
  pendingCount,
  mergeStores,
  onToggleMerge,
  onOpenApprovals,
}: {
  alerts: PayrollHoursAlert[];
  employees: PayrollHoursEmployee[];
  /** 后端没给 Pending 提醒时用合计的待审数兜底。 */
  pendingCount: number;
  mergeStores: boolean;
  onToggleMerge: (next: boolean) => void;
  onOpenApprovals?: () => void;
}) {
  const { t, language } = useAppTranslation("attendanceHours");
  const nameOf = (alert: PayrollHoursAlert) =>
    alert.employeeName ?? employees.find((employee) => employee.userGuid === alert.userGuid)?.name ?? "";
  const reviewAction = onOpenApprovals ? { label: t("alerts.review"), onPress: onOpenApprovals } : undefined;

  const bars: ReactNode[] = [];
  for (const [index, alert] of alerts.entries()) {
    if (alert.type === "MinorBirthday" || alert.type === "Birthday") {
      const date = alert.date ? formatShortDate(alert.date, language) : "";
      const text = alert.type === "MinorBirthday" && alert.ageYears !== undefined
        ? t("alerts.minorBirthday", { name: nameOf(alert), date, age: alert.ageYears })
        : t("alerts.birthday", { name: nameOf(alert), date });
      bars.push(<HoursAlertBar key={`birthday-${index}`} tone="pink" icon="cake-variant-outline" text={text} />);
    }
  }

  const pendingAlert = alerts.find((alert) => alert.type === "Pending");
  const pendingShown = pendingAlert?.count ?? pendingCount;
  if (pendingShown > 0) {
    bars.push(<HoursAlertBar key="pending" tone="warning" icon="alert-outline" text={t("alerts.pending", { count: pendingShown })} action={reviewAction} />);
  }

  const holidayAlert = alerts.find((alert) => alert.type === "PublicHolidayPay");
  if (holidayAlert && (holidayAlert.count ?? 1) > 0) {
    bars.push(
      <HoursAlertBar
        key="holiday"
        tone="warning"
        icon="calendar-star"
        text={t("alerts.publicHolidayPay", { count: holidayAlert.count ?? 1 })}
        action={reviewAction}
      />,
    );
  }

  // 其他分店：多人合成一条；已合并时改为「已合并」说明 + 「只看本店」。
  if (mergeStores) {
    bars.push(<HoursAlertBar key="merged" tone="info" icon="store-outline" text={t("messages.merged")} action={{ label: t("alerts.unmerge"), onPress: () => onToggleMerge(false) }} />);
  } else {
    const otherStoreAlerts = alerts.filter((alert) => alert.type === "OtherStores");
    if (otherStoreAlerts.length > 0) {
      const names = Array.from(new Set(otherStoreAlerts.map(nameOf).filter(Boolean)));
      const text = names.length > 0 ? t("alerts.otherStores", { name: names.join(listSeparator(language)) }) : t("alerts.otherStoresAnonymous");
      bars.push(<HoursAlertBar key="other-stores" tone="info" icon="store-outline" text={text} action={{ label: t("alerts.merge"), onPress: () => onToggleMerge(true) }} />);
    }
  }

  // 后端新增、前端还不认识的提醒：有 message 就原样显示。
  for (const [index, alert] of alerts.entries()) {
    if (["MinorBirthday", "Birthday", "Pending", "PublicHolidayPay", "OtherStores"].includes(alert.type)) continue;
    if (alert.message) bars.push(<HoursAlertBar key={`other-${index}`} tone="neutral" icon="information-outline" text={alert.message} />);
  }

  if (bars.length === 0) return null;
  return <View style={styles.list}>{bars}</View>;
}

const styles = StyleSheet.create({
  list: { gap: 8 },
});
