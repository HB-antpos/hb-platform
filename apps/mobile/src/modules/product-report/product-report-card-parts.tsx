import { Pressable, StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { Text } from "react-native-paper";
import { REPORT_UI } from "@/modules/reports/report-card-parts";
import type { ReportSort, ReportSortField } from "@/modules/product-report/sorting";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 商品报告卡片化版式的纯展示部件：指标格、占比横条、排序芯片、紧凑翻页与筛选芯片。
 * 只负责排版，取值、格式化与联动逻辑都由 product-report-screen 传入，避免两处口径漂移。
 */

export const PRODUCT_CARD_COLORS = {
  shareBar: "#9B8AFB",
  shareTrack: "#F2F4F7",
  pending: "#B54708",
  link: "#0958D9",
  disabled: "#D0D5DD",
} as const;

// 芯片只有 30pt 左右高，上下扩大点击区域接近 44pt 触控下限。
const CHIP_HIT_SLOP = { top: 7, bottom: 7 };

export interface MetricCell {
  key: string;
  label: string;
  value: string;
  /** 同期值；null / undefined 表示该位置不显示同期（澳洲页签、商品行）。 */
  compare?: string | null;
  /** 成本未齐等待补全时用琥珀色提示，与普通数值区分。 */
  pending?: boolean;
}

/**
 * 并排指标格：boxed 是供应商 / 商品卡里的浅灰四格，plain 是分店下钻抽屉里的无框五格。
 * 数值单行显示，放大字体或长英文时按比例缩小而不是截断。
 */
export function MetricGrid({
  cells,
  variant = "boxed",
  style,
}: {
  cells: readonly MetricCell[];
  variant?: "boxed" | "plain";
  style?: StyleProp<ViewStyle>;
}) {
  const boxed = variant === "boxed";
  return (
    <View style={[boxed ? styles.gridBoxed : styles.gridPlain, style]}>
      {cells.map((cell, index) => (
        <View
          key={cell.key}
          style={[styles.gridCell, boxed ? styles.gridCellBoxed : null, boxed && index > 0 ? styles.gridCellDivider : null]}
        >
          <Text style={styles.gridLabel} numberOfLines={1}>{cell.label}</Text>
          <Text
            style={[styles.gridValue, boxed ? null : styles.gridValuePlain, cell.pending ? styles.gridPending : null]}
            numberOfLines={1}
            adjustsFontSizeToFit
            minimumFontScale={0.8}
          >
            {cell.value}
          </Text>
          {/* 同期小字很短，不做自动缩放：iOS 首次布局时 adjustsFontSizeToFit 偶发把它缩到下限（模拟器实测过）。 */}
          {cell.compare != null ? (
            <Text style={styles.gridCompare} numberOfLines={1}>
              {cell.compare}
            </Text>
          ) : null}
        </View>
      ))}
    </View>
  );
}

/** 占比横条：上行「名称 … 本期%（· 同期 x%）」，下行横条。percent 为 0-100 的条长。 */
export function ShareMeter({
  label,
  value,
  compareText,
  percent,
}: {
  label: string;
  value: string;
  compareText?: string | null;
  percent: number;
}) {
  const width = Math.min(100, Math.max(0, Number.isFinite(percent) ? percent : 0));
  return (
    <View style={styles.share}>
      <View style={styles.shareLine}>
        <Text style={styles.shareLabel} numberOfLines={1}>{label}</Text>
        <Text style={styles.shareValueLine} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.75}>
          <Text style={styles.shareValue}>{value}</Text>
          {compareText ? <Text style={styles.shareCompare}>{` · ${compareText}`}</Text> : null}
        </Text>
      </View>
      <View style={styles.shareTrack}>
        <View style={[styles.shareFill, { width: `${width}%` as `${number}%` }]} />
      </View>
    </View>
  );
}

/** 排序芯片：当前字段高亮并带 ▼ / ▲，再点同一字段切换升降序（由 onSort 内的 toggleReportSort 决定）。 */
export function SortChips({
  fields,
  sort,
  onSort,
}: {
  fields: readonly { field: ReportSortField; label: string }[];
  sort: ReportSort;
  onSort: (field: ReportSortField) => void;
}) {
  const { t } = useAppTranslation("common");
  return (
    <View style={styles.sortBar}>
      <Text style={styles.sortLabel}>{t("productReport.sort.label")}</Text>
      {fields.map(({ field, label }) => {
        const active = sort.field === field;
        const descending = sort.order === "desc";
        const state = !active
          ? t("productReport.sort.none")
          : descending
            ? t("productReport.sort.desc")
            : t("productReport.sort.asc");
        return (
          <Pressable
            key={field}
            accessibilityRole="button"
            accessibilityState={{ selected: active }}
            accessibilityLabel={t("productReport.sort.accessibilityLabel", { column: label, state })}
            hitSlop={CHIP_HIT_SLOP}
            onPress={() => onSort(field)}
            style={[styles.chip, active ? styles.chipActive : null]}
          >
            <Text style={[styles.chipText, active ? styles.chipTextActive : null]} numberOfLines={1}>
              {active ? `${label} ${descending ? "▼" : "▲"}` : label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** 普通筛选芯片（日期快捷键）。 */
export function FilterChip({ label, selected, onPress }: { label: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      hitSlop={CHIP_HIT_SLOP}
      onPress={onPress}
      style={[styles.chip, selected ? styles.chipActive : null]}
    >
      <Text style={[styles.chipText, selected ? styles.chipTextActive : null]} numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

/** 紧凑翻页「‹ 1/3 ›」；箭头的读屏文字沿用上一页 / 下一页。 */
export function CompactPager({
  page,
  pageCount,
  onPrevious,
  onNext,
  previousLabel,
  nextLabel,
}: {
  page: number;
  pageCount: number;
  onPrevious: () => void;
  onNext: () => void;
  previousLabel: string;
  nextLabel: string;
}) {
  const previousDisabled = page <= 1;
  const nextDisabled = page >= pageCount;
  return (
    <View style={styles.pager}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={previousLabel}
        accessibilityState={{ disabled: previousDisabled }}
        disabled={previousDisabled}
        hitSlop={CHIP_HIT_SLOP}
        onPress={onPrevious}
        style={styles.pagerArrow}
      >
        <Text style={[styles.pagerArrowText, previousDisabled ? styles.pagerDisabled : null]}>‹</Text>
      </Pressable>
      <View style={styles.pagerMiddle}>
        <Text style={styles.pagerText}>{page}/{pageCount}</Text>
      </View>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={nextLabel}
        accessibilityState={{ disabled: nextDisabled }}
        disabled={nextDisabled}
        hitSlop={CHIP_HIT_SLOP}
        onPress={onNext}
        style={styles.pagerArrow}
      >
        <Text style={[styles.pagerArrowText, nextDisabled ? styles.pagerDisabled : null]}>›</Text>
      </Pressable>
    </View>
  );
}

/** 二选一分段（澳洲 / 中国供应商）：选中段深蓝实底。 */
export function SegmentToggle<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: readonly { value: T; label: string }[];
  onChange: (value: T) => void;
}) {
  return (
    <View style={styles.segment} accessibilityRole="tablist">
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <Pressable
            key={option.value}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => { if (!selected) onChange(option.value); }}
            style={[styles.segmentButton, selected ? styles.segmentSelected : null]}
          >
            <Text style={[styles.segmentText, selected ? styles.segmentTextSelected : null]} numberOfLines={1}>
              {option.label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  gridBoxed: {
    flexDirection: "row",
    borderWidth: 1,
    borderColor: REPORT_UI.divider,
    borderRadius: 9,
    backgroundColor: REPORT_UI.tile,
  },
  gridPlain: {
    flexDirection: "row",
    gap: 4,
  },
  gridCell: {
    flex: 1,
    minWidth: 0,
  },
  gridCellBoxed: {
    paddingHorizontal: 7,
    paddingVertical: 6,
  },
  gridCellDivider: {
    borderLeftWidth: 1,
    borderLeftColor: REPORT_UI.divider,
  },
  gridLabel: {
    color: REPORT_UI.muted,
    fontSize: 10.5,
    lineHeight: 14,
  },
  gridValue: {
    color: REPORT_UI.text,
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  gridValuePlain: {
    fontSize: 12.5,
  },
  gridPending: {
    color: PRODUCT_CARD_COLORS.pending,
    fontSize: 10.5,
    fontWeight: "700",
  },
  gridCompare: {
    color: REPORT_UI.muted,
    fontSize: 10.5,
    lineHeight: 14,
    fontVariant: ["tabular-nums"],
  },
  share: {
    flex: 1,
    minWidth: 0,
    gap: 4,
  },
  shareLine: {
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: 4,
  },
  shareLabel: {
    flexShrink: 0,
    color: REPORT_UI.muted,
    fontSize: 11,
  },
  shareValueLine: {
    flexShrink: 1,
    textAlign: "right",
    fontVariant: ["tabular-nums"],
  },
  shareValue: {
    color: REPORT_UI.text,
    fontSize: 12.5,
    fontWeight: "700",
  },
  shareCompare: {
    color: REPORT_UI.muted,
    fontSize: 11,
  },
  shareTrack: {
    height: 6,
    overflow: "hidden",
    borderRadius: 3,
    backgroundColor: PRODUCT_CARD_COLORS.shareTrack,
  },
  shareFill: {
    height: 6,
    borderRadius: 3,
    backgroundColor: PRODUCT_CARD_COLORS.shareBar,
  },
  sortBar: {
    flexShrink: 1,
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    gap: 6,
  },
  sortLabel: {
    color: REPORT_UI.muted,
    fontSize: 11.5,
  },
  chip: {
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 16,
    backgroundColor: REPORT_UI.card,
    paddingHorizontal: 11,
    paddingVertical: 5,
  },
  chipActive: {
    borderColor: "#B2CCFF",
    backgroundColor: REPORT_UI.accentSoft,
  },
  chipText: {
    color: REPORT_UI.subText,
    fontSize: 12.5,
    lineHeight: 17,
    fontWeight: "600",
  },
  chipTextActive: {
    color: REPORT_UI.accent,
    fontWeight: "700",
  },
  pager: {
    flexDirection: "row",
    alignItems: "center",
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 10,
    backgroundColor: REPORT_UI.card,
    overflow: "hidden",
  },
  pagerArrow: {
    width: 32,
    height: 30,
    alignItems: "center",
    justifyContent: "center",
  },
  pagerArrowText: {
    color: REPORT_UI.subText,
    fontSize: 18,
    lineHeight: 22,
    fontWeight: "600",
  },
  pagerDisabled: {
    color: PRODUCT_CARD_COLORS.disabled,
  },
  pagerMiddle: {
    minWidth: 40,
    height: 30,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 6,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    borderColor: REPORT_UI.border,
  },
  pagerText: {
    color: REPORT_UI.subText,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
  },
  segment: {
    flexDirection: "row",
    gap: 2,
    borderRadius: 9,
    backgroundColor: "#F2F4F7",
    padding: 3,
  },
  segmentButton: {
    flex: 1,
    minHeight: 36,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 7,
    paddingHorizontal: 8,
  },
  segmentSelected: {
    backgroundColor: REPORT_UI.accent,
  },
  segmentText: {
    color: "#475467",
    fontSize: 13.5,
    fontWeight: "600",
  },
  segmentTextSelected: {
    color: "#FFFFFF",
  },
});
