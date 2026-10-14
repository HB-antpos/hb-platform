// 「抓拍与比对」页：留存抓拍 + 比对结果 + 名单命中与通知发送情况，按时间倒序。
// 列表不含任何照片（缩略图位置只是占位图标）；点开一条后才在详情里按需读取图片（见 CaptureResultDetail）。
// 筛选：判定 chip（全部 / 仅命中 / 员工 / 新访客 / 回头客）、门店、时间范围（默认今天）。
import { useEffect, useMemo, useState } from "react";
import { AppState, FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter } from "expo-router";
import { ActivityIndicator, Menu, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { CaptureResultDetail } from "./CaptureResultDetail";
import {
  CAPTURE_FILTERS,
  CAPTURE_RANGES,
  captureFilterParams,
  captureScoreParts,
  describeDecision,
  describeHitNotify,
  formatCaptureListTime,
  resolveCaptureRange,
  type CaptureFilterKey,
  type CaptureRangeKey,
} from "./capture-logic";
import { formatStoreLabel, useFaceCaptureResultsGuard, useFaceErrorMessage, useStoreNameMap } from "./shared";
import type { FaceCaptureResult } from "./types";
import { useCaptureResults, useFaceStoreOptions } from "./use-face-match";
import { FACE_SOFT, FaceCard, FaceScreenFrame, FaceScreenMessage, FilterChip, MONO, Notice, Tag, faceStyles } from "./ui";

export function FaceCaptureResultsScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useFaceCaptureResultsGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <FaceScreenMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的筛选与结果留在屏幕上。
  return <FaceCaptureResultsContent key={userGuid} onBack={goBack} />;
}

function FaceCaptureResultsContent({ onBack }: { onBack: () => void }) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const { stores } = useFaceStoreOptions();
  const storeNames = useStoreNameMap(stores);
  const [filterKey, setFilterKey] = useState<CaptureFilterKey>("all");
  const [rangeKey, setRangeKey] = useState<CaptureRangeKey>("today");
  const [rangeMenuOpen, setRangeMenuOpen] = useState(false);
  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [target, setTarget] = useState<FaceCaptureResult | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  // 「今天」按本地自然日算：应用在后台跨过午夜后回到前台，要换成新的一天（范围与查询键随之更新；同一天内键不变，不会重复请求）。
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next === "active") setNow(new Date());
    });
    return () => subscription.remove();
  }, []);
  const range = useMemo(() => resolveCaptureRange(rangeKey, now), [rangeKey, now]);
  const filters = useMemo(
    () => ({ storeCode: storeCode ?? undefined, ...captureFilterParams(filterKey), ...range }),
    [storeCode, filterKey, range],
  );
  const { query, items, refresh } = useCaptureResults(filters, true);
  const selectedStore = stores.find((store) => store.storeCode === storeCode) ?? null;
  const rangeLabelKey = CAPTURE_RANGES.find((option) => option.key === rangeKey)?.labelKey ?? "capture.range.today";

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
      <Notice tone="warning" text={t("capture.notice")} />
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
        {CAPTURE_FILTERS.map((filter) => (
          <FilterChip key={filter.key} label={t(filter.labelKey)} selected={filter.key === filterKey} onPress={() => setFilterKey(filter.key)} />
        ))}
      </ScrollView>
      <View style={faceStyles.rowWrap}>
        <FilterChip
          icon="storefront-outline"
          label={selectedStore ? selectedStore.storeName || selectedStore.storeCode : t("filters.allStores")}
          selected={storeCode !== null}
          onPress={() => setPickerOpen(true)}
        />
        <Menu
          visible={rangeMenuOpen}
          onDismiss={() => setRangeMenuOpen(false)}
          anchor={<FilterChip icon="calendar-range" label={t(rangeLabelKey)} selected={rangeKey !== "today"} onPress={() => setRangeMenuOpen(true)} />}
        >
          {CAPTURE_RANGES.map((option) => (
            <Menu.Item
              key={option.key}
              title={t(option.labelKey)}
              leadingIcon={option.key === rangeKey ? "check" : undefined}
              onPress={() => {
                setRangeKey(option.key);
                setRangeMenuOpen(false);
              }}
            />
          ))}
        </Menu>
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
        <EmptyState title={t("capture.empty")} />
      </View>
    );
  }

  return (
    <FaceScreenFrame title={t("capture.title")} onBack={onBack}>
      <FlatList
        data={items}
        keyExtractor={(item) => item.captureId}
        renderItem={({ item }) => (
          <CaptureCard
            result={item}
            storeLabel={storeCode ? null : formatStoreLabel(item.storeCode, storeNames)}
            onOpen={() => setTarget(item)}
          />
        )}
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
      {/* 仅点击后挂载；以抓拍编号作 key，切换即重新挂载，旧图片随之丢弃。 */}
      {target ? (
        <CaptureResultDetail
          key={target.captureId}
          result={target}
          storeLabel={formatStoreLabel(target.storeCode, storeNames)}
          onClose={() => setTarget(null)}
        />
      ) : null}
    </FaceScreenFrame>
  );
}

/** 一条抓拍：左侧占位图标（列表里不放真实照片），命中的整卡淡红高亮并标出通知发送情况。 */
function CaptureCard({ result, storeLabel, onOpen }: { result: FaceCaptureResult; storeLabel: string | null; onOpen: () => void }) {
  const { t } = useAppTranslation("faceMatch");
  const decision = describeDecision(result);
  const scores = captureScoreParts(result);
  const { hit } = result;
  const notify = hit ? describeHitNotify(hit) : null;
  return (
    <Pressable accessibilityRole="button" onPress={onOpen}>
      <FaceCard style={hit ? styles.hitCard : undefined}>
        <View style={styles.cardRow}>
          <View style={styles.thumb}>
            <MaterialCommunityIcons name="account" size={28} color={HB_COLORS.textSecondary} />
          </View>
          <View style={styles.cardBody}>
            <View style={faceStyles.rowBetween}>
              <Text style={[styles.time, MONO]}>{formatCaptureListTime(result.capturedAtUtc, new Date())}</Text>
              <Text style={[faceStyles.muted, styles.camera]} numberOfLines={1}>
                {result.cameraId}
              </Text>
            </View>
            {storeLabel ? <Text style={faceStyles.muted}>{storeLabel}</Text> : null}
            {scores.length > 0 ? (
              <Text style={[faceStyles.muted, MONO]}>{scores.map((part) => t(part.labelKey, { percent: part.percent })).join(" · ")}</Text>
            ) : null}
            <View style={faceStyles.rowWrap}>
              <Tag label={t(decision.labelKey)} tone={decision.tone} />
              {hit ? <Tag label={t("capture.hit.tag", { percent: hit.scorePercent })} tone="danger" /> : null}
              {result.isDuplicate ? <Tag label={t("capture.duplicate")} /> : null}
            </View>
            {notify ? (
              <View style={styles.notifyRow}>
                <MaterialCommunityIcons name={notify.icon} size={14} color={notify.tone === "success" ? HB_COLORS.success : HB_COLORS.textSecondary} />
                <Text style={faceStyles.muted}>{t(notify.labelKey, notify.params)}</Text>
              </View>
            ) : null}
          </View>
        </View>
      </FaceCard>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  content: { paddingBottom: HB_SPACING.xl },
  header: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.sm, gap: HB_SPACING.xs },
  chips: { gap: HB_SPACING.xs, paddingVertical: 2 },
  footer: { paddingVertical: HB_SPACING.md, textAlign: "center", alignSelf: "center" },
  footerError: { fontSize: 13, color: HB_COLORS.danger },
  hitCard: { backgroundColor: FACE_SOFT.danger, borderColor: "#FDA29B" },
  cardRow: { flexDirection: "row", gap: HB_SPACING.sm },
  thumb: {
    width: 56,
    height: 56,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  cardBody: { flex: 1, gap: 4 },
  time: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  camera: { flexShrink: 1 },
  notifyRow: { flexDirection: "row", alignItems: "center", gap: 4 },
});
