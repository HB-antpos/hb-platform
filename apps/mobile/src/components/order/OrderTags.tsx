import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Image, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { HB_COLORS } from "@/shared/theme/tokens";
import { ORDER_COLORS, resolveGradeColors } from "./order-ui";

export function GradeTag({ grade }: { grade?: string | null }) {
  const normalized = grade?.trim().toUpperCase();
  if (!normalized) {
    return null;
  }

  const colors = resolveGradeColors(normalized);
  return (
    <View style={[styles.gradeTag, { backgroundColor: colors.background }]}>
      <Text style={[styles.gradeText, { color: colors.text }]}>{normalized}</Text>
    </View>
  );
}

type OrderTagTone = "solidDark" | "solidAction" | "danger";

const TAG_TONES: Record<OrderTagTone, { background: string; text: string }> = {
  solidDark: { background: ORDER_COLORS.paused, text: HB_COLORS.white },
  solidAction: { background: HB_COLORS.action, text: HB_COLORS.white },
  danger: { background: ORDER_COLORS.dangerBackground, text: ORDER_COLORS.danger },
};

/** 行内状态标签：暂停供货（深色实心）、刚扫入（蓝色实心）、进价为 0（红色浅底）。 */
export function OrderStatusTag({ label, tone }: { label: string; tone: OrderTagTone }) {
  const colors = TAG_TONES[tone];
  return (
    <View style={[styles.statusTag, { backgroundColor: colors.background }]}>
      <Text style={[styles.statusText, { color: colors.text }]}>{label}</Text>
    </View>
  );
}

export function OrderThumbnail({ uri, size, muted = false }: { uri?: string | null; size: number; muted?: boolean }) {
  return uri ? (
    <Image
      source={{ uri }}
      style={[styles.thumbnail, { width: size, height: size }, muted ? styles.thumbnailMuted : null]}
      resizeMode="contain"
    />
  ) : (
    <View style={[styles.thumbnail, styles.thumbnailPlaceholder, { width: size, height: size }]}>
      <MaterialCommunityIcons
        name="package-variant-closed"
        size={Math.round(size * 0.42)}
        color={muted ? HB_COLORS.outline : ORDER_COLORS.placeholderIcon}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  gradeTag: {
    minWidth: 18,
    height: 18,
    paddingHorizontal: 5,
    borderRadius: 4,
    alignItems: "center",
    justifyContent: "center",
  },
  gradeText: {
    fontSize: 11,
    lineHeight: 14,
    fontWeight: "700",
  },
  statusTag: {
    height: 18,
    paddingHorizontal: 6,
    borderRadius: 4,
    justifyContent: "center",
  },
  statusText: {
    fontSize: 11,
    lineHeight: 14,
    fontWeight: "700",
  },
  thumbnail: {
    borderRadius: 8,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  thumbnailMuted: {
    opacity: 0.45,
  },
  thumbnailPlaceholder: {
    alignItems: "center",
    justifyContent: "center",
  },
});
