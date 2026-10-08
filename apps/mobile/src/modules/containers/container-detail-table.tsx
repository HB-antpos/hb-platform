import { memo, useCallback, useImperativeHandle, useMemo, useRef, type Ref } from "react";
import { Animated, FlatList, Platform, Pressable, StyleSheet, View, type ListRenderItemInfo } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  CONTAINER_DETAIL_CHECKBOX_WIDTH,
  CONTAINER_DETAIL_FIXED_COLUMN_WIDTH,
  CONTAINER_DETAIL_HEADER_HEIGHT,
  CONTAINER_DETAIL_ROW_HEIGHT,
  CONTAINER_DETAIL_SCROLL_COLUMNS,
  CONTAINER_DETAIL_SCROLL_COLUMNS_WIDTH,
  CONTAINER_DETAIL_TABLE_WIDTH,
  buildContainerDetailRowCells,
  getContainerDetailRowKey,
  getContainerDetailRowLayout,
  type ContainerDetailScrollColumnKey,
} from "./container-detail-table-columns";
import { LOCATE_ROW_VIEW_POSITION, type PageSelectionState } from "./container-detail-selection";
import { getDetailGuid } from "./query";
import type { ContainerDetail } from "./types";

/**
 * 货柜明细表格：固定首列 + 横向滚动列 + 吸顶表头 + 虚拟化。
 *
 * 实现要点（React Native 没有 sticky 列，纯 JS 方案）：
 * - 只有一个纵向 FlatList（唯一的纵向滚动源），整张表放进一个横向 Animated.ScrollView；
 * - 固定列不是单独的列表，而是每一行（含表头）里一个绝对定位的单元格，
 *   用 `translateX = 横向滚动偏移` 把它「钉」在可视区左缘；
 *   偏移由 Animated.event + useNativeDriver 在 UI 线程直接驱动，与滚动同帧、不经过 JS，不会有两个列表 onScroll 同步的错位/抖动；
 * - 左右两部分天然在同一行里，所以行对齐是结构性的，不依赖任何偏移同步；
 * - 行高固定，FlatList 用 getItemLayout，windowSize 限制只挂载约 3 屏的行，500 行一页也只渲染几十行；
 * - 表头在纵向 FlatList 之外（因此吸顶），但在横向滚动容器之内（因此与列同步横向滚动）。
 */

/** 勾选行的浅蓝底（半透明，叠在白底之上，不影响文字对比度）。 */
const SELECTED_ROW_TINT = "rgba(22, 119, 255, 0.08)";
/** 「定位到该行」后的短暂高亮。 */
const HIGHLIGHT_ROW_TINT = "rgba(181, 71, 8, 0.16)";
const SKELETON_ROWS = 8;

export interface ContainerDetailTableHandle {
  /** 滚动到指定下标的行（行高固定 + getItemLayout，结果精确） */
  scrollToRow: (index: number) => void;
}

export type ContainerDetailTableStatus = "loading" | "error" | "empty" | "ready";

interface ContainerDetailTableProps {
  details: readonly ContainerDetail[];
  /** 表格视口总高度（含表头），由页面根据可视区计算 */
  height: number;
  status: ContainerDetailTableStatus;
  errorMessage?: string;
  selectedSet: ReadonlySet<string>;
  highlightedHguid: string;
  pageSelection: PageSelectionState;
  /** 有筛选/搜索时空状态给出「清除筛选」入口 */
  onClearFilters?: () => void;
  onToggleRow: (hguid: string) => void;
  onToggleAll: () => void;
  onRowPress: (detail: ContainerDetail) => void;
  onRetry: () => void;
  controllerRef?: Ref<ContainerDetailTableHandle>;
}

interface RowLabels {
  newTag: string;
  existingTag: string;
  activeTag: string;
  inactiveTag: string;
  attention: string;
  retailMissing: string;
  selectRow: string;
}

function Tag({ label, tone }: { label: string; tone: "brand" | "neutral" | "warning" }) {
  return (
    <View style={[styles.tag, tone === "brand" && styles.tagBrand, tone === "warning" && styles.tagWarning]}>
      <Text numberOfLines={1} style={[styles.tagText, tone === "brand" && styles.tagTextBrand, tone === "warning" && styles.tagTextWarning]}>
        {label}
      </Text>
    </View>
  );
}

function CheckIcon({ state }: { state: "checked" | "partial" | "unchecked" }) {
  const name = state === "checked" ? "checkbox-marked" : state === "partial" ? "minus-box" : "checkbox-blank-outline";
  return (
    <MaterialCommunityIcons
      name={name}
      size={22}
      color={state === "unchecked" ? HB_COLORS.textSecondary : HB_COLORS.action}
    />
  );
}

interface TableRowProps {
  detail: ContainerDetail;
  selected: boolean;
  highlighted: boolean;
  scrollX: Animated.Value;
  labels: RowLabels;
  onToggle: (hguid: string) => void;
  onPress: (detail: ContainerDetail) => void;
}

/** 数据行：memo 后只有勾选/高亮/数据变化的行才会重渲染。 */
const TableRow = memo(function TableRow({ detail, selected, highlighted, scrollX, labels, onToggle, onPress }: TableRowProps) {
  const cells = useMemo(() => buildContainerDetailRowCells(detail), [detail]);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${cells.itemNumber} ${cells.productName}`.trim()}
      onPress={() => onPress(detail)}
      style={styles.row}
    >
      <View style={styles.scrollCells}>
        {CONTAINER_DETAIL_SCROLL_COLUMNS.map((column) => (
          <View key={column.key} style={[styles.cell, { width: column.width }, column.align === "right" && styles.cellRight]}>
            {renderScrollCell(column.key, cells, labels)}
          </View>
        ))}
      </View>
      {/* 固定列：绝对定位 + 随横向滚动偏移反向平移，始终贴在可视区左缘；底色必须不透明才能盖住下方滚动列 */}
      <Animated.View style={[styles.fixedCell, { transform: [{ translateX: scrollX }] }]}>
        <Pressable
          accessibilityRole="checkbox"
          accessibilityState={{ checked: selected }}
          accessibilityLabel={labels.selectRow}
          onPress={() => onToggle(cells.hguid)}
          style={styles.checkboxHit}
        >
          <CheckIcon state={selected ? "checked" : "unchecked"} />
        </Pressable>
        <View style={styles.fixedText}>
          <Text numberOfLines={1} style={styles.itemNumber}>{cells.itemNumber || "--"}</Text>
          <Text numberOfLines={1} style={styles.productName}>{cells.productName || "--"}</Text>
        </View>
      </Animated.View>
      {selected ? <View pointerEvents="none" style={[StyleSheet.absoluteFill, { backgroundColor: SELECTED_ROW_TINT }]} /> : null}
      {highlighted ? <View pointerEvents="none" style={[StyleSheet.absoluteFill, { backgroundColor: HIGHLIGHT_ROW_TINT }]} /> : null}
    </Pressable>
  );
});

function renderScrollCell(key: ContainerDetailScrollColumnKey, cells: ReturnType<typeof buildContainerDetailRowCells>, labels: RowLabels) {
  switch (key) {
    case "status":
      return (
        <View style={styles.statusCell}>
          <Tag label={cells.isNewProduct ? labels.newTag : labels.existingTag} tone={cells.isNewProduct ? "brand" : "neutral"} />
          <View style={styles.statusSecondLine}>
            <Tag label={cells.isActive ? labels.activeTag : labels.inactiveTag} tone={cells.isActive ? "neutral" : "warning"} />
            {cells.needsAttention ? (
              <MaterialCommunityIcons
                name="alert-circle-outline"
                size={16}
                color={HB_COLORS.warning}
                accessibilityLabel={labels.attention}
              />
            ) : null}
          </View>
        </View>
      );
    case "containerQuantity":
      return <Text style={styles.number}>{cells.containerQuantity}</Text>;
    case "middlePack":
      return <Text style={styles.number}>{cells.middlePack}</Text>;
    case "domesticPrice":
      return <Text style={styles.number}>{cells.domesticPrice}</Text>;
    case "warehouseImportPrice":
      return <Text style={styles.number}>{cells.warehouseImportPrice}</Text>;
    case "importPrice":
      return <Text style={styles.number}>{cells.importPrice}</Text>;
    case "retailPrice":
      return (
        <View style={styles.retailCell}>
          {cells.retailPriceMissing ? (
            <MaterialCommunityIcons name="alert-circle" size={14} color={HB_COLORS.danger} accessibilityLabel={labels.retailMissing} />
          ) : null}
          <Text style={[styles.number, cells.retailPriceMissing && styles.numberDanger]}>{cells.retailPrice}</Text>
        </View>
      );
  }
}

export function ContainerDetailTable({
  details,
  height,
  status,
  errorMessage,
  selectedSet,
  highlightedHguid,
  pageSelection,
  onClearFilters,
  onToggleRow,
  onToggleAll,
  onRowPress,
  onRetry,
  controllerRef,
}: ContainerDetailTableProps) {
  const { t } = useAppTranslation("containerDetail");
  const listRef = useRef<FlatList<ContainerDetail>>(null);
  // 横向滚动偏移：UI 线程直接驱动固定列平移
  const scrollX = useRef(new Animated.Value(0)).current;
  const onHorizontalScroll = useRef(
    Animated.event([{ nativeEvent: { contentOffset: { x: scrollX } } }], { useNativeDriver: true }),
  ).current;

  // 行回调走 ref，保证传给 memo 行的函数引用稳定，父组件重渲染不会让所有行跟着重渲染
  const onToggleRowRef = useRef(onToggleRow);
  const onRowPressRef = useRef(onRowPress);
  onToggleRowRef.current = onToggleRow;
  onRowPressRef.current = onRowPress;
  const stableToggle = useCallback((hguid: string) => onToggleRowRef.current(hguid), []);
  const stablePress = useCallback((detail: ContainerDetail) => onRowPressRef.current(detail), []);

  useImperativeHandle(controllerRef, () => ({
    scrollToRow: (index: number) => {
      listRef.current?.scrollToIndex({ index, viewPosition: LOCATE_ROW_VIEW_POSITION, animated: true });
    },
  }), []);

  const labels = useMemo<RowLabels>(() => ({
    newTag: t("table.tags.new"),
    existingTag: t("table.tags.existing"),
    activeTag: t("table.tags.active"),
    inactiveTag: t("table.tags.inactive"),
    attention: t("table.tags.attention"),
    retailMissing: t("table.retailMissing"),
    selectRow: t("table.selectRow"),
  }), [t]);

  const listExtra = useMemo(() => ({ selectedSet, highlightedHguid }), [selectedSet, highlightedHguid]);

  const renderItem = useCallback(({ item }: ListRenderItemInfo<ContainerDetail>) => {
    const hguid = getDetailGuid(item).trim();
    return (
      <TableRow
        detail={item}
        selected={Boolean(hguid) && selectedSet.has(hguid)}
        highlighted={Boolean(hguid) && hguid === highlightedHguid}
        scrollX={scrollX}
        labels={labels}
        onToggle={stableToggle}
        onPress={stablePress}
      />
    );
  }, [highlightedHguid, labels, scrollX, selectedSet, stablePress, stableToggle]);

  const keyExtractor = useCallback((item: ContainerDetail, index: number) => getContainerDetailRowKey(item, index), []);

  if (status === "loading") {
    return (
      <View style={[styles.frame, { height }]} accessibilityLabel={t("states.loading")}>
        <View style={styles.headerStatic}>
          <ActivityIndicator size="small" color={HB_COLORS.action} />
          <Text style={styles.headerStaticText}>{t("states.loading")}</Text>
        </View>
        {Array.from({ length: SKELETON_ROWS }, (_, index) => (
          <View key={index} style={styles.skeletonRow}>
            <View style={[styles.skeletonBar, { width: 28 }]} />
            <View style={styles.skeletonLines}>
              <View style={[styles.skeletonBar, { width: "62%" }]} />
              <View style={[styles.skeletonBar, { width: "86%", height: 8 }]} />
            </View>
            <View style={[styles.skeletonBar, { width: 52 }]} />
          </View>
        ))}
      </View>
    );
  }

  if (status === "error") {
    return (
      <View style={[styles.frame, styles.statePanel, { height }]}>
        <MaterialCommunityIcons name="alert-circle-outline" size={32} color={HB_COLORS.danger} />
        <Text style={styles.stateTitle}>{t("states.loadFailed")}</Text>
        {errorMessage ? <Text style={styles.stateDescription} numberOfLines={3}>{errorMessage}</Text> : null}
        <Button mode="contained" icon="refresh" onPress={onRetry} style={styles.retryButton} contentStyle={styles.retryButtonContent}>
          {t("actions.retry")}
        </Button>
      </View>
    );
  }

  if (status === "empty") {
    return (
      <View style={[styles.frame, styles.statePanel, { height }]}>
        <EmptyState
          title={t("states.empty")}
          description={t("states.emptyHint")}
          actionLabel={onClearFilters ? t("states.clearFilters") : undefined}
          onAction={onClearFilters}
        />
      </View>
    );
  }

  const headerState = pageSelection.allSelected ? "checked" : pageSelection.partiallySelected ? "partial" : "unchecked";

  return (
    <View style={[styles.frame, { height }]}>
      <Animated.ScrollView
        horizontal
        directionalLockEnabled
        nestedScrollEnabled
        bounces={false}
        showsHorizontalScrollIndicator
        scrollEventThrottle={16}
        onScroll={onHorizontalScroll}
        style={{ height }}
        contentContainerStyle={{ width: CONTAINER_DETAIL_TABLE_WIDTH, height }}
      >
        <View style={{ width: CONTAINER_DETAIL_TABLE_WIDTH, height }}>
          <View style={styles.headerRow}>
            <View style={[styles.scrollCells, styles.headerCells]}>
              {CONTAINER_DETAIL_SCROLL_COLUMNS.map((column) => (
                <View key={column.key} style={[styles.cell, { width: column.width }, column.align === "right" && styles.cellRight]}>
                  <Text numberOfLines={1} style={styles.headerText}>{t(`table.columns.${column.key}`)}</Text>
                </View>
              ))}
            </View>
            <Animated.View style={[styles.fixedHeaderCell, { transform: [{ translateX: scrollX }] }]}>
              <Pressable
                accessibilityRole="checkbox"
                accessibilityState={{ checked: pageSelection.allSelected }}
                accessibilityLabel={t("table.selectAllOnPage")}
                onPress={onToggleAll}
                style={styles.checkboxHit}
              >
                <CheckIcon state={headerState} />
              </Pressable>
              <Text numberOfLines={1} style={[styles.headerText, styles.fixedHeaderText]}>{t("table.columns.product")}</Text>
            </Animated.View>
          </View>
          <FlatList
            ref={listRef}
            data={details as ContainerDetail[]}
            extraData={listExtra}
            keyExtractor={keyExtractor}
            renderItem={renderItem}
            getItemLayout={getContainerDetailRowLayout}
            // 一屏约 6~10 行：首屏多渲一点避免白屏，windowSize=7 上下各缓冲约 3 屏
            initialNumToRender={14}
            maxToRenderPerBatch={14}
            updateCellsBatchingPeriod={40}
            windowSize={7}
            // Android 上卸载屏外原生视图能明显减内存；iOS 无收益且有已知闪烁问题，故只在 Android 开启
            removeClippedSubviews={Platform.OS === "android"}
            nestedScrollEnabled
            showsVerticalScrollIndicator={false}
            style={{ width: CONTAINER_DETAIL_TABLE_WIDTH, height: height - CONTAINER_DETAIL_HEADER_HEIGHT }}
            onScrollToIndexFailed={(info) => {
              // 行高固定理论上不会失败；兜底直接按偏移滚动
              listRef.current?.scrollToOffset({ offset: info.index * CONTAINER_DETAIL_ROW_HEIGHT, animated: true });
            }}
          />
        </View>
      </Animated.ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  frame: {
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    overflow: "hidden",
  },
  headerRow: {
    width: CONTAINER_DETAIL_TABLE_WIDTH,
    height: CONTAINER_DETAIL_HEADER_HEIGHT,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderBottomWidth: 1,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  headerCells: { height: CONTAINER_DETAIL_HEADER_HEIGHT },
  headerText: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700" },
  fixedHeaderText: { flex: 1, paddingRight: HB_SPACING.xs },
  fixedHeaderCell: {
    position: "absolute",
    left: 0,
    top: 0,
    bottom: 0,
    width: CONTAINER_DETAIL_FIXED_COLUMN_WIDTH,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRightWidth: 1,
    borderRightColor: HB_COLORS.outlineMuted,
  },
  row: {
    width: CONTAINER_DETAIL_TABLE_WIDTH,
    height: CONTAINER_DETAIL_ROW_HEIGHT,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outline,
  },
  // 滚动列整体右移一个固定列宽，让出被固定列覆盖的位置
  scrollCells: {
    marginLeft: CONTAINER_DETAIL_FIXED_COLUMN_WIDTH,
    width: CONTAINER_DETAIL_SCROLL_COLUMNS_WIDTH,
    height: "100%",
    flexDirection: "row",
    alignItems: "center",
  },
  cell: { height: "100%", justifyContent: "center", paddingHorizontal: HB_SPACING.xs },
  cellRight: { alignItems: "flex-end" },
  fixedCell: {
    position: "absolute",
    left: 0,
    top: 0,
    bottom: 0,
    width: CONTAINER_DETAIL_FIXED_COLUMN_WIDTH,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderRightWidth: 1,
    borderRightColor: HB_COLORS.outlineMuted,
  },
  checkboxHit: {
    width: CONTAINER_DETAIL_CHECKBOX_WIDTH,
    height: "100%",
    alignItems: "center",
    justifyContent: "center",
  },
  fixedText: { flex: 1, minWidth: 0, paddingRight: HB_SPACING.xs, gap: 2 },
  itemNumber: { color: HB_COLORS.textPrimary, fontSize: 13, lineHeight: 17, fontWeight: "700" },
  productName: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 16 },
  number: { color: HB_COLORS.textPrimary, fontSize: 13, fontVariant: ["tabular-nums"] },
  numberDanger: { color: HB_COLORS.danger, fontWeight: "700" },
  retailCell: { flexDirection: "row", alignItems: "center", gap: 4 },
  statusCell: { gap: 4 },
  statusSecondLine: { flexDirection: "row", alignItems: "center", gap: 4 },
  tag: {
    alignSelf: "flex-start",
    height: 20,
    paddingHorizontal: 6,
    borderRadius: 4,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    justifyContent: "center",
  },
  tagBrand: { borderColor: HB_COLORS.brand },
  tagWarning: { borderColor: HB_COLORS.warning },
  tagText: { color: HB_COLORS.textSecondary, fontSize: 11, lineHeight: 14, fontWeight: "600" },
  tagTextBrand: { color: HB_COLORS.action },
  tagTextWarning: { color: HB_COLORS.warning },
  headerStatic: {
    height: CONTAINER_DETAIL_HEADER_HEIGHT,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  headerStaticText: { color: HB_COLORS.textSecondary, fontSize: 12 },
  skeletonRow: {
    height: CONTAINER_DETAIL_ROW_HEIGHT,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outline,
  },
  skeletonLines: { flex: 1, gap: 6 },
  skeletonBar: { height: 12, borderRadius: 4, backgroundColor: HB_COLORS.surfaceMuted },
  statePanel: { alignItems: "center", justifyContent: "center", gap: HB_SPACING.xs, padding: HB_SPACING.md },
  stateTitle: { color: HB_COLORS.textPrimary, fontSize: 16, fontWeight: "700" },
  stateDescription: { color: HB_COLORS.textSecondary, fontSize: 13, textAlign: "center" },
  retryButton: { borderRadius: HB_RADIUS.control },
  retryButtonContent: { minHeight: 44 },
});
