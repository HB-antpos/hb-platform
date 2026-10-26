// 「带薪年假 · 休息」（设计图 C）与「带薪年假 · 发放（照常上班）」共用一页，按 type 切换规则：
// - 休息：提前 annualLeaveLeadWeeks 周、最长 annualLeaveMaxWeeks 周，且这几天还没有已发布的排班；批准后这些天自动「不能上班」。
// - 发放：提前 annualCashOutLeadDays 天、最长 annualCashOutMaxWeeks 周，照常排班不查排班，只提示合规需会计确认。
// 申请工时 = 区间内工作日（周一至周五、非公假）× annualLeaveHoursPerWorkday；最终发多少由财务核定。
import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, IconButton, Text } from "react-native-paper";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getMyLeaveOptions,
  LEAVE_OPTIONS_QUERY_KEY,
  MY_LEAVE_REQUESTS_QUERY_KEY,
  submitMyLeaveRequest,
} from "@/modules/attendance/leave-api";
import {
  annualEarliestDate,
  applyCalendarTap,
  buildMonthGrid,
  formatHours,
  isWeekend,
  shiftMonth,
  validateAnnualRange,
  type AnnualCheck,
  type AnnualLeaveKind,
  type AnnualRangeValidation,
} from "@/modules/attendance/leave-rules";
import type { AttendanceLeaveRules, AttendanceMyLeaveOptions } from "@/modules/attendance/leave-types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { listSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  HatchFill,
  LEAVE_TONES,
  LeaveCard,
  LeaveCardHeader,
  LeaveNotice,
  LeaveScreenFrame,
  PrimaryBottomButton,
  useLeaveDateFormat,
} from "./leave-ui";

const WEEKDAY_KEYS = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;
const ANNUAL = LEAVE_TONES.annual;

export interface AnnualLeaveContentProps {
  storeCode?: string;
  type: AnnualLeaveKind;
  onBack: () => void;
  /** 提交成功（列表已刷新）后调用，路由外壳在这里返回上一页。 */
  onSubmitted: () => void;
}

export function AnnualLeaveContent({ storeCode, type, onBack, onSubmitted }: AnnualLeaveContentProps) {
  const { t } = useAppTranslation(["attendanceLeave", "common"]);
  const optionsQuery = useQuery({
    queryKey: [...LEAVE_OPTIONS_QUERY_KEY, storeCode ?? ""],
    queryFn: () => getMyLeaveOptions(storeCode),
  });
  const options = optionsQuery.data;
  const title = t(type === "PaidAnnual" ? "annual.titlePaidAnnual" : "annual.titleCashOut");

  if (!options) {
    return (
      <LeaveScreenFrame title={title} onBack={onBack}>
        {optionsQuery.isError ? (
          <LeaveNotice text={t("messages.loadFailed")} actionLabel={t("messages.retry")} onAction={() => void optionsQuery.refetch()} />
        ) : (
          <ActivityIndicator style={styles.loading} />
        )}
      </LeaveScreenFrame>
    );
  }
  if (!options.paidLeaveEligible) {
    return (
      <LeaveScreenFrame title={title} onBack={onBack}>
        <LeaveNotice tone="info" text={t("messages.notEligible")} />
      </LeaveScreenFrame>
    );
  }
  return (
    <AnnualLeaveForm
      key={type}
      type={type}
      title={title}
      options={options}
      storeCode={storeCode ?? options.storeCode}
      onBack={onBack}
      onSubmitted={onSubmitted}
    />
  );
}

function AnnualLeaveForm({
  type,
  title,
  options,
  storeCode,
  onBack,
  onSubmitted,
}: {
  type: AnnualLeaveKind;
  title: string;
  options: AttendanceMyLeaveOptions;
  storeCode?: string;
  onBack: () => void;
  onSubmitted: () => void;
}) {
  const { t, language } = useAppTranslation(["attendanceLeave", "common"]);
  const { monthDay, dayLabel, range } = useLeaveDateFormat();
  const queryClient = useQueryClient();
  const { rules, today } = options;
  const earliest = annualEarliestDate(type, today, rules);
  const [selection, setSelection] = useState<{ startDate?: string; endDate?: string }>({});
  // 下一次点日历改的是起点还是终点；点上方两个日期框可以切换。
  const [target, setTarget] = useState<"start" | "end">("start");
  const [visibleMonth, setVisibleMonth] = useState(() => ({ year: Number(earliest.slice(0, 4)), month: Number(earliest.slice(5, 7)) }));
  const [error, setError] = useState("");

  const holidayDates = useMemo(() => options.publicHolidays.map((holiday) => holiday.date), [options.publicHolidays]);
  const holidaySet = useMemo(() => new Set(holidayDates), [holidayDates]);
  const validation = useMemo(
    () => validateAnnualRange({
      kind: type,
      startDate: selection.startDate,
      endDate: selection.endDate,
      today,
      rules,
      publicHolidays: holidayDates,
      publishedScheduleDates: options.publishedScheduleDates,
    }),
    [holidayDates, options.publishedScheduleDates, rules, selection.endDate, selection.startDate, today, type],
  );

  const onDayPress = (date: string) => {
    if (target === "start" || !selection.startDate) {
      setSelection(applyCalendarTap(selection, date, "start"));
      setTarget("end");
      return;
    }
    const next = applyCalendarTap(selection, date, "end");
    setSelection(next);
    // 选完一段后，下一次点击重新从起点开始。
    setTarget(next.endDate ? "start" : "end");
  };

  const focusBox = (which: "start" | "end") => {
    setTarget(which);
    const date = which === "start" ? selection.startDate : selection.endDate ?? selection.startDate;
    if (date) setVisibleMonth({ year: Number(date.slice(0, 4)), month: Number(date.slice(5, 7)) });
  };

  const mutation = useMutation({
    mutationFn: submitMyLeaveRequest,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: MY_LEAVE_REQUESTS_QUERY_KEY });
      onSubmitted();
    },
    onError: (cause) =>
      setError(resolveLocalizedErrorMessage(cause, { t, language, fallbackKey: "attendanceLeave:messages.submitFailed" })),
  });

  const submit = () => {
    if (!validation.ok || !selection.startDate) return;
    setError("");
    mutation.mutate({
      storeCode,
      leaveType: type,
      startDate: selection.startDate,
      endDate: selection.endDate ?? selection.startDate,
    });
  };

  const earliestMonth = Number(earliest.slice(0, 4)) * 12 + Number(earliest.slice(5, 7));
  const canPrevMonth = visibleMonth.year * 12 + visibleMonth.month > earliestMonth;
  const rangeEnd = selection.endDate ?? selection.startDate;

  return (
    <LeaveScreenFrame
      title={title}
      onBack={onBack}
      footer={(
        <>
          {error ? <Text variant="bodySmall" style={styles.error}>{error}</Text> : null}
          <PrimaryBottomButton
            label={validation.requestedHours > 0
              ? t("annual.submit", { hours: formatHours(validation.requestedHours) })
              : t("annual.submitPlain")}
            disabled={!validation.ok}
            loading={mutation.isPending}
            onPress={submit}
          />
        </>
      )}
    >
      <LeaveCard>
        <LeaveCardHeader title={t("annual.dateTitle")} aside={t("annual.earliest", { date: monthDay(earliest) })} />
        <View style={styles.dateBoxes}>
          <DateBox
            label={selection.startDate ? dayLabel(selection.startDate) : t("annual.startPlaceholder")}
            accessibilityLabel={t("annual.startPlaceholder")}
            active={target === "start"}
            empty={!selection.startDate}
            onPress={() => focusBox("start")}
          />
          <Text variant="bodyMedium" style={styles.muted}>{t("annual.to")}</Text>
          <DateBox
            label={rangeEnd ? dayLabel(rangeEnd) : t("annual.endPlaceholder")}
            accessibilityLabel={t("annual.endPlaceholder")}
            active={target === "end"}
            empty={!rangeEnd}
            onPress={() => focusBox("end")}
          />
        </View>
        <LeaveCalendar
          year={visibleMonth.year}
          month={visibleMonth.month}
          canPrev={canPrevMonth}
          onShift={(delta) => setVisibleMonth((current) => shiftMonth(current.year, current.month, delta))}
          earliest={earliest}
          startDate={selection.startDate}
          endDate={rangeEnd}
          holidays={holidaySet}
          hoursPerWorkday={rules.annualLeaveHoursPerWorkday}
          onPress={onDayPress}
        />
      </LeaveCard>

      <RuleChecks type={type} rules={rules} validation={validation} />

      {validation.hasRange ? (
        <LeaveCard>
          <LeaveCardHeader title={t("annual.hoursTitle")} />
          <View style={styles.hoursRow}>
            <Text variant="bodyMedium" style={styles.hoursLabel}>
              {t("annual.workdaysLine", {
                days: t("units.days", { count: validation.breakdown.workdays }),
                hours: formatHours(rules.annualLeaveHoursPerWorkday),
              })}
            </Text>
            <Text variant="titleSmall" style={styles.hoursValue}>
              {t("units.hours", { hours: formatHours(validation.requestedHours) })}
            </Text>
          </View>
          <View style={[styles.hoursRow, styles.hoursDivider]}>
            <Text variant="bodyMedium" style={styles.hoursLabel}>
              {[
                t("annual.weekendPart", { days: t("units.days", { count: validation.breakdown.weekendDays }) }),
                t("annual.holidayPart", { days: t("units.days", { count: validation.breakdown.holidayDays }) }),
              ].join(listSeparator(language))}
            </Text>
            <Text variant="bodySmall" style={styles.muted}>{t("annual.notCounted")}</Text>
          </View>
          <Text variant="bodySmall" style={styles.note}>{t("annual.financeNote")}</Text>
          <Text variant="bodySmall" style={styles.note}>
            {type === "PaidAnnual"
              ? t("annual.blockNote", { range: range(selection.startDate ?? "", rangeEnd ?? "") })
              : t("annual.cashOutNote")}
          </Text>
        </LeaveCard>
      ) : null}
    </LeaveScreenFrame>
  );
}

function DateBox({
  label,
  accessibilityLabel,
  active,
  empty,
  onPress,
}: {
  label: string;
  accessibilityLabel: string;
  active: boolean;
  empty: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${accessibilityLabel} ${empty ? "" : label}`.trim()}
      accessibilityState={{ selected: active }}
      onPress={onPress}
      style={[styles.dateBox, active ? styles.dateBoxActive : null]}
    >
      <Text variant="titleSmall" style={[styles.dateBoxText, empty ? styles.placeholder : null]} numberOfLines={1}>
        {label}
      </Text>
    </Pressable>
  );
}

function LeaveCalendar({
  year,
  month,
  canPrev,
  onShift,
  earliest,
  startDate,
  endDate,
  holidays,
  hoursPerWorkday,
  onPress,
}: {
  year: number;
  month: number;
  canPrev: boolean;
  onShift: (delta: number) => void;
  earliest: string;
  startDate?: string;
  endDate?: string;
  holidays: ReadonlySet<string>;
  hoursPerWorkday: number;
  onPress: (date: string) => void;
}) {
  const { t } = useAppTranslation("attendanceLeave");
  const { dayLabel } = useLeaveDateFormat();
  const rows = useMemo(() => buildMonthGrid(year, month), [month, year]);
  const hoursTag = t("calendar.hoursTag", { hours: formatHours(hoursPerWorkday) });
  return (
    <View style={styles.calendar}>
      <View style={styles.monthRow}>
        <IconButton icon="chevron-left" size={20} disabled={!canPrev} accessibilityLabel={t("calendar.previousMonth")} onPress={() => onShift(-1)} />
        <Text variant="titleSmall" style={styles.monthTitle}>
          {t("calendar.title", { year, month: t(`calendar.months.${month}`) })}
        </Text>
        <IconButton icon="chevron-right" size={20} accessibilityLabel={t("calendar.nextMonth")} onPress={() => onShift(1)} />
      </View>
      <View style={styles.weekRow}>
        {WEEKDAY_KEYS.map((key) => (
          <Text key={key} variant="labelSmall" style={styles.weekHeader}>{t(`calendar.header.${key}`)}</Text>
        ))}
      </View>
      {rows.map((row, rowIndex) => (
        <View key={rowIndex} style={styles.weekRow}>
          {row.map((date, cellIndex) => {
            if (!date) return <View key={`empty-${cellIndex}`} style={styles.cellSlot} />;
            const disabled = date < earliest;
            const nonWork = isWeekend(date) || holidays.has(date);
            const inRange = Boolean(startDate && endDate && date >= startDate && date <= endDate);
            const isEdge = date === startDate || date === endDate;
            // 无障碍：读出日期、是否不计工时、是否已选。
            const a11y = [dayLabel(date), nonWork ? t("calendar.notCounted") : hoursTag].join(" ");
            return (
              <View key={date} style={styles.cellSlot}>
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={a11y}
                  accessibilityState={{ disabled, selected: inRange }}
                  disabled={disabled}
                  onPress={() => onPress(date)}
                  style={[
                    styles.cell,
                    inRange && !nonWork ? styles.cellInRange : null,
                    inRange && nonWork ? styles.cellNonWorkInRange : null,
                    isEdge ? styles.cellEdge : null,
                  ]}
                >
                  {/* 斜纹 = 不算工时：区间内的周末 / 公假，以及任何时候的公假。 */}
                  {!disabled && nonWork && (inRange || holidays.has(date)) ? <HatchFill /> : null}
                  <Text
                    style={[
                      styles.cellDay,
                      disabled ? styles.cellDisabled : null,
                      !disabled && nonWork ? styles.cellNonWork : null,
                      inRange && !nonWork ? styles.cellDayInRange : null,
                    ]}
                  >
                    {Number(date.slice(8, 10))}
                  </Text>
                  {!disabled && !nonWork ? (
                    <Text style={[styles.cellHours, inRange ? styles.cellHoursInRange : null]}>{hoursTag}</Text>
                  ) : null}
                </Pressable>
              </View>
            );
          })}
        </View>
      ))}
    </View>
  );
}

function RuleChecks({ type, rules, validation }: { type: AnnualLeaveKind; rules: AttendanceLeaveRules; validation: AnnualRangeValidation }) {
  const { t, language } = useAppTranslation("attendanceLeave");
  const { monthDay } = useLeaveDateFormat();
  const days = (count: number) => t("units.days", { count });
  const weeks = (count: number) => t("units.weeks", { count });

  const describe = (check: AnnualCheck) => {
    switch (check.key) {
      case "lead":
        return t("annual.checkLead", {
          lead: type === "PaidAnnual" ? weeks(rules.annualLeaveLeadWeeks) : days(rules.annualCashOutLeadDays),
          days: days(validation.daysFromToday),
        });
      case "maxLength":
        return t("annual.checkMax", {
          max: weeks(type === "PaidAnnual" ? rules.annualLeaveMaxWeeks : rules.annualCashOutMaxWeeks),
          days: days(validation.totalDays),
        });
      default: {
        if (check.ok) return t("annual.checkNoSchedule");
        const shown = validation.conflictDates.slice(0, 3).map(monthDay).join(listSeparator(language));
        return t("annual.checkScheduleConflict", {
          dates: validation.conflictDates.length > 3 ? `${shown}…` : shown,
        });
      }
    }
  };

  return (
    <LeaveCard>
      <LeaveCardHeader title={t("annual.checksTitle")} />
      {validation.hasRange ? (
        validation.checks.map((check) => (
          <View key={check.key} style={styles.checkRow}>
            <MaterialCommunityIcons
              name={check.ok ? "check-circle-outline" : "close-circle-outline"}
              size={18}
              color={check.ok ? HB_COLORS.success : HB_COLORS.danger}
            />
            <Text variant="bodyMedium" style={[styles.checkText, { color: check.ok ? HB_COLORS.success : HB_COLORS.danger }]}>
              {describe(check)}
            </Text>
          </View>
        ))
      ) : (
        <Text variant="bodySmall" style={styles.muted}>{t("annual.pickHint")}</Text>
      )}
      {type === "AnnualCashOut" ? <LeaveNotice tone="warning" text={t("annual.cashOutNotice")} /> : null}
    </LeaveCard>
  );
}

const styles = StyleSheet.create({
  loading: { paddingVertical: HB_SPACING.lg },
  muted: { color: HB_COLORS.textSecondary },
  error: { color: HB_COLORS.danger, textAlign: "center" },
  dateBoxes: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  dateBox: {
    alignItems: "center",
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
    minHeight: 48,
    paddingHorizontal: HB_SPACING.xs,
  },
  dateBoxActive: { borderColor: HB_COLORS.action, borderWidth: 2 },
  dateBoxText: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], fontWeight: "700" },
  placeholder: { color: "#98A2B3", fontWeight: "400" },
  calendar: { gap: 2 },
  monthRow: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  monthTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  weekRow: { flexDirection: "row" },
  weekHeader: { color: HB_COLORS.textSecondary, flex: 1, paddingVertical: HB_SPACING.xxs, textAlign: "center" },
  cellSlot: { flex: 1, padding: 2 },
  cell: {
    alignItems: "center",
    borderColor: "transparent",
    borderRadius: 6,
    borderWidth: 1,
    height: 44,
    justifyContent: "center",
    overflow: "hidden",
  },
  cellInRange: { backgroundColor: ANNUAL.background },
  cellNonWorkInRange: { borderColor: HB_COLORS.outlineMuted },
  cellEdge: { borderColor: ANNUAL.foreground },
  cellDay: { color: HB_COLORS.textPrimary, fontSize: 14, fontVariant: ["tabular-nums"] },
  cellDayInRange: { color: ANNUAL.foreground, fontWeight: "700" },
  cellDisabled: { color: "#D0D5DD" },
  cellNonWork: { color: "#98A2B3" },
  cellHours: { color: "#98A2B3", fontSize: 10, lineHeight: 12 },
  cellHoursInRange: { color: ANNUAL.foreground, fontWeight: "600" },
  checkRow: { alignItems: "flex-start", flexDirection: "row", gap: HB_SPACING.xs },
  checkText: { flex: 1, lineHeight: 20 },
  hoursRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, justifyContent: "space-between", paddingVertical: HB_SPACING.xxs },
  hoursDivider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, paddingTop: HB_SPACING.xs },
  hoursLabel: { color: HB_COLORS.textPrimary, flex: 1 },
  hoursValue: { color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"], fontWeight: "700" },
  note: { color: HB_COLORS.textSecondary, lineHeight: 18 },
});
