import React from "react";
import { StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { describeNativeAppDownloadProgress } from "./native-app-download-progress";
import type { NativeAppDownloadProgress } from "./native-app-update";

type Props = {
  progress: NativeAppDownloadProgress;
  color: string;
  trackColor: string;
  labelColor: string;
  /** 网络差时显示在进度下方的提示（已翻译）；不传则不提示。 */
  slowNetworkHint?: string;
  hintColor?: string;
};

/**
 * APK 下载确定进度：细轨道 + 等宽数字文案。不做宽度动画，
 * 每前进 1% 直接跳到新宽度，安卓 10 低端机上也不额外占用帧。
 */
export function NativeAppDownloadProgressBar({
  progress,
  color,
  trackColor,
  labelColor,
  slowNetworkHint,
  hintColor,
}: Props) {
  const { percent, label } = describeNativeAppDownloadProgress(progress);
  return (
    <View style={styles.container}>
      <View
        accessible
        accessibilityRole="progressbar"
        accessibilityValue={{ min: 0, max: 100, now: percent }}
        style={[styles.track, { backgroundColor: trackColor }]}
      >
        <View style={[styles.fill, { backgroundColor: color, width: `${percent}%` }]} />
      </View>
      <Text variant="bodySmall" style={[styles.label, { color: labelColor }]}>
        {label}
      </Text>
      {progress.slowNetwork && slowNetworkHint ? (
        <Text variant="bodySmall" style={{ color: hintColor ?? labelColor }}>
          {slowNetworkHint}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { gap: 6 },
  track: { height: 4, borderRadius: 2, overflow: "hidden" },
  fill: { height: "100%", borderRadius: 2 },
  label: { fontVariant: ["tabular-nums"] },
});
