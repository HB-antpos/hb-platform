import { useState } from "react";
import { StyleSheet, View } from "react-native";
import Svg, { Circle, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import type { ProductMovementTrend } from "@/modules/product-insights/movement-summary";
import { HB_COLORS } from "@/shared/theme/tokens";

export const MOVEMENT_COLORS = {
  in: HB_COLORS.action,
  inBar: "#4C8DF6",
  inSoft: "#E8F0FE",
  sales: HB_COLORS.success,
  salesBar: "#32A06A",
  salesSoft: "#E6F4EC",
} as const;

export type MovementTrendMode = "buckets" | "cumulative";

const HEIGHT = 168;
const LEFT = 28;
const RIGHT = 8;
const BOTTOM = 22;
const MARKER_ROW = 20;

/** 把最大值放大到 1 / 2 / 5 × 10^n 的整刻度，保证网格线是整数。 */
function niceScale(max: number) {
  const target = Math.max(1, max) / 3;
  const power = 10 ** Math.floor(Math.log10(target));
  const step =
    [1, 2, 5, 10].map((item) => item * power).find((item) => item >= target) ??
    10 * power;
  const top = Math.max(step, Math.ceil(max / step) * step);
  return { step, top };
}

function compact(value: number) {
  return Number.isInteger(value) ? `${value}` : value.toFixed(1);
}

function dayMonth(day: string) {
  return `${day.slice(8, 10)}/${day.slice(5, 7)}`;
}

export function MovementTrendChart({
  trend,
  mode,
  accessibilityLabel,
}: {
  trend: ProductMovementTrend;
  mode: MovementTrendMode;
  accessibilityLabel: string;
}) {
  const [width, setWidth] = useState(0);
  const buckets = trend.buckets;
  const plotWidth = Math.max(0, width - LEFT - RIGHT);
  const slot = buckets.length ? plotWidth / buckets.length : 0;
  const labelEvery = Math.max(1, Math.ceil(buckets.length / 7));

  // 到货气泡：相邻气泡会重叠时放到第二行，图表顶部按行数留白。
  const markers: { index: number; x: number; row: number; label: string; w: number }[] = [];
  if (mode === "buckets") {
    let lastRight = [-Infinity, -Infinity];
    buckets.forEach((bucket, index) => {
      if (bucket.inQuantity <= 0) return;
      const label = `+${compact(bucket.inQuantity)}`;
      const w = label.length * 6.6 + 12;
      const x = LEFT + slot * index + slot / 2;
      const row = x - w / 2 < lastRight[0] + 2 ? 1 : 0;
      lastRight = row === 0 ? [x + w / 2, lastRight[1]] : [lastRight[0], x + w / 2];
      markers.push({ index, x, row, label, w });
    });
  }
  const markerRows = markers.some((item) => item.row === 1) ? 2 : markers.length ? 1 : 0;
  const top = mode === "buckets" ? 10 + markerRows * MARKER_ROW : 14;
  const plotHeight = HEIGHT - top - BOTTOM;
  const max =
    mode === "buckets"
      ? Math.max(0, ...buckets.map((item) => item.salesQuantity))
      : Math.max(
          0,
          ...buckets.map((item) => Math.max(item.cumulativeIn, item.cumulativeSales)),
        );
  const scale = niceScale(max);
  const y = (value: number) => top + plotHeight - (plotHeight * Math.max(0, value)) / scale.top;
  const ticks = Array.from(
    { length: Math.floor(scale.top / scale.step) + 1 },
    (_, index) => index * scale.step,
  );

  const cumulativePath = (pick: "cumulativeIn" | "cumulativeSales") =>
    buckets.reduce(
      (path, bucket, index) => `${path} L${LEFT + slot * (index + 1)},${y(bucket[pick])}`,
      `M${LEFT},${y(0)}`,
    );
  const last = buckets[buckets.length - 1];

  return (
    <View
      style={styles.root}
      onLayout={(event) => setWidth(event.nativeEvent.layout.width)}
      accessible
      accessibilityRole="image"
      accessibilityLabel={accessibilityLabel}
    >
      {width > 0 ? (
        <Svg width={width} height={HEIGHT}>
          {ticks.map((tick) => (
            <Line
              key={`grid-${tick}`}
              x1={LEFT}
              x2={width - RIGHT}
              y1={y(tick)}
              y2={y(tick)}
              stroke="#EEF0F3"
              strokeWidth={1}
            />
          ))}
          {ticks.map((tick) => (
            <SvgText
              key={`tick-${tick}`}
              x={LEFT - 6}
              y={y(tick) + 3.5}
              fontSize={10}
              fill="#98A2B3"
              textAnchor="end"
            >
              {compact(tick)}
            </SvgText>
          ))}
          {mode === "buckets"
            ? buckets.map((bucket, index) => {
                const barTop = y(bucket.salesQuantity);
                return (
                  <Rect
                    key={bucket.startDate}
                    x={LEFT + slot * index + slot * 0.2}
                    y={barTop}
                    width={slot * 0.6}
                    height={Math.max(0, y(0) - barTop)}
                    rx={Math.min(3, slot * 0.15)}
                    fill={MOVEMENT_COLORS.salesBar}
                    opacity={bucket.partial ? 0.55 : 1}
                  />
                );
              })
            : null}
          {markers.map((marker) => {
            const pillY = 6 + marker.row * MARKER_ROW;
            return (
              <Line
                key={`stem-${marker.index}`}
                x1={marker.x}
                x2={marker.x}
                y1={pillY + 17}
                y2={y(buckets[marker.index].salesQuantity) - 3}
                stroke={MOVEMENT_COLORS.inBar}
                strokeWidth={1}
                strokeDasharray="2 2"
              />
            );
          })}
          {markers.map((marker) => {
            const pillY = 6 + marker.row * MARKER_ROW;
            return (
              <Rect
                key={`pill-${marker.index}`}
                x={marker.x - marker.w / 2}
                y={pillY}
                width={marker.w}
                height={17}
                rx={8.5}
                fill={MOVEMENT_COLORS.inSoft}
                stroke={MOVEMENT_COLORS.inBar}
                strokeWidth={1}
              />
            );
          })}
          {markers.map((marker) => (
            <SvgText
              key={`label-${marker.index}`}
              x={marker.x}
              y={6 + marker.row * MARKER_ROW + 12.5}
              fontSize={10.5}
              fontWeight="700"
              fill={MOVEMENT_COLORS.in}
              textAnchor="middle"
            >
              {marker.label}
            </SvgText>
          ))}
          {mode === "cumulative" && last ? (
            <>
              <Path
                d={cumulativePath("cumulativeIn")}
                stroke={MOVEMENT_COLORS.inBar}
                strokeWidth={2}
                fill="none"
              />
              <Path
                d={cumulativePath("cumulativeSales")}
                stroke={MOVEMENT_COLORS.salesBar}
                strokeWidth={2}
                fill="none"
              />
              <Circle
                cx={LEFT + slot * buckets.length}
                cy={y(last.cumulativeIn)}
                r={3}
                fill={MOVEMENT_COLORS.inBar}
              />
              <Circle
                cx={LEFT + slot * buckets.length}
                cy={y(last.cumulativeSales)}
                r={3}
                fill={MOVEMENT_COLORS.salesBar}
              />
            </>
          ) : null}
          {buckets.map((bucket, index) =>
            index % labelEvery === 0 ? (
              <SvgText
                key={`x-${bucket.startDate}`}
                x={LEFT + slot * index + slot / 2}
                y={HEIGHT - 6}
                fontSize={10}
                fill="#98A2B3"
                textAnchor="middle"
              >
                {dayMonth(bucket.startDate)}
              </SvgText>
            ) : null,
          )}
        </Svg>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { height: HEIGHT, marginHorizontal: -4 },
});
