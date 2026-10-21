import { memo, useCallback, useState, type ReactNode, type RefObject } from "react";
import {
  Pressable,
  ScrollView,
  StyleSheet,
  View,
  type LayoutChangeEvent,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from "react-native";
import { Text } from "react-native-paper";
import { LINKED_COLORS } from "@/modules/product-report/linked-colors";
import { PRODUCT_CARD_COLORS } from "@/modules/product-report/product-report-card-parts";
import type { ReportSort, ReportSortField } from "@/modules/product-report/sorting";
import { REPORT_UI } from "@/modules/reports/report-card-parts";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 供应商紧凑表：左列（序号 + 供应商名）固定，右侧所有指标列放在同一个横向滚动容器里整体左右滑。
 * 首屏（不滑动）能看到 金额 · 数量 · 增长率 · 占比，其余 均价 · 毛利额 · 毛利率 · 总占比 左滑后出现。
 * 只负责排版：取值、格式化、联动与打开弹窗都由 product-report-screen 通过行模型传入。
 */

export type SupplierTableColumnKey =
  | "revenue"
  | "quantity"
  | "share"
  | "averagePrice"
  | "grossProfit"
  | "grossMargin"
  | "totalShare";

export interface SupplierTableCell {
  value: string;
  /** 同期值；null / undefined 表示该位置不显示同期（澳洲页签）。 */
  compare?: string | null;
  /** 成本未齐等待补全时用琥珀色提示。 */
  pending?: boolean;
  /** 占比迷你条长度（0-100）；只有占比列带条。 */
  barPercent?: number;
}

export interface SupplierTableRow {
  id: string;
  rank: string;
  title: string;
  /** 名称下方的供应商编号；空串表示不显示（后端缺名时名称已经回退成编号）。 */
  code: string;
  selected: boolean;
  accessibilityLabel: string;
  /** 点任意位置（名称、金额、其它指标）都走这一个回调：打开供应商选择弹窗。 */
  onPress: () => void;
  /** 增长率胶囊由调用方渲染，保持与其它卡片同一套口径。 */
  growth: ReactNode;
  cells: Record<SupplierTableColumnKey, SupplierTableCell>;
}

interface ColumnSpec {
  key: SupplierTableColumnKey | "growth";
  width: number;
  labelKey: string;
  sortField?: ReportSortField;
}

// 首屏四列 + 左滑四列。宽度按 393pt 屏幕设计：固定列 112 + 首屏四列 234 + 右缘露出下一列约 13pt 作为可滑动提示。
export const SUPPLIER_TABLE_FROZEN_WIDTH = 112;
const FIRST_SCREEN_COLUMNS: readonly ColumnSpec[] = [
  { key: "revenue", width: 70, labelKey: "productReport.columns.amount", sortField: "amount" },
  { key: "quantity", width: 46, labelKey: "productReport.columns.quantity", sortField: "quantity" },
  { key: "growth", width: 64, labelKey: "productReport.metrics.growthRate" },
  { key: "share", width: 54, labelKey: "productReport.supplierTable.share" },
];
const SWIPE_COLUMNS: readonly ColumnSpec[] = [
  { key: "averagePrice", width: 60, labelKey: "productReport.columns.averagePrice", sortField: "unitPrice" },
  { key: "grossProfit", width: 68, labelKey: "productReport.metrics.grossProfit" },
  { key: "grossMargin", width: 62, labelKey: "productReport.metrics.grossMarginRate" },
  { key: "totalShare", width: 62, labelKey: "productReport.metrics.totalShare" },
];
const COLUMNS: readonly ColumnSpec[] = [...FIRST_SCREEN_COLUMNS, ...SWIPE_COLUMNS];
const TRACK_WIDTH = COLUMNS.reduce((sum, column) => sum + column.width, 0);

// 行高固定，左列与右侧逐行对齐：中国页签每格多一行同期小字、名称下多一行编号，所以更高。
const HEADER_HEIGHT = 36;
const ROW_HEIGHT = 52;
const ROW_HEIGHT_WITH_COMPARE = 58;

type ScrollPhase = "start" | "middle" | "end";

// 列宽固定：实测 flexBasis + flexGrow 会被文字内容撑开，表头与数据行的列对不齐；
// 屏幕比整张表宽（平板）时多出的空间由每行末尾的 filler 吃掉。
const columnStyle = (width: number) => ({ width, flexShrink: 0 }) as const;

function SupplierNameCell({ row, alwaysShowCode }: { row: SupplierTableRow; alwaysShowCode: boolean }) {
  // 澳洲页签：名称一行放得下才显示编号；中国页签：编号固定显示在名称下方。
  const [nameLines, setNameLines] = useState(2);
  const showCode = row.code !== "" && (alwaysShowCode || nameLines <= 1);
  return (
    <View style={styles.nameCell}>
      <Text
        style={[styles.name, row.selected ? styles.nameSelected : null]}
        numberOfLines={2}
        onTextLayout={(event) => setNameLines(event.nativeEvent.lines.length)}
      >
        {row.title}
      </Text>
      {showCode ? <Text style={styles.code} numberOfLines={1}>{row.code}</Text> : null}
    </View>
  );
}

function SupplierValueCell({
  column,
  cell,
  compareLabel,
}: {
  column: ColumnSpec;
  cell: SupplierTableCell;
  compareLabel: string;
}) {
  const isRevenue = column.key === "revenue";
  return (
    <View style={[styles.cell, columnStyle(column.width)]}>
      <Text
        style={[
          styles.value,
          isRevenue ? styles.revenueValue : null,
          cell.pending ? styles.pendingValue : null,
        ]}
        numberOfLines={cell.pending ? 2 : 1}
        adjustsFontSizeToFit={!cell.pending}
        minimumFontScale={0.7}
      >
        {cell.value}
      </Text>
      {/* 同期小字很短，不做自动缩放：iOS 首次布局时 adjustsFontSizeToFit 偶发把它缩到下限（模拟器实测过）。 */}
      {cell.compare != null ? (
        <Text
          style={styles.compare}
          numberOfLines={1}
          accessibilityLabel={`${compareLabel} ${cell.compare}`}
        >
          {cell.compare}
        </Text>
      ) : null}
      {cell.barPercent !== undefined ? (
        <View style={styles.miniTrack}>
          <View style={[styles.miniFill, { width: `${Math.min(100, Math.max(0, cell.barPercent))}%` }]} />
        </View>
      ) : null}
    </View>
  );
}

// 右缘渐隐：没有渐变依赖，用几条逐渐变白的窄条模拟，提示右边还有列。
const FADE_ALPHAS = [0.15, 0.35, 0.55, 0.75, 0.92] as const;
function EdgeFade({ top }: { top: number }) {
  return (
    <View pointerEvents="none" style={[styles.edgeFade, { top }]}>
      {FADE_ALPHAS.map((alpha) => (
        <View key={alpha} style={[styles.edgeFadeStrip, { backgroundColor: `rgba(255,255,255,${alpha})` }]} />
      ))}
    </View>
  );
}

function SupplierReportTableImpl({
  rows,
  isChina,
  hint,
  sort,
  onSort,
  firstRowRef,
  onFirstRowLayout,
}: {
  rows: readonly SupplierTableRow[];
  isChina: boolean;
  hint: string;
  sort: ReportSort;
  onSort: (field: ReportSortField) => void;
  firstRowRef?: RefObject<View | null>;
  onFirstRowLayout?: () => void;
}) {
  const { t } = useAppTranslation("common");
  const [phase, setPhase] = useState<ScrollPhase>("start");
  const [viewportWidth, setViewportWidth] = useState(0);
  const rowHeight = isChina ? ROW_HEIGHT_WITH_COMPARE : ROW_HEIGHT;
  const canScroll = viewportWidth > 0 && TRACK_WIDTH > viewportWidth + 1;
  const compareLabel = t("productReport.metrics.compare");

  const handleScroll = useCallback((event: NativeSyntheticEvent<NativeScrollEvent>) => {
    const x = event.nativeEvent.contentOffset.x;
    // 只在阶段切换时更新状态，滑动过程中不会每帧重渲染整张表。
    setPhase(x <= 2 ? "start" : x >= TRACK_WIDTH - viewportWidth - 2 ? "end" : "middle");
  }, [viewportWidth]);
  const handleViewportLayout = useCallback((event: LayoutChangeEvent) => {
    setViewportWidth(event.nativeEvent.layout.width);
  }, []);

  const subLabel = (key: ColumnSpec["key"]) => {
    if (key === "share") {
      return isChina ? t("productReport.chinaGoods.compareShareOfChina") : t("productReport.supplierTable.shareRevenue");
    }
    if (key === "totalShare" && isChina) return t("productReport.chinaGoods.compareShareOfTotal");
    // 中国页签每格下方的灰色小字是同期，在最常看的两列表头标注一次。
    if (isChina && (key === "revenue" || key === "averagePrice")) return t("productReport.supplierTable.compareLegend");
    return null;
  };

  return (
    <View>
      <View style={styles.hintRow}>
        <Text style={styles.hintText} numberOfLines={2}>{hint}</Text>
        {canScroll ? (
          <View style={styles.swipePill}>
            <Text style={styles.swipePillText} numberOfLines={1}>
              {phase === "end" ? `→ ${t("productReport.supplierTable.swipeBack")}` : `← ${t("productReport.supplierTable.swipeMore")}`}
            </Text>
          </View>
        ) : null}
      </View>
      <View style={styles.table}>
        <View
          style={[styles.frozen, { width: SUPPLIER_TABLE_FROZEN_WIDTH }, phase !== "start" ? styles.frozenShadow : null]}
        >
          <View style={[styles.frozenHead, { height: HEADER_HEIGHT }]}>
            <Text style={styles.headLabel} numberOfLines={1}>{t("productReport.sections.suppliers")}</Text>
          </View>
          {rows.map((row, index) => (
            <Pressable
              key={row.id}
              ref={index === 0 ? firstRowRef : undefined}
              collapsable={index === 0 ? false : undefined}
              onLayout={index === 0 ? onFirstRowLayout : undefined}
              accessibilityRole="button"
              accessibilityLabel={row.accessibilityLabel}
              accessibilityState={{ selected: row.selected }}
              onPress={row.onPress}
              style={[styles.frozenRow, { height: rowHeight }, row.selected ? styles.rowSelected : null]}
            >
              {row.selected ? <View style={styles.selectedAccent} /> : null}
              <Text style={styles.rank} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.7}>{row.rank}</Text>
              <SupplierNameCell row={row} alwaysShowCode={isChina} />
            </Pressable>
          ))}
        </View>
        <View style={styles.scrollSide} onLayout={handleViewportLayout}>
          <ScrollView
            horizontal
            bounces={false}
            showsHorizontalScrollIndicator={false}
            onScroll={handleScroll}
            scrollEventThrottle={16}
            contentContainerStyle={styles.scrollContent}
          >
            <View style={styles.track}>
              <View style={[styles.headRow, { height: HEADER_HEIGHT }]}>
                {COLUMNS.map((column) => {
                  const sortable = column.sortField !== undefined;
                  const active = sortable && sort.field === column.sortField;
                  const label = t(column.labelKey);
                  const sub = subLabel(column.key);
                  const content = (
                    <>
                      <Text
                        style={[styles.headLabel, active ? styles.headLabelActive : null]}
                        numberOfLines={1}
                        adjustsFontSizeToFit
                        minimumFontScale={0.75}
                      >
                        {active ? `${label} ${sort.order === "desc" ? "▼" : "▲"}` : label}
                      </Text>
                      {sub ? <Text style={styles.headSub} numberOfLines={1}>{sub}</Text> : null}
                    </>
                  );
                  if (!sortable) {
                    return (
                      <View key={column.key} style={[styles.headCell, columnStyle(column.width)]}>{content}</View>
                    );
                  }
                  const state = !active
                    ? t("productReport.sort.none")
                    : sort.order === "desc"
                      ? t("productReport.sort.desc")
                      : t("productReport.sort.asc");
                  return (
                    <Pressable
                      key={column.key}
                      accessibilityRole="button"
                      accessibilityState={{ selected: active }}
                      accessibilityLabel={t("productReport.sort.accessibilityLabel", { column: label, state })}
                      hitSlop={{ top: 4, bottom: 4 }}
                      onPress={() => onSort(column.sortField!)}
                      style={[styles.headCell, columnStyle(column.width)]}
                    >
                      {content}
                    </Pressable>
                  );
                })}
                <View style={styles.rowFiller} />
              </View>
              {rows.map((row) => (
                <Pressable
                  key={row.id}
                  // 右侧行只是名称行的延伸，读屏只念左列那一份，避免每家供应商念两遍。
                  accessible={false}
                  importantForAccessibility="no-hide-descendants"
                  onPress={row.onPress}
                  style={[styles.dataRow, { height: rowHeight }, row.selected ? styles.rowSelected : null]}
                >
                  {COLUMNS.map((column) =>
                    column.key === "growth" ? (
                      <View key={column.key} style={[styles.cell, styles.growthCell, columnStyle(column.width)]}>{row.growth}</View>
                    ) : (
                      <SupplierValueCell
                        key={column.key}
                        column={column}
                        cell={row.cells[column.key]}
                        compareLabel={compareLabel}
                      />
                    ),
                  )}
                  <View style={styles.rowFiller} />
                </Pressable>
              ))}
            </View>
          </ScrollView>
          {canScroll && phase !== "end" ? <EdgeFade top={HEADER_HEIGHT} /> : null}
        </View>
      </View>
    </View>
  );
}

export const SupplierReportTable = memo(SupplierReportTableImpl);

const styles = StyleSheet.create({
  hintRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    marginBottom: 8,
  },
  hintText: {
    flex: 1,
    minWidth: 0,
    color: REPORT_UI.muted,
    fontSize: 11.5,
    lineHeight: 16,
  },
  swipePill: {
    flexShrink: 0,
    height: 22,
    justifyContent: "center",
    borderRadius: 11,
    paddingHorizontal: 9,
    backgroundColor: REPORT_UI.accentSoft,
  },
  swipePillText: {
    color: REPORT_UI.accent,
    fontSize: 11,
    fontWeight: "700",
  },
  table: {
    flexDirection: "row",
    overflow: "hidden",
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
  },
  frozen: {
    zIndex: 2,
    backgroundColor: REPORT_UI.card,
  },
  // 右侧内容被滑走后，在固定列右缘出现一道阴影，提示它盖在滚动内容上方。
  frozenShadow: {
    shadowColor: "#101828",
    shadowOpacity: 0.14,
    shadowRadius: 5,
    shadowOffset: { width: 3, height: 0 },
    elevation: 4,
  },
  frozenHead: {
    justifyContent: "center",
    paddingHorizontal: 8,
    borderBottomWidth: 1,
    borderBottomColor: REPORT_UI.border,
    backgroundColor: REPORT_UI.tile,
  },
  frozenRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingLeft: 8,
    paddingRight: 6,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: REPORT_UI.divider,
    backgroundColor: REPORT_UI.card,
  },
  // 供应商选中色：浅紫底 + 左侧色条 + 名称变色，与吸顶联动条的供应商 chip 同色。
  rowSelected: {
    backgroundColor: "#FCFBFF",
  },
  selectedAccent: {
    position: "absolute",
    left: 0,
    top: 0,
    bottom: 0,
    width: 3,
    backgroundColor: LINKED_COLORS.supplier,
  },
  rank: {
    width: 17,
    color: "#98A2B3",
    fontSize: 10.5,
    fontWeight: "800",
    textAlign: "center",
    fontVariant: ["tabular-nums"],
  },
  nameCell: {
    flex: 1,
    minWidth: 0,
  },
  name: {
    color: REPORT_UI.text,
    fontSize: 12.5,
    lineHeight: 16,
    fontWeight: "700",
  },
  nameSelected: {
    color: LINKED_COLORS.supplierText,
  },
  code: {
    color: REPORT_UI.muted,
    fontSize: 10.5,
    lineHeight: 13,
    fontVariant: ["tabular-nums"],
  },
  scrollSide: {
    flex: 1,
    minWidth: 0,
  },
  scrollContent: {
    flexGrow: 1,
  },
  track: {
    flexGrow: 1,
    minWidth: TRACK_WIDTH,
  },
  headRow: {
    flexDirection: "row",
    borderBottomWidth: 1,
    borderBottomColor: REPORT_UI.border,
    backgroundColor: REPORT_UI.tile,
  },
  rowFiller: {
    flexGrow: 1,
    flexShrink: 1,
    minWidth: 0,
  },
  headCell: {
    justifyContent: "center",
    alignItems: "flex-end",
    paddingHorizontal: 6,
  },
  headLabel: {
    color: REPORT_UI.muted,
    fontSize: 11.5,
    fontWeight: "700",
  },
  headLabelActive: {
    color: REPORT_UI.accent,
  },
  headSub: {
    color: "#98A2B3",
    fontSize: 9.5,
    lineHeight: 11,
    fontWeight: "600",
  },
  dataRow: {
    flexDirection: "row",
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: REPORT_UI.divider,
  },
  cell: {
    justifyContent: "center",
    alignItems: "flex-end",
    paddingHorizontal: 6,
  },
  growthCell: {
    paddingHorizontal: 3,
  },
  value: {
    color: REPORT_UI.text,
    fontSize: 13,
    lineHeight: 16,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  revenueValue: {
    color: PRODUCT_CARD_COLORS.link,
    fontSize: 13.5,
    fontWeight: "800",
  },
  pendingValue: {
    color: PRODUCT_CARD_COLORS.pending,
    fontSize: 11,
    lineHeight: 14,
    textAlign: "right",
  },
  compare: {
    color: REPORT_UI.muted,
    fontSize: 10.5,
    lineHeight: 14,
    fontVariant: ["tabular-nums"],
  },
  miniTrack: {
    width: 34,
    height: 4,
    marginTop: 3,
    overflow: "hidden",
    borderRadius: 2,
    backgroundColor: PRODUCT_CARD_COLORS.shareTrack,
  },
  miniFill: {
    height: "100%",
    borderRadius: 2,
    backgroundColor: PRODUCT_CARD_COLORS.shareBar,
  },
  edgeFade: {
    position: "absolute",
    right: 0,
    bottom: 0,
    flexDirection: "row",
  },
  edgeFadeStrip: {
    width: 4,
  },
});
