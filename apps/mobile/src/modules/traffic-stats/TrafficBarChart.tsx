// 「按星期平均」柱状图：7 根柱，最高的一根用深色标出；只读，不做触摸交互（数值直接标在柱顶）。
import { useMemo, useState } from "react";
import { StyleSheet, View, type LayoutChangeEvent } from "react-native";
import Svg, { G, Line, Rect, Text as SvgText } from "react-native-svg";
import { niceScale, plotWidth, seriesMax, xAt, yAt, type ChartLayout } from "./chart-model";
import { TRAFFIC_CHART_COLORS } from "./TrafficLineChart";

const MARGIN = { left: 36, right: 12, top: 18, bottom: 24 } as const;

export function TrafficBarChart({
  values,
  labels,
  highlightIndex,
  height = 150,
  accessibilityLabel,
}: {
  /** null = 该星期没有营业日。 */
  values: readonly (number | null)[];
  labels: readonly string[];
  highlightIndex: number | null;
  height?: number;
  accessibilityLabel: string;
}) {
  const [width, setWidth] = useState(0);
  const scale = useMemo(() => niceScale(seriesMax(values)), [values]);
  const layout = useMemo<ChartLayout>(
    () => ({ width: Math.max(width, 1), height, margin: MARGIN, count: values.length, yMax: scale.max, mode: "bar" }),
    [height, scale.max, values.length, width],
  );
  const onLayout = (event: LayoutChangeEvent) => setWidth(Math.round(event.nativeEvent.layout.width));
  const barWidth = (plotWidth(layout) / values.length) * 0.58;
  const ticks: number[] = [];
  for (let value = 0; value <= scale.max + 1e-9; value += scale.step) ticks.push(value);

  return (
    <View style={[styles.wrap, { height }]} onLayout={onLayout} accessible accessibilityRole="image" accessibilityLabel={accessibilityLabel}>
      {width > 0 ? (
        <Svg width={width} height={height}>
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
                {value}
              </SvgText>
            </G>
          ))}
          {values.map((value, index) => {
            const x = xAt(layout, index);
            const highlighted = index === highlightIndex;
            return (
              <G key={`bar-${index}`}>
                {value != null ? (
                  <G>
                    <Rect
                      x={x - barWidth / 2}
                      y={yAt(layout, value)}
                      width={barWidth}
                      height={Math.max(0, yAt(layout, 0) - yAt(layout, value))}
                      rx={4}
                      fill={highlighted ? TRAFFIC_CHART_COLORS.current : TRAFFIC_CHART_COLORS.barSoft}
                    />
                    <SvgText
                      x={x}
                      y={yAt(layout, value) - 5}
                      fontSize={10.5}
                      fontWeight={highlighted ? "700" : "500"}
                      fill={highlighted ? "#1D4ED8" : "#475467"}
                      textAnchor="middle"
                    >
                      {Math.round(value)}
                    </SvgText>
                  </G>
                ) : (
                  <SvgText x={x} y={yAt(layout, 0) - 5} fontSize={10.5} fill={TRAFFIC_CHART_COLORS.axisText} textAnchor="middle">
                    —
                  </SvgText>
                )}
                <SvgText x={x} y={height - 7} fontSize={10.5} fill={TRAFFIC_CHART_COLORS.axisText} textAnchor="middle">
                  {labels[index]}
                </SvgText>
              </G>
            );
          })}
        </Svg>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { width: "100%" },
});
