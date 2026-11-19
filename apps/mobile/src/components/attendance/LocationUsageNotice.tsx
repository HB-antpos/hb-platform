import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 打卡页的「定位使用说明」卡：上班前显示。默认只展示「扫码打卡时」这一项；
 * 其余说明（上班期间打开 App / 回到前台、后台、所需权限）都放在「了解详情」弹层里，
 * 由 LocationUsageSheet 完整列出。
 * 班中后台定位已取消，所以不再有「定位已开启 / 已停止」这类状态条。
 */
export function LocationUsageNotice({ onOpenDetails }: { onOpenDetails: () => void }) {
  const { t } = useAppTranslation("attendance");

  return (
    <View style={styles.card}>
      <View style={styles.titleRow}>
        <Icon source="shield-check-outline" size={16} color={HB_COLORS.action} />
        <Text variant="labelLarge" style={styles.title}>{t("locationNotice.title")}</Text>
      </View>
      <View style={styles.row}>
        <View style={[styles.iconBox, styles.iconOn]}>
          <Icon source="qrcode-scan" size={16} color={HB_COLORS.action} />
        </View>
        <Text variant="bodySmall" style={styles.rowText}>
          <Text variant="labelMedium" style={styles.rowLabel}>{t("locationNotice.scanLabel")}</Text>
          {`：${t("locationNotice.scanBody")}`}
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

const styles = StyleSheet.create({
  card: {
    backgroundColor: "#F5F8FF",
    borderColor: "#B2CCFF",
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
  },
  titleRow: { alignItems: "center", flexDirection: "row", gap: 6 },
  title: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  row: { alignItems: "flex-start", flexDirection: "row", gap: HB_SPACING.xs },
  iconBox: { alignItems: "center", borderRadius: 8, height: 26, justifyContent: "center", width: 26 },
  iconOn: { backgroundColor: "#D1E9FF" },
  rowText: { color: HB_COLORS.textPrimary, flex: 1, lineHeight: 20 },
  rowLabel: { fontWeight: "700" },
  link: { color: HB_COLORS.action, fontWeight: "700" },
  pressed: { opacity: 0.6 },
});
