// 「命中复核」分区：复核任务列表（待复核 / 已驳回 / 已确认），点开后在全屏弹框里并排看现场抓拍与条目参考照片。
// 列表不含姓名、事件细节，也不含任何图片；图片只在打开复核弹框后按需读取，弹框关闭立即丢弃。
import { useState } from "react";
import { FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import type { Store } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { isWriteBlocked } from "./access";
import { formatLocalDateTime } from "./dates";
import {
  REVIEW_STATUS_FILTERS,
  REVIEW_STATUS_LABEL_KEYS,
  reviewStatusFromFilter,
  reviewStatusTone,
  type ReviewStatusFilterKey,
} from "./entries-logic";
import { ReviewTaskModal } from "./ReviewTaskModal";
import { BAND_LABEL_KEYS, REVIEW_STEP_HINT_KEYS, formatSimilarityPercent, getReviewStep } from "./review-logic";
import { formatStoreLabel, useFaceErrorMessage, useStoreNameMap } from "./shared";
import type { FaceMatchConfig, FaceUnwelcomeTask } from "./types";
import { useInvalidateUnwelcome, useUnwelcomeTasks } from "./use-face-match";
import { FaceCard, FilterChip, MONO, Notice, Tag, faceStyles } from "./ui";

export function ReviewPanel({ config, stores }: { config: FaceMatchConfig; stores: readonly Store[] }) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const invalidate = useInvalidateUnwelcome();
  const storeNames = useStoreNameMap(stores);
  const [statusKey, setStatusKey] = useState<ReviewStatusFilterKey>("pending");
  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [target, setTarget] = useState<FaceUnwelcomeTask | null>(null);
  const [snack, setSnack] = useState("");
  const [refreshing, setRefreshing] = useState(false);
  const { query, items, refresh } = useUnwelcomeTasks(reviewStatusFromFilter(statusKey), storeCode, true);
  const writeBlocked = isWriteBlocked(config);
  const selectedStore = stores.find((store) => store.storeCode === storeCode) ?? null;

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await refresh();
    } finally {
      setRefreshing(false);
    }
  };

  const header = (
    <View style={styles.header}>
      <Notice tone="warning" text={t("review.intro")} />
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
        {REVIEW_STATUS_FILTERS.map((filter) => (
          <FilterChip key={filter.key} label={t(filter.labelKey)} selected={filter.key === statusKey} onPress={() => setStatusKey(filter.key)} />
        ))}
      </ScrollView>
      <View style={faceStyles.rowWrap}>
        <FilterChip
          icon="storefront-outline"
          label={selectedStore ? selectedStore.storeName || selectedStore.storeCode : t("filters.allStores")}
          selected={storeCode !== null}
          onPress={() => setPickerOpen(true)}
        />
      </View>
    </View>
  );

  let footer = null;
  if (query.isFetchingNextPage) footer = <ActivityIndicator size="small" color={HB_COLORS.brand} style={styles.footer} />;
  else if (query.isFetchNextPageError) {
    footer = (
      <Pressable accessibilityRole="button" onPress={() => void query.fetchNextPage()} style={styles.footer}>
        <Text style={styles.footerError}>{t("states.loadMoreFailed")}</Text>
      </Pressable>
    );
  } else if (items.length > 0 && !query.hasNextPage) {
    footer = <Text style={[faceStyles.muted, styles.footer]}>{t("states.end")}</Text>;
  }

  let empty;
  if (query.isPending) {
    empty = (
      <View style={faceStyles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={faceStyles.muted}>{t("states.loading")}</Text>
      </View>
    );
  } else if (query.isError && items.length === 0) {
    empty = (
      <View style={faceStyles.state}>
        <EmptyState title={t("states.loadFailed")} description={describeError(query.error)} actionLabel={t("common:actions.retry")} onAction={() => void query.refetch()} />
      </View>
    );
  } else {
    empty = (
      <View style={faceStyles.state}>
        <EmptyState title={t("review.empty")} />
      </View>
    );
  }

  return (
    <View style={styles.flex}>
      <FlatList
        data={items}
        keyExtractor={(item) => item.taskId}
        renderItem={({ item }) => <TaskCard task={item} storeLabel={formatStoreLabel(item.storeCode, storeNames)} writeBlocked={writeBlocked} onOpen={() => setTarget(item)} />}
        ListHeaderComponent={header}
        ListEmptyComponent={empty}
        ListFooterComponent={footer}
        onEndReachedThreshold={0.4}
        onEndReached={() => {
          if (query.hasNextPage && !query.isFetchingNextPage && !query.isFetchNextPageError) void query.fetchNextPage();
        }}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={HB_COLORS.brand} />}
        contentContainerStyle={styles.content}
      />
      <StorePickerModal
        presentation="sheet"
        visible={pickerOpen}
        stores={[...stores]}
        selectedStoreCode={storeCode}
        includeAllOption
        allLabel={t("filters.allStores")}
        title={t("filters.pickStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setPickerOpen(false)}
        onSelectStore={(store) => {
          setStoreCode(store?.storeCode ?? null);
          setPickerOpen(false);
        }}
      />
      {/* 仅点击后挂载；以 taskId 作 key，切换任务即重新挂载，旧图片随之丢弃。 */}
      {target ? (
        <ReviewTaskModal
          key={target.taskId}
          task={target}
          storeLabel={formatStoreLabel(target.storeCode, storeNames)}
          writeBlocked={writeBlocked}
          onClose={() => setTarget(null)}
          onReviewed={(verdict) => {
            setSnack(t(verdict === "match" ? "review.submittedMatch" : "review.submittedNotMatch"));
            void invalidate();
          }}
          onStale={() => void invalidate()}
        />
      ) : null}
      <Snackbar visible={snack !== ""} onDismiss={() => setSnack("")} duration={3500}>
        {snack}
      </Snackbar>
    </View>
  );
}

function TaskCard({
  task,
  storeLabel,
  writeBlocked,
  onOpen,
}: {
  task: FaceUnwelcomeTask;
  storeLabel: string;
  writeBlocked: boolean;
  onOpen: () => void;
}) {
  const { t } = useAppTranslation("faceMatch");
  const statusKey = REVIEW_STATUS_LABEL_KEYS[task.reviewStatus];
  const percent = formatSimilarityPercent(task);
  const canReviewNow = task.canReview && !writeBlocked && task.imageAvailable;
  return (
    <FaceCard>
      <View style={styles.titleRow}>
        <Text style={styles.store} numberOfLines={1}>
          {storeLabel}
        </Text>
        <Tag label={statusKey ? t(statusKey) : task.reviewStatusName} tone={reviewStatusTone(task.reviewStatus)} />
      </View>
      <Text style={[styles.time, MONO]}>{formatLocalDateTime(task.capturedAtUtc)}</Text>
      {/* 口径写在文案里：这是相似度，不等于是同一个人的概率；任何位置都不单独出现一个裸百分比。 */}
      <Text style={[styles.similarity, MONO]}>{t("review.similarity", { percent })}</Text>
      <View style={styles.similarityRow}>
        <Tag label={t(BAND_LABEL_KEYS[task.band])} tone={task.band === "high" ? "danger" : "warning"} />
        {/* 系统按门店阈值自动确认的任务：没有人判断过，单独打标签，避免被误认为人工确认。 */}
        {task.autoConfirmed ? <Tag label={t("review.autoConfirmedTag")} tone="info" icon="robot-outline" /> : null}
        {!task.imageAvailable ? <Tag label={t("review.imageDeleted")} /> : null}
      </View>
      <Text style={faceStyles.muted}>{t(REVIEW_STEP_HINT_KEYS[getReviewStep(task)])}</Text>
      {/* 复核人：新任务只有一位；secondReviewerName 只会出现在旧版遗留的、已有两位复核人的任务上，按「补充复核人」展示。 */}
      {task.autoConfirmed ? (
        <Text style={faceStyles.muted}>{t("review.autoConfirmedBy")}</Text>
      ) : task.firstReviewerName ? (
        <Text style={faceStyles.muted}>{t("review.reviewerFirst", { name: task.firstReviewerName })}</Text>
      ) : null}
      {task.secondReviewerName ? (
        <Text style={faceStyles.muted}>{t("review.reviewerSecond", { name: task.secondReviewerName })}</Text>
      ) : null}
      <View style={styles.actions}>
        <Button mode={canReviewNow ? "contained" : "outlined"} compact disabled={!task.imageAvailable} onPress={onOpen}>
          {canReviewNow ? t("review.actions.review") : t("review.actions.view")}
        </Button>
      </View>
    </FaceCard>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  content: { paddingBottom: HB_SPACING.xl },
  header: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm, paddingBottom: HB_SPACING.xs, gap: HB_SPACING.xs },
  chips: { gap: HB_SPACING.xs, paddingVertical: 2 },
  footer: { paddingVertical: HB_SPACING.sm, alignItems: "center", textAlign: "center" },
  footerError: { fontSize: 13, color: HB_COLORS.action },
  titleRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  store: { flex: 1, fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  time: { fontSize: 13, color: HB_COLORS.textSecondary },
  similarityRow: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: HB_SPACING.xs },
  similarity: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  actions: { flexDirection: "row", marginTop: HB_SPACING.xxs },
});
