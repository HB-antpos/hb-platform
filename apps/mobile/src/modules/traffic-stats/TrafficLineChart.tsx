// 客流曲线图（react-native-svg 手绘，与报表中心的累计营业额图同一思路，不引入图表库）。
// 交互：点一下选中最近的数据点，再点同一处取消；左右拖动时竖线吸附到整点 / 整天并弹出明细。
// 竖向滑动不会被图表吞掉（页面仍可滚动）：只有水平位移占优时才进入拖动选点，
// 选点在松手 / 拖动中才发生，页面滚动打断触摸（terminate）时不会误选。
import { useMemo, useRef, useState } from "react";
import { PanResponder, StyleSheet, View, type LayoutChangeEvent } from "react-native";
import Svg, { Circle, Defs, G, Line, LinearGradient, Path, Rect, Stop, Text as SvgText } from "react-native-svg";
import {
  areaPath,
  estimateTextWidth,
  labelStep,
  monotonePath,
  nearestIndex,
  plotHeight,
  projectSeries,
  tooltipLeft,
  xAt,
  yAt,
  type ChartLayout,
} from "./chart-model";

export const TRAFFIC_CHART_COLORS = {
  current: "#2563EB",
  compare: "#98A2B3",
  grid: "#EEF2F7",
  baseline: "#D0D5DD",
  axisText: "#667085",
  band: "#F2F4F7",
  tooltip: "#101828",
  barSoft: "#B2CCFF",
} as const;

const MARGIN = { left: 36, right: 12, top: 14, bottom: 24 } as const;
const TAP_SLOP = 6;

export interface LineChartSeries {
  /** 与横轴等长；null = 没有数据（今天之后）。 */
  values: readonly (number | null)[];
  color: string;
  dashed?: boolean;
  /** 曲线下方画淡色渐变。 */
  area?: boolean;
  /** 进行中的数据点下标：最后一段画虚线，终点画空心圆。 */
  partialIndex?: number | null;
  /** 每个数据点画实心小圆点（点不多的每日曲线）。 */
  dots?: boolean;
}

export interface LineChartTooltip {
  title: string;
  lines: string[];
  /** 选中位置上要标出的圆点（按序列顺序）。 */
  dots: { value: number; color: string }[];
}

export interface TrafficLineChartProps {
  count: number;
  yMax: number;
  yStep: number;
  series: readonly LineChartSeries[];
  /** 横轴标签：下标 → 文案；不给则按间隔自动标 labelFor(index)。 */
  labelFor: (index: number) => string;
  /** 强制显示标签的下标（如月视图的 1、5、10…），不给则按 labelStep 取。 */
  labelIndexes?: readonly number[];
  /** 周末灰底。 */
  bands?: readonly boolean[];
  nowIndex?: number | null;
  nowLabel?: string | null;
  /** 峰值标注。 */
  peak?: { index: number; label: string } | null;
  selectedIndex: number | null;
  onSelect: (index: number | null) => void;
  tooltipFor: (index: number) => LineChartTooltip | null;
  /** 提示框放在竖线左侧（被右侧的「现在」标签挡住时）。 */
  tooltipOnLeft?: boolean;
  height?: number;
  accessibilityLabel: string;
}

export function TrafficLineChart({
  count,
  yMax,
  yStep,
  series,
  labelFor,
  labelIndexes,
  bands,
  nowIndex = null,
  nowLabel = null,
  peak = null,
  selectedIndex,
  onSelect,
  tooltipFor,
  tooltipOnLeft = false,
  height = 196,
  accessibilityLabel,
}: TrafficLineChartProps) {
  const [width, setWidth] = useState(0);
  const layout = useMemo<ChartLayout>(
    () => ({ width: Math.max(width, 1), height, margin: MARGIN, count, yMax, mode: "line" }),
    [count, height, width, yMax],
  );

  // PanResponder 只创建一次，实时值走 ref，避免每次渲染重建手势导致拖动中断。
  const live = useRef({ layout, selectedIndex, onSelect });
  live.current = { layout, selectedIndex, onSelect };
  const gesture = useRef({ startX: 0, scrubbing: false });

  const responder = useMemo(
    () =>
      PanResponder.create({
        onStartShouldSetPanResponder: () => true,
        onPanResponderTerminationRequest: () => true,
        onPanResponderGrant: (event) => {
          gesture.current = { startX: event.nativeEvent.locationX, scrubbing: false };
        },
        onPanResponderMove: (event, state) => {
          if (!gesture.current.scrubbing) {
            if (Math.abs(state.dx) < TAP_SLOP || Math.abs(state.dx) <= Math.abs(state.dy)) return;
            gesture.current.scrubbing = true;
          }
          const current = live.current;
          current.onSelect(nearestIndex(current.layout, gesture.current.startX + state.dx));
        },
        onPanResponderRelease: (event, state) => {
          const current = live.current;
          if (gesture.current.scrubbing) return;
          // 没有拖动 = 点按：点在已选中的位置上取消，否则选中最近的点。
          if (Math.abs(state.dx) > TAP_SLOP || Math.abs(state.dy) > TAP_SLOP) return;
          // 用按下时记下的 x：抬起事件里的 locationX 在部分平台不可靠。
          const index = nearestIndex(current.layout, gesture.current.startX);
          current.onSelect(current.selectedIndex === index ? null : index);
        },
      }),
    [],
  );

  const onLayout = (event: LayoutChangeEvent) => setWidth(Math.round(event.nativeEvent.layout.width));

  const baseline = yAt(layout, 0);
  const ticks: number[] = [];
  for (let value = 0; value <= yMax + 1e-9; value += yStep) ticks.push(value);
  const step = labelStep(count);
  const showLabel = (index: number) => (labelIndexes ? labelIndexes.includes(index) : index % step === 0);
  const tooltip = selectedIndex !== null ? tooltipFor(selectedIndex) : null;
  const gradientId = "traffic-area";

  return (
    <View
      style={[styles.wrap, { height }]}
      onLayout={onLayout}
      accessible
      accessibilityRole="image"
      accessibilityLabel={accessibilityLabel}
    >
      {width > 0 ? (
        <Svg width={width} height={height} pointerEvents="none">
          <Defs>
            <LinearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
              <Stop offset="0" stopColor={TRAFFIC_CHART_COLORS.current} stopOpacity={0.22} />
              <Stop offset="1" stopColor={TRAFFIC_CHART_COLORS.current} stopOpacity={0} />
            </LinearGradient>
          </Defs>

          {bands?.map((on, index) =>
            on ? (
              <Rect
                key={`band-${index}`}
                x={xAt(layout, index) - (count > 1 ? (width - MARGIN.left - MARGIN.right) / (count - 1) / 2 : 0)}
                y={MARGIN.top}
                width={count > 1 ? (width - MARGIN.left - MARGIN.right) / (count - 1) : 0}
                height={plotHeight(layout)}
                fill={TRAFFIC_CHART_COLORS.band}
              />
            ) : null,
          )}

          {ticks.map((value) => (
            <G key={`tick-${value}`}>
              <Line
                x1={MARGIN.left}
                x2={width - MARGIN.right}
                y1={yAt(layout, value)}
                y2={yAt(layout, value)}
                stroke={value === 0 ? TRAFFIC_CHART_COLORS.baseline : TRAFFIC_CHART_COLORS.grid}
                strokeWidth={1}
              />
              <SvgText x={MARGIN.left - 6} y={yAt(layout, value) + 3.5} fontSize={10.5} fill={TRAFFIC_CHART_COLORS.axisText} textAnchor="end">
                {formatTick(value)}
              </SvgText>
            </G>
          ))}

          {Array.from({ length: count }, (_, index) =>
            showLabel(index) ? (
              <SvgText key={`x-${index}`} x={xAt(layout, index)} y={height - 7} fontSize={10.5} fill={TRAFFIC_CHART_COLORS.axisText} textAnchor="middle">
                {labelFor(index)}
              </SvgText>
            ) : null,
          )}

          {series.map((item, seriesIndex) => (
            <SeriesPath key={`series-${seriesIndex}`} layout={layout} series={item} baseline={baseline} gradientId={gradientId} />
          ))}

          {peak ? <PeakMark layout={layout} index={peak.index} label={peak.label} series={series} /> : null}

          {nowIndex !== null ? (
            <G>
              <Line
                x1={xAt(layout, nowIndex)}
                x2={xAt(layout, nowIndex)}
                y1={MARGIN.top}
                y2={baseline}
                stroke={TRAFFIC_CHART_COLORS.current}
                strokeWidth={1}
                strokeDasharray="2 3"
                opacity={0.7}
              />
              {nowLabel ? (
                <G>
                  <Rect x={xAt(layout, nowIndex) - 17} y={MARGIN.top - 4} width={34} height={15} rx={7.5} fill={TRAFFIC_CHART_COLORS.current} />
                  <SvgText x={xAt(layout, nowIndex)} y={MARGIN.top + 7} fontSize={9.5} fontWeight="700" fill="#FFFFFF" textAnchor="middle">
                    {nowLabel}
                  </SvgText>
                </G>
              ) : null}
            </G>
          ) : null}

          {tooltip && selectedIndex !== null ? (
            <TooltipMark layout={layout} index={selectedIndex} tooltip={tooltip} onLeft={tooltipOnLeft} baseline={baseline} />
          ) : null}
        </Svg>
      ) : null}
      {/* 手势层盖在 SVG 上：locationX 才是相对图表容器的坐标（SVG 子元素会改变触点参考系）。 */}
      <View style={StyleSheet.absoluteFill} {...responder.panHandlers} />
    </View>
  );
}

function formatTick(value: number): string {
  return Number.isInteger(value) ? String(value) : String(Math.round(value * 10) / 10);
}

function SeriesPath({
  layout,
  series,
  baseline,
  gradientId,
}: {
  layout: ChartLayout;
  series: LineChartSeries;
  baseline: number;
  gradientId: string;
}) {
  const points = projectSeries(layout, series.values);
  if (points.length === 0) return null;
  const strokeWidth = series.dashed ? 2 : 2.4;
  const dash = series.dashed ? "5 4" : undefined;

  if (points.length === 1) {
    return <Circle cx={points[0].x} cy={points[0].y} r={3.5} fill={series.color} />;
  }

  const last = points[points.length - 1];
  const partial = series.partialIndex != null && last.index === series.partialIndex;
  const solid = partial ? points.slice(0, -1) : points;
  const curve = monotonePath(points);
  return (
    <G>
      {series.area ? <Path d={areaPath(curve, points, baseline)} fill={`url(#${gradientId})`} /> : null}
      {partial ? (
        <G>
          {solid.length >= 2 ? <Path d={monotonePath(solid)} stroke={series.color} strokeWidth={strokeWidth} fill="none" strokeLinecap="round" /> : null}
          <Path
            d={`M${solid[solid.length - 1].x},${solid[solid.length - 1].y} L${last.x},${last.y}`}
            stroke={series.color}
            strokeWidth={strokeWidth}
            strokeDasharray="3 3"
            fill="none"
            strokeLinecap="round"
          />
          <Circle cx={last.x} cy={last.y} r={4.5} fill="#FFFFFF" stroke={series.color} strokeWidth={2} />
        </G>
      ) : (
        <Path d={curve} stroke={series.color} strokeWidth={strokeWidth} strokeDasharray={dash} fill="none" strokeLinecap="round" strokeLinejoin="round" />
      )}
      {series.dots ? solid.map((point) => <Circle key={`dot-${point.index}`} cx={point.x} cy={point.y} r={3} fill={series.color} />) : null}
    </G>
  );
}

function PeakMark({
  layout,
  index,
  label,
  series,
}: {
  layout: ChartLayout;
  index: number;
  label: string;
  series: readonly LineChartSeries[];
}) {
  const primary = series.find((item) => !item.dashed);
  const value = primary?.values[index];
  if (value == null) return null;
  const x = xAt(layout, index);
  const y = yAt(layout, value);
  return (
    <G>
      <Circle cx={x} cy={y} r={4} fill={TRAFFIC_CHART_COLORS.current} stroke="#FFFFFF" strokeWidth={2} />
      <SvgText x={x} y={y - 9} fontSize={11} fontWeight="700" fill={TRAFFIC_CHART_COLORS.current} textAnchor="middle">
        {label}
      </SvgText>
    </G>
  );
}

function TooltipMark({
  layout,
  index,
  tooltip,
  onLeft,
  baseline,
}: {
  layout: ChartLayout;
  index: number;
  tooltip: LineChartTooltip;
  onLeft: boolean;
  baseline: number;
}) {
  const x = xAt(layout, index);
  const rows = [tooltip.title, ...tooltip.lines];
  const boxWidth = Math.ceil(Math.max(...rows.map((row) => estimateTextWidth(row, 11)))) + 20;
  const boxHeight = rows.length * 14 + 14;
  const left = onLeft ? Math.max(layout.margin.left, x - boxWidth - 10) : tooltipLeft(layout, x, boxWidth);
  const top = layout.margin.top;
  return (
    <G>
      <Line x1={x} x2={x} y1={layout.margin.top} y2={baseline} stroke={TRAFFIC_CHART_COLORS.tooltip} strokeWidth={1} opacity={0.35} />
      {tooltip.dots.map((dot, dotIndex) => (
        <Circle key={`tip-dot-${dotIndex}`} cx={x} cy={yAt(layout, dot.value)} r={5} fill={dot.color} stroke="#FFFFFF" strokeWidth={2.2} />
      ))}
      <Rect x={left} y={top} width={boxWidth} height={boxHeight} rx={8} fill={TRAFFIC_CHART_COLORS.tooltip} opacity={0.94} />
      {rows.map((row, rowIndex) => (
        <SvgText
          key={`tip-${rowIndex}`}
          x={left + 10}
          y={top + 17 + rowIndex * 14}
          fontSize={11}
          fontWeight={rowIndex === 0 ? "700" : "500"}
          fill={rowIndex === 0 ? "#FFFFFF" : "#D0D5DD"}
        >
          {row}
        </SvgText>
      ))}
    </G>
  );
}

const styles = StyleSheet.create({
  wrap: { width: "100%" },
});
