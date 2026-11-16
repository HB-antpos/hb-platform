import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import type { LocationNoticeVariant } from "@/modules/attendance/location-notice-state";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 打卡页的「定位使用说明」：上班前是说明卡，上班中 / 下班后是一行状态条。
 * 状态条只在班中定位真实运行 / 确认已停止时出现，口径见 location-notice-state.ts。
 */
export function LocationUsageNotice({
  variant,
  onOpenDetails,
}: {
  variant: Exclude<LocationNoticeVariant, "none">;
  onOpenDetails: () => void;
}) {
  const { t } = useAppTranslation("attendance");

  if (variant === "notice") {
    return (
      <View style={[styles.card, styles.noticeCard]}>
        <View style={styles.titleRow}>
          <Icon source="shield-check-outline" size={16} color={HB_COLORS.action} />
          <Text variant="labelLarge" style={styles.title}>{t("locationNotice.title")}</Text>
        </View>
        <View style={styles.row}>
          <View style={[styles.iconBox, styles.iconOn]}>
            <Icon source="map-marker" size={16} color={HB_COLORS.action} />
          </View>
          <Text variant="bodySmall" style={styles.rowText}>
            <Text variant="labelMedium" style={styles.rowLabel}>{t("locationNotice.shiftLabel")}</Text>
            {`：${t("locationNotice.shiftBody")}`}
          </Text>
        </View>
        <View style={styles.row}>
          <View style={[styles.iconBox, styles.iconOff]}>
            <Icon source="stop-circle-outline" size={16} color={HB_COLORS.textSecondary} />
          </View>
          <Text variant="bodySmall" style={styles.rowText}>
            <Text variant="labelMedium" style={styles.rowLabel}>{t("locationNotice.afterLabel")}</Text>
            {`：${t("locationNotice.afterBody")}`}
          </Text>
        </View>
        <Pressable
          accessibilityRole="button"
          onPress={onOpenDetails}
          hitSlop={8}
          style={({ pressed }) => (pressed ? styles.pressed : null)}
        >
          <Text variant="labelMedium" style={styles.link}>{`${t("locationNotice.details")} ›`}</Text>
        </Pressable>
      </View>
    );
  }

  const active = variant === "active";
  return (
    <View
      accessibilityRole="summary"
      style={[styles.card, styles.barCard, active ? styles.barActive : styles.barStopped]}
    >
      {active ? (
        <View style={styles.dotHalo}><View style={styles.dot} /></View>
      ) : (
        <Icon source="check-circle" size={24} color={HB_COLORS.textSecondary} />
      )}
      <View style={styles.barText}>
        <Text variant="labelLarge" style={styles.title}>
          {t(active ? "locationNotice.activeTitle" : "locationNotice.stoppedTitle")}
        </Text>
        <Text variant="bodySmall" style={styles.barBody}>
          {t(active ? "locationNotice.activeBody" : "locationNotice.stoppedBody")}
        </Text>
      </View>
      {active ? (
        <Pressable
          accessibilityRole="button"
          onPress={onOpenDetails}
          hitSlop={8}
          style={({ pressed }) => (pressed ? styles.pressed : null)}
        >
          <Text variant="labelMedium" style={styles.activeLink}>{t("locationNotice.explain")}</Text>
        </Pressable>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    padding: HB_SPACING.sm,
  },
  noticeCard: { backgroundColor: "#F5F8FF", borderColor: "#B2CCFF", gap: HB_SPACING.xs },
  titleRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  title: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  row: { alignItems: "flex-start", flexDirection: "row", gap: HB_SPACING.xs },
  iconBox: { alignItems: "center", borderRadius: 8, height: 26, justifyContent: "center", width: 26 },
  iconOn: { backgroundColor: "#D1E9FF" },
  iconOff: { backgroundColor: "#EAECF0" },
  rowText: { color: HB_COLORS.textPrimary, flex: 1, lineHeight: 20 },
  rowLabel: { fontWeight: "700" },
  link: { color: HB_COLORS.action, fontWeight: "700" },
  pressed: { opacity: 0.6 },
  barCard: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs },
  barActive: { backgroundColor: "#ECFDF3", borderColor: "#ABEFC6" },
  barStopped: { backgroundColor: "#F9FAFB", borderColor: HB_COLORS.outlineMuted },
  barText: { flex: 1, minWidth: 0 },
  barBody: { color: HB_COLORS.textSecondary, lineHeight: 18 },
  activeLink: { color: HB_COLORS.success, fontWeight: "700" },
  dotHalo: {
    alignItems: "center",
    backgroundColor: "rgba(18,183,106,0.22)",
    borderRadius: 10,
    height: 20,
    justifyContent: "center",
    width: 20,
  },
  dot: { backgroundColor: "#12B76A", borderRadius: 5, height: 10, width: 10 },
});
