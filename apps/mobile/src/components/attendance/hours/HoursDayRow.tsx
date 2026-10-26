// 单人明细里的一天一行：星期 + 日期、内容（打卡段 / 公假 / 病假 / 待审标签 / 自动下班红字）、右侧小时。
// 没上班的日子（休息）压成细行；不能上班用斜纹细行；待审黄虚线边框；公假行浅橙底、病假行浅红底。
import { memo, type ReactNode } from "react";
import { StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import {
  formatHours,
  formatShortDate,
  isoWeekday,
  pendingLabelKey,
  resolveDayDisplay,
} from "@/modules/attendance/payroll-hours";
import type { PayrollHoursDay } from "@/modules/attendance/payroll-hours-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { Hatch, HOURS_COLORS, MONO } from "./hours-ui";

/** 小方块角标：公 / 病 / 年。 */
function KindBadge({ label, color, background, border }: { label: string; color: string; background: string; border: string }) {
  return (
    <View style={[styles.kindBadge, { backgroundColor: background, borderColor: border }]}>
      <Text style={[styles.kindBadgeText, { color }]}>{label}</Text>
    </View>
  );
}

function PendingTag({ label }: { label: string }) {
  return (
    <View style={styles.pendingTag}>
      <Text style={styles.pendingTagText}>{label}</Text>
    </View>
  );
}

export const HoursDayRow = memo(function HoursDayRow({ day }: { day: PayrollHoursDay }) {
  const { t, language } = useAppTranslation("attendanceHours");
  const display = resolveDayDisplay(day);
  const dateLabel = `${t(`weekday.short${isoWeekday(day.date)}`)} ${formatShortDate(day.date, language)}`;

  // 细行：休息、不能上班（没有待审时）。
  if ((display.kind === "rest" || display.kind === "unavailable") && !display.isPending) {
    const unavailable = display.kind === "unavailable";
    return (
      <View style={[styles.compact, unavailable ? styles.compactUnavailable : styles.compactRest]} accessible accessibilityLabel={`${dateLabel} ${unavailable ? t("detail.unavailable") : t("detail.rest")}`}>
        {unavailable ? <Hatch /> : null}
        <Text style={[styles.date, MONO]}>{dateLabel}</Text>
        <Text style={styles.compactText} numberOfLines={1}>
          {unavailable ? t("detail.unavailable") : t("detail.rest")}
          {day.holidayName ? ` · ${day.holidayName}` : ""}
        </Text>
        {display.isBirthday ? <Text style={styles.emoji}>🎂</Text> : null}
      </View>
    );
  }

  const lines: ReactNode[] = [];
  const tags: ReactNode[] = [];

  // 公假：「公 King's Birthday · 带薪公假 ✓」。
  if (display.isPublicHoliday) {
    const holidayPay = day.publicHolidayPay;
    lines.push(
      <View key="holiday" style={styles.inline}>
        <KindBadge label={t("detail.publicHolidayBadge")} color={HOURS_COLORS.holidayText} background={HB_COLORS.white} border={HOURS_COLORS.holidayBorder} />
        <Text style={styles.text} numberOfLines={2}>
          {day.holidayName ?? t("detail.publicHoliday")}
          {holidayPay?.state === "Confirmed" ? ` · ${t("detail.publicHolidayPay")}` : ""}
        </Text>
        {holidayPay?.state === "Confirmed" ? (
          <View style={styles.check}>
            <MaterialCommunityIcons name="check" size={12} color={HB_COLORS.success} />
          </View>
        ) : null}
      </View>,
    );
    if (holidayPay?.state === "Pending") tags.push(<PendingTag key="holiday-pending" label={t("detail.publicHolidayPayPending")} />);
  }

  // 病假 / 年假休息。
  if (display.kind === "sickLeave" && day.leave) {
    lines.push(
      <View key="sick" style={styles.inline}>
        <KindBadge label={t("detail.sickBadge")} color={HOURS_COLORS.sickText} background={HB_COLORS.white} border={HOURS_COLORS.sickBorder} />
        <Text style={styles.text}>{day.leave.type === "PaidSick" ? t("detail.paidSick") : t("detail.sickLeave")}</Text>
        {day.leave.hasCertificate ? (
          <View style={styles.inline}>
            <MaterialCommunityIcons name="paperclip" size={14} color={HB_COLORS.textSecondary} />
            <Text style={styles.subtle}>{t("detail.certificate")}</Text>
          </View>
        ) : null}
      </View>,
    );
  }
  if (display.kind === "annualLeave" && day.leave) {
    lines.push(
      <View key="annual" style={styles.inline}>
        <KindBadge label={t("detail.annualBadge")} color={HOURS_COLORS.annualText} background={HB_COLORS.white} border={HOURS_COLORS.annualBorder} />
        <Text style={styles.text}>{day.leave.type === "PaidAnnual" ? t("detail.paidAnnual") : t("detail.annualLeave")}</Text>
      </View>,
    );
  }

  // 打卡段：「08:56—13:00 · 13:31—17:30」，自动下班段红字「13:02—00:00 自动」（不计薪）。
  const segments = day.segments ?? [];
  if (segments.length > 0) {
    lines.push(
      <Text key="segments" style={[styles.text, MONO]}>
        {segments.map((segment, index) => (
          <Text key={`${segment.start}-${index}`} style={segment.isAutoClockOut ? styles.auto : null}>
            {index > 0 ? " · " : ""}
            {segment.start}—{segment.end ?? "?"}
            {segment.isAutoClockOut ? ` ${t("detail.auto")}` : ""}
          </Text>
        ))}
      </Text>,
    );
  } else if (display.kind === "scheduled" || display.kind === "pending") {
    lines.push(
      <Text key="no-punch" style={styles.subtle}>
        {t("detail.noPunch")}
      </Text>,
    );
  } else if (display.kind === "unavailable") {
    lines.push(
      <Text key="unavailable" style={styles.subtle}>
        {t("detail.unavailable")}
      </Text>,
    );
  }

  if (display.isCashOut) {
    const hours = day.leave?.type === "AnnualCashOut" ? day.leave.hours : 0;
    tags.push(
      <View key="cash-out" style={styles.cashOutTag}>
        <Text style={styles.cashOutText}>{t("detail.cashOut", { hours: formatHours(hours) })}</Text>
      </View>,
    );
  }
  for (const [index, pending] of day.pending.entries()) {
    tags.push(<PendingTag key={`pending-${index}`} label={t(`pending.${pendingLabelKey(pending.type)}`)} />);
  }
  for (const other of day.otherStores) {
    tags.push(
      <Text key={`other-${other.storeCode}`} style={styles.subtle}>
        {t("detail.otherStore", { store: other.storeName ?? other.storeCode, hours: formatHours(other.hours) })}
      </Text>,
    );
  }
  if (display.isBirthday) tags.push(<Text key="birthday" style={styles.emoji}>🎂</Text>);

  const rowTone = display.kind === "sickLeave" ? styles.rowSick : display.isPublicHoliday ? styles.rowHoliday : null;
  const hoursText = display.hours > 0 ? formatHours(display.hours) : display.kind === "worked" ? "0" : "";
  return (
    <View style={[styles.row, rowTone, display.isPending ? styles.rowPending : null]}>
      <Text style={[styles.date, MONO]}>{dateLabel}</Text>
      <View style={styles.content}>
        {lines}
        {tags.length > 0 ? <View style={styles.tags}>{tags}</View> : null}
      </View>
      {hoursText ? (
        <Text style={[styles.hours, MONO, display.kind === "sickLeave" ? styles.hoursSick : null]}>{hoursText}</Text>
      ) : null}
    </View>
  );
});

const styles = StyleSheet.create({
  row: {
    flexDirection: "row",
    alignItems: "center",
    minHeight: 46,
    paddingHorizontal: 12,
    paddingVertical: 8,
    gap: 8,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HOURS_COLORS.card,
  },
  rowSick: { backgroundColor: HOURS_COLORS.sickBg, borderColor: HOURS_COLORS.sickBorder },
  rowHoliday: { backgroundColor: HOURS_COLORS.holidayBg, borderColor: HOURS_COLORS.holidayBorder },
  rowPending: { borderColor: HOURS_COLORS.pendingBorder, borderStyle: "dashed", borderWidth: 1.5, backgroundColor: HOURS_COLORS.pendingBg },
  compact: {
    flexDirection: "row",
    alignItems: "center",
    height: 32,
    paddingHorizontal: 12,
    gap: 8,
    borderRadius: 8,
    borderWidth: 1,
    overflow: "hidden",
  },
  compactRest: { borderColor: HOURS_COLORS.restBorder, borderStyle: "dashed", backgroundColor: "transparent" },
  compactUnavailable: { borderColor: HOURS_COLORS.cellBorder, backgroundColor: HOURS_COLORS.hatchBg },
  compactText: { flex: 1, fontSize: 13, color: HB_COLORS.textSecondary },
  date: { width: 74, fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary },
  content: { flex: 1, minWidth: 0, gap: 4 },
  inline: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: 4 },
  text: { fontSize: 13.5, color: HB_COLORS.textPrimary, flexShrink: 1 },
  subtle: { fontSize: 12.5, color: HB_COLORS.textSecondary },
  auto: { color: HOURS_COLORS.auto },
  tags: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: 4 },
  kindBadge: { borderWidth: 1, borderRadius: 4, paddingHorizontal: 3, minWidth: 18, alignItems: "center" },
  kindBadgeText: { fontSize: 11, fontWeight: "700" },
  check: { width: 16, height: 16, borderRadius: 4, borderWidth: 1, borderColor: HOURS_COLORS.cashOutBorder, backgroundColor: HOURS_COLORS.cashOutBg, alignItems: "center", justifyContent: "center" },
  pendingTag: { borderWidth: 1, borderStyle: "dashed", borderColor: HOURS_COLORS.pendingBorder, borderRadius: 4, paddingHorizontal: 4, backgroundColor: "#FFFAEB" },
  pendingTagText: { fontSize: 11, fontWeight: "700", color: HB_COLORS.warning },
  cashOutTag: { borderWidth: 1, borderColor: HOURS_COLORS.cashOutBorder, borderRadius: 4, paddingHorizontal: 4, backgroundColor: HOURS_COLORS.cashOutBg },
  cashOutText: { fontSize: 11, fontWeight: "600", color: HOURS_COLORS.cashOutText },
  emoji: { fontSize: 12 },
  hours: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary, minWidth: 34, textAlign: "right" },
  hoursSick: { color: HOURS_COLORS.sickText },
});
