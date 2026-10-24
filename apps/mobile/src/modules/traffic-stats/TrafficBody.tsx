// 客流统计页主体：把一份 TrafficOverview 渲染成「汇总卡 + 曲线卡 + 辅助卡 + 明细表 + 说明」。
// 今天 / 本周 / 本月三个视图共用同一套卡片，只是数据与曲线不同（见 metrics.ts 的口径说明）。
import { useMemo } from "react";
import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { Notice } from "@/modules/face-match/ui";
import type { AppLanguage } from "@/shared/i18n/types";
import { HB_COLORS } from "@/shared/theme/tokens";
import { niceScale, seriesMax } from "./chart-model";
import {
  buildDailyChart,
  buildHourlyChart,
  buildProfileChart,
  busiestDay,
  dailyAverage,
  dailyAverageUnique,
  findPeak,
  formatAverage,
  formatCount,
  formatHourRange,
  hourlyVisits,
  resolveCompare,
  weekdayAverages,
  weekendAverage,
} from "./metrics";
import { formatClock, formatMonthDay, formatShortSlash, isFutureDate, parseAsOf, weekdayLabel, weekdayLabels } from "./period";
import { TRAFFIC_CHART_COLORS, TrafficLineChart, type LineChartTooltip } from "./TrafficLineChart";
import { TrafficBarChart } from "./TrafficBarChart";
import {
  CardHeader,
  ChartNote,
  CompositionBar,
  DetailTable,
  EstimateTag,
  HeroCard,
  InlineTag,
  LegendRow,
  LinkButton,
  TrafficCard,
  type HeroKpi,
  type TableRow,
} from "./traffic-parts";
import type { TrafficOverview } from "./types";

type Translate = (key: string, options?: Record<string, unknown>) => string;

export interface TrafficBodyProps {
  overview: TrafficOverview;
  language: AppLanguage;
  t: Translate;
  storeLabel: string;
  selectedIndex: number | null;
  onSelect: (index: number | null) => void;
  tableExpanded: boolean;
  onToggleTable: () => void;
}

/** 明细表折叠时最多显示的行数。 */
const COLLAPSED_ROWS = 7;
const HOURLY_COLLAPSED_ROWS = 6;

export function TrafficBody(props: TrafficBodyProps) {
  const { overview, t } = props;
  const dedup = overview.trafficDedupEnabled;
  const compare = useMemo(() => resolveCompare(overview), [overview]);

  return (
    <View>
      <HeroCard
        label={heroLabel(overview, t)}
        value={formatCount(overview.current.total.visitCount)}
        unit={t("unit.visits")}
        percent={compare?.percent ?? null}
        compareText={compareText(overview, compare, t)}
        kpis={buildKpis(overview, props.language, t)}
      />
      {overview.period === "day" ? <DayCards {...props} /> : <MultiDayCards {...props} />}
      <Notice tone="warning" icon="information-outline" text={t(dedup ? "notice.estimateDedup" : "notice.estimatePlain")} style={styles.notice} />
      <BasisCard overview={overview} t={t} />
    </View>
  );
}

// ───────────── 汇总卡文案 ─────────────

function heroLabel(overview: TrafficOverview, t: Translate): string {
  if (overview.period === "day") return t("hero.day");
  const live = overview.isCurrent;
  return t(`hero.${overview.period}${live ? "Live" : "Past"}`);
}

function compareText(overview: TrafficOverview, compare: ReturnType<typeof resolveCompare>, t: Translate): string {
  if (!compare) return t(overview.isCurrent ? "compare.notYet" : "compare.noBase");
  if (compare.percent === null) return t("compare.noBase");
  const { basis } = compare;
  if (basis.kind === "hours") return t("compare.hours", { time: formatClock(basis.throughHour) });
  if (basis.kind === "days") return t(overview.period === "week" ? "compare.daysWeek" : "compare.daysMonth", { days: basis.days });
  return t(overview.period === "day" ? "compare.fullDay" : overview.period === "week" ? "compare.fullWeek" : "compare.fullMonth");
}

function buildKpis(overview: TrafficOverview, language: AppLanguage, t: Translate): HeroKpi[] {
  const none = t("kpi.none");
  const dedup = overview.trafficDedupEnabled;
  const visits = t("unit.visits");
  if (overview.period === "day") {
    const total = overview.current.total;
    const peak = findPeak(hourlyVisits(overview.current.hours));
    const peakKpi: HeroKpi = {
      key: "peak",
      label: t("kpi.peakHour"),
      value: peak ? formatClock(peak.hour) : none,
      suffix: peak ? `${peak.value} ${visits}` : undefined,
    };
    return dedup
      ? [
          { key: "unique", label: t("kpi.uniqueVisitors"), value: formatCount(total.uniqueVisitorCount), estimateTag: t("kpi.estimate") },
          { key: "repeat", label: t("kpi.repeat"), value: formatCount(total.repeatCount) },
          peakKpi,
        ]
      : [
          { key: "staff", label: t("kpi.staffExcluded"), value: formatCount(total.staffMatchCount) },
          { key: "invalid", label: t("kpi.invalid"), value: formatCount(total.lowQualityCount + total.apiFailureCount) },
          peakKpi,
        ];
  }

  const average = dailyAverage(overview);
  const busiest = busiestDay(overview);
  const busiestLabel = busiest ? (overview.period === "week" ? weekdayLabel(busiest.date, language) : formatMonthDay(busiest.date, language)) : none;
  const thirdUnique = overview.period === "week" && dedup;
  const uniqueAverage = dailyAverageUnique(overview);
  const weekend = weekendAverage(overview);
  return [
    { key: "average", label: t("kpi.dailyAverage"), value: average === null ? none : formatCount(average) },
    {
      key: "busiest",
      label: t("kpi.busiestDay"),
      value: busiestLabel,
      suffix: busiest ? String(busiest.counts.visitCount) : undefined,
    },
    thirdUnique
      ? { key: "uniqueAverage", label: t("kpi.averageUnique"), value: uniqueAverage === null ? none : formatCount(uniqueAverage), estimateTag: t("kpi.estimate") }
      : { key: "weekend", label: t("kpi.weekendAverage"), value: weekend === null ? none : formatCount(weekend) },
  ];
}

function legendLabels(overview: TrafficOverview, t: Translate): { current: string; compare: string } {
  const key = overview.period === "day" ? "Day" : overview.period === "week" ? "Week" : "Month";
  if (overview.period === "day") {
    return overview.isCurrent
      ? { current: t("legend.today"), compare: t("legend.yesterday") }
      : { current: t("legend.selectedDay"), compare: t("legend.previousDay") };
  }
  const name = overview.period === "week" ? "Week" : "Month";
  return overview.isCurrent
    ? { current: t(`legend.this${name}`), compare: t(`legend.last${name}`) }
    : { current: t(`legend.selected${key}`), compare: t(`legend.previous${key}`) };
}

// ───────────── 今天 ─────────────

function DayCards({ overview, language, t, storeLabel, selectedIndex, onSelect, tableExpanded, onToggleTable }: TrafficBodyProps) {
  const model = useMemo(() => buildHourlyChart(overview), [overview]);
  const scale = useMemo(() => niceScale(seriesMax(model.current, model.previous)), [model]);
  const legend = legendLabels(overview, t);
  const dedup = overview.trafficDedupEnabled;
  const asOf = overview.isCurrent ? parseAsOf(overview.asOfLocal) : null;
  const total = overview.current.total;
  const noData = total.captureCount === 0;
  const peak = findPeak(model.current);

  const tooltipFor = (index: number): LineChartTooltip | null => {
    const hour = model.window.start + index;
    const current = model.current[index];
    const previous = model.previous[index];
    const lines: string[] = [];
    const dots: LineChartTooltip["dots"] = [];
    if (current !== null) {
      lines.push(t("tooltip.visits", { label: legend.current, value: current }));
      dots.push({ value: current, color: TRAFFIC_CHART_COLORS.current });
    }
    if (previous !== undefined) {
      lines.push(t("tooltip.visits", { label: legend.compare, value: previous }));
      dots.push({ value: previous, color: TRAFFIC_CHART_COLORS.compare });
    }
    if (dedup && current !== null) {
      lines.push(t("tooltip.unique", { value: overview.current.hours[hour].counts.uniqueVisitorCount }));
    }
    const live = model.partialIndex === index ? t("tooltip.inProgress") : "";
    return { title: `${formatHourRange(hour)}${live}`, lines, dots };
  };

  // 明细表：只列到当前小时（今天）或整个窗口（历史日）；折叠时只留最近 6 行。
  const lastHour = asOf ? Math.min(asOf.hour, model.window.end) : model.window.end;
  const allRows: TableRow[] = [];
  for (let hour = model.window.start; hour <= lastHour; hour += 1) {
    const current = overview.current.hours[hour].counts.visitCount;
    const previous = overview.previous.hours[hour].counts.visitCount;
    const live = asOf !== null && hour === asOf.hour;
    const delta = current - previous;
    allRows.push({
      key: String(hour),
      highlight: peak !== null && hour === peak.hour,
      cells: [
        <View key="hour" style={styles.rowLabel}>
          <Text style={[styles.cellText, peak !== null && hour === peak.hour ? styles.cellStrong : null]} numberOfLines={1}>
            {formatHourRange(hour)}
          </Text>
          {live ? <InlineTag label={t("table.inProgress")} /> : null}
        </View>,
        String(current),
        String(previous),
        live ? "—" : `${delta > 0 ? "+" : ""}${delta}`,
      ],
    });
  }
  const rows = tableExpanded ? allRows : allRows.slice(-HOURLY_COLLAPSED_ROWS);

  return (
    <View>
      <TrafficCard>
        <CardHeader title={t("chart.hourlyTitle")} right={<LegendRow current={legend.current} compare={legend.compare} />} />
        {noData ? (
          <ChartNote text={t("chart.noDataToday")} />
        ) : null}
        <TrafficLineChart
          count={model.current.length}
          yMax={scale.max}
          yStep={scale.step}
          series={[
            { values: model.previous, color: TRAFFIC_CHART_COLORS.compare, dashed: true },
            { values: model.current, color: TRAFFIC_CHART_COLORS.current, area: true, partialIndex: model.partialIndex },
          ]}
          labelFor={(index) => String(model.window.start + index)}
          labelIndexes={model.current.map((_, index) => index).filter((index) => (model.window.start + index) % 2 === 0)}
          nowIndex={model.nowIndex}
          nowLabel={model.nowLabel}
          peak={peak ? { index: peak.hour - model.window.start, label: String(peak.value) } : null}
          selectedIndex={selectedIndex}
          onSelect={onSelect}
          tooltipFor={tooltipFor}
          tooltipOnLeft={model.nowIndex !== null}
          accessibilityLabel={t("a11y.hourlyChart", { store: storeLabel, total: formatCount(total.visitCount) })}
        />
        <ChartNote
          text={
            asOf
              ? t("chart.hourlyLiveHint", { hour: asOf.hour, time: formatClock(asOf.hour, asOf.minute) })
              : t("chart.hourlyHint")
          }
        />
      </TrafficCard>

      {dedup && total.uniqueVisitorCount + total.repeatCount > 0 ? (
        <TrafficCard>
          <CardHeader title={t("composition.title")} hint={t("composition.hint")} />
          <CompositionBar
            first={total.uniqueVisitorCount}
            repeat={total.repeatCount}
            firstLabel={t("composition.first")}
            repeatLabel={t("composition.repeat")}
          />
        </TrafficCard>
      ) : null}

      <TrafficCard>
        <CardHeader title={t("table.hourlyTitle")} hint={t("table.unit")} />
        <DetailTable
          columns={[
            { key: "hour", label: t("table.hour") },
            { key: "current", label: legend.current, width: 68 },
            { key: "compare", label: legend.compare, width: 76 },
            { key: "delta", label: t("table.delta"), width: 44 },
          ]}
          rows={rows}
        />
        {allRows.length > HOURLY_COLLAPSED_ROWS ? (
          <LinkButton
            label={tableExpanded ? t("table.collapse") : t("table.expand", { count: allRows.length })}
            onPress={onToggleTable}
          />
        ) : null}
      </TrafficCard>
    </View>
  );
}

// ───────────── 本周 / 本月 ─────────────

function MultiDayCards({ overview, language, t, storeLabel, selectedIndex, onSelect, tableExpanded, onToggleTable }: TrafficBodyProps) {
  const daily = useMemo(() => buildDailyChart(overview), [overview]);
  const profile = useMemo(() => buildProfileChart(overview), [overview]);
  const scale = useMemo(() => niceScale(seriesMax(daily.current, daily.previous)), [daily]);
  const profileScale = useMemo(() => niceScale(profile ? seriesMax(profile.values) : 0), [profile]);
  const weekdayValues = useMemo(() => (overview.period === "month" ? weekdayAverages(overview) : null), [overview]);
  const legend = legendLabels(overview, t);
  const dedup = overview.trafficDedupEnabled;
  const isWeek = overview.period === "week";
  const dayLabel = (date: string) => (isWeek ? weekdayLabel(date, language) : String(Number(date.slice(8, 10))));
  const count = daily.dates.length;
  const noData = overview.current.total.captureCount === 0;

  const tooltipFor = (index: number): LineChartTooltip | null => {
    const date = daily.dates[index];
    if (!date) return null;
    const current = daily.current[index];
    const previous = daily.previous[index];
    const lines: string[] = [];
    const dots: LineChartTooltip["dots"] = [];
    if (current !== null) {
      lines.push(t("tooltip.visits", { label: legend.current, value: current }));
      dots.push({ value: current, color: TRAFFIC_CHART_COLORS.current });
    }
    if (previous !== null) {
      lines.push(t("tooltip.visits", { label: legend.compare, value: previous }));
      dots.push({ value: previous, color: TRAFFIC_CHART_COLORS.compare });
    }
    if (dedup && current !== null) {
      lines.push(t("tooltip.unique", { value: overview.current.days[index].counts.uniqueVisitorCount }));
    }
    const live = daily.partialIndex === index ? t("tooltip.inProgress") : "";
    return { title: `${formatMonthDay(date, language)} ${weekdayLabel(date, language)}${live}`, lines, dots };
  };

  const labelIndexes = isWeek ? undefined : [0, 4, 9, 14, 19, 24, 29].filter((index) => index < count);
  const profileRange = profile?.peak ? formatHourRange(profile.window.start + profile.peak.hour) : "";

  // 明细表：本周列满 7 天（未到的日子显示 —），本月只列到今天；折叠时留最近 7 行。
  const busiest = busiestDay(overview);
  const allRows: TableRow[] = [];
  overview.current.days.forEach((day, index) => {
    const future = overview.isCurrent && isFutureDate(day.date, overview.today);
    if (future && !isWeek) return;
    const live = overview.isCurrent && day.date === overview.today;
    const previous = overview.previous.days[index]?.counts.visitCount ?? null;
    const visits = day.counts.visitCount;
    const change = !future && !live && previous !== null && previous > 0 ? Math.round(((visits - previous) / previous) * 100) : null;
    const strong = busiest !== null && day.date === busiest.date;
    const label = `${weekdayLabel(day.date, language)} ${formatShortSlash(day.date)}`;
    const cells: TableRow["cells"] = [
      <View key="date" style={styles.rowLabel}>
        <Text style={[styles.cellText, strong ? styles.cellStrong : null]} numberOfLines={1}>
          {label}
        </Text>
        {live ? <InlineTag label={t("table.inProgress")} /> : null}
      </View>,
      future ? "—" : String(visits),
      ...(dedup ? [future ? "—" : String(day.counts.uniqueVisitorCount)] : []),
      change === null ? "—" : `${change > 0 ? "+" : ""}${change}%`,
    ];
    allRows.push({ key: day.date, highlight: strong, muted: future, cells });
  });
  const rows = tableExpanded ? allRows : allRows.slice(-COLLAPSED_ROWS);
  const columns = [
    { key: "date", label: t("table.date") },
    { key: "visits", label: t("table.visits"), width: 60 },
    ...(dedup ? [{ key: "unique", label: t("table.unique"), width: 92, suffix: <EstimateTag label={t("kpi.estimate")} /> }] : []),
    { key: "change", label: t("table.change"), width: 58 },
  ];

  return (
    <View>
      <TrafficCard>
        <CardHeader title={t("chart.dailyTitle")} right={<LegendRow current={legend.current} compare={legend.compare} />} />
        {noData ? <ChartNote text={t("chart.noData")} /> : null}
        <TrafficLineChart
          count={count}
          yMax={scale.max}
          yStep={scale.step}
          series={[
            { values: daily.previous, color: TRAFFIC_CHART_COLORS.compare, dashed: true },
            { values: daily.current, color: TRAFFIC_CHART_COLORS.current, area: true, partialIndex: daily.partialIndex, dots: isWeek },
          ]}
          labelFor={(index) => dayLabel(daily.dates[index])}
          labelIndexes={labelIndexes}
          bands={isWeek ? undefined : daily.weekend}
          selectedIndex={selectedIndex}
          onSelect={onSelect}
          tooltipFor={tooltipFor}
          accessibilityLabel={t("a11y.dailyChart", { store: storeLabel, total: formatCount(overview.current.total.visitCount) })}
        />
        <ChartNote text={overview.isCurrent ? t("chart.dailyLiveHint") : t("chart.dailyHint")} />
        {!isWeek ? <ChartNote text={t("chart.weekendHint")} /> : null}
      </TrafficCard>

      {profile ? (
        <TrafficCard>
          <CardHeader title={t("chart.profileTitle")} hint={t("chart.profileHint", { days: overview.current.hourlyDays })} />
          <TrafficLineChart
            count={profile.values.length}
            yMax={profileScale.max}
            yStep={profileScale.step}
            series={[{ values: profile.values, color: TRAFFIC_CHART_COLORS.current, area: true }]}
            labelFor={(index) => String(profile.window.start + index)}
            labelIndexes={profile.values.map((_, index) => index).filter((index) => (profile.window.start + index) % 2 === 0)}
            peak={profile.peak ? { index: profile.peak.hour, label: formatAverage(profile.peak.value) } : null}
            selectedIndex={null}
            onSelect={() => undefined}
            tooltipFor={() => null}
            height={150}
            accessibilityLabel={t("a11y.profileChart")}
          />
          {profile.peak ? <ChartNote text={t("chart.peakNote", { range: profileRange, value: formatAverage(profile.peak.value) })} /> : null}
        </TrafficCard>
      ) : null}

      {weekdayValues ? (
        <TrafficCard>
          <CardHeader title={t("chart.weekdayTitle")} hint={t("chart.weekdayHint")} />
          <TrafficBarChart
            values={weekdayValues}
            labels={weekdayLabels(language).map((label) => label.replace("周", ""))}
            highlightIndex={highestIndex(weekdayValues)}
            accessibilityLabel={t("a11y.weekdayChart")}
          />
        </TrafficCard>
      ) : null}

      <TrafficCard>
        <CardHeader title={t("table.dailyTitle")} hint={t("table.unit")} />
        <DetailTable columns={columns} rows={rows} />
        {allRows.length > COLLAPSED_ROWS ? (
          <LinkButton label={tableExpanded ? t("table.collapse") : t("table.expand", { count: allRows.length })} onPress={onToggleTable} />
        ) : null}
        <ChartNote text={t("chart.averageNote")} />
      </TrafficCard>
    </View>
  );
}

function highestIndex(values: readonly (number | null)[]): number | null {
  let best: number | null = null;
  values.forEach((value, index) => {
    if (value !== null && (best === null || value > (values[best] ?? -1))) best = index;
  });
  return best;
}

// ───────────── 数据口径 ─────────────

function BasisCard({ overview, t }: { overview: TrafficOverview; t: Translate }) {
  const total = overview.current.total;
  return (
    <TrafficCard>
      <CardHeader title={t("basis.title")} />
      <Text style={styles.basisText}>
        {t("basis.formula", {
          capture: total.captureCount,
          staff: total.staffMatchCount,
          low: total.lowQualityCount,
          fail: total.apiFailureCount,
          visit: total.visitCount,
        })}
      </Text>
      <Text style={styles.basisText}>{t("basis.timezone", { zone: overview.timeZoneId || "—" })}</Text>
    </TrafficCard>
  );
}

const styles = StyleSheet.create({
  notice: { marginBottom: 10 },
  rowLabel: { flexDirection: "row", alignItems: "center", flexShrink: 1 },
  cellText: { fontSize: 13, color: HB_COLORS.textPrimary, flexShrink: 1 },
  cellStrong: { fontWeight: "700" },
  basisText: { fontSize: 12, lineHeight: 20, color: HB_COLORS.textSecondary },
});
