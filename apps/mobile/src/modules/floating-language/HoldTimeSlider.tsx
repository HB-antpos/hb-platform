import { useEffect, useMemo, useRef, useState } from "react";
import { PanResponder, StyleSheet, Text, View, type LayoutChangeEvent } from "react-native";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import {
  FLOATING_LONG_PRESS_MAX_MS,
  FLOATING_LONG_PRESS_MIN_MS,
  longPressFromRatio,
  normalizeLongPressMs,
  ratioFromLongPress,
} from "./geometry";

const THUMB_SIZE = 26;
const TRACK_HEIGHT = 4;
/** 读屏「增加 / 减少」每次调整的幅度。 */
const ACCESSIBILITY_STEP_MS = 500;

interface HoldTimeSliderProps {
  /** 已保存的按住时长（毫秒）。 */
  value: number;
  /** 松手（或读屏调整）后提交；拖动过程中不提交，避免频繁写存储。 */
  onCommit: (ms: number) => void;
  label: string;
  /** 当前值的显示文案，如「2 秒」。 */
  formatValue: (ms: number) => string;
  minLabel: string;
  maxLabel: string;
  testID?: string;
}

/**
 * 纯 JS 滑轨（PanResponder）：项目里没有滑块依赖，加原生滑块会让这次更新不能走 OTA。
 * 触摸只由外层容器接收（子元素 pointerEvents="none"），locationX 才始终相对轨道，点轨道任意位置也能直接跳过去。
 */
export function HoldTimeSlider({
  value,
  onCommit,
  label,
  formatValue,
  minLabel,
  maxLabel,
  testID,
}: HoldTimeSliderProps) {
  const [width, setWidth] = useState(0);
  // 拖动中显示草稿值；没在拖动时跟随已保存的值。
  const [draft, setDraft] = useState<number | null>(null);
  const shown = draft ?? normalizeLongPressMs(value);

  const latest = useRef({ width, onCommit });
  latest.current = { width, onCommit };
  const gesture = useRef({ startX: 0, current: normalizeLongPressMs(value) });

  // 已保存的值被外部改了（例如另一处重置）且没在拖动：丢掉草稿。
  useEffect(() => {
    if (draft === null) gesture.current.current = normalizeLongPressMs(value);
  }, [draft, value]);

  const panResponder = useMemo(() => {
    const msAt = (x: number) => {
      const usable = Math.max(1, latest.current.width - THUMB_SIZE);
      return longPressFromRatio((x - THUMB_SIZE / 2) / usable);
    };
    return PanResponder.create({
      onStartShouldSetPanResponder: () => true,
      onMoveShouldSetPanResponder: () => true,
      // 页面外层是纵向滚动，拖滑块时不能被它抢走。
      onPanResponderTerminationRequest: () => false,
      onPanResponderGrant: (event) => {
        gesture.current.startX = event.nativeEvent.locationX;
        gesture.current.current = msAt(gesture.current.startX);
        setDraft(gesture.current.current);
      },
      onPanResponderMove: (_event, state) => {
        const ms = msAt(gesture.current.startX + state.dx);
        if (ms !== gesture.current.current) {
          gesture.current.current = ms;
          setDraft(ms);
        }
      },
      onPanResponderRelease: () => {
        latest.current.onCommit(gesture.current.current);
        setDraft(null);
      },
      onPanResponderTerminate: () => {
        // 被系统打断：不提交，回到已保存的值。
        setDraft(null);
      },
    });
  }, []);

  const onLayout = (event: LayoutChangeEvent) => setWidth(event.nativeEvent.layout.width);
  const usable = Math.max(0, width - THUMB_SIZE);
  const thumbLeft = ratioFromLongPress(shown) * usable;

  return (
    <View style={styles.wrap} testID={testID}>
      <View style={styles.header}>
        <Text style={styles.label} maxFontSizeMultiplier={1.4}>
          {label}
        </Text>
        <Text style={styles.value} maxFontSizeMultiplier={1.4}>
          {formatValue(shown)}
        </Text>
      </View>
      <View
        {...panResponder.panHandlers}
        onLayout={onLayout}
        style={styles.touchArea}
        accessible
        accessibilityRole="adjustable"
        accessibilityLabel={label}
        accessibilityValue={{
          min: FLOATING_LONG_PRESS_MIN_MS,
          max: FLOATING_LONG_PRESS_MAX_MS,
          now: shown,
          text: formatValue(shown),
        }}
        accessibilityActions={[{ name: "increment" }, { name: "decrement" }]}
        onAccessibilityAction={(event) => {
          const delta = event.nativeEvent.actionName === "increment" ? ACCESSIBILITY_STEP_MS : -ACCESSIBILITY_STEP_MS;
          onCommit(normalizeLongPressMs(normalizeLongPressMs(value) + delta));
        }}
      >
        <View pointerEvents="none" style={styles.track} />
        <View pointerEvents="none" style={[styles.fill, { width: thumbLeft }]} />
        <View pointerEvents="none" style={[styles.thumb, { left: thumbLeft }]} />
      </View>
      <View style={styles.scale} pointerEvents="none">
        <Text style={styles.scaleText} maxFontSizeMultiplier={1.4}>
          {minLabel}
        </Text>
        <Text style={styles.scaleText} maxFontSizeMultiplier={1.4}>
          {maxLabel}
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: {
    paddingHorizontal: HB_SPACING.md,
    paddingBottom: HB_SPACING.sm,
  },
  header: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "baseline",
  },
  label: {
    fontSize: 14,
    color: HB_COLORS.textSecondary,
  },
  value: {
    fontSize: 15,
    fontWeight: "800",
    color: HB_COLORS.action,
  },
  touchArea: {
    height: 40,
    justifyContent: "center",
  },
  track: {
    position: "absolute",
    left: THUMB_SIZE / 2,
    right: THUMB_SIZE / 2,
    height: TRACK_HEIGHT,
    borderRadius: TRACK_HEIGHT / 2,
    backgroundColor: HB_COLORS.outlineMuted,
  },
  fill: {
    position: "absolute",
    // 填充从轨道左端（缩进半个滑块）起，到滑块中心为止。
    left: THUMB_SIZE / 2,
    height: TRACK_HEIGHT,
    borderRadius: TRACK_HEIGHT / 2,
    backgroundColor: HB_COLORS.brand,
  },
  thumb: {
    position: "absolute",
    width: THUMB_SIZE,
    height: THUMB_SIZE,
    borderRadius: THUMB_SIZE / 2,
    backgroundColor: HB_COLORS.white,
    borderWidth: 2,
    borderColor: HB_COLORS.brand,
    shadowColor: "#101828",
    shadowOpacity: 0.2,
    shadowRadius: 3,
    shadowOffset: { width: 0, height: 1 },
    elevation: 3,
  },
  scale: {
    flexDirection: "row",
    justifyContent: "space-between",
    paddingHorizontal: 2,
  },
  scaleText: {
    fontSize: 12,
    color: HB_COLORS.textSecondary,
  },
});
