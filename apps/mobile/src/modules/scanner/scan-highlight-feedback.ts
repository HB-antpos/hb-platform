import { playScanHighlightSound } from "@/modules/scanner/scan-sound";
import {
  SCAN_HIGHLIGHT_HAPTICS,
  type HapticStrength,
  type ScanHighlightKind,
} from "@/modules/scanner/scan-highlight";

type HapticsModule = typeof import("expo-haptics");

const IMPACT_STYLE_KEY: Record<HapticStrength, "Light" | "Medium" | "Heavy"> = {
  light: "Light",
  medium: "Medium",
  heavy: "Heavy",
};

async function pulse(strength: HapticStrength) {
  try {
    // 懒加载：模块缺失（旧原生包 / 测试环境）时静默跳过，震动不能影响扫码主流程。
    const Haptics: HapticsModule = await import("expo-haptics");
    await Haptics.impactAsync(Haptics.ImpactFeedbackStyle[IMPACT_STYLE_KEY[strength]]);
  } catch {
    // 设备没有震动马达、用户在系统里关闭了触感反馈等情况都属正常。
  }
}

/** 折扣 / 促销 / 多码命中：铃音 + 跟着铃音节奏的震动。 */
export function playScanHighlight(kind: ScanHighlightKind) {
  playScanHighlightSound(kind);
  for (const { delayMs, strength } of SCAN_HIGHLIGHT_HAPTICS[kind]) {
    if (delayMs <= 0) {
      void pulse(strength);
    } else {
      setTimeout(() => void pulse(strength), delayMs);
    }
  }
}
