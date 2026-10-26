import { memo, useMemo } from "react";
import Svg, { Rect } from "react-native-svg";

import { buildEan13Modules, ean13BarRuns, EAN13_MODULE_COUNT } from "./ean13-modules";

// 左右各留 9 个模块的静区，扫码枪才能稳定识别起止护线。
const QUIET_ZONE = 9;
const TOTAL_MODULES = EAN13_MODULE_COUNT + QUIET_ZONE * 2;
// 条高与整体宽度之比，近似标签上的比例（约 285×80 点）。
const HEIGHT_RATIO = 0.36;

/** 屏幕上的 EAN13 一维码；数值不是合法 EAN13 时返回 null，由调用方回退到二维码。 */
export const Ean13Barcode = memo(function Ean13Barcode({
  value,
  width,
}: {
  value: string;
  width: number;
}) {
  const runs = useMemo(() => {
    const modules = buildEan13Modules(value);
    return modules ? ean13BarRuns(modules) : null;
  }, [value]);
  if (!runs) return null;
  // viewBox 以“模块”为单位，屏幕尺寸只由 width 决定，条宽自然等比缩放。
  const viewHeight = TOTAL_MODULES * HEIGHT_RATIO;
  return (
    <Svg width={width} height={Math.round(width * HEIGHT_RATIO)} viewBox={`0 0 ${TOTAL_MODULES} ${viewHeight}`}>
      <Rect x={0} y={0} width={TOTAL_MODULES} height={viewHeight} fill="#FFFFFF" />
      {runs.map(([start, length]) => (
        <Rect
          key={start}
          x={QUIET_ZONE + start}
          y={0}
          width={length}
          height={viewHeight}
          fill="#111111"
        />
      ))}
    </Svg>
  );
});
