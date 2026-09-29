import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, View, type LayoutChangeEvent } from "react-native";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { CumulativeRevenueChart } from "@/modules/reports/CumulativeRevenueChart";
import { formatMoney, formatSignedWholeDollars, formatWholeCount, formatWholeMoney } from "@/modules/reports/format";
import { formatGrowthRate, getGrowthTone, type GrowthTone } from "@/modules/reports/growth-rate";
import {
  FULL_DAY_CUTOFF_HOUR,
  buildCumulativeChartModel,
  formatHourLabel,
  getCumulativeTotals,
  getCutoffOptions,
  getDisplayCutoffHour,
  getLiveHourSummary,
  isLowBase,
  type CutoffResolution,
  type HourlySeries,
} from "@/modules/reports/hourly-cumulative";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

const STRIP_CELL_WIDTH = 96;
const STRIP_CELL_GAP = 4;
const STRIP_BAR_HALF = 19;

const PILL_COLORS: Record<GrowthTone | "new", { background: string; text: string }> = {
  up: { background: "#DCFCE7", text: "#166534" },
  down: { background: "#FEE4E2", text: "#B42318" },
  flat: { background: "#F2F4F7", text: "#344054" },
  new: { background: "#EAF2FF", text: "#073B83" },
};

const STRIP_TEXT_COLORS: Record<GrowthTone, string> = {
  up: "#15803D",
  down: "#DC2626",
  flat: "#6B7280",
};

const STRIP_BAR_COLORS: Record<GrowthTone, string> = {
  up: "#16A34A",
  down: "#DC2626",
  flat: "#98A2B3",
};

export interface CumulativeRevenueCardProps {
  scopeLabel: string;
  compareDate: string;
  series: HourlySeries | null;
  /** 默认截止（最近完整整点或整天）；统计时间未知时为 null。 */
  cutoff: CutoffResolution | null;
  effectiveCutoffHour: number | null;
  liveTimeLabel: string | null;
  loading: boolean;
  error: boolean;
  statisticsIncomplete: boolean;
  onRetry: () => void;
  onSelectCutoff: (hour: number) => void;
}

interface MiniStatItem {
  key: string;
  label: string;
  value: string;
  note?: string;
  muted?: boolean;
}

export function CumulativeRevenueCard({
  scopeLabel,
  compareDate,
  series,
  cutoff,
  effectiveCutoffHour,
  liveTimeLabel,
  loading,
  error,
  statisticsIncomplete,
  onRetry,
  onSelectCutoff,
}: CumulativeRevenueCardProps) {
  const { t } = useAppTranslation("common");
  const newLabel = t("reports.metrics.newGrowth");

  const header = (
    <View style={styles.titleRow}>
      <Text variant="titleMedium" style={styles.title}>
        {t("reports.cumulative.title")}
      </Text>
      <Text variant="bodySmall" style={styles.caption} numberOfLines={1}>
        {t("reports.cumulative.compareWith", { date: compareDate })}
      </Text>
    </View>
  );

  if (!series || !cutoff || effectiveCutoffHour === null) {
    const failed = error || (series === null && statisticsIncomplete);
    return (
      <View style={styles.card}>
        {header}
        <View style={styles.stateBox}>
          {failed ? (
            <>
              <Text variant="bodyMedium" style={styles.stateText}>
                {t(error ? "reports.cumulative.loadFailed" : "reports.states.statisticsIncomplete")}
              </Text>
              <Button mode="outlined" compact onPress={onRetry}>
                {t("actions.retry")}
              </Button>
            </>
          ) : (
            <>
              {loading ? <ActivityIndicator /> : null}
              <Text variant="bodyMedium" style={styles.stateText}>
                {t(loading ? "loading" : "reports.cumulative.cutoffUnavailable")}
              </Text>
            </>
          )}
        </View>
      </View>
    );
  }

  if (series.firstHour === null) {
    return (
      <View style={styles.card}>
        {header}
        <View style={styles.stateBox}>
          <Text variant="bodyMedium" style={styles.stateText}>
            {t("reports.cumulative.noData")}
          </Text>
        </View>
      </View>
    );
  }

  return (
    <CumulativeRevenueContent
      header={header}
      scopeLabel={scopeLabel}
      compareDate={compareDate}
      series={series}
      cutoff={cutoff}
      effectiveCutoffHour={effectiveCutoffHour}
      liveTimeLabel={liveTimeLabel}
      newLabel={newLabel}
      onSelectCutoff={onSelectCutoff}
    />
  );
}

function CumulativeRevenueContent({
  header,
  scopeLabel,
  compareDate,
  series,
  cutoff,
  effectiveCutoffHour,
  liveTimeLabel,
  newLabel,
  onSelectCutoff,
}: {
  header: ReactNode;
  scopeLabel: string;
  compareDate: string;
  series: HourlySeries;
  cutoff: CutoffResolution;
  effectiveCutoffHour: number;
  liveTimeLabel: string | null;
  newLabel: string;
  onSelectCutoff: (hour: number) => void;
}) {
  const { t } = useAppTranslation("common");
  const options = useMemo(() => getCutoffOptions(series, cutoff.cutoffHour), [cutoff.cutoffHour, series]);
  const displayCutoff = getDisplayCutoffHour(series, effectiveCutoffHour);
  const totals = getCumulativeTotals(series, effectiveCutoffHour);
  const fullDay = getCumulativeTotals(series, FULL_DAY_CUTOFF_HOUR);
  const coversFullDay = !cutoff.live && displayCutoff >= (series.endHour ?? FULL_DAY_CUTOFF_HOUR);
  const noCompleteHour = cutoff.live && options.length === 0;
  const lowBase = isLowBase(totals.compareRevenue, fullDay.compareRevenue);
  const difference = totals.revenue - totals.compareRevenue;
  // 曲线画到最近完整整点（或整天），用户点选的整点只移动标记。
  const chartModel = useMemo(
    () =>
      buildCumulativeChartModel(series, {
        cutoffHour: cutoff.cutoffHour,
        live: cutoff.live,
        liveHourFraction: cutoff.liveHourFraction,
      }),
    [cutoff.cutoffHour, cutoff.live, cutoff.liveHourFraction, series],
  );

  const growthTone =
    totals.compareRevenue === 0 && totals.revenue > 0 ? "new" : getGrowthTone(totals.revenue, totals.compareRevenue);
  const pillColors = lowBase ? PILL_COLORS.flat : PILL_COLORS[growthTone];
  const pillText = lowBase
    ? formatSignedWholeDollars(difference)
    : formatGrowthRate(totals.revenue, totals.compareRevenue, newLabel);
  const differenceKey = difference < 0 ? "behind" : difference > 0 ? "ahead" : "level";
  const differenceText = t(`reports.cumulative.${differenceKey}${coversFullDay ? "FullDay" : ""}`, {
    amount: formatWholeMoney(Math.abs(difference)),
  });
  // 进行中的小时已有入账时，说明这部分为什么还没进对比、哪次统计后计入。
  const liveHour = getLiveHourSummary(series, cutoff);
  const liveHourNote = !noCompleteHour && liveHour && liveTimeLabel
    ? t("reports.cumulative.liveHourNote", {
        from: formatHourLabel(liveHour.startHour),
        to: liveTimeLabel,
        amount: formatWholeMoney(liveHour.revenue),
        next: formatHourLabel(liveHour.nextHour),
      })
    : null;

  const stats: MiniStatItem[] = coversFullDay
    ? [
        {
          key: "transactions",
          label: t("reports.metrics.transactions"),
          value: formatWholeCount(totals.transactions),
          note: `${t("reports.metrics.compare")} ${formatWholeCount(totals.compareTransactions)}`,
        },
        {
          key: "aov",
          label: t("reports.metrics.averageTransaction"),
          value: formatMoney(totals.transactions > 0 ? totals.revenue / totals.transactions : 0),
          note: `${t("reports.metrics.compare")} ${formatMoney(
            totals.compareTransactions > 0 ? totals.compareRevenue / totals.compareTransactions : 0,
          )}`,
        },
        {
          key: "compareFullDay",
          label: t("reports.cumulative.lyFullDay"),
          value: formatWholeMoney(fullDay.compareRevenue),
          muted: true,
        },
      ]
    : [
        {
          key: "compareSameTime",
          label: t("reports.cumulative.lySameTime"),
          value: formatWholeMoney(totals.compareRevenue),
        },
        cutoff.live
          ? {
              key: "live",
              label: t("reports.cumulative.liveAt", {
                time: liveTimeLabel ?? formatHourLabel(cutoff.cutoffHour),
              }),
              value: formatWholeMoney(fullDay.revenue),
            }
          : {
              key: "dayTotal",
              label: t("reports.cumulative.dayTotal"),
              value: formatWholeMoney(fullDay.revenue),
            },
        {
          key: "compareFullDay",
          label: t("reports.cumulative.lyFullDay"),
          value: formatWholeMoney(fullDay.compareRevenue),
          muted: true,
        },
      ];

  const asOfLabel = noCompleteHour
    ? t("reports.cumulative.noCompleteHour", {
        time: formatHourLabel(series.firstHour === null ? 0 : series.firstHour + 1),
      })
    : coversFullDay
      ? t("reports.cumulative.fullDay", { scope: scopeLabel })
      : t("reports.cumulative.asOf", {
          time: formatHourLabel(displayCutoff),
          scope: scopeLabel,
        });

  return (
    <View style={styles.card}>
      {header}
      <View style={styles.headline}>
        <View style={styles.asOfRow}>
          <Icon source="clock-outline" size={15} color="#0958D9" />
          <Text variant="bodySmall" style={styles.asOfText} numberOfLines={1}>
            {asOfLabel}
          </Text>
        </View>
        <View style={styles.amountRow}>
          <Text style={styles.amount} selectable>
            {formatWholeMoney(noCompleteHour ? fullDay.revenue : totals.revenue)}
          </Text>
          {!noCompleteHour ? (
            <View style={[styles.pill, { backgroundColor: pillColors.background }]}>
              <Text style={[styles.pillText, { color: pillColors.text }]}>{pillText}</Text>
            </View>
          ) : null}
        </View>
        {!noCompleteHour ? (
          <Text variant="bodySmall" style={styles.differenceText}>
            {lowBase ? t("reports.cumulative.lowBaseNote") : differenceText}
          </Text>
        ) : null}
        {liveHourNote ? (
          <View style={styles.liveNoteRow}>
            <Icon source="progress-clock" size={14} color="#6B7280" />
            <Text variant="bodySmall" style={styles.liveNoteText}>
              {liveHourNote}
            </Text>
          </View>
        ) : null}
      </View>

      {!noCompleteHour ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.statsScrollContent}>
          <View style={styles.stats}>
            {stats.map((stat) => (
              <View key={stat.key} style={styles.stat}>
                <Text style={styles.statLabel}>{stat.label}</Text>
                <Text style={[styles.statValue, stat.muted ? styles.statValueMuted : null]}>{stat.value}</Text>
                {stat.note ? <Text style={styles.statNote}>{stat.note}</Text> : null}
              </View>
            ))}
          </View>
        </ScrollView>
      ) : null}

      {chartModel ? (
        <View style={styles.chart}>
          <CumulativeRevenueChart
            model={chartModel}
            markerHour={displayCutoff}
            compareFullDayLabel={`${t("reports.cumulative.lyFullDay")} ${formatWholeMoney(fullDay.compareRevenue)}`}
            accessibilityLabel={t("reports.cumulative.chartLabel", {
              scope: scopeLabel,
              time: formatHourLabel(displayCutoff),
              current: formatWholeMoney(totals.revenue),
              compare: formatWholeMoney(totals.compareRevenue),
            })}
          />
          <View style={styles.legend}>
            <View style={styles.legendItem}>
              <View style={[styles.legendLine, styles.legendLineCurrent]} />
              <Text style={styles.legendText}>
                {t(cutoff.live ? "reports.cumulative.legendToday" : "reports.cumulative.legendSelectedDay")}
              </Text>
            </View>
            <View style={styles.legendItem}>
              <View style={styles.legendDash} />
              <Text style={styles.legendText}>{t("reports.cumulative.legendCompare", { date: compareDate })}</Text>
            </View>
            <View style={styles.legendItem}>
              <View style={styles.legendGap}>
                <View style={[styles.legendGapHalf, styles.legendGapBehind]} />
                <View style={[styles.legendGapHalf, styles.legendGapAhead]} />
              </View>
              <Text style={styles.legendText}>{t("reports.cumulative.legendGap")}</Text>
            </View>
          </View>
        </View>
      ) : null}

      {options.length > 0 ? (
        <>
          <View style={styles.divider} />
          <View style={styles.stripHeader}>
            <Text style={styles.stripTitle}>{t("reports.cumulative.changeByHour")}</Text>
            <Text style={styles.stripHint}>{t("reports.cumulative.tapHint")}</Text>
          </View>
          <HourChangeStrip
            series={series}
            options={options}
            selectedHour={displayCutoff}
            compareFullDay={fullDay.compareRevenue}
            newLabel={newLabel}
            onSelect={onSelectCutoff}
          />
        </>
      ) : null}
    </View>
  );
}

function HourChangeStrip({
  series,
  options,
  selectedHour,
  compareFullDay,
  newLabel,
  onSelect,
}: {
  series: HourlySeries;
  options: number[];
  selectedHour: number;
  compareFullDay: number;
  newLabel: string;
  onSelect: (hour: number) => void;
}) {
  const { t } = useAppTranslation("common");
  const scrollRef = useRef<ScrollView>(null);
  const [viewportWidth, setViewportWidth] = useState(0);
  const cells = useMemo(
    () =>
      options.map((hour) => {
        const totals = getCumulativeTotals(series, hour);
        const lowBase = isLowBase(totals.compareRevenue, compareFullDay);
        const rate =
          totals.compareRevenue > 0 ? (totals.revenue - totals.compareRevenue) / totals.compareRevenue : null;
        const tone = getGrowthTone(totals.revenue, totals.compareRevenue);
        return {
          hour,
          lowBase,
          rate,
          tone,
          label: lowBase
            ? formatSignedWholeDollars(totals.revenue - totals.compareRevenue)
            : formatGrowthRate(totals.revenue, totals.compareRevenue, newLabel),
        };
      }),
    [compareFullDay, newLabel, options, series],
  );
  // 柱高按非小基数格子里的最大涨跌幅归一，小基数格子不参与，避免把其他柱子压扁。
  const maxAbsRate = Math.max(
    0.001,
    ...cells.filter((cell) => !cell.lowBase && cell.rate !== null).map((cell) => Math.abs(cell.rate ?? 0)),
  );
  const selectedIndex = options.indexOf(selectedHour);

  useEffect(() => {
    if (!viewportWidth || selectedIndex < 0) return;
    // 默认截止在最右侧；保证选中的格子始终在可视范围内。
    const cellEnd = (selectedIndex + 1) * (STRIP_CELL_WIDTH + STRIP_CELL_GAP);
    scrollRef.current?.scrollTo({
      x: Math.max(0, cellEnd - viewportWidth),
      animated: false,
    });
  }, [selectedIndex, viewportWidth]);

  return (
    <ScrollView
      ref={scrollRef}
      horizontal
      showsHorizontalScrollIndicator={false}
      onLayout={(event: LayoutChangeEvent) => setViewportWidth(Math.round(event.nativeEvent.layout.width))}
      contentContainerStyle={styles.strip}
    >
      {cells.map((cell) => {
        const selected = cell.hour === selectedHour;
        const barHeight =
          cell.rate === null || cell.lowBase ? 0 : Math.max(3, (Math.abs(cell.rate) / maxAbsRate) * STRIP_BAR_HALF);
        return (
          <Pressable
            key={cell.hour}
            accessibilityRole="button"
            accessibilityState={{ selected }}
            accessibilityLabel={t("reports.cumulative.compareAsOf", {
              time: formatHourLabel(cell.hour),
              change: cell.label,
            })}
            onPress={() => onSelect(cell.hour)}
            style={[styles.stripCell, selected ? styles.stripCellSelected : null]}
          >
            <Text
              style={[
                styles.stripValue,
                {
                  color: cell.lowBase ? "#6B7280" : STRIP_TEXT_COLORS[cell.tone],
                },
              ]}
            >
              {cell.label}
            </Text>
            <View style={styles.stripBarArea}>
              {cell.lowBase ? (
                <Text style={styles.stripLowBase}>{t("reports.cumulative.lowBase")}</Text>
              ) : (
                <>
                  <View style={styles.stripZeroLine} />
                  {barHeight > 0 ? (
                    <View
                      style={[
                        styles.stripBar,
                        {
                          backgroundColor: STRIP_BAR_COLORS[cell.tone],
                          height: barHeight,
                        },
                        // 零线在 STRIP_BAR_HALF 处：领先向上长，落后向下长。
                        {
                          top: cell.tone === "up" ? STRIP_BAR_HALF - barHeight : STRIP_BAR_HALF + 1,
                        },
                      ]}
                    />
                  ) : null}
                </>
              )}
            </View>
            <Text style={[styles.stripHour, selected ? styles.stripHourSelected : null]}>
              {formatHourLabel(cell.hour)}
            </Text>
          </Pressable>
        );
      })}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  card: {
    borderWidth: 1,
    borderColor: "#E2E8F0",
    borderRadius: 10,
    paddingHorizontal: 12,
    paddingTop: 8,
    paddingBottom: 12,
    backgroundColor: "#FFFFFF",
  },
  titleRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    paddingBottom: 6,
    borderBottomWidth: 1,
    borderBottomColor: "#E2E8F0",
  },
  title: {
    color: "#111827",
    fontWeight: "700",
  },
  caption: {
    flexShrink: 1,
    color: "#475467",
  },
  stateBox: {
    minHeight: 96,
    alignItems: "center",
    justifyContent: "center",
    gap: 10,
    paddingVertical: 12,
  },
  stateText: {
    color: "#64748B",
    textAlign: "center",
  },
  headline: {
    gap: 2,
    paddingTop: 10,
  },
  asOfRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  asOfText: {
    flexShrink: 1,
    color: "#475467",
    fontSize: 13,
    lineHeight: 18,
  },
  amountRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 10,
  },
  amount: {
    color: "#111827",
    fontSize: 30,
    lineHeight: 38,
    fontWeight: "700",
    letterSpacing: -0.3,
    fontVariant: ["tabular-nums"],
  },
  pill: {
    borderRadius: 999,
    paddingHorizontal: 9,
    paddingVertical: 3,
  },
  pillText: {
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  differenceText: {
    color: "#475467",
    fontSize: 13,
    lineHeight: 18,
  },
  liveNoteRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    marginTop: 2,
  },
  liveNoteText: {
    flexShrink: 1,
    color: "#6B7280",
    fontSize: 12,
    lineHeight: 16,
  },
  stats: {
    minWidth: "100%",
    flexDirection: "row",
    gap: 8,
  },
  statsScrollContent: {
    marginTop: 12,
    paddingVertical: 10,
    borderTopWidth: 1,
    borderBottomWidth: 1,
    borderColor: "#EEF2F7",
  },
  stat: {
    flex: 1,
    flexBasis: 124,
    minWidth: 124,
    gap: 2,
  },
  statLabel: {
    color: "#475467",
    fontSize: 12,
    lineHeight: 16,
    letterSpacing: 0.4,
  },
  statValue: {
    color: "#111827",
    fontSize: 15,
    lineHeight: 20,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  statValueMuted: {
    color: "#475467",
  },
  statNote: {
    color: "#6B7280",
    fontSize: 12,
    lineHeight: 16,
    fontVariant: ["tabular-nums"],
  },
  chart: {
    marginTop: 10,
    gap: 6,
  },
  legend: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    columnGap: 14,
    rowGap: 4,
  },
  legendItem: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  legendLine: {
    width: 16,
    height: 3,
    borderRadius: 2,
  },
  legendLineCurrent: {
    backgroundColor: "#2563EB",
  },
  legendDash: {
    width: 16,
    height: 0,
    borderTopWidth: 2,
    borderStyle: "dashed",
    borderColor: "#6B7280",
  },
  legendGap: {
    flexDirection: "row",
    width: 12,
    height: 10,
    overflow: "hidden",
    borderRadius: 2,
  },
  legendGapHalf: {
    flex: 1,
  },
  legendGapBehind: {
    backgroundColor: "rgba(220, 38, 38, 0.22)",
  },
  legendGapAhead: {
    backgroundColor: "rgba(22, 163, 74, 0.26)",
  },
  legendText: {
    color: "#475467",
    fontSize: 12,
    lineHeight: 16,
    letterSpacing: 0.4,
  },
  divider: {
    height: 1,
    marginTop: 12,
    marginBottom: 10,
    backgroundColor: "#E2E8F0",
  },
  stripHeader: {
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: 8,
    marginBottom: 8,
  },
  stripTitle: {
    flexShrink: 1,
    color: "#111827",
    fontSize: 14,
    lineHeight: 20,
    fontWeight: "700",
  },
  stripHint: {
    color: "#475467",
    fontSize: 12,
    lineHeight: 16,
  },
  strip: {
    gap: STRIP_CELL_GAP,
  },
  stripCell: {
    width: STRIP_CELL_WIDTH,
    minHeight: 88,
    alignItems: "center",
    gap: 4,
    paddingTop: 7,
    paddingBottom: 6,
    paddingHorizontal: 2,
    borderWidth: 1,
    borderColor: "#E5E7EB",
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
  },
  stripCellSelected: {
    borderColor: "#0958D9",
    backgroundColor: "#EAF2FF",
  },
  stripValue: {
    fontSize: 11,
    lineHeight: 14,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  stripBarArea: {
    width: "100%",
    height: STRIP_BAR_HALF * 2 + 2,
    justifyContent: "center",
  },
  stripZeroLine: {
    position: "absolute",
    left: 6,
    right: 6,
    top: STRIP_BAR_HALF,
    height: 1,
    backgroundColor: "#E5E7EB",
  },
  stripBar: {
    position: "absolute",
    left: "50%",
    width: 12,
    marginLeft: -6,
    borderRadius: 3,
  },
  stripLowBase: {
    color: "#6B7280",
    fontSize: 10,
    lineHeight: 14,
    textAlign: "center",
  },
  stripHour: {
    color: "#475467",
    fontSize: 11,
    lineHeight: 14,
    fontWeight: "500",
    fontVariant: ["tabular-nums"],
  },
  stripHourSelected: {
    color: "#0958D9",
    fontWeight: "700",
  },
});
