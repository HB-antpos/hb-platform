// 工时统计·单人明细（设计图 B）：页头头像 + 姓名 + 类型，右侧 1 周 / 2 周；四个汇总格；按周分组每天一行。
// 数据与列表页同一个接口、同一份缓存（全店结果里取这个人），所以从列表点进来不会再等一次。
// 拆成路由外壳 HoursEmployeeDetailScreen（参数、守卫）与只收 props 的 HoursEmployeeDetailContent。
import { useState } from "react";
import { RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter } from "expo-router";
import { Text } from "react-native-paper";
import {
  formatHours,
  formatShortDate,
  groupDaysByWeek,
  localToday,
  periodStartForWeeksSwitch,
  weekendHoursOf,
} from "@/modules/attendance/payroll-hours";
import { getPayrollHours, payrollHoursQueryKey } from "@/modules/attendance/payroll-hours-api";
import type { PayrollHoursQuery } from "@/modules/attendance/payroll-hours-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { HoursDayRow } from "./HoursDayRow";
import { employeeBadges } from "./HoursEmployeeCard";
import { firstParam, parseWeeksParam } from "./hours-params";
import {
  HOURS_COLORS,
  HoursAlertBar,
  HoursAvatar,
  HoursBadge,
  HoursCard,
  HoursError,
  HoursFrame,
  HoursLoading,
  HoursMessage,
  HoursTitle,
  MONO,
  SummaryTiles,
  WeeksSegment,
} from "./hours-ui";
import { useHoursGuard } from "./use-hours-guard";

/** 路由外壳：/(shell)/attendance-hours-detail?storeCode&userGuid&periodStart&weeks */
export function HoursEmployeeDetailScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ storeCode?: string; userGuid?: string; periodStart?: string; weeks?: string }>();
  const { t } = useAppTranslation(["attendanceHours", "common"]);
  const viewerGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useHoursGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/attendance-hours"));
  const storeCode = firstParam(params.storeCode);
  const userGuid = firstParam(params.userGuid);
  if (blocked) return <HoursMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  if (!storeCode || !userGuid) return <HoursMessage message={t("messages.employeeMissing")} backLabel={t("common:actions.back")} onBack={goBack} />;
  return (
    <HoursEmployeeDetailContent
      key={`${viewerGuid ?? ""}:${storeCode}:${userGuid}`}
      storeCode={storeCode}
      userGuid={userGuid}
      initialPeriodStart={firstParam(params.periodStart)}
      initialWeeks={parseWeeksParam(params.weeks) ?? 1}
      onBack={goBack}
    />
  );
}

export interface HoursEmployeeDetailContentProps {
  storeCode: string;
  userGuid: string;
  /** 列表页当前期的起点；不传 = 后端取当前期。 */
  initialPeriodStart?: string;
  /** 默认沿用列表页的周数。 */
  initialWeeks?: 1 | 2;
  onBack: () => void;
  /** 「今天」，只影响 2 周切回 1 周时看哪一周；预览截图时可固定。 */
  today?: string;
}

export function HoursEmployeeDetailContent({
  storeCode,
  userGuid,
  initialPeriodStart,
  initialWeeks = 1,
  onBack,
  today = localToday(),
}: HoursEmployeeDetailContentProps) {
  const { t, language } = useAppTranslation(["attendanceHours", "common"]);
  const [weeks, setWeeks] = useState<1 | 2>(initialWeeks);
  const [periodStart, setPeriodStart] = useState<string | undefined>(initialPeriodStart);
  const [manualRefreshing, setManualRefreshing] = useState(false);

  const query: PayrollHoursQuery = { storeCode, periodStart, weeks };
  const hours = useQuery({
    queryKey: payrollHoursQueryKey(query),
    queryFn: () => getPayrollHours(query),
    placeholderData: keepPreviousData,
  });
  const report = hours.data;
  const employee = report?.employees.find((item) => item.userGuid.toLowerCase() === userGuid.toLowerCase());

  const changeWeeks = (next: 1 | 2) => {
    if (next === weeks) return;
    if (report) setPeriodStart(periodStartForWeeksSwitch({ periodStart: report.periodStart, fromWeeks: report.weeks, toWeeks: next, today }));
    setWeeks(next);
  };
  const onRefresh = async () => {
    setManualRefreshing(true);
    try {
      await hours.refetch();
    } finally {
      setManualRefreshing(false);
    }
  };

  const middle = employee ? (
    <>
      <HoursAvatar name={employee.name} seed={employee.userGuid} size={30} />
      <Text variant="titleMedium" style={styles.name} numberOfLines={1}>
        {employee.name}
      </Text>
      {employeeBadges(employee, t)
        .filter((badge) => badge.key !== "multi")
        .map((badge) => (
          <HoursBadge key={badge.key} label={badge.label} tone={badge.tone} />
        ))}
    </>
  ) : (
    <HoursTitle title={t("title")} />
  );

  return (
    <HoursFrame
      onBack={onBack}
      middle={middle}
      right={
        <WeeksSegment
          value={weeks}
          onChange={changeWeeks}
          labels={{ one: t("period.oneWeek"), two: t("period.twoWeeks") }}
          accessibilityLabel={t("period.weeksSwitch")}
        />
      }
    >
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={manualRefreshing} onRefresh={() => void onRefresh()} />}
        showsVerticalScrollIndicator={false}
      >
        {!report ? (
          hours.isError ? (
            <HoursError title={t("messages.loadFailed")} retryLabel={t("messages.retry")} onRetry={() => void hours.refetch()} />
          ) : (
            <HoursLoading />
          )
        ) : !employee ? (
          <HoursCard>
            <Text style={styles.emptyText}>{t("messages.employeeMissing")}</Text>
          </HoursCard>
        ) : (
          <View style={styles.body}>
            <SummaryTiles
              items={[
                { label: t("summary.paid"), value: formatHours(employee.totalHours) },
                {
                  label: t("summary.weekendPublic"),
                  value: `${formatHours(weekendHoursOf(employee.byTier))} / ${formatHours(employee.byTier.publicHoliday)}`,
                  color: HOURS_COLORS.tierText,
                },
                { label: t("summary.paidLeave"), value: formatHours(employee.leaveHours) },
                {
                  label: t("summary.pending"),
                  value: String(employee.pendingCount),
                  color: employee.pendingCount > 0 ? HB_COLORS.danger : undefined,
                },
              ]}
            />
            {hours.isError ? (
              <HoursAlertBar tone="warning" icon="wifi-off" text={t("messages.loadFailed")} action={{ label: t("messages.retry"), onPress: () => void hours.refetch() }} />
            ) : null}
            {groupDaysByWeek(employee.days, report.periodStart, report.weeks).map((group) => (
              <View key={group.start} style={styles.week}>
                <View style={styles.weekHeader}>
                  <Text style={[styles.weekTitle, MONO]} numberOfLines={1}>
                    {t("detail.weekTitle", {
                      week: group.weekNumber,
                      from: formatShortDate(group.start, language),
                      to: formatShortDate(group.end, language),
                    })}
                  </Text>
                  <Text style={[styles.weekTotal, MONO]}>{t("summary.hours", { value: formatHours(group.totalHours) })}</Text>
                </View>
                {group.days.map((day) => (
                  <HoursDayRow key={day.date} day={day} />
                ))}
              </View>
            ))}
          </View>
        )}
      </ScrollView>
    </HoursFrame>
  );
}

const styles = StyleSheet.create({
  content: { padding: HB_SPACING.sm, paddingBottom: HB_SPACING.xl },
  name: { fontWeight: "700", color: HB_COLORS.textPrimary, flexShrink: 1 },
  body: { gap: 12 },
  emptyText: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center", paddingVertical: HB_SPACING.md },
  week: { gap: 6 },
  weekHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingHorizontal: 2, gap: 8 },
  weekTitle: { flex: 1, fontSize: 13, fontWeight: "600", color: HB_COLORS.textSecondary },
  weekTotal: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textSecondary },
});
