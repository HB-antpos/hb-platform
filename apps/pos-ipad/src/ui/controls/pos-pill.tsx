import { Text, View, type StyleProp, type ViewStyle } from "react-native";

import { createPosStyles } from "@/ui/theme";

export type PosPillTone =
  | "neutral"
  | "good"
  | "warning"
  | "danger"
  | "info"
  | "accent";

export type PosPillProps = Readonly<{
  label: string;
  tone?: PosPillTone;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}>;

/** 状态标签：浅底 + 同色系文字，用颜色区分好 / 提醒 / 危险 / 信息。 */
export function PosPill({ label, style, testID, tone = "neutral" }: PosPillProps) {
  return (
    <View style={[styles.pill, pillTone(tone), style]} testID={testID}>
      <Text numberOfLines={1} style={[styles.label, labelTone(tone)]}>
        {label}
      </Text>
    </View>
  );
}

function pillTone(tone: PosPillTone) {
  switch (tone) {
    case "good":
      return styles.good;
    case "warning":
      return styles.warning;
    case "danger":
      return styles.danger;
    case "info":
      return styles.info;
    case "accent":
      return styles.accent;
    case "neutral":
      return styles.neutral;
  }
}

function labelTone(tone: PosPillTone) {
  switch (tone) {
    case "good":
      return styles.goodLabel;
    case "warning":
      return styles.warningLabel;
    case "danger":
      return styles.dangerLabel;
    case "info":
      return styles.infoLabel;
    case "accent":
      return styles.accentLabel;
    case "neutral":
      return styles.neutralLabel;
  }
}

const styles = createPosStyles((colors) => ({
  accent: { backgroundColor: colors.accentSoft },
  accentLabel: { color: colors.accent },
  danger: { backgroundColor: colors.redSoft },
  dangerLabel: { color: colors.red },
  good: { backgroundColor: colors.greenSoft },
  goodLabel: { color: colors.green },
  info: { backgroundColor: colors.blueSoft },
  infoLabel: { color: colors.blue },
  label: {
    fontSize: 12.5,
    fontWeight: "800",
  },
  neutral: { backgroundColor: colors.surfaceSunken },
  neutralLabel: { color: colors.mutedInk },
  pill: {
    alignItems: "center",
    alignSelf: "flex-start",
    borderRadius: 13,
    flexDirection: "row",
    justifyContent: "center",
    minHeight: 26,
    paddingHorizontal: 10,
  },
  warning: { backgroundColor: colors.yellowSoft },
  warningLabel: { color: colors.yellowInk },
}));
