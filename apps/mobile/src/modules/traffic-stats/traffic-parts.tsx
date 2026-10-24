// 客流统计页的展示部件（无数据逻辑）：卡片、周期分段、日期步进、汇总卡、对照胶囊、图例、构成条、明细表。
// 视觉沿用报表中心的卡片语言（白底、浅边框、12 圆角），颜色来自 HB_COLORS / REPORT_UI。
import type { ReactNode } from "react";
import { Pressable, StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import { REPORT_UI } from "@/modules/reports/report-card-parts";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { MONO } from "@/modules/face-match/ui";
import { TRAFFIC_CHART_COLORS } from "./TrafficLineChart";
import type { TrafficPeriod } from "./types";

const TONE = {
  up: { background: "#DCFCE7", text: "#166534" },
  down: { background: "#FEE4E2", text: "#B42318" },
  flat: { background: "#F2F4F7", text: "#344054" },
} as const;

export function TrafficCard({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  return <View style={[styles.card, style]}>{children}</View>;
}

export function CardHeader({ title, hint, right }: { title: string; hint?: string; right?: ReactNode }) {
  return (
    <View style={styles.cardHeader}>
      <Text style={styles.cardTitle} numberOfLines={1}>
        {title}
      </Text>
      {right ?? (hint ? <Text style={styles.cardHint} numberOfLines={1}>{hint}</Text> : null)}
    </View>
  );
}

/** 今天 / 本周 / 本月 分段。不用 Paper 的 SegmentedButtons：它每段有最小宽度，窄屏英文会撑破。 */
export function PeriodSegment({
  value,
  labels,
  onChange,
}: {
  value: TrafficPeriod;
  labels: Record<TrafficPeriod, string>;
  onChange: (period: TrafficPeriod) => void;
}) {
  return (
    <View style={styles.segment} accessibilityRole="tablist">
      {(["day", "week", "month"] as const).map((period) => {
        const selected = period === value;
        return (
          <Pressable
            key={period}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(period)}
            style={[styles.segmentItem, selected ? styles.segmentItemOn : null]}
          >
            <Text style={[styles.segmentText, selected ? styles.segmentTextOn : null]} numberOfLines={1}>
              {labels[period]}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** ◀ 标题 ▶：下一周期按钮在当前周期时置灰，不能越过今天。 */
export function DateStepper({
  title,
  subtitle,
  canNext,
  previousLabel,
  nextLabel,
  onPrevious,
  onNext,
}: {
  title: string;
  subtitle: string;
  canNext: boolean;
  previousLabel: string;
  nextLabel: string;
  onPrevious: () => void;
  onNext: () => void;
}) {
  return (
    <View style={styles.stepper}>
      <Pressable accessibilityRole="button" accessibilityLabel={previousLabel} onPress={onPrevious} style={styles.stepperButton} hitSlop={6}>
        <MaterialCommunityIcons name="chevron-left" size={22} color={HB_COLORS.textPrimary} />
      </Pressable>
      <View style={styles.stepperMid} accessibilityLiveRegion="polite">
        <Text style={styles.stepperTitle} numberOfLines={1}>
          {title}
        </Text>
        <Text style={styles.stepperSub} numberOfLines={1}>
          {subtitle}
        </Text>
      </View>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={nextLabel}
        accessibilityState={{ disabled: !canNext }}
        disabled={!canNext}
        onPress={onNext}
        style={[styles.stepperButton, !canNext ? styles.stepperButtonDisabled : null]}
        hitSlop={6}
      >
        <MaterialCommunityIcons name="chevron-right" size={22} color={HB_COLORS.textPrimary} />
      </Pressable>
    </View>
  );
}

/** 对照胶囊：+5% / -3% / 0%。 */
export function ChangePill({ percent }: { percent: number }) {
  const tone = percent > 0 ? TONE.up : percent < 0 ? TONE.down : TONE.flat;
  const icon = percent > 0 ? "arrow-up-thin" : percent < 0 ? "arrow-down-thin" : "minus";
  const label = `${percent > 0 ? "+" : ""}${percent}%`;
  return (
    <View style={[styles.pill, { backgroundColor: tone.background }]}>
      <MaterialCommunityIcons name={icon} size={13} color={tone.text} />
      <Text style={[styles.pillText, { color: tone.text }, MONO]}>{label}</Text>
    </View>
  );
}

export interface HeroKpi {
  key: string;
  label: string;
  value: string;
  /** 值后面的小字（单位 / 数量）。 */
  suffix?: string;
  /** 标签旁的「估算」小标。 */
  estimateTag?: string;
}

export function HeroCard({
  label,
  value,
  unit,
  percent,
  compareText,
  kpis,
}: {
  label: string;
  value: string;
  unit: string;
  percent: number | null;
  compareText: string;
  kpis: readonly HeroKpi[];
}) {
  return (
    <TrafficCard>
      <Text style={styles.heroLabel}>{label}</Text>
      <View style={styles.heroValueRow}>
        <Text style={[styles.heroValue, MONO]} accessibilityLabel={`${value} ${unit}`}>
          {value}
        </Text>
        <Text style={styles.heroUnit}>{unit}</Text>
      </View>
      <View style={styles.heroCompare}>
        {percent !== null ? <ChangePill percent={percent} /> : null}
        <Text style={styles.heroCompareText} numberOfLines={2}>
          {compareText}
        </Text>
      </View>
      <View style={styles.kpiRow}>
        {kpis.map((kpi) => (
          <View key={kpi.key} style={styles.kpi}>
            <View style={styles.kpiLabelRow}>
              <Text style={styles.kpiLabel} numberOfLines={1}>
                {kpi.label}
              </Text>
              {kpi.estimateTag ? (
                <View style={styles.estimateTag}>
                  <Text style={styles.estimateTagText}>{kpi.estimateTag}</Text>
                </View>
              ) : null}
            </View>
            <Text style={[styles.kpiValue, MONO]} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.8}>
              {kpi.value}
              {kpi.suffix ? <Text style={styles.kpiSuffix}> {kpi.suffix}</Text> : null}
            </Text>
          </View>
        ))}
      </View>
    </TrafficCard>
  );
}

export function LegendRow({ current, compare }: { current: string; compare?: string }) {
  return (
    <View style={styles.legend}>
      <View style={styles.legendItem}>
        <View style={[styles.legendLine, { borderColor: TRAFFIC_CHART_COLORS.current }]} />
        <Text style={styles.legendText}>{current}</Text>
      </View>
      {compare ? (
        <View style={styles.legendItem}>
          <View style={[styles.legendLine, styles.legendDashed, { borderColor: TRAFFIC_CHART_COLORS.compare }]} />
          <Text style={styles.legendText}>{compare}</Text>
        </View>
      ) : null}
    </View>
  );
}

export function ChartNote({ text }: { text: string }) {
  return <Text style={styles.chartNote}>{text}</Text>;
}

/** 客流构成条：首次出现 + 重复到访 = 进店人次。 */
export function CompositionBar({
  first,
  repeat,
  firstLabel,
  repeatLabel,
}: {
  first: number;
  repeat: number;
  firstLabel: string;
  repeatLabel: string;
}) {
  const total = Math.max(first + repeat, 1);
  const firstShare = Math.round((first / total) * 100);
  return (
    <View>
      <View style={styles.split} accessibilityRole="progressbar" accessibilityValue={{ min: 0, max: 100, now: firstShare }}>
        <View style={{ flex: first, backgroundColor: TRAFFIC_CHART_COLORS.current }} />
        <View style={{ flex: repeat, backgroundColor: TRAFFIC_CHART_COLORS.barSoft }} />
      </View>
      <View style={styles.splitLegend}>
        <Text style={styles.splitText}>
          {firstLabel} <Text style={[styles.splitStrong, MONO]}>{first}</Text>（{firstShare}%）
        </Text>
        <Text style={styles.splitText}>
          {repeatLabel} <Text style={[styles.splitStrong, MONO]}>{repeat}</Text>（{100 - firstShare}%）
        </Text>
      </View>
    </View>
  );
}

export interface TableColumn {
  key: string;
  label: string;
  /** 第一列左对齐占满剩余宽度，其余右对齐定宽。 */
  width?: number;
  suffix?: ReactNode;
}

export interface TableRow {
  key: string;
  cells: readonly ReactNode[];
  highlight?: boolean;
  muted?: boolean;
}

/** 紧凑明细表（不横向滚动）：第一列自适应，其余列右对齐定宽。 */
export function DetailTable({ columns, rows }: { columns: readonly TableColumn[]; rows: readonly TableRow[] }) {
  return (
    <View accessibilityRole="list">
      <View style={styles.tableRow}>
        {columns.map((column, index) => (
          <View
            key={column.key}
            style={[index === 0 ? styles.tableFirst : { width: column.width ?? 56 }, styles.tableHeadCell, index === 0 ? styles.tableHeadFirst : null]}
          >
            <Text style={[styles.tableHead, index > 0 ? styles.tableRight : null]} numberOfLines={1}>
              {column.label}
            </Text>
            {column.suffix}
          </View>
        ))}
      </View>
      {rows.map((row) => (
        <View key={row.key} style={[styles.tableRow, styles.tableBodyRow, row.highlight ? styles.tableHighlight : null]} accessibilityRole="text">
          {row.cells.map((cell, index) => (
            <View key={columns[index]?.key ?? index} style={index === 0 ? styles.tableFirst : { width: columns[index]?.width ?? 56 }}>
              {typeof cell === "string" || typeof cell === "number" ? (
                <Text
                  style={[
                    styles.tableCell,
                    index > 0 ? [styles.tableRight, MONO] : null,
                    row.highlight ? styles.tableCellStrong : null,
                    row.muted ? styles.tableMuted : null,
                  ]}
                  numberOfLines={1}
                >
                  {cell}
                </Text>
              ) : (
                cell
              )}
            </View>
          ))}
        </View>
      ))}
    </View>
  );
}

export function LinkButton({ label, onPress }: { label: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.linkButton} hitSlop={8}>
      <Text style={styles.linkText}>{label}</Text>
    </Pressable>
  );
}

export function InlineTag({ label, tone = "info" }: { label: string; tone?: "info" | "warning" }) {
  return (
    <View style={[styles.inlineTag, tone === "warning" ? styles.inlineTagWarning : null]}>
      <Text style={[styles.inlineTagText, tone === "warning" ? styles.inlineTagWarningText : null]}>{label}</Text>
    </View>
  );
}

export function EstimateTag({ label }: { label: string }) {
  return (
    <View style={styles.estimateTag}>
      <Text style={styles.estimateTagText}>{label}</Text>
    </View>
  );
}

export function SkeletonBlock({ height, width = "100%" }: { height: number; width?: number | `${number}%` }) {
  return <View style={[styles.skeleton, { height, width }]} />;
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: REPORT_UI.card,
    borderColor: REPORT_UI.border,
    borderWidth: StyleSheet.hairlineWidth * 2,
    borderRadius: HB_RADIUS.surface,
    padding: 14,
    marginBottom: HB_SPACING.sm - 2,
  },
  cardHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", marginBottom: HB_SPACING.xs, gap: HB_SPACING.xs },
  cardTitle: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary, flexShrink: 1 },
  cardHint: { fontSize: 11.5, color: REPORT_UI.muted, flexShrink: 1 },
  segment: { flexDirection: "row", backgroundColor: "#E8ECF1", borderRadius: 10, padding: 3, marginBottom: HB_SPACING.sm - 2 },
  segmentItem: { flex: 1, height: 34, alignItems: "center", justifyContent: "center", borderRadius: 8 },
  segmentItemOn: { backgroundColor: HB_COLORS.white, shadowColor: "#101828", shadowOpacity: 0.14, shadowRadius: 3, shadowOffset: { width: 0, height: 1 }, elevation: 1 },
  segmentText: { fontSize: 14, color: HB_COLORS.textSecondary },
  segmentTextOn: { color: HB_COLORS.action, fontWeight: "700" },
  stepper: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", marginBottom: HB_SPACING.sm - 2 },
  stepperButton: { width: 38, height: 38, borderRadius: 10, backgroundColor: HB_COLORS.surface, borderColor: HB_COLORS.outlineMuted, borderWidth: 1, alignItems: "center", justifyContent: "center" },
  stepperButtonDisabled: { opacity: 0.35 },
  stepperMid: { flex: 1, alignItems: "center", paddingHorizontal: HB_SPACING.xs },
  stepperTitle: { fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  stepperSub: { fontSize: 11.5, color: REPORT_UI.muted, marginTop: 1 },
  pill: { flexDirection: "row", alignItems: "center", height: 22, paddingHorizontal: 7, borderRadius: 11, gap: 1 },
  pillText: { fontSize: 12, fontWeight: "700" },
  heroLabel: { fontSize: 12.5, color: HB_COLORS.textSecondary },
  heroValueRow: { flexDirection: "row", alignItems: "baseline", marginTop: 2, marginBottom: 4, gap: 6 },
  heroValue: { fontSize: 42, lineHeight: 48, fontWeight: "700", color: HB_COLORS.textPrimary },
  heroUnit: { fontSize: 14, color: REPORT_UI.muted },
  heroCompare: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  heroCompareText: { flex: 1, fontSize: 12, color: REPORT_UI.muted },
  kpiRow: { flexDirection: "row", gap: HB_SPACING.xs, marginTop: HB_SPACING.sm, paddingTop: HB_SPACING.sm, borderTopColor: REPORT_UI.divider, borderTopWidth: 1 },
  kpi: { flex: 1, minWidth: 0 },
  kpiLabelRow: { flexDirection: "row", alignItems: "center", gap: 4 },
  kpiLabel: { fontSize: 11.5, color: REPORT_UI.muted, flexShrink: 1 },
  kpiValue: { fontSize: 19, fontWeight: "700", color: HB_COLORS.textPrimary, marginTop: 2 },
  kpiSuffix: { fontSize: 11.5, fontWeight: "500", color: REPORT_UI.muted },
  estimateTag: { backgroundColor: "#FFFAEB", borderColor: "#FEDF89", borderWidth: 1, borderRadius: 4, paddingHorizontal: 4 },
  estimateTagText: { fontSize: 10, lineHeight: 14, fontWeight: "700", color: HB_COLORS.warning },
  legend: { flexDirection: "row", gap: HB_SPACING.sm },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 4 },
  legendLine: { width: 14, height: 0, borderTopWidth: 2.5 },
  legendDashed: { borderTopWidth: 2, borderStyle: "dashed" },
  legendText: { fontSize: 11.5, color: HB_COLORS.textSecondary },
  chartNote: { fontSize: 11.5, color: REPORT_UI.muted, marginTop: 4 },
  split: { flexDirection: "row", height: 10, borderRadius: 5, overflow: "hidden", marginTop: 6, marginBottom: 8, backgroundColor: REPORT_UI.divider },
  splitLegend: { flexDirection: "row", justifyContent: "space-between", gap: HB_SPACING.xs },
  splitText: { fontSize: 12, color: HB_COLORS.textSecondary },
  splitStrong: { fontWeight: "700", color: HB_COLORS.textPrimary },
  tableRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  tableBodyRow: { minHeight: 36, borderBottomColor: HB_COLORS.surfaceMuted, borderBottomWidth: 1, paddingVertical: 6 },
  tableHighlight: { backgroundColor: "#F5F9FF" },
  tableFirst: { flex: 1, minWidth: 0 },
  tableHeadCell: { flexDirection: "row", alignItems: "center", justifyContent: "flex-end", paddingBottom: 6, borderBottomColor: HB_COLORS.outlineMuted, borderBottomWidth: 1 },
  tableHeadFirst: { justifyContent: "flex-start" },
  tableHead: { fontSize: 11.5, color: REPORT_UI.muted },
  tableRight: { textAlign: "right" },
  tableCell: { fontSize: 13, color: HB_COLORS.textPrimary },
  tableCellStrong: { fontWeight: "700" },
  tableMuted: { color: "#98A2B3" },
  linkButton: { alignItems: "center", paddingTop: HB_SPACING.sm },
  linkText: { fontSize: 13, fontWeight: "600", color: HB_COLORS.action },
  inlineTag: { backgroundColor: "#EFF8FF", borderColor: "#B2DDFF", borderWidth: 1, borderRadius: 4, paddingHorizontal: 4, marginLeft: 4 },
  inlineTagWarning: { backgroundColor: "#FFFAEB", borderColor: "#FEDF89" },
  inlineTagText: { fontSize: 10, lineHeight: 14, fontWeight: "700", color: "#175CD3" },
  inlineTagWarningText: { color: HB_COLORS.warning },
  skeleton: { backgroundColor: "#EEF1F5", borderRadius: 6, marginBottom: 10 },
});
