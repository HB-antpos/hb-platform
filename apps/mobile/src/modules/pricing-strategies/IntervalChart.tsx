import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View, type LayoutChangeEvent } from "react-native";
import Svg, { Line, Path, Polygon, Rect, Text as SvgText } from "react-native-svg";
import { HB_COLORS } from "@/shared/theme/tokens";
import { actualPrice, chartRange, isFixed, priceDrops, theoryPrice, visibleMaxPrice, type Rule } from "./pricing-intervals";
import { PRICING_COLORS } from "./shared";

const HEIGHT = 168;
const PAD_L = 34;
const PAD_R = 8;
const PAD_T = 8;
const PAD_B = 18;
const SAMPLES = 120;

/** 取 1 / 2 / 2.5 / 5 × 10^k 的整齐刻度（与 Web 编辑器同一算法）。 */
function niceTicks(lo: number, hi: number, count: number): number[] {
  const raw = (hi - lo) / count;
  if (!(raw > 0)) return [lo];
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = ([1, 2, 2.5, 5, 10].find((m) => m * mag >= raw) ?? 10) * mag;
  const ticks: number[] = [];
  for (let t = Math.ceil(lo / step) * step; t <= hi + 1e-9; t += step) ticks.push(Math.round(t * 1e6) / 1e6);
  return ticks;
}
const tickText = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(2).replace(/0$/, ""));

/**
 * 只读区间图：金色固定 / 蓝色曲线为理论价，橙色阶梯为取尾数后的实际售价，浅绿带是 1.5～5 倍合法范围。
 * 手机上不做拖动（手指精度不够），点图上的区间只是选中它，具体数值在区间面板里输入。
 */
export function IntervalChart({
  rules,
  selected,
  onSelect,
  accessibilityLabel,
}: {
  rules: Rule[];
  selected: number;
  onSelect: (index: number) => void;
  accessibilityLabel: string;
}) {
  const [width, setWidth] = useState(0);
  const onLayout = (event: LayoutChangeEvent) => setWidth(Math.round(event.nativeEvent.layout.width));

  const drawing = useMemo(() => {
    if (!width || !rules.length) return null;
    const { first, end } = chartRange(rules);
    const maxY = Math.max(visibleMaxPrice(rules, end) * 1.15, 5);
    const plotW = width - PAD_L - PAD_R;
    const plotH = HEIGHT - PAD_T - PAD_B;
    const X = (x: number) => PAD_L + ((x - first) / (end - first)) * plotW;
    const Y = (y: number) => HEIGHT - PAD_B - (y / maxY) * plotH;
    const paths = rules.map((r) => {
      if (r.minPrice >= end) return null;
      const lo = r.minPrice;
      const hi = Math.min(r.maxPrice, end);
      let line = "";
      let steps = "";
      for (let k = 0; k <= SAMPLES; k++) {
        const c = lo + ((hi - lo) * k) / SAMPLES;
        const p = theoryPrice(r, c);
        const f = actualPrice(r, c);
        line += `${k ? "L" : "M"}${X(c).toFixed(1)},${Y(p).toFixed(1)}`;
        steps += k ? `H${X(c).toFixed(1)}V${Y(f).toFixed(1)}` : `M${X(c).toFixed(1)},${Y(f).toFixed(1)}`;
      }
      return { fixed: isFixed(r), line, steps, x0: X(lo), x1: X(hi) };
    });
    return {
      band: `${X(first)},${Y(first * 1.5)} ${X(end)},${Y(end * 1.5)} ${X(end)},${Y(Math.min(end * 5, maxY))} ${X(first)},${Y(Math.min(first * 5, maxY))}`,
      paths,
      yTicks: niceTicks(0, maxY, 3),
      xTicks: niceTicks(first, end, 4),
      boundaries: rules.slice(0, -1).filter((r) => r.maxPrice < end).map((r) => X(r.maxPrice)),
      drops: priceDrops(rules).filter((d) => d.at < end).map((d) => ({ x: X(d.at), y0: Y(d.from), y1: Y(d.to) })),
      X,
      Y,
    };
  }, [rules, width]);

  return (
    <View onLayout={onLayout} style={styles.wrap} accessible accessibilityRole="image" accessibilityLabel={accessibilityLabel}>
      {drawing ? (
        <>
          <Svg width={width} height={HEIGHT}>
            <Polygon points={drawing.band} fill={PRICING_COLORS.legalBand} opacity={0.7} />
            {drawing.paths.map((p, i) =>
              p ? (
                <Rect
                  key={`bg${i}`}
                  x={p.x0}
                  y={PAD_T}
                  width={Math.max(p.x1 - p.x0, 0)}
                  height={HEIGHT - PAD_T - PAD_B}
                  fill={p.fixed ? PRICING_COLORS.goldBg : PRICING_COLORS.blueBg}
                  opacity={0.6}
                />
              ) : null,
            )}
            {drawing.yTicks.map((t) => (
              <Line key={`y${t}`} x1={PAD_L} x2={width - PAD_R} y1={drawing.Y(t)} y2={drawing.Y(t)} stroke={HB_COLORS.outlineMuted} />
            ))}
            {drawing.yTicks.map((t) => (
              <SvgText key={`yt${t}`} x={PAD_L - 4} y={drawing.Y(t) + 3} fontSize={9} fill={PRICING_COLORS.muted} textAnchor="end">
                {tickText(t)}
              </SvgText>
            ))}
            {drawing.xTicks.map((t) => (
              <SvgText key={`xt${t}`} x={drawing.X(t)} y={HEIGHT - 4} fontSize={9} fill={PRICING_COLORS.muted} textAnchor="middle">
                {tickText(t)}
              </SvgText>
            ))}
            {drawing.paths.map((p, i) =>
              p ? <Path key={`s${i}`} d={p.steps} fill="none" stroke={PRICING_COLORS.orange} strokeWidth={1.3} /> : null,
            )}
            {drawing.paths.map((p, i) =>
              p ? (
                <Path key={`l${i}`} d={p.line} fill="none" stroke={p.fixed ? PRICING_COLORS.gold : PRICING_COLORS.blue} strokeWidth={2.2} />
              ) : null,
            )}
            {drawing.boundaries.map((x, i) => (
              <Line key={`b${i}`} x1={x} x2={x} y1={PAD_T} y2={HEIGHT - PAD_B} stroke={HB_COLORS.outline} strokeDasharray="3 3" />
            ))}
            {drawing.drops.map((d, i) => (
              <Line key={`d${i}`} x1={d.x} x2={d.x} y1={d.y0} y2={d.y1} stroke={HB_COLORS.danger} strokeWidth={3} />
            ))}
            {drawing.paths[selected] ? (
              <Rect
                x={drawing.paths[selected]!.x0}
                y={PAD_T}
                width={Math.max(drawing.paths[selected]!.x1 - drawing.paths[selected]!.x0, 0)}
                height={HEIGHT - PAD_T - PAD_B}
                fill="none"
                stroke={HB_COLORS.brand}
                strokeWidth={1.5}
                rx={3}
              />
            ) : null}
          </Svg>
          {/* 透明点击层：点哪个区间就选中哪个（窄区间至少给 24pt 的点击宽度）。 */}
          {drawing.paths.map((p, i) =>
            p ? (
              <Pressable
                key={`hit${i}`}
                accessible={false}
                onPress={() => onSelect(i)}
                style={[styles.hit, { left: Math.min(p.x0, width - 24), width: Math.max(p.x1 - p.x0, 24) }]}
              />
            ) : null,
          )}
        </>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { height: HEIGHT, width: "100%" },
  hit: { position: "absolute", top: PAD_T, height: HEIGHT - PAD_T - PAD_B },
});
