// 「申请假期」首页（设计图 A）：员工卡 + 三张带薪假卡（规则写在卡上，数字来自后端参数）+ 其他请假（无薪）+ 我的申请。
// 临时工（paidLeaveEligible = false）不显示带薪假卡；规则读取失败时只保留「其他请假」，不挡原有请假流程。
import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter } from "expo-router";
import { ActivityIndicator, Snackbar, Text } from "react-native-paper";
import { useQuery } from "@tanstack/react-query";
import { ATTENDANCE_STATUS_TONES, type AttendanceStatusTone, StatusPill } from "@/components/attendance/AdjustmentFormControls";
import { leaveDayCount } from "@/modules/attendance/attendance-my-week";
import {
  getMyLeaveOptions,
  getMyLeaveRequestsForLeaveHome,
  LEAVE_OPTIONS_QUERY_KEY,
  MY_LEAVE_REQUESTS_QUERY_KEY,
} from "@/modules/attendance/leave-api";
import { formatHours, localDateString } from "@/modules/attendance/leave-rules";
import type { AttendanceLeaveRules, AttendanceMyLeaveOptions, PaidLeaveType } from "@/modules/attendance/leave-types";
import type { AttendanceLeaveRequest } from "@/modules/attendance/types";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { OtherLeaveSheet } from "./OtherLeaveSheet";
import { LEAVE_TONES, type LeaveTone, LeaveCard, LeaveNotice, LeaveScreenFrame, LeaveScreenMessage, useLeaveDateFormat } from "./leave-ui";
import { paidLeaveHref, useLeaveGoBack, useLeaveRouteGuard, useLeaveRouteParams } from "./leave-route-guard";

type IconName = keyof typeof MaterialCommunityIcons.glyphMap;

/** 路由外壳：读参数、做登录态守卫，再交给只收 props 的 Content。 */
export function LeaveHomeScreen() {
  const router = useRouter();
  const { t } = useAppTranslation("attendanceLeave");
  const { storeCode } = useLeaveRouteParams();
  const blocked = useLeaveRouteGuard();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const goBack = useLeaveGoBack();
  if (blocked) return <LeaveScreenMessage title={t("title")} message={blocked} onBack={goBack} />;
  // 账号变化时重建页面，避免把上一个账号的数据留在屏幕上。
  return (
    <LeaveHomeContent
      key={userGuid}
      storeCode={storeCode}
      onBack={goBack}
      onOpenPaidLeave={(type) => router.push(paidLeaveHref(type, storeCode))}
    />
  );
}

export interface LeaveHomeContentProps {
  storeCode?: string;
  onBack: () => void;
  onOpenPaidLeave: (type: PaidLeaveType) => void;
}

export function LeaveHomeContent({ storeCode, onBack, onOpenPaidLeave }: LeaveHomeContentProps) {
  const { t } = useAppTranslation(["attendanceLeave", "common"]);
  const [otherVisible, setOtherVisible] = useState(false);
  const [message, setMessage] = useState("");

  const optionsQuery = useQuery({
    queryKey: [...LEAVE_OPTIONS_QUERY_KEY, storeCode ?? ""],
    queryFn: () => getMyLeaveOptions(storeCode),
  });
  const leavesQuery = useQuery({
    queryKey: MY_LEAVE_REQUESTS_QUERY_KEY,
    queryFn: getMyLeaveRequestsForLeaveHome,
  });
  const options = optionsQuery.data;
  const today = options?.today ?? localDateString();
  const effectiveStoreCode = storeCode ?? options?.storeCode;

  const leaves = useMemo(
    () => [...(leavesQuery.data ?? [])].sort((left, right) => right.startDate.localeCompare(left.startDate)),
    [leavesQuery.data],
  );

  return (
    <View style={styles.root}>
      <LeaveScreenFrame title={t("title")} onBack={onBack}>
        {options ? <EmployeeCard options={options} /> : null}
        {optionsQuery.isLoading ? <ActivityIndicator style={styles.loading} /> : null}
        {optionsQuery.isError ? (
          <LeaveNotice
            tone="warning"
            text={t("home.optionsFailed")}
            actionLabel={t("messages.retry")}
            onAction={() => void optionsQuery.refetch()}
          />
        ) : null}

        {options?.paidLeaveEligible ? (
          <PaidLeaveCards rules={options.rules} onOpen={onOpenPaidLeave} />
        ) : options ? (
          <Text variant="bodySmall" style={styles.muted}>{t("home.casualHint")}</Text>
        ) : null}

        <LeaveTypeCard
          tone="other"
          icon="calendar-remove-outline"
          title={t("home.other.title")}
          lines={[t("home.other.line1")]}
          onPress={() => setOtherVisible(true)}
        />

        <Text variant="labelLarge" style={styles.sectionTitle}>{t("home.myRequests")}</Text>
        {leavesQuery.isLoading ? <ActivityIndicator style={styles.loading} /> : null}
        {leavesQuery.isError ? (
          <LeaveNotice text={t("messages.loadFailed")} actionLabel={t("messages.retry")} onAction={() => void leavesQuery.refetch()} />
        ) : null}
        {!leavesQuery.isLoading && !leavesQuery.isError && leaves.length === 0 ? (
          <Text variant="bodySmall" style={styles.muted}>{t("home.empty")}</Text>
        ) : null}
        {leaves.map((leave) => <LeaveRequestRow key={leave.leaveGuid} leave={leave} />)}
      </LeaveScreenFrame>

      <OtherLeaveSheet
        visible={otherVisible}
        storeCode={effectiveStoreCode}
        today={today}
        onDismiss={() => setOtherVisible(false)}
        onMessage={setMessage}
      />
      <Snackbar visible={Boolean(message)} onDismiss={() => setMessage("")} duration={3000}>
        {message}
      </Snackbar>
    </View>
  );
}

/** 头像取姓名首字母：跳过「[样例]」这类非字母开头的词；中文名取第一个字。 */
function initialsOf(name?: string) {
  const words = (name ?? "").split(/\s+/).filter((word) => /^[A-Za-zÀ-ɏ一-鿿]/.test(word));
  if (!words.length) return "?";
  const first = words[0];
  if (/^[一-鿿]/.test(first)) return first.charAt(0);
  const last = words.length > 1 ? words[words.length - 1] : "";
  return `${first.charAt(0)}${last.charAt(0)}`.toUpperCase();
}

function EmployeeCard({ options }: { options: AttendanceMyLeaveOptions }) {
  const { t } = useAppTranslation("attendanceLeave");
  const type = options.employmentType;
  return (
    <LeaveCard style={styles.employeeCard}>
      <View style={styles.avatar}>
        <Text style={styles.avatarText}>{initialsOf(options.employeeName)}</Text>
      </View>
      <Text variant="titleSmall" style={styles.employeeName} numberOfLines={1}>{options.employeeName ?? ""}</Text>
      {type ? (
        <View style={styles.typeTag}>
          <Text style={styles.typeTagText}>{t(`employmentType.${type}`, { defaultValue: type })}</Text>
        </View>
      ) : null}
      <Text variant="bodySmall" style={styles.storeName} numberOfLines={1}>{options.storeName ?? options.storeCode ?? ""}</Text>
    </LeaveCard>
  );
}

function PaidLeaveCards({ rules, onOpen }: { rules: AttendanceLeaveRules; onOpen: (type: PaidLeaveType) => void }) {
  const { t } = useAppTranslation("attendanceLeave");
  const days = (count: number) => t("units.days", { count });
  const weeks = (count: number) => t("units.weeks", { count });
  return (
    <>
      <LeaveTypeCard
        tone="sick"
        icon="medical-bag"
        title={t("home.paidSick.title")}
        lines={[t("home.paidSick.line1"), t("home.paidSick.line2", { days: days(rules.sickLeaveSubmitDays) })]}
        onPress={() => onOpen("PaidSick")}
      />
      <LeaveTypeCard
        tone="annual"
        icon="beach"
        title={t("home.paidAnnual.title")}
        lines={[
          t("home.paidAnnual.line1", { lead: weeks(rules.annualLeaveLeadWeeks), max: weeks(rules.annualLeaveMaxWeeks) }),
          t("home.paidAnnual.line2", { hours: formatHours(rules.annualLeaveHoursPerWorkday) }),
        ]}
        onPress={() => onOpen("PaidAnnual")}
      />
      <LeaveTypeCard
        tone="cashOut"
        icon="cash-multiple"
        title={t("home.cashOut.title")}
        lines={[
          t("home.cashOut.line1", { lead: days(rules.annualCashOutLeadDays), max: weeks(rules.annualCashOutMaxWeeks) }),
          t("home.cashOut.line2"),
        ]}
        onPress={() => onOpen("AnnualCashOut")}
      />
    </>
  );
}

function LeaveTypeCard({
  tone,
  icon,
  title,
  lines,
  onPress,
}: {
  tone: LeaveTone;
  icon: IconName;
  title: string;
  lines: string[];
  onPress: () => void;
}) {
  const palette = LEAVE_TONES[tone];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={[title, ...lines].join(clauseSeparator())}
      onPress={onPress}
      style={({ pressed }) => [styles.typeCard, pressed ? styles.pressed : null]}
    >
      <View style={[styles.typeIcon, { backgroundColor: palette.background }]}>
        <MaterialCommunityIcons name={icon} size={22} color={palette.foreground} />
      </View>
      <View style={styles.typeBody}>
        <Text variant="titleSmall" style={styles.typeTitle}>{title}</Text>
        {lines.map((line) => (
          <Text key={line} variant="bodySmall" style={styles.muted}>{line}</Text>
        ))}
      </View>
      <MaterialCommunityIcons name="chevron-right" size={22} color={HB_COLORS.textSecondary} />
    </Pressable>
  );
}

const BADGE_TONES: Record<string, LeaveTone> = { PaidSick: "sick", PaidAnnual: "annual", AnnualCashOut: "cashOut" };

function statusOf(leave: AttendanceLeaveRequest): { key: string; tone: AttendanceStatusTone } {
  const normalized = leave.status.toLowerCase();
  if (normalized === "approved" || normalized === "applied") return { key: "approved", tone: "success" };
  if (normalized === "rejected") return { key: "rejected", tone: "danger" };
  if (normalized === "cancelled") return { key: "cancelled", tone: "neutral" };
  return { key: "pending", tone: "warning" };
}

function LeaveRequestRow({ leave }: { leave: AttendanceLeaveRequest }) {
  const { t } = useAppTranslation("attendanceLeave");
  const { range } = useLeaveDateFormat();
  const badgeTone = BADGE_TONES[leave.leaveType] ?? "other";
  const palette = LEAVE_TONES[badgeTone];
  const status = statusOf(leave);
  // 带薪类型显示申请工时；原有无薪请假显示天数。
  const amount = badgeTone !== "other" && leave.paidHours !== undefined
    ? t("units.hours", { hours: formatHours(leave.paidHours) })
    : t("units.days", { count: leaveDayCount(leave.startDate, leave.endDate) });
  // 年假已批准且财务核定过：显示核定工时（可能少于申请工时）。
  const statusLabel = status.key === "approved" && leave.approvedPaidHours !== undefined
    ? t("requests.status.approvedWithHours", { hours: formatHours(leave.approvedPaidHours) })
    : t(`requests.status.${status.key}`);
  return (
    <LeaveCard style={styles.requestRow}>
      <View style={styles.requestMain}>
        <View style={[styles.badge, { backgroundColor: palette.background }]}>
          <Text style={[styles.badgeText, { color: palette.foreground }]}>
            {t(`requests.badge.${badgeTone === "other" ? "other" : leave.leaveType}`)}
          </Text>
        </View>
        <Text variant="bodyMedium" style={styles.requestText} numberOfLines={1}>
          {t("requests.line", { range: range(leave.startDate, leave.endDate), amount })}
        </Text>
        <StatusPill label={statusLabel} tone={status.tone} />
      </View>
      {status.key === "rejected" && leave.reviewRemark ? (
        <Text variant="bodySmall" style={{ color: ATTENDANCE_STATUS_TONES.danger.text }}>
          {t("requests.rejectedRemark", { remark: leave.reviewRemark })}
        </Text>
      ) : null}
    </LeaveCard>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  loading: { paddingVertical: HB_SPACING.sm },
  muted: { color: HB_COLORS.textSecondary },
  sectionTitle: { color: HB_COLORS.textPrimary, fontWeight: "700", marginTop: HB_SPACING.xs },
  employeeCard: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, paddingVertical: HB_SPACING.sm },
  avatar: {
    alignItems: "center",
    backgroundColor: HB_COLORS.brand,
    borderRadius: 16,
    height: 32,
    justifyContent: "center",
    width: 32,
  },
  avatarText: { color: HB_COLORS.white, fontSize: 12, fontWeight: "700" },
  employeeName: { color: HB_COLORS.textPrimary, flexShrink: 1, fontWeight: "600" },
  typeTag: { backgroundColor: HB_COLORS.surfaceMuted, borderRadius: 4, paddingHorizontal: 6, paddingVertical: 1 },
  typeTagText: { color: HB_COLORS.textSecondary, fontSize: 11, fontWeight: "600" },
  storeName: { color: HB_COLORS.textSecondary, flex: 1, textAlign: "right" },
  typeCard: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.md,
  },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  typeIcon: { alignItems: "center", alignSelf: "flex-start", borderRadius: 10, height: 40, justifyContent: "center", width: 40 },
  typeBody: { flex: 1, gap: 2 },
  typeTitle: { color: HB_COLORS.textPrimary, fontWeight: "700", marginBottom: 2 },
  requestRow: { gap: HB_SPACING.xxs, paddingVertical: HB_SPACING.sm },
  requestMain: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  badge: { borderRadius: 4, paddingHorizontal: 5, paddingVertical: 1 },
  badgeText: { fontSize: 11, fontWeight: "700" },
  requestText: { color: HB_COLORS.textPrimary, flex: 1, fontVariant: ["tabular-nums"] },
});
