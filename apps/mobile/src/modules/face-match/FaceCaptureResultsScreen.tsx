// 「抓拍」内容面板与独立页：留存抓拍 + 比对结果 + 黑名单命中流，按时间倒序。
// - 抓拍流（默认）：判定 chip（全部 / 员工 / 新访客 / 回头客）、门店、时间范围（默认今天）；
// - 「黑名单」chip：数据源换成命中流（按命中任务查，不依赖留存抓拍；Match 模式不留存抓拍但命中通知照发），
//   有二级筛选（全部 / 未读 / 待复核 / 自动确认 / 已确认 / 已驳回）、「共 N 条 · 未读 M」与「全部标为已读」；
// - 头像位显示缩略图（服务端缩到最长边 128px）：只加载屏幕里看得到的、一批最多 30 项、只放内存（thumbnail-loader / use-thumbnails），
//   页头提示条右侧的「眼睛」一键隐藏 / 显示并记住选择；离开页面、切后台、隐藏时立即销毁。整图仍在详情里按需读取（CaptureResultDetail）。
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AppState, FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, View, type ViewToken } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useIsFocused } from "@react-navigation/native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Menu, Snackbar, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { CaptureCard } from "./CaptureCard";
import { CaptureResultDetail } from "./CaptureResultDetail";
import {
  CAPTURE_FILTERS,
  CAPTURE_RANGES,
  HIT_STATUS_FILTERS,
  blacklistChipBadge,
  captureFilterParams,
  captureResultToRow,
  collectThumbnailTargets,
  describeHitListBar,
  describeHitStatusChip,
  describeRowThumbnail,
  hitResultToRow,
  isBlacklistFilter,
  planRowThumbnail,
  resolveCaptureRange,
  resolveInitialSelection,
  type CaptureFilterKey,
  type CaptureRangeKey,
  type CaptureRow,
  type ThumbDisplay,
} from "./capture-logic";
import { formatStoreLabel, useFaceCaptureResultsGuard, useFaceErrorMessage, useStoreNameMap } from "./shared";
import { thumbnailKey } from "./thumbnail-loader";
import type { FaceHitStatusKey } from "./types";
import { useThumbnails, useThumbnailsVisible } from "./use-thumbnails";
import { useCaptureResults, useFaceStoreOptions, useHitReadActions, useHitResults, useHitSummary } from "./use-face-match";
import { FaceScreenFrame, FaceScreenMessage, FilterChip, Notice, faceStyles } from "./ui";

export function FaceCaptureResultsScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useFaceCaptureResultsGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <FaceScreenMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的筛选与结果留在屏幕上。
  return (
    <FaceScreenFrame title={t("capture.title")} onBack={goBack}>
      <CapturePanel key={userGuid} />
    </FaceScreenFrame>
  );
}

/** 命中流的状态筛选（与后端 hitStatus 取值一致）。 */
export type CaptureHitStatusKey = FaceHitStatusKey;

export interface CapturePanelProps {
  /** 初始筛选：blacklist = 直接进「黑名单」命中流（通知跳转用）。 */
  initialFilter?: "all" | "blacklist";
  /** 命中流的初始状态筛选（只在 initialFilter 为 blacklist 时有意义）。 */
  initialHitStatus?: CaptureHitStatusKey;
}

/** 屏幕可见比例达到这个值才算「可见」。 */
const VIEWABILITY_CONFIG = { itemVisiblePercentThreshold: 20 } as const;

/** 「抓拍」内容面板：不含页面框架与权限守卫，由外壳（单页或「人脸比对」合并页）负责。 */
export function CapturePanel({ initialFilter, initialHitStatus }: CapturePanelProps = {}) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const { stores } = useFaceStoreOptions();
  const storeNames = useStoreNameMap(stores);
  // 入口参数只读取初始值（外壳在参数变化时以它们作 key 重建面板）。
  const [selection] = useState(() => resolveInitialSelection(initialFilter, initialHitStatus));
  const [filterKey, setFilterKey] = useState<CaptureFilterKey>(selection.filterKey);
  const [hitStatus, setHitStatus] = useState<FaceHitStatusKey>(selection.hitStatus);
  const [rangeKey, setRangeKey] = useState<CaptureRangeKey>("today");
  const [rangeMenuOpen, setRangeMenuOpen] = useState(false);
  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [target, setTarget] = useState<CaptureRow | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [markingAll, setMarkingAll] = useState(false);
  const [toast, setToast] = useState("");
  // 「今天」按本地自然日算：应用在后台跨过午夜后回到前台，要换成新的一天（范围与查询键随之更新；同一天内键不变，不会重复请求）。
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next === "active") setNow(new Date());
    });
    return () => subscription.remove();
  }, []);
  const range = useMemo(() => resolveCaptureRange(rangeKey, now), [rangeKey, now]);

  const blacklist = isBlacklistFilter(filterKey);
  const captureFilters = useMemo(
    () => ({ storeCode: storeCode ?? undefined, ...captureFilterParams(filterKey), ...range }),
    [storeCode, filterKey, range],
  );
  const hitFilters = useMemo(() => ({ storeCode: storeCode ?? undefined, hitStatus, ...range }), [storeCode, hitStatus, range]);
  const summaryFilters = useMemo(() => ({ storeCode: storeCode ?? undefined, ...range }), [storeCode, range]);
  const captureList = useCaptureResults(captureFilters, !blacklist);
  const hitList = useHitResults(hitFilters, blacklist);
  // 汇总始终取：「黑名单」chip 的红色数字与二级筛选的数字都来自它。
  const summaryQuery = useHitSummary(summaryFilters, true);
  const summary = summaryQuery.data ?? null;
  const { markRead, markAllRead } = useHitReadActions();
  const query = blacklist ? hitList.query : captureList.query;
  const refreshList = blacklist ? hitList.refresh : captureList.refresh;
  const rows = useMemo<CaptureRow[]>(
    () => (blacklist ? hitList.items.map(hitResultToRow) : captureList.items.map(captureResultToRow)),
    [blacklist, hitList.items, captureList.items],
  );
  const selectedStore = stores.find((store) => store.storeCode === storeCode) ?? null;
  const rangeLabelKey = CAPTURE_RANGES.find((option) => option.key === rangeKey)?.labelKey ?? "capture.range.today";

  // ───────── 缩略图：只加载看得到的几行（加前后预取），只放内存 ─────────
  const isFocused = useIsFocused();
  const { visible: thumbsVisible, ready: thumbsReady, setVisible: setThumbsVisible } = useThumbnailsVisible();
  const thumbsEnabled = thumbsReady && thumbsVisible;
  // 页面失焦（压入别的路由）时销毁缩略图；外壳切走「抓拍」页签会卸载面板，同样销毁。切后台由钩子自己处理。
  const thumbs = useThumbnails(thumbsEnabled && isFocused);
  const rowsRef = useRef(rows);
  rowsRef.current = rows;
  const visibleIndexesRef = useRef<number[]>([]);
  const requestThumbnails = thumbs.request;
  const requestVisible = useCallback(() => {
    requestThumbnails(collectThumbnailTargets(rowsRef.current, visibleIndexesRef.current));
  }, [requestThumbnails]);
  const requestVisibleRef = useRef(requestVisible);
  requestVisibleRef.current = requestVisible;
  // FlatList 不允许运行中更换 onViewableItemsChanged，所以用稳定的回调，内部读最新的 ref。
  const onViewableItemsChanged = useRef(({ viewableItems }: { viewableItems: ViewToken[] }) => {
    visibleIndexesRef.current = viewableItems.map((token) => token.index).filter((index): index is number => typeof index === "number");
    requestVisibleRef.current();
  }).current;
  // 翻页追加 / 筛选切换 / 刷新之后可见的行没变也要补取（此时 FlatList 不一定再回调）。
  useEffect(() => {
    requestVisible();
  }, [rows, requestVisible]);

  const toggleThumbs = () => {
    // 隐藏：先同步清空内存里的缩略图并中止在途请求，再记住选择。
    if (thumbsVisible) thumbs.clear();
    setThumbsVisible(!thumbsVisible);
  };
  const thumbFor = (row: CaptureRow): ThumbDisplay => {
    const plan = planRowThumbnail(row);
    const state = plan.type === "request" ? thumbs.states.get(thumbnailKey(plan.target)) : undefined;
    return describeRowThumbnail(plan, state, thumbsEnabled);
  };
  const listExtra = useMemo(() => ({ states: thumbs.states, enabled: thumbsEnabled }), [thumbs.states, thumbsEnabled]);

  const onRefresh = async () => {
    setRefreshing(true);
    // 失败的缩略图只在下拉刷新时重试。
    thumbs.retryFailed();
    try {
      await Promise.all([refreshList(), summaryQuery.refetch()]);
    } finally {
      setRefreshing(false);
    }
  };

  // 打开详情：命中还没读的，顺手把我这条通知标为已读（成功后写回红色数字、更新列表行与汇总；失败静默，不影响看详情）。
  const openRow = (row: CaptureRow) => {
    setTarget(row);
    const hit = row.hit;
    if (hit?.unread) void markRead(hit.taskId).catch(() => undefined);
  };

  const onMarkAllRead = async () => {
    if (markingAll) return;
    setMarkingAll(true);
    try {
      await markAllRead();
    } catch {
      setToast(t("hits.markAllFailed"));
    } finally {
      setMarkingAll(false);
    }
  };

  const bar = describeHitListBar(summary, hitStatus, { count: rows.length, hasMore: Boolean(query.hasNextPage) });
  const barText = [
    bar.total !== null ? t("hits.bar.total", { count: bar.total }) : null,
    bar.unread !== null ? t("hits.bar.unread", { count: bar.unread }) : null,
  ]
    .filter(Boolean)
    .join(" · ");

  const header = (
    <View style={styles.header}>
      <Notice
        tone="warning"
        text={t("capture.notice")}
        trailing={
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t(thumbsVisible ? "thumb.hide" : "thumb.show")}
            accessibilityHint={t("thumb.hint")}
            hitSlop={8}
            onPress={toggleThumbs}
            style={styles.eye}
          >
            <MaterialCommunityIcons name={thumbsVisible ? "eye-outline" : "eye-off-outline"} size={22} color={HB_COLORS.warning} />
          </Pressable>
        }
      />
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
        {CAPTURE_FILTERS.map((filter) =>
          filter.key === "blacklist" ? (
            <FilterChip
              key={filter.key}
              icon="shield-alert-outline"
              tone="danger"
              label={t(filter.labelKey)}
              badge={blacklistChipBadge(summary)}
              accessibilityLabel={summary && summary.unread > 0 ? `${t(filter.labelKey)}, ${t("hits.bar.unread", { count: summary.unread })}` : undefined}
              selected={blacklist}
              onPress={() => setFilterKey(filter.key)}
            />
          ) : (
            <FilterChip key={filter.key} label={t(filter.labelKey)} selected={filter.key === filterKey} onPress={() => setFilterKey(filter.key)} />
          ),
        )}
      </ScrollView>
      {blacklist ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
          {HIT_STATUS_FILTERS.map((filter) => {
            const chip = describeHitStatusChip(summary, filter.key);
            return (
              <FilterChip
                key={filter.key}
                compact
                label={chip.countText ? `${t(chip.labelKey)} ${chip.countText}` : t(chip.labelKey)}
                selected={filter.key === hitStatus}
                onPress={() => setHitStatus(filter.key)}
              />
            );
          })}
        </ScrollView>
      ) : null}
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
      {blacklist && barText ? (
        <View style={styles.listBar}>
          <Text style={styles.listBarText}>{barText}</Text>
          {bar.canMarkAll ? (
            <Pressable accessibilityRole="button" disabled={markingAll} hitSlop={8} onPress={() => void onMarkAllRead()} style={styles.markAll}>
              {markingAll ? <ActivityIndicator size="small" color={HB_COLORS.action} /> : <Text style={styles.markAllText}>{t("hits.markAll")}</Text>}
            </Pressable>
          ) : null}
        </View>
      ) : null}
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
  } else if (rows.length > 0 && !query.hasNextPage) {
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
  } else if (query.isError && rows.length === 0) {
    empty = (
      <View style={faceStyles.state}>
        <EmptyState title={t("states.loadFailed")} description={describeError(query.error)} actionLabel={t("common:actions.retry")} onAction={() => void query.refetch()} />
      </View>
    );
  } else {
    empty = (
      <View style={faceStyles.state}>
        <EmptyState title={blacklist ? t("hits.empty") : t("capture.empty")} />
      </View>
    );
  }

  return (
    <>
      <FlatList
        data={rows}
        extraData={listExtra}
        keyExtractor={(row) => row.key}
        renderItem={({ item }) => (
          <CaptureCard row={item} storeLabel={storeCode ? null : formatStoreLabel(item.storeCode, storeNames)} thumb={thumbFor(item)} onOpen={() => openRow(item)} />
        )}
        ListHeaderComponent={header}
        ListEmptyComponent={empty}
        ListFooterComponent={footer}
        onViewableItemsChanged={onViewableItemsChanged}
        viewabilityConfig={VIEWABILITY_CONFIG}
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
      {/* 仅点击后挂载；以行 key 作 key，切换即重新挂载，旧图片随之丢弃。 */}
      {target ? (
        <CaptureResultDetail key={target.key} result={target} storeLabel={formatStoreLabel(target.storeCode, storeNames)} onClose={() => setTarget(null)} />
      ) : null}
      <Snackbar visible={Boolean(toast)} onDismiss={() => setToast("")} duration={3500}>
        {toast}
      </Snackbar>
    </>
  );
}

const styles = StyleSheet.create({
  content: { paddingBottom: HB_SPACING.xl },
  header: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.sm, gap: HB_SPACING.xs },
  chips: { gap: HB_SPACING.xs, paddingVertical: 2 },
  // 提示条右侧的「眼睛」：36px 的按钮 + 8px 触控扩展，上下负边距让它不撑高提示条。
  eye: { width: 36, height: 36, alignItems: "center", justifyContent: "center", marginVertical: -8, marginRight: -6 },
  listBar: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs, minHeight: 28 },
  listBarText: { flex: 1, fontSize: 13, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  markAll: { minWidth: 48, minHeight: 28, alignItems: "flex-end", justifyContent: "center" },
  markAllText: { fontSize: 13, fontWeight: "700", color: HB_COLORS.action },
  footer: { paddingVertical: HB_SPACING.md, textAlign: "center", alignSelf: "center" },
  footerError: { fontSize: 13, color: HB_COLORS.danger },
});
