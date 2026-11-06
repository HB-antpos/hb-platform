import { useCallback, useMemo, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { FilterChipRow, InfoCard, NoticeBox, PeopleTag, type PeopleTone } from "@/modules/people";
import { PeopleScreenFrame } from "@/modules/people/people-frame";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { PERMISSIONS } from "@/shared/utils/access";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useStaffNoticeBatch, useStaffNoticeBatches, useStaffNoticeRetry } from "./hooks";
import {
  channelMark,
  countFailedRecipients,
  filterBatches,
  formatNoticeTime,
  isBatchFinished,
  type StaffNoticeRecordFilter,
} from "./logic";
import { StaffNoticeProgressSheet } from "./progress-sheet";
import type { StaffNoticeBatch, StaffNoticeBatchResult } from "./types";
import { templateLabel } from "./ui";

const RESULT_TONES: Record<StaffNoticeBatchResult, PeopleTone> = {
  running: "blue",
  all_sent: "green",
  partial_failed: "amber",
  all_failed: "red",
};

const NO_BATCHES: StaffNoticeBatch[] = [];

/**
 * 发送记录（`/users/notices`）：按批次列出，展开看逐人结果，失败项可重试。
 * 店长只看得到自己发的，管理员看全部（后端按账号过滤）。只存打码信息，不存密码和正文。
 */
export function StaffNoticeRecordsScreen() {
  const router = useRouter();
  const { t, language } = useAppTranslation(["staffNotices", "common"]);
  const access = useAuthStore((state) => state.access);
  // 客户端不认别名，必须写「全局码 OR 店员码」。
  const canResetPasswords = access.isAdmin
    || access.hasPermission(PERMISSIONS.Users.ResetPassword)
    || access.hasPermission(PERMISSIONS.Users.ResetStoreStaffPassword);

  const batchesQuery = useStaffNoticeBatches();
  const [filter, setFilter] = useState<StaffNoticeRecordFilter>("all");
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [progress, setProgress] = useState<{ batchId: string; templateName: string } | null>(null);

  const batches = useMemo(
    () => batchesQuery.data?.pages.flatMap((page) => page.items) ?? NO_BATCHES,
    [batchesQuery.data],
  );
  const visible = useMemo(() => filterBatches(batches, filter), [batches, filter]);
  const chrome = useMemo(() => ({ tabBar: null, onBack: () => router.back() }), [router]);
  const { hasNextPage, isFetchingNextPage, fetchNextPage, refetch } = batchesQuery;

  const handleEndReached = useCallback(() => {
    if (hasNextPage && !isFetchingNextPage) void fetchNextPage();
  }, [fetchNextPage, hasNextPage, isFetchingNextPage]);

  if (!canResetPasswords) {
    return (
      <PeopleScreenFrame title={t("records.title")} chrome={chrome}>
        <EmptyState title={t("review.noAccessTitle")} description={t("review.noAccessDescription")} />
      </PeopleScreenFrame>
    );
  }

  return (
    <PeopleScreenFrame
      title={t("records.title")}
      subtitle={access.isAdmin ? t("records.subtitleAll") : t("records.subtitleMine")}
      chrome={chrome}
    >
      <View style={styles.filters}>
        <FilterChipRow
          items={[
            { key: "all" as const, label: t("records.filters.all") },
            { key: "failed" as const, label: t("records.filters.failed") },
            { key: "running" as const, label: t("records.filters.running") },
          ]}
          value={filter}
          onChange={setFilter}
        />
      </View>
      <FlatList
        data={visible}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        onEndReached={handleEndReached}
        onEndReachedThreshold={0.4}
        refreshControl={<RefreshControl refreshing={batchesQuery.isRefetching && !isFetchingNextPage} onRefresh={() => void refetch()} />}
        renderItem={({ item }) => (
          <BatchCard
            batch={item}
            showActor={access.isAdmin}
            expanded={expandedId === item.id}
            onToggle={() => setExpandedId((current) => (current === item.id ? null : item.id))}
            onRetried={() => setProgress({
              batchId: item.id,
              templateName: templateLabel({ key: item.templateKey, name: item.templateName, description: "" }, t).name,
            })}
          />
        )}
        ListEmptyComponent={
          batchesQuery.isLoading ? (
            <View style={styles.loading}><ActivityIndicator /></View>
          ) : batchesQuery.isError ? (
            <EmptyState
              title={t("records.loadFailed")}
              description={resolveLocalizedErrorMessage(batchesQuery.error, { t, language, fallbackKey: "records.loadFailed" })}
              primaryAction={{ label: t("common:actions.retry"), icon: "refresh", onPress: () => void refetch() }}
            />
          ) : (
            <EmptyState title={t("records.emptyTitle")} description={t("records.emptyDescription")} />
          )
        }
        ListFooterComponent={isFetchingNextPage ? <View style={styles.loading}><ActivityIndicator /></View> : null}
      />
      {progress ? (
        <StaffNoticeProgressSheet
          batchId={progress.batchId}
          templateName={progress.templateName}
          onClose={() => setProgress(null)}
        />
      ) : null}
    </PeopleScreenFrame>
  );
}

function BatchCard({
  batch,
  showActor,
  expanded,
  onToggle,
  onRetried,
}: {
  batch: StaffNoticeBatch;
  showActor: boolean;
  expanded: boolean;
  onToggle: () => void;
  onRetried: () => void;
}) {
  const { t } = useAppTranslation("staffNotices");
  const templateName = templateLabel({ key: batch.templateKey, name: batch.templateName, description: "" }, t).name;
  const time = formatNoticeTime(batch.createdAtUtc, { today: t("time.today"), yesterday: t("time.yesterday") });
  const parts = [
    time,
    t("records.people", { count: batch.totalCount }),
    batch.emailRequested > 0 ? t("result.emailCount", { sent: batch.emailSent, total: batch.emailRequested }) : null,
    batch.smsRequested > 0 ? t("result.smsCount", { sent: batch.smsSent, total: batch.smsRequested }) : null,
    batch.smsSenderId ? t("records.sender", { name: batch.smsSenderId }) : null,
  ].filter(Boolean);

  return (
    <InfoCard style={styles.card}>
      <Pressable accessibilityRole="button" accessibilityState={{ expanded }} onPress={onToggle} style={styles.cardHeader}>
        <View style={styles.cardMain}>
          <View style={styles.cardTitleRow}>
            <Text style={styles.cardTitle} numberOfLines={1}>{templateName}</Text>
            <PeopleTag label={t(`records.results.${batch.result}`)} tone={RESULT_TONES[batch.result]} />
          </View>
          <Text style={styles.cardMeta}>{parts.join(" · ")}</Text>
          {showActor ? <Text style={styles.cardMeta}>{t("records.createdBy", { name: batch.createdBy })}</Text> : null}
        </View>
        <Icon source={expanded ? "chevron-up" : "chevron-down"} size={20} color="#98A2B3" />
      </Pressable>
      {expanded ? <BatchDetail batchId={batch.id} onRetried={onRetried} /> : null}
    </InfoCard>
  );
}

function BatchDetail({ batchId, onRetried }: { batchId: string; onRetried: () => void }) {
  const { t, language } = useAppTranslation(["staffNotices", "common"]);
  // 还没结束的批次在展开时继续刷新，结束后自动停。
  const detailQuery = useStaffNoticeBatch(batchId, { poll: true });
  const retryMutation = useStaffNoticeRetry();
  const [retryError, setRetryError] = useState<string | null>(null);
  const detail = detailQuery.data;

  const handleRetry = async () => {
    setRetryError(null);
    try {
      await retryMutation.mutateAsync(batchId);
      onRetried();
    } catch (error) {
      setRetryError(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "result.retryFailed" }));
      void detailQuery.refetch();
    }
  };

  if (!detail) {
    return detailQuery.isError ? (
      <View style={styles.detailPad}>
        <NoticeBox tone="red" icon="alert-circle-outline">
          {resolveLocalizedErrorMessage(detailQuery.error, { t, language, fallbackKey: "records.loadFailed" })}
        </NoticeBox>
      </View>
    ) : (
      <View style={styles.loading}><ActivityIndicator /></View>
    );
  }

  return (
    <View>
      {detail.recipients.map((row) => (
        <View key={row.id} style={styles.detailRow}>
          <View style={styles.detailTop}>
            <Text style={styles.detailName} numberOfLines={1}>{row.displayName}</Text>
            <Text style={styles.detailMarks}>
              {row.emailStatus !== "none" ? `${t("channels.email")} ${channelMark(row.emailStatus)}` : ""}
              {row.emailStatus !== "none" && row.smsStatus !== "none" ? "  " : ""}
              {row.smsStatus !== "none" ? `${t("channels.sms")} ${channelMark(row.smsStatus)}` : ""}
            </Text>
          </View>
          {row.note ? <Text style={styles.detailNote}>{row.note}</Text> : null}
        </View>
      ))}
      {retryError ? <View style={styles.detailPad}><NoticeBox tone="red" icon="alert-circle-outline">{retryError}</NoticeBox></View> : null}
      {detail.canRetry && isBatchFinished(detail) ? (
        <View style={styles.detailActions}>
          <Button
            mode="outlined"
            icon="refresh"
            onPress={() => void handleRetry()}
            loading={retryMutation.isPending}
            disabled={retryMutation.isPending}
            style={styles.retry}
          >
            {t("result.retry", { count: countFailedRecipients(detail) })}
          </Button>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { marginBottom: HB_SPACING.sm },
  cardHeader: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, padding: 14 },
  cardMain: { flex: 1, gap: 3, minWidth: 0 },
  cardMeta: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  cardTitle: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 15, fontWeight: "700" },
  cardTitleRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  detailActions: { alignItems: "flex-end", borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, padding: HB_SPACING.sm },
  detailMarks: { color: HB_COLORS.textSecondary, flexShrink: 0, fontSize: 12.5 },
  detailName: { color: HB_COLORS.textPrimary, flex: 1, fontSize: 14, fontWeight: "600" },
  detailNote: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 2 },
  detailPad: { padding: HB_SPACING.sm },
  detailRow: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, paddingHorizontal: 14, paddingVertical: 10 },
  detailTop: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  filters: { backgroundColor: HB_COLORS.white, paddingBottom: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md },
  list: { padding: HB_SPACING.md, paddingBottom: HB_SPACING.xl },
  loading: { alignItems: "center", paddingVertical: HB_SPACING.lg },
  retry: { borderRadius: HB_RADIUS.control },
});
