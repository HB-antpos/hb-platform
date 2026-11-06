import { StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { PEOPLE_TONES } from "@/modules/people";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** 详情页情境卡片：员工还没登录过时，一键进入发送「账号已开通」。显示条件见 shouldOfferActivationCard。 */
export function StaffNoticeActivationCard({ onPress }: { onPress: () => void }) {
  const { t } = useAppTranslation("staffNotices");
  return (
    <View style={styles.card} testID="staff-notice-activation-card">
      <Icon source="email-fast-outline" size={22} color={PEOPLE_TONES.amber.fg} />
      <View style={styles.main}>
        <Text style={styles.title}>{t("entry.activationTitle")}</Text>
        <Text style={styles.body}>{t("entry.activationBody")}</Text>
        <Button mode="contained" icon="send-outline" onPress={onPress} style={styles.button}>
          {t("entry.activationAction")}
        </Button>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { color: PEOPLE_TONES.amber.fg, fontSize: 12.5, lineHeight: 18, marginTop: 2 },
  button: { alignSelf: "flex-start", borderRadius: HB_RADIUS.control, marginTop: HB_SPACING.xs },
  card: {
    alignItems: "flex-start",
    backgroundColor: PEOPLE_TONES.amber.bg,
    borderColor: "#FEDF89",
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: 14,
  },
  main: { flex: 1, minWidth: 0 },
  title: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "800" },
});
