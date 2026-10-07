import { StyleSheet, Text, View } from "react-native";

import {
  describeApkDownloadProgress,
  type ApkDownloadProgress,
} from "./apk-download-progress";

import { posColors } from "@/ui/theme";

/**
 * APK 下载确定进度：细轨道 + 等宽数字文案。不做宽度动画，
 * 每前进 1% 直接跳到新宽度，TC26（安卓 10）上不额外占用帧。
 */
export function ApkDownloadProgressBar({
  progress,
}: Readonly<{ progress: ApkDownloadProgress }>) {
  const { percent, label } = describeApkDownloadProgress(progress);
  return (
    <View style={styles.container} testID="app-update-download-progress">
      <View
        accessible
        accessibilityRole="progressbar"
        accessibilityValue={{ min: 0, max: 100, now: percent }}
        style={styles.track}
      >
        <View style={[styles.fill, { width: `${percent}%` }]} />
      </View>
      <Text style={styles.label}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    gap: 6,
    marginTop: 8,
  },
  track: {
    backgroundColor: posColors.orangeSoft,
    borderRadius: 2,
    height: 4,
    overflow: "hidden",
  },
  fill: {
    backgroundColor: posColors.orange,
    borderRadius: 2,
    height: "100%",
  },
  label: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    lineHeight: 18,
  },
});
