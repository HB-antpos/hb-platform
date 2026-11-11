// 考勤管理页头：标题 + 门店胶囊一行、今日概况四格、下划线页签（带审核角标）。
// 只用于管理模式；个人考勤页仍用原来的页头。
import { useMemo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQuery } from "@tanstack/react-query";
import { Text } from "react-native-paper";
import { getManagedAttendanceRecords } from "@/modules/attendance/api";
import { classifyManagedRecord, isManagedRecordException } from "@/modules/attendance/attendance-managed-records";
import { formatHours } from "@/modules/attendance/payroll-hours";
import { getPayrollHours, payrollHoursQueryKey } from "@/modules/attendance/payroll-hours-api";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useApprovalDateLabels } from "../approvals/use-approval-labels";

export function localDateString(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

export function localMinutesNow(date = new Date()) {
  return date.getHours() * 60 + date.getMinutes();
}

/** 标题 + 门店胶囊：多家门店时胶囊可点开选择器；刷新改为下拉刷新。 */
export function ManagementTitleBar({
  title,
  storeLabel,
  canPickStore,
  onPickStore,
}: {
  title: string;
  storeLabel: string;
  canPickStore: boolean;
  onPickStore: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  return (
    <View style={styles.titleBar}>
      <Text variant="headlineSmall" style={styles.title} numberOfLines={1}>{title}</Text>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("common:labels.selectStore")}
        disabled={!canPickStore}
        onPress={onPickStore}
        style={styles.storeChip}
      >
        <MaterialCommunityIcons name="storefront-outline" size={16} color={HB_COLORS.textSecondary} />
        <Text style={styles.storeText} numberOfLines={1}>{storeLabel}</Text>
        {canPickStore ? <MaterialCommunityIcons name="chevron-down" size={16} color={HB_COLORS.textSecondary} /> : null}
      </Pressable>
    </View>
  );
}

/**
 * 今日概况：在岗 / 需处理 / 待审 / 本周工时。数据与打卡记录、工时统计共用缓存键，
 * 打卡记录里改了打卡会一起刷新。
 */
export function AttendanceTodaySnapshot({
  storeCode,
  pendingCount,
  onOpenPunches,
  onOpenApprovals,
  onOpenHours,
}: {
  storeCode: string;
  pendingCount: number;
  onOpenPunches: () => void;
  onOpenApprovals: () => void;
  onOpenHours: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const { weekdayDate } = useApprovalDateLabels();
  const today = localDateString(new Date());
  const records = useQuery({
    queryKey: ["attendance", "managed", "records", storeCode, today],
    queryFn: () => getManagedAttendanceRecords({ storeCode, workDate: today }),
  });
  const hoursQuery = { storeCode, weeks: 1 as const };
  const hours = useQuery({
    queryKey: payrollHoursQueryKey(hoursQuery),
    queryFn: () => getPayrollHours(hoursQuery),
  });

  const summary = useMemo(() => {
    const now = localMinutesNow();
    const sessions = records.data ?? [];
    const issues = sessions.map((session) => ({ session, issue: classifyManagedRecord(session, today, now) }));
    return {
      onDuty: sessions.filter((session) => session.hasOpenSegment).length,
      scheduled: issues.filter((row) => row.issue !== "onLeave").length,
      attention: issues.filter((row) => isManagedRecordException(row.issue)).length,
    };
  }, [records.data, today]);
  const loading = records.isLoading;
  const paidHours = hours.data && hours.data.storeCode === storeCode ? hours.data.totals.paidHours : undefined;

  return (
    <View style={styles.snapshot}>
      <View style={styles.snapshotHead}>
        <Text variant="labelLarge" style={styles.snapshotTitle}>{t("managementHome.today", { date: weekdayDate(today) })}</Text>
        <Text variant="bodySmall" style={styles.muted}>{t("managementHome.pullToRefresh")}</Text>
      </View>
      <View style={styles.kpis}>
        <Kpi label={t("managementHome.onDuty")} value={loading ? "–" : String(summary.onDuty)} suffix={loading ? undefined : ` / ${summary.scheduled}`} />
        <Kpi
          label={t("managementHome.attention")}
          value={loading ? "–" : String(summary.attention)}
          tone={summary.attention > 0 ? "danger" : undefined}
          link={summary.attention > 0 ? t("managementHome.viewList") : undefined}
          onPress={onOpenPunches}
        />
        <Kpi
          label={t("managementHome.pending")}
          value={String(pendingCount)}
          tone={pendingCount > 0 ? "warning" : undefined}
          link={t("managementHome.openApprovals")}
          onPress={onOpenApprovals}
        />
        <Kpi
          label={t("managementHome.weekHours")}
          value={paidHours === undefined ? "–" : formatHours(paidHours)}
          suffix={paidHours === undefined ? undefined : " h"}
          small
          link={t("managementHome.openHours")}
          onPress={onOpenHours}
          last
        />
      </View>
    </View>
  );
}

function Kpi({
  label,
  value,
  suffix,
  tone,
  link,
  small,
  last,
  onPress,
}: {
  label: string;
  value: string;
  suffix?: string;
  tone?: "danger" | "warning";
  link?: string;
  small?: boolean;
  last?: boolean;
  onPress?: () => void;
}) {
  return (
    <Pressable
      accessibilityRole={onPress ? "button" : undefined}
      disabled={!onPress}
      onPress={onPress}
      style={[styles.kpi, last ? styles.kpiLast : null]}
    >
      <Text variant="bodySmall" style={styles.muted}>{label}</Text>
      <Text
        style={[styles.kpiValue, small ? styles.kpiValueSmall : null, tone === "danger" ? styles.danger : tone === "warning" ? styles.warning : null]}
        numberOfLines={1}
        adjustsFontSizeToFit
        minimumFontScale={0.7}
      >
        {value}
        {suffix ? <Text style={styles.kpiSuffix}>{suffix}</Text> : null}
      </Text>
      {link ? <Text variant="labelSmall" style={styles.kpiLink} numberOfLines={1}>{link}</Text> : null}
    </Pressable>
  );
}

export interface ManagementTab<K extends string> {
  key: K;
  label: string;
  badge?: number;
}

/** 下划线页签：选中项加粗 + 底部蓝条；审核等带红色数量角标。 */
export function ManagementTabBar<K extends string>({
  tabs,
  value,
  onChange,
}: {
  tabs: ManagementTab<K>[];
  value: K;
  onChange: (key: K) => void;
}) {
  return (
    <View style={styles.tabs} accessibilityRole="tablist">
      {tabs.map((tab) => {
        const selected = tab.key === value;
        return (
          <Pressable
            key={tab.key}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(tab.key)}
            style={styles.tab}
          >
            <View style={styles.tabLabelRow}>
              <Text style={[styles.tabText, selected ? styles.tabTextSelected : null]} numberOfLines={1}>{tab.label}</Text>
              {tab.badge ? (
                <View style={styles.badge}>
                  <Text style={styles.badgeText}>{tab.badge > 99 ? "99+" : tab.badge}</Text>
                </View>
              ) : null}
            </View>
            <View style={[styles.underline, selected ? styles.underlineSelected : null]} />
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  titleBar: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.sm, justifyContent: "space-between" },
  title: { color: HB_COLORS.textPrimary, flexShrink: 1, fontWeight: "800" },
  storeChip: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderRadius: 17,
    borderWidth: 1,
    flexDirection: "row",
    gap: 4,
    height: 34,
    maxWidth: 190,
    paddingHorizontal: 10,
  },
  storeText: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 14, fontWeight: "600" },
  snapshot: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface + 2,
    borderWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: 14,
    paddingVertical: HB_SPACING.sm,
  },
  snapshotHead: { alignItems: "center", flexDirection: "row", justifyContent: "space-between", marginBottom: 6 },
  snapshotTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  kpis: { flexDirection: "row" },
  kpi: { borderRightColor: HB_COLORS.outlineMuted, borderRightWidth: StyleSheet.hairlineWidth, flex: 1, paddingHorizontal: 6 },
  kpiLast: { borderRightWidth: 0 },
  kpiValue: { color: HB_COLORS.textPrimary, fontSize: 24, fontVariant: ["tabular-nums"], fontWeight: "800", lineHeight: 30 },
  kpiValueSmall: { fontSize: 20 },
  kpiSuffix: { color: "#98A2B3", fontSize: 13, fontWeight: "600" },
  kpiLink: { color: HB_COLORS.action, fontWeight: "600" },
  danger: { color: HB_COLORS.danger },
  warning: { color: HB_COLORS.warning },
  muted: { color: HB_COLORS.textSecondary },
  tabs: { borderBottomColor: HB_COLORS.outlineMuted, borderBottomWidth: StyleSheet.hairlineWidth, flexDirection: "row" },
  tab: { alignItems: "center", flex: 1, paddingTop: 8 },
  tabLabelRow: { alignItems: "center", flexDirection: "row", gap: 3, minHeight: 22 },
  tabText: { color: HB_COLORS.textSecondary, fontSize: 15, fontWeight: "500" },
  tabTextSelected: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  badge: {
    alignItems: "center",
    backgroundColor: HB_COLORS.danger,
    borderRadius: 9,
    height: 18,
    justifyContent: "center",
    minWidth: 18,
    paddingHorizontal: 4,
  },
  badgeText: { color: HB_COLORS.white, fontSize: 11, fontWeight: "700" },
  underline: { borderRadius: 2, height: 3, marginTop: 8, width: "56%" },
  underlineSelected: { backgroundColor: HB_COLORS.action },
});
