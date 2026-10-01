import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Card, Chip, Icon, IconButton, Text } from "react-native-paper";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createManagedAttendancePunchAdjustment,
  getManagedAttendanceRecords,
  previewManagedAttendancePunchAdjustment,
} from "@/modules/attendance/api";
import {
  classifyManagedRecord,
  isManagedRecordException,
  type ManagedRecordIssue,
  sortManagedRecords,
} from "@/modules/attendance/attendance-managed-records";
import { resolveAttendancePunchDisplayTime } from "@/modules/attendance/attendance-device-time";
import type { AttendanceScheduleSession } from "@/modules/attendance/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { type AttendanceStatusTone, ATTENDANCE_STATUS_TONES, StatusPill } from "./AdjustmentFormControls";
import { ManagedPunchEditSheet } from "./ManagedPunchEditSheet";

const ISSUE_TONES: Record<ManagedRecordIssue, AttendanceStatusTone> = {
  missingClockOut: "danger",
  noPunch: "danger",
  late: "warning",
  earlyLeave: "warning",
  inProgress: "accent",
  notStarted: "neutral",
  normal: "success",
};

function toDateString(date: Date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

function shiftDate(value: string, days: number) {
  const date = new Date(`${value}T00:00:00`);
  date.setDate(date.getDate() + days);
  return toDateString(date);
}

function formatTime(value?: string) {
  if (!value) return "--:--";
  const timePart = value.includes("T") ? value.split("T").pop() : value;
  return timePart?.slice(0, 5) || "--:--";
}

function initials(name?: string) {
  const trimmed = name?.trim() ?? "";
  if (!trimmed) return "?";
  const words = trimmed.split(/\s+/);
  return words.length > 1
    ? `${words[0][0]}${words[words.length - 1][0]}`.toUpperCase()
    : trimmed.slice(0, 2).toUpperCase();
}

/** 首段上班与末段下班，作为列表行的一眼摘要。 */
function summarizePunches(session: AttendanceScheduleSession) {
  const first = session.segments[0];
  const last = session.segments[session.segments.length - 1];
  return {
    clockIn: formatTime(resolveAttendancePunchDisplayTime(first?.clockIn)),
    clockOut: formatTime(resolveAttendancePunchDisplayTime(last?.clockOut)),
  };
}

export function ManagedPunchRecordsCard({
  initialWorkDate,
  storeCode,
  canAdjust,
  onMessage,
}: {
  /** 从审核「补录下班」跳转时定位到的日期，默认今天。 */
  initialWorkDate?: string;
  storeCode?: string;
  canAdjust: boolean;
  onMessage: (message: string) => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const queryClient = useQueryClient();
  const today = useMemo(() => toDateString(new Date()), []);
  const [workDate, setWorkDate] = useState(initialWorkDate ?? today);
  const [exceptionsOnly, setExceptionsOnly] = useState(false);
  const [editingSession, setEditingSession] = useState<AttendanceScheduleSession>();

  const recordsQuery = useQuery({
    queryKey: ["attendance", "managed", "records", storeCode ?? "", workDate],
    queryFn: () => getManagedAttendanceRecords({ storeCode: storeCode!, workDate }),
    enabled: Boolean(storeCode),
  });
  const previewMutation = useMutation({ mutationFn: previewManagedAttendancePunchAdjustment });
  const createMutation = useMutation({
    mutationFn: createManagedAttendancePunchAdjustment,
    onSuccess: async () => {
      onMessage(t("managedRecords.saved"));
      // 修改会改变该员工的工时、加班候选与待审事项，三处一起刷新。
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["attendance", "managed", "records"] }),
        queryClient.invalidateQueries({ queryKey: ["attendance", "approvals"] }),
        queryClient.invalidateQueries({ queryKey: ["attendance", "my", "today"] }),
      ]);
    },
  });

  const rows = useMemo(() => sortManagedRecords(
    (recordsQuery.data ?? []).map((session) => ({ session, issue: classifyManagedRecord(session, today) })),
  ), [recordsQuery.data, today]);
  const exceptionCount = rows.filter((row) => isManagedRecordException(row.issue)).length;
  const visibleRows = exceptionsOnly ? rows.filter((row) => isManagedRecordException(row.issue)) : rows;

  if (!storeCode) {
    return (
      <Card mode="outlined" style={styles.card}>
        <Card.Content>
          <Text variant="bodySmall" style={styles.muted}>{t("managedRecords.noStore")}</Text>
        </Card.Content>
      </Card>
    );
  }

  return (
    <Card mode="outlined" style={styles.card}>
      <Card.Content style={styles.content}>
        <View style={styles.dateBar}>
          <IconButton
            icon="chevron-left"
            accessibilityLabel={t("managedRecords.previousDay")}
            onPress={() => setWorkDate((current) => shiftDate(current, -1))}
          />
          <View style={styles.dateLabel}>
            <Text variant="titleSmall" style={styles.tabular}>{workDate}</Text>
            {workDate === today ? <StatusPill label={t("managedRecords.today")} tone="accent" /> : null}
          </View>
          <IconButton
            icon="chevron-right"
            accessibilityLabel={t("managedRecords.nextDay")}
            disabled={workDate >= today}
            onPress={() => setWorkDate((current) => shiftDate(current, 1))}
          />
        </View>

        <View style={styles.filters}>
          <Chip
            compact
            selected={!exceptionsOnly}
            showSelectedCheck={false}
            style={!exceptionsOnly ? styles.filterSelected : styles.filter}
            onPress={() => setExceptionsOnly(false)}
          >
            {t("managedRecords.filterAll", { count: rows.length })}
          </Chip>
          <Chip
            compact
            selected={exceptionsOnly}
            showSelectedCheck={false}
            style={exceptionsOnly ? styles.filterSelected : styles.filter}
            onPress={() => setExceptionsOnly(true)}
          >
            {t("managedRecords.filterException", { count: exceptionCount })}
          </Chip>
          {workDate !== today ? (
            <Chip compact style={styles.filter} onPress={() => setWorkDate(today)}>
              {t("managedRecords.today")}
            </Chip>
          ) : null}
        </View>

        {recordsQuery.isLoading ? <ActivityIndicator /> : null}
        {recordsQuery.error ? (
          <Text variant="bodySmall" style={styles.dangerText} accessibilityRole="alert">
            {t("managedRecords.loadFailed")}
          </Text>
        ) : null}
        {!recordsQuery.isLoading && !recordsQuery.error && !visibleRows.length ? (
          <Text variant="bodySmall" style={styles.muted}>
            {exceptionsOnly ? t("managedRecords.emptyException") : t("managedRecords.empty")}
          </Text>
        ) : null}

        <View>
          {visibleRows.map(({ session, issue }, index) => {
            const summary = summarizePunches(session);
            return (
              <Pressable
                key={session.scheduleGuid}
                accessibilityRole="button"
                onPress={() => setEditingSession(session)}
                style={({ pressed }) => [
                  styles.row,
                  index === visibleRows.length - 1 ? styles.rowLast : null,
                  pressed ? styles.rowPressed : null,
                ]}
              >
                <View style={styles.avatar}>
                  <Text variant="labelMedium" style={styles.avatarText}>{initials(session.employeeName)}</Text>
                </View>
                <View style={styles.rowBody}>
                  <View style={styles.rowHeader}>
                    <Text variant="bodyLarge" numberOfLines={1} style={styles.flexText}>
                      {session.employeeName || session.userGuid}
                    </Text>
                    <StatusPill label={t(`managedRecords.issues.${issue}`)} tone={ISSUE_TONES[issue]} />
                  </View>
                  <Text variant="bodySmall" style={[styles.muted, styles.tabular]}>
                    {`${formatTime(session.startTime)}–${formatTime(session.endTime)} · `}
                    {t("managedRecords.punchSummary", summary)}
                  </Text>
                </View>
                <Icon source="chevron-right" size={20} color={HB_COLORS.textSecondary} />
              </Pressable>
            );
          })}
        </View>
      </Card.Content>

      <ManagedPunchEditSheet
        visible={Boolean(editingSession)}
        session={editingSession}
        today={today}
        canAdjust={canAdjust}
        isBusy={previewMutation.isPending || createMutation.isPending}
        onPreview={(payload) => previewMutation.mutateAsync(payload)}
        onSubmit={async (payload) => {
          await createMutation.mutateAsync(payload);
        }}
        onDismiss={() => setEditingSession(undefined)}
      />
    </Card>
  );
}

const styles = StyleSheet.create({
  avatar: {
    alignItems: "center",
    backgroundColor: ATTENDANCE_STATUS_TONES.accent.background,
    borderRadius: 18,
    height: 36,
    justifyContent: "center",
    width: 36,
  },
  avatarText: { color: ATTENDANCE_STATUS_TONES.accent.text },
  card: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    elevation: 0,
  },
  content: { gap: HB_SPACING.xs },
  dangerText: { color: HB_COLORS.danger },
  dateBar: { alignItems: "center", flexDirection: "row", justifyContent: "space-between" },
  dateLabel: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  filter: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.outline, borderWidth: StyleSheet.hairlineWidth },
  filterSelected: { backgroundColor: ATTENDANCE_STATUS_TONES.accent.background },
  filters: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  flexText: { flex: 1 },
  muted: { color: HB_COLORS.textSecondary },
  row: {
    alignItems: "center",
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.sm,
    minHeight: 60,
    paddingVertical: HB_SPACING.xs,
  },
  rowBody: { flex: 1, gap: 2 },
  rowHeader: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  rowLast: { borderBottomWidth: 0 },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  tabular: { fontVariant: ["tabular-nums"] },
});
