// 列表页每人一张卡：头像 + 姓名 + 类型角标，右侧总小时与分档小计；下面每天一个小格（2 周时分两行，每行一周）。
import { memo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import {
  fillPeriodDays,
  formatHours,
  formatShortDate,
  isoWeekday,
  resolveDayDisplay,
  tierSummaryParts,
} from "@/modules/attendance/payroll-hours";
import type { PayrollHoursDay, PayrollHoursEmployee } from "@/modules/attendance/payroll-hours-types";
import { clauseSeparator, labelColon } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { Hatch, HOURS_COLORS, HoursAvatar, HoursBadge, MONO } from "./hours-ui";

type Translate = (key: string, options?: Record<string, unknown>) => string;

const EMPLOYMENT_TYPES = new Set(["FullTime", "PartTime", "Casual"]);

/** 姓名旁角标：类型、未成年周岁、多店。 */
export function employeeBadges(employee: PayrollHoursEmployee, t: Translate): { key: string; label: string; tone: "neutral" | "info" | "pink" }[] {
  const badges: { key: string; label: string; tone: "neutral" | "info" | "pink" }[] = [];
  if (employee.employmentType && EMPLOYMENT_TYPES.has(employee.employmentType)) {
    badges.push({ key: "type", label: t(`employment.${employee.employmentType}`), tone: "neutral" });
  }
  if (employee.ageYears !== undefined && employee.ageYears < 18) {
    badges.push({ key: "age", label: t("employment.age", { age: employee.ageYears }), tone: "pink" });
  }
  if (employee.hasOtherStores) badges.push({ key: "multi", label: t("employment.multiStore"), tone: "info" });
  return badges;
}

/** 分档小计文字：「平 31.72 · 日 7.38」，只列非零项。 */
export function tierSummaryText(employee: Pick<PayrollHoursEmployee, "byTier" | "leaveHours">, t: Translate): string {
  return tierSummaryParts(employee.byTier, employee.leaveHours)
    .map((part) => `${t(`tier.${part.key}`)} ${formatHours(part.hours)}`)
    .join(" · ");
}

/** 一个日格：上方星期、下方小时；颜色与角标表达假别、周末、待审、年假发放、生日。 */
const HoursDayCell = memo(function HoursDayCell({ day }: { day: PayrollHoursDay }) {
  const { t, language } = useAppTranslation("attendanceHours");
  const display = resolveDayDisplay(day);
  const weekday = t(`weekday.short${isoWeekday(day.date)}`);

  let value: string | null = null;
  let toneStyle: object = styles.cellNormal;
  let valueStyle: object | null = null;
  switch (display.kind) {
    case "worked":
      value = formatHours(display.hours);
      if (display.isWeekend || display.isPublicHoliday) {
        toneStyle = styles.cellWeekend;
        valueStyle = styles.valueWeekend;
      }
      break;
    case "sickLeave":
      value = t("cell.sick", { hours: formatHours(display.hours) });
      toneStyle = styles.cellSick;
      valueStyle = styles.valueSick;
      break;
    case "annualLeave":
      value = t("cell.annual", { hours: formatHours(display.hours) });
      toneStyle = styles.cellAnnual;
      valueStyle = styles.valueAnnual;
      break;
    case "publicHoliday":
      value = display.hours > 0 ? t("cell.publicHoliday", { hours: formatHours(display.hours) }) : t("cell.publicHolidayPending");
      toneStyle = styles.cellHoliday;
      valueStyle = styles.valueHoliday;
      break;
    case "unavailable":
      value = t("cell.unavailable");
      toneStyle = styles.cellUnavailable;
      break;
    case "scheduled":
    case "pending":
      value = "—";
      break;
    case "rest":
      toneStyle = styles.cellRest;
      break;
  }

  // 读屏文案：「周三 10-14：7.98 小时，有待审」。
  const a11yParts = [value === null ? t("cell.a11yRest") : display.kind === "worked" ? t("cell.a11yHours", { hours: value }) : value];
  if (display.isPending) a11yParts.push(t("cell.a11yPending"));
  if (display.isCashOut) a11yParts.push(t("cell.a11yCashOut"));
  if (display.isBirthday) a11yParts.push(t("cell.a11yBirthday"));

  return (
    <View style={styles.cellWrap} accessible accessibilityLabel={`${weekday} ${formatShortDate(day.date, language)}${labelColon(language)}${a11yParts.join(clauseSeparator(language))}`}>
      <View style={[styles.cell, toneStyle, display.isPending ? styles.cellPending : null]}>
        {display.kind === "unavailable" ? <Hatch /> : null}
        <Text style={[styles.weekday, value === null ? styles.weekdayRest : null]} numberOfLines={1}>
          {weekday}
        </Text>
        {value !== null ? (
          <Text style={[styles.value, MONO, valueStyle]} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.65}>
            {value}
          </Text>
        ) : null}
      </View>
      {display.isCashOut || display.isBirthday ? (
        <View style={styles.cornerBadges} pointerEvents="none">
          {display.isBirthday ? <Text style={styles.birthday}>🎂</Text> : null}
          {display.isCashOut ? (
            <View style={styles.cashOut}>
              <Text style={styles.cashOutText}>{t("cell.cashOutBadge")}</Text>
            </View>
          ) : null}
        </View>
      ) : null}
    </View>
  );
});

export const HoursEmployeeCard = memo(function HoursEmployeeCard({
  employee,
  periodStart,
  weeks,
  onPress,
}: {
  employee: PayrollHoursEmployee;
  periodStart: string;
  weeks: 1 | 2;
  onPress?: (employee: PayrollHoursEmployee) => void;
}) {
  const { t } = useAppTranslation("attendanceHours");
  const days = fillPeriodDays(employee.days, periodStart, weeks);
  const rows = Array.from({ length: weeks }, (_, index) => days.slice(index * 7, index * 7 + 7));
  const tierText = tierSummaryText(employee, t);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${employee.name} ${formatHours(employee.totalHours)} h`}
      onPress={onPress ? () => onPress(employee) : undefined}
      disabled={!onPress}
      style={({ pressed }) => [styles.card, pressed ? styles.cardPressed : null]}
    >
      <View style={styles.head}>
        <HoursAvatar name={employee.name} seed={employee.userGuid} />
        <View style={styles.nameBlock}>
          <Text style={styles.name} numberOfLines={1}>
            {employee.name}
          </Text>
          <View style={styles.badges}>
            {employeeBadges(employee, t).map((badge) => (
              <HoursBadge key={badge.key} label={badge.label} tone={badge.tone} />
            ))}
          </View>
        </View>
        <View style={styles.totals}>
          <Text style={[styles.total, MONO]}>{formatHours(employee.totalHours)}</Text>
          {tierText ? (
            <Text style={[styles.tier, MONO]} numberOfLines={1}>
              {tierText}
            </Text>
          ) : null}
        </View>
      </View>
      {rows.map((row, index) => (
        <View key={row[0]?.date ?? index} style={styles.row}>
          {row.map((day) => (
            <HoursDayCell key={day.date} day={day} />
          ))}
        </View>
      ))}
    </Pressable>
  );
});

const styles = StyleSheet.create({
  card: {
    backgroundColor: HOURS_COLORS.card,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: 1,
    borderRadius: HB_RADIUS.surface,
    padding: HB_SPACING.sm,
    paddingBottom: HB_SPACING.sm - 2,
  },
  cardPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  head: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs },
  nameBlock: { flex: 1, minWidth: 0, gap: 3, paddingTop: 1 },
  name: { fontSize: 15.5, fontWeight: "600", color: HB_COLORS.textPrimary },
  badges: { flexDirection: "row", flexWrap: "wrap", gap: 4 },
  totals: { alignItems: "flex-end", flexShrink: 0, maxWidth: "52%" },
  total: { fontSize: 20, fontWeight: "700", color: HB_COLORS.textPrimary },
  tier: { fontSize: 12, color: HOURS_COLORS.tierText, marginTop: 1 },
  // 行间留出角标（发 / 🎂）溢出的位置。
  row: { flexDirection: "row", gap: 4, marginTop: 10 },
  cellWrap: { flex: 1, minWidth: 0 },
  cell: {
    height: 48,
    borderRadius: 8,
    borderWidth: 1,
    alignItems: "center",
    justifyContent: "center",
    overflow: "hidden",
    paddingHorizontal: 2,
  },
  cellNormal: { backgroundColor: HOURS_COLORS.cellBg, borderColor: HOURS_COLORS.cellBorder },
  cellWeekend: { backgroundColor: HOURS_COLORS.weekendBg, borderColor: HOURS_COLORS.weekendBorder },
  cellSick: { backgroundColor: HOURS_COLORS.sickBg, borderColor: HOURS_COLORS.sickBorder },
  cellAnnual: { backgroundColor: HOURS_COLORS.annualBg, borderColor: HOURS_COLORS.annualBorder },
  cellHoliday: { backgroundColor: HOURS_COLORS.holidayBg, borderColor: HOURS_COLORS.holidayBorder },
  cellUnavailable: { backgroundColor: HOURS_COLORS.hatchBg, borderColor: HOURS_COLORS.cellBorder },
  cellRest: { backgroundColor: "transparent", borderColor: HOURS_COLORS.restBorder, borderStyle: "dashed" },
  cellPending: { borderColor: HOURS_COLORS.pendingBorder, borderStyle: "dashed", borderWidth: 1.5 },
  weekday: { fontSize: 10.5, color: HB_COLORS.textSecondary },
  weekdayRest: { fontSize: 12, color: HOURS_COLORS.restText },
  value: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary, marginTop: 1 },
  valueWeekend: { color: HOURS_COLORS.weekendText },
  valueSick: { color: HOURS_COLORS.sickText },
  valueAnnual: { color: HOURS_COLORS.annualText },
  valueHoliday: { color: HOURS_COLORS.holidayText },
  cornerBadges: { position: "absolute", top: -7, right: -3, flexDirection: "row", alignItems: "center", gap: 1 },
  birthday: { fontSize: 11 },
  cashOut: {
    minWidth: 16,
    height: 16,
    paddingHorizontal: 2,
    borderRadius: 4,
    borderWidth: 1,
    borderColor: HOURS_COLORS.cashOutBorder,
    backgroundColor: HOURS_COLORS.cashOutBg,
    alignItems: "center",
    justifyContent: "center",
  },
  cashOutText: { fontSize: 10, fontWeight: "700", color: HOURS_COLORS.cashOutText, lineHeight: 12 },
});
