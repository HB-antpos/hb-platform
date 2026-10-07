import { StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import type { DailyCloseDifferenceKind } from "@/modules/daily-closes/types";
import { HB_COLORS } from "@/shared/theme/tokens";
import { DIFFERENCE_TONES } from "./ui";

/** 状态标签：图标 + 文字（▼ 短款 / ▲ 长款 / ✓ 已平 / — 无金额），不只靠颜色。 */
export function DifferenceTag({ kind, label }: { kind: DailyCloseDifferenceKind; label: string }) {
  const tone = DIFFERENCE_TONES[kind];
  return (
    <View style={[styles.tag, { backgroundColor: tone.background, borderColor: tone.border }]} accessibilityLabel={label}>
      <Icon source={tone.icon} size={14} color={tone.text} />
      <Text style={[styles.tagText, { color: tone.text }]}>{label}</Text>
    </View>
  );
}

/** 小标记：第 N 次 / 补录 / 推算。 */
export function MarkChip({ label, emphasis = false }: { label: string; emphasis?: boolean }) {
  return (
    <View style={[styles.mark, emphasis ? styles.markEmphasis : null]}>
      <Text style={[styles.markText, emphasis ? styles.markTextEmphasis : null]}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  tag: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    paddingLeft: 3,
    paddingRight: 8,
    paddingVertical: 1,
    borderRadius: 6,
    borderWidth: StyleSheet.hairlineWidth,
    alignSelf: "flex-start",
  },
  tagText: { fontSize: 11, lineHeight: 18, fontWeight: "700" },
  mark: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 6,
    backgroundColor: HB_COLORS.surfaceMuted,
    alignSelf: "flex-start",
  },
  markEmphasis: { backgroundColor: "#EFF8FF" },
  markText: { fontSize: 11, lineHeight: 16, color: HB_COLORS.textSecondary },
  markTextEmphasis: { color: "#175CD3", fontWeight: "600" },
});
