import type { ReactNode } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import type { UseQueryResult } from "@tanstack/react-query";
import { ActivityIndicator, Button, Chip, Text } from "react-native-paper";
import { WeeklyScheduleTable } from "@/components/attendance/WeeklyScheduleTable";
import type { AttendanceWeek } from "@/modules/attendance/types";
import { InfoCard } from "@/modules/people";
import {
  StaffAttendanceEndpointUnavailableError,
  type StaffAttendanceRecord,
} from "@/modules/users";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

function formatDate(value: string | undefined, locale: string) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value.slice(0, 10);
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(parsed);
}

function formatDateTime(value: string | undefined, locale: string) {
  if (!value) return null;
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(parsed);
}

function formatShortValue(value: string | undefined) {
  if (!value) return null;
  if (value.includes("T")) return value.split("T").pop()?.slice(0, 5) ?? value;
  return value.length >= 5 && value.includes(":") ? value.slice(0, 5) : value;
}

function StateCard({ children }: { children: ReactNode }) {
  return <InfoCard style={styles.stateCard}>{children}</InfoCard>;
}

/**
 * 排班 / 考勤记录完整视图（详情页内部切换，不新增路由）。
 * 数据由详情页的同一批查询提供（摘要行与完整视图共用缓存），这里只负责渲染各种状态。
 */
export function ScheduleRecordsView({
  mode,
  weekQuery,
  recordsQuery,
}: {
  mode: "schedule" | "records";
  weekQuery: UseQueryResult<AttendanceWeek>;
  recordsQuery: UseQueryResult<StaffAttendanceRecord[]>;
}) {
  const { t, language } = useAppTranslation(["userManagement", "attendance", "common"]);
  const locale = resolveLocaleTag(language);

  const renderError = (error: unknown, kind: "schedule" | "records", onRetry: () => void) => (
    <StateCard>
      <Text variant="titleMedium">{t(`detail.${kind}.loadFailedTitle`)}</Text>
      <Text variant="bodyMedium" style={styles.muted}>
        {error instanceof StaffAttendanceEndpointUnavailableError
          ? t(`detail.${kind}.endpointUnavailable`)
          : resolveLocalizedErrorMessage(error, { t, language, fallbackKey: `detail.${kind}.loadFailedDescription` })}
      </Text>
      <Button mode="outlined" icon="refresh" onPress={onRetry}>{t("common:actions.retry")}</Button>
    </StateCard>
  );

  const renderLoading = (kind: "schedule" | "records") => (
    <StateCard>
      <ActivityIndicator size="small" />
      <Text variant="bodyMedium" style={styles.muted}>{t(`detail.${kind}.loading`)}</Text>
    </StateCard>
  );

  if (mode === "schedule") {
    return (
      <ScrollView contentContainerStyle={styles.content}>
        {weekQuery.isLoading && !weekQuery.data ? renderLoading("schedule") : null}
        {weekQuery.isError && !weekQuery.data ? renderError(weekQuery.error, "schedule", () => void weekQuery.refetch()) : null}
        {weekQuery.data || (!weekQuery.isLoading && !weekQuery.isError) ? (
          <>
            <Text variant="bodyMedium" style={styles.muted}>{t("detail.schedule.description")}</Text>
            <WeeklyScheduleTable week={weekQuery.data} />
          </>
        ) : null}
      </ScrollView>
    );
  }

  const getRecordTypeLabel = (record: StaffAttendanceRecord) => {
    if (record.type === "Leave") {
      return record.leaveType
        ? t(`attendance:leaveTypes.${record.leaveType}`, record.leaveType)
        : t("detail.records.types.leave");
    }
    if (record.type === "Approval") {
      return record.sourceType
        ? t(`attendance:sourceTypes.${record.sourceType}`, record.sourceType)
        : t("detail.records.types.approval");
    }
    if (record.punchType) return t(`attendance:punchTypes.${record.punchType}`, record.punchType);
    return t("detail.records.types.punch");
  };

  const renderRecordMeta = (record: StaffAttendanceRecord) => {
    const startValue = record.startTime?.includes("-") ? formatDate(record.startTime, locale) : formatShortValue(record.startTime);
    const endValue = record.endTime?.includes("-") ? formatDate(record.endTime, locale) : formatShortValue(record.endTime);
    const rows: { label: string; value: string | null }[] = [];
    if (record.type === "Leave") {
      rows.push({
        label: t("detail.records.dateRange"),
        value: startValue && endValue ? `${startValue} - ${endValue}` : startValue || endValue,
      });
    } else {
      rows.push({ label: t("detail.records.workDate"), value: formatDate(record.workDate, locale) });
    }
    const submittedAt = formatDateTime(record.submittedAt, locale);
    if (submittedAt) rows.push({ label: t("detail.records.submittedAt"), value: submittedAt });
    if (record.storeName || record.storeCode) {
      rows.push({ label: t("detail.records.store"), value: record.storeName || record.storeCode || null });
    }
    return rows
      .filter((row) => row.value)
      .map((row) => (
        <View key={`${record.recordGuid}-${row.label}`} style={styles.recordMetaRow}>
          <Text variant="labelMedium" style={styles.muted}>{row.label}</Text>
          <Text variant="bodyMedium">{row.value}</Text>
        </View>
      ));
  };

  const records = recordsQuery.data ?? [];
  return (
    <ScrollView contentContainerStyle={styles.content}>
      {recordsQuery.isLoading && !recordsQuery.data ? renderLoading("records") : null}
      {recordsQuery.isError && !recordsQuery.data ? renderError(recordsQuery.error, "records", () => void recordsQuery.refetch()) : null}
      {!recordsQuery.isLoading && !recordsQuery.isError && !records.length ? (
        <StateCard>
          <Text variant="titleMedium">{t("detail.records.emptyTitle")}</Text>
          <Text variant="bodyMedium" style={styles.muted}>{t("detail.records.emptyDescription")}</Text>
        </StateCard>
      ) : null}
      {records.length ? (
        <>
          <Text variant="bodyMedium" style={styles.muted}>{t("detail.records.description")}</Text>
          {records.map((record) => (
            <View key={`${record.type}-${record.recordGuid || record.workDate}`} style={styles.recordCard}>
              <View style={styles.recordHeader}>
                <View style={styles.recordHeaderCopy}>
                  <Text variant="titleSmall">{getRecordTypeLabel(record)}</Text>
                </View>
                <Chip compact>{t(`attendance:statuses.${record.status}`, record.status)}</Chip>
              </View>
              <View style={styles.recordMetaBlock}>{renderRecordMeta(record)}</View>
              {record.detail ? <Text variant="bodySmall" style={styles.muted}>{record.detail}</Text> : null}
            </View>
          ))}
        </>
      ) : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  content: { gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xl, paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.xs },
  muted: { color: HB_COLORS.textSecondary },
  stateCard: { alignItems: "center", gap: HB_SPACING.xs, padding: HB_SPACING.md },
  recordCard: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
  },
  recordHeader: { alignItems: "flex-start", flexDirection: "row", gap: 8, justifyContent: "space-between" },
  recordHeaderCopy: { flex: 1, gap: 2 },
  recordMetaBlock: { gap: 6 },
  recordMetaRow: { gap: 2 },
});
