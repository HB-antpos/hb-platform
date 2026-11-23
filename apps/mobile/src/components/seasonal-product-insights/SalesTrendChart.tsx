import { useState } from "react";
import { StyleSheet, View, type LayoutChangeEvent } from "react-native";
import { Text } from "react-native-paper";
import Svg, { Circle, G, Line, Polyline, Rect, Text as SvgText } from "react-native-svg";
import { MOVEMENT_COLORS } from "@/components/product-insights/MovementTrendChart";
import {
  formatQuantity,
  shortDate,
  weeklyBarCeiling,
} from "@/modules/seasonal-product-insights/logic";
import type {
  DailyTrendPoint,
  WeeklyTrendPoint,
} from "@/modules/seasonal-product-insights/types";

const CHART_HEIGHT = 184;

// 配色和「商品进销查询」保持一致：进货 = 蓝，销量 = 绿，存货折线用中性深灰。
const COLORS = {
  sales: MOVEMENT_COLORS.salesBar,
  peak: MOVEMENT_COLORS.sales,
  muted: "#A6D9BC",
  inbound: MOVEMENT_COLORS.inBar,
  inboundText: MOVEMENT_COLORS.in,
  stockLine: "#475467",
  grid: "#EEF2F7",
  baseline: "#D0D5DD",
  axisText: "#667085",
  value: "#344054",
};

/** 纵轴取「好读」的刻度：约 4 格，顶部留 12% 给数值标签。 */
function niceScale(maxValue: number) {
  const target = Math.max(maxValue, 1) * 1.12;
  const rough = target / 4;
  const magnitude = 10 ** Math.floor(Math.log10(rough));
  const step = [1, 2, 5, 10].map((m) => m * magnitude).find((s) => s >= rough) ?? 10 * magnitude;
  return { max: Math.ceil(target / step) * step, step };
}

function useWidth() {
  const [width, setWidth] = useState(0);
  const onLayout = (event: LayoutChangeEvent) => {
    const next = Math.round(event.nativeEvent.layout.width);
    if (next !== width) setWidth(next);
  };
  return { width, onLayout };
}

function Grid({ scale, left, right, y }: { scale: { max: number; step: number }; left: number; right: number; y: (v: number) => number }) {
  const values: number[] = [];
  for (let value = 0; value <= scale.max + 1e-9; value += scale.step) values.push(value);
  return (
    <>
      {values.map((value) => (
        <Line key={`g-${value}`} x1={left} x2={right} y1={y(value)} y2={y(value)} stroke={value === 0 ? COLORS.baseline : COLORS.grid} strokeWidth={1} />
      ))}
      {values.map((value) => (
        <SvgText key={`gl-${value}`} x={left - 6} y={y(value) + 3.5} fontSize={10} fill={COLORS.axisText} textAnchor="end">
          {formatQuantity(value)}
        </SvgText>
      ))}
    </>
  );
}

export interface DailyTrendChartProps {
  dayCount: number;
  points: DailyTrendPoint[];
  markers: { index: number; quantity: number }[];
  startDate: string;
  endDate: string;
  /** 门店当地今天；落在区间末日时该柱浅色显示「未结束」。 */
  today: string | null;
  peakDate: string | null;
  endLabel: string;
  accessibilityLabel: string;
}

export function DailyTrendChart({
  dayCount,
  points,
  markers,
  startDate,
  endDate,
  today,
  peakDate,
  endLabel,
  accessibilityLabel,
}: DailyTrendChartProps) {
  const { width, onLayout } = useWidth();
  const top = 24;
  const bottom = CHART_HEIGHT - 20;
  const scale = niceScale(Math.max(0, ...points.map((point) => point.quantity)));
  const left = Math.max(30, formatQuantity(scale.max).length * 6 + 10);
  const right = Math.max(left + 1, width - 4);
  const step = (right - left) / Math.max(dayCount, 1);
  const barWidth = Math.max(2, Math.min(14, step * 0.66));
  const x = (index: number) => left + index * step + step / 2;
  const y = (value: number) => bottom - (value / scale.max) * (bottom - top);
  const peak = points.find((point) => point.date === peakDate);

  // 横轴约 4 个刻度：起点、中间两个、终点，避免文字互相压住。
  const tickIndexes = dayCount <= 1 ? [0] : [0, Math.round((dayCount - 1) / 3), Math.round(((dayCount - 1) * 2) / 3), dayCount - 1];
  const tickLabel = (index: number) => {
    if (index === dayCount - 1) return endLabel;
    const ms = Date.parse(`${startDate}T00:00:00Z`) + index * 86_400_000;
    return shortDate(new Date(ms).toISOString());
  };

  // 相邻到货标注太近时只画虚线不写数字，避免数字重叠。
  const labelledMarkers = new Set<number>();
  let lastLabelX = -Infinity;
  for (const marker of markers) {
    const mx = x(marker.index);
    if (mx - lastLabelX > 46 && mx + 30 < width) {
      labelledMarkers.add(marker.index);
      lastLabelX = mx;
    }
  }

  return (
    <View style={styles.container} onLayout={onLayout} accessible accessibilityRole="image" accessibilityLabel={accessibilityLabel}>
      {width > 0 ? (
        <Svg width={width} height={CHART_HEIGHT}>
          <Grid scale={scale} left={left} right={right} y={y} />
          {markers.filter((marker) => labelledMarkers.has(marker.index)).map((marker) => (
            <SvgText key={`ml-${marker.index}`} x={x(marker.index) + 3} y={top - 9} fontSize={10} fontWeight="700" fill={COLORS.inboundText}>
              {`+${formatQuantity(marker.quantity)}`}
            </SvgText>
          ))}
          {markers.map((marker) => (
            <Line key={`m-${marker.index}`} x1={x(marker.index)} x2={x(marker.index)} y1={top - 6} y2={bottom} stroke={COLORS.inbound} strokeWidth={1} strokeDasharray="3 3" />
          ))}
          {points.map((point) => {
            const barTop = y(point.quantity);
            const fill = point.date === today && point.date === endDate
              ? COLORS.muted
              : point.date === peakDate
                ? COLORS.peak
                : COLORS.sales;
            return (
              <Rect key={point.date} x={x(point.index) - barWidth / 2} y={barTop} width={barWidth} height={Math.max(bottom - barTop, 1)} rx={1.2} fill={fill} />
            );
          })}
          {peak ? (
            <SvgText x={x(peak.index)} y={y(peak.quantity) - 5} fontSize={10} fontWeight="800" fill={COLORS.peak} textAnchor="middle">
              {formatQuantity(peak.quantity)}
            </SvgText>
          ) : null}
          {tickIndexes.map((index, position) => (
            <SvgText
              key={`t-${position}-${index}`}
              x={position === 0 ? left : position === tickIndexes.length - 1 ? right : x(index)}
              y={CHART_HEIGHT - 5}
              fontSize={10}
              fill={COLORS.axisText}
              textAnchor={position === 0 ? "start" : position === tickIndexes.length - 1 ? "end" : "middle"}
            >
              {tickLabel(index)}
            </SvgText>
          ))}
        </Svg>
      ) : null}
    </View>
  );
}

export interface WeeklyComboChartProps {
  weeks: WeeklyTrendPoint[];
  /**
   * 每周周末的理论存货（与 weeks 一一对应），画成折线并启用右侧纵轴；
   * 进货与销售区间不一致时存货没有意义，传 null 则不画折线和右轴。
   */
  stock: number[] | null;
  accessibilityLabel: string;
  /** 进货柱超过纵轴上限被截断时在图下方的说明，参数是纵轴上限。 */
  clipNote: (max: string) => string;
}

/**
 * 右轴（周末存货）的取值范围与刻度。
 * 存货全为负或 0 时上限取 0（不能对 0 取「好读刻度」，会得到 0.5 并与 0 刻度重叠）；
 * 有负值时向下延伸，折线才看得出趋势而不是被压在 0 线上。
 */
function stockAxis(values: number[]) {
  const maxPositive = Math.max(0, ...values);
  const minNegative = Math.min(0, ...values);
  const lo = minNegative < 0 ? -niceScale(-minNegative).max : 0;
  const hi = maxPositive > 0 || minNegative >= 0 ? niceScale(maxPositive).max : 0;
  const ticks = lo < 0 ? (hi > 0 ? [lo, 0, hi] : [lo, 0]) : [0, hi / 2, hi];
  return { lo, hi, ticks };
}

/** 每周进货（蓝柱）+ 销量（绿柱）+ 周末存货（折线，右轴）画在同一张图里。 */
export function WeeklyComboChart({ weeks, stock, accessibilityLabel, clipNote }: WeeklyComboChartProps) {
  const { width, onLayout } = useWidth();
  const top = 18;
  const bottom = CHART_HEIGHT - 34;
  // 左轴由销量决定：进货常常是一整批到货、远高于单周销量，按它定轴会把销量压成一条线，
  // 所以超过上限的进货柱被截断，柱顶仍标出实际数量。
  const scale = niceScale(weeklyBarCeiling(weeks));
  const axis = stock ? stockAxis(stock) : null;
  const left = Math.max(30, formatQuantity(scale.max).length * 6 + 10);
  // 右轴宽度按最长的刻度文字算（负数标签带「−」更宽，按最大值算会被裁掉）。
  const rightPad = axis ? Math.max(30, Math.max(...axis.ticks.map((tick) => formatQuantity(tick).length)) * 6 + 10) : 4;
  const right = Math.max(left + 1, width - rightPad);
  const step = (right - left) / Math.max(weeks.length, 1);
  const pairWidth = Math.max(4, Math.min(30, step * 0.7));
  const barWidth = pairWidth / 2 - 0.5;
  const x = (index: number) => left + index * step + step / 2;
  const y = (value: number) => bottom - (Math.min(value, scale.max) / scale.max) * (bottom - top);
  const yStock = (value: number) => (axis ? bottom - ((value - axis.lo) / (axis.hi - axis.lo)) * (bottom - top) : bottom);
  // 周数多时隔几周标一次日期与数值，保证文字不重叠。
  const labelEvery = Math.max(1, Math.ceil(34 / Math.max(step, 1)));
  const clipped = weeks.some((week) => week.inboundQuantity > scale.max);
  const lastIndex = weeks.length - 1;

  return (
    <View onLayout={onLayout} accessible accessibilityRole="image" accessibilityLabel={accessibilityLabel}>
      <View style={styles.container}>
        {width > 0 ? (
          <Svg width={width} height={CHART_HEIGHT}>
            <Grid scale={scale} left={left} right={right} y={y} />
            {axis
              ? axis.ticks.map((value) => (
                  <SvgText key={`sr-${value}`} x={right + 5} y={yStock(value) + 3.5} fontSize={10} fill={COLORS.axisText}>
                    {formatQuantity(value)}
                  </SvgText>
                ))
              : null}
            {/* 先画所有柱和折线，文字标签统一放在最上层，避免被后画的柱盖住。 */}
            {weeks.map((week, index) => (
              <G key={week.startDate}>
                {week.inboundQuantity > 0 ? (
                  <Rect x={x(index) - barWidth - 0.5} y={y(week.inboundQuantity)} width={barWidth} height={Math.max(bottom - y(week.inboundQuantity), 1)} rx={1.5} fill={COLORS.inbound} />
                ) : null}
                {week.quantity > 0 ? (
                  <Rect x={x(index) + 0.5} y={y(week.quantity)} width={barWidth} height={Math.max(bottom - y(week.quantity), 1)} rx={1.5} fill={week.partial ? COLORS.muted : COLORS.sales} />
                ) : null}
              </G>
            ))}
            {stock && stock.length > 1 ? (
              <Polyline
                points={stock.map((value, index) => `${x(index)},${yStock(value)}`).join(" ")}
                fill="none"
                stroke={COLORS.stockLine}
                strokeWidth={1.8}
                strokeLinejoin="round"
              />
            ) : null}
            {weeks.map((week, index) => {
              // 最后一周强制显示；离它不足一个间隔的前一个标签让位，避免两个日期叠在一起。
              const labelled = index === lastIndex || (index % labelEvery === 0 && lastIndex - index >= labelEvery);
              return (
                <G key={`l-${week.startDate}`}>
                  {week.inboundQuantity > 0 ? (
                    <SvgText x={x(index) - barWidth / 2 - 0.5} y={y(week.inboundQuantity) - 4} fontSize={10} fontWeight="700" fill={COLORS.inboundText} textAnchor="middle">
                      {`+${formatQuantity(week.inboundQuantity)}`}
                    </SvgText>
                  ) : null}
                  {labelled ? (
                    <>
                      {week.quantity > 0 && week.inboundQuantity === 0 ? (
                        <SvgText x={x(index) + barWidth / 2 + 0.5} y={y(week.quantity) - 4} fontSize={10} fontWeight="700" fill={COLORS.value} textAnchor="middle">
                          {formatQuantity(week.quantity)}
                        </SvgText>
                      ) : null}
                      <SvgText x={x(index)} y={bottom + 14} fontSize={10} fill={COLORS.axisText} textAnchor="middle">
                        {shortDate(week.startDate)}
                      </SvgText>
                    </>
                  ) : null}
                </G>
              );
            })}
            {stock && stock.length ? (
              <>
                <Circle cx={x(lastIndex)} cy={yStock(stock[lastIndex])} r={3.5} fill={COLORS.stockLine} />
                {/* 终点数值带白色描边：压在柱和别的标签上时仍然读得清。 */}
                <SvgText x={x(lastIndex) - 6} y={yStock(stock[lastIndex]) - 7} fontSize={10} fontWeight="800" fill="#FFFFFF" stroke="#FFFFFF" strokeWidth={3} strokeLinejoin="round" textAnchor="end">
                  {formatQuantity(stock[lastIndex])}
                </SvgText>
                <SvgText x={x(lastIndex) - 6} y={yStock(stock[lastIndex]) - 7} fontSize={10} fontWeight="800" fill="#101828" textAnchor="end">
                  {formatQuantity(stock[lastIndex])}
                </SvgText>
              </>
            ) : null}
          </Svg>
        ) : null}
      </View>
      {clipped ? <Text style={styles.note}>{clipNote(formatQuantity(scale.max))}</Text> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { width: "100%", height: CHART_HEIGHT },
  note: { fontSize: 11, color: "#667085", paddingHorizontal: 12, paddingBottom: 8 },
});
