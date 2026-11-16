import { StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 定位使用说明弹层。
 * - consent：上班扫码前的披露，必须在系统权限弹窗之前出现；「同意并扫码打卡」才继续，其余一律视为取消。
 * - info：打卡页点「了解详情 / 说明」打开的完整说明，只读，右上角关闭或「知道了」收起。
 */
export function LocationUsageSheet({
  visible,
  mode,
  onConfirm,
  onDismiss,
}: {
  visible: boolean;
  mode: "consent" | "info";
  /** 仅 consent 模式使用。 */
  onConfirm?: () => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);

  const steps = [
    { icon: "qrcode-scan", off: false, title: t("locationNotice.sheet.scanTitle"), body: t("locationNotice.sheet.scanBody") },
    { icon: "map-marker", off: false, title: t("locationNotice.sheet.shiftTitle"), body: t("locationNotice.sheet.shiftBody") },
    { icon: "stop-circle-outline", off: true, title: t("locationNotice.sheet.afterTitle"), body: t("locationNotice.sheet.afterBody") },
  ];
  const extras = [
    { title: t("locationNotice.sheet.autoStopTitle"), body: t("locationNotice.sheet.autoStopBody") },
    { title: t("locationNotice.sheet.uploadTitle"), body: t("locationNotice.sheet.uploadBody") },
  ];

  return (
    <BusinessSheet
      visible={visible}
      title={t(mode === "consent" ? "locationNotice.sheet.consentTitle" : "locationNotice.sheet.infoTitle")}
      onDismiss={onDismiss}
      footer={
        mode === "consent" ? (
          <View style={styles.footerRow}>
            <Button mode="outlined" onPress={onDismiss} style={styles.footerCancel}>
              {t("common:actions.cancel")}
            </Button>
            <Button mode="contained" onPress={onConfirm} style={styles.footerConfirm}>
              {t("locationNotice.sheet.consentConfirm")}
            </Button>
          </View>
        ) : (
          <Button mode="contained" onPress={onDismiss}>
            {t("locationNotice.sheet.gotIt")}
          </Button>
        )
      }
    >
      {mode === "consent" ? (
        <Text variant="bodyMedium" style={styles.lead}>{t("locationNotice.sheet.consentLead")}</Text>
      ) : null}
      {steps.map((step) => (
        <View key={step.title} style={styles.step}>
          <View style={[styles.iconBox, step.off ? styles.iconOff : styles.iconOn]}>
            <Icon
              source={step.icon}
              size={18}
              color={step.off ? HB_COLORS.textSecondary : HB_COLORS.action}
            />
          </View>
          <View style={styles.stepText}>
            <Text variant="labelLarge" style={styles.stepTitle}>{step.title}</Text>
            <Text variant="bodySmall" style={styles.stepBody}>{step.body}</Text>
          </View>
        </View>
      ))}
      {mode === "info" ? extras.map((item) => (
        <View key={item.title} style={styles.extra}>
          <Text variant="labelLarge" style={styles.stepTitle}>{item.title}</Text>
          <Text variant="bodySmall" style={styles.stepBody}>{item.body}</Text>
        </View>
      )) : (
        <View style={styles.tip}>
          <Text variant="bodySmall" style={styles.stepBody}>{t("locationNotice.sheet.consentTip")}</Text>
        </View>
      )}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  lead: { color: HB_COLORS.textSecondary, lineHeight: 22 },
  step: { flexDirection: "row", gap: HB_SPACING.sm },
  iconBox: { alignItems: "center", borderRadius: 10, height: 32, justifyContent: "center", width: 32 },
  iconOn: { backgroundColor: "#D1E9FF" },
  iconOff: { backgroundColor: "#EAECF0" },
  stepText: { flex: 1, minWidth: 0 },
  stepTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  stepBody: { color: HB_COLORS.textSecondary, lineHeight: 20 },
  extra: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth, paddingTop: HB_SPACING.sm },
  tip: { backgroundColor: "#F9FAFB", borderRadius: 10, padding: HB_SPACING.sm },
  footerRow: { flexDirection: "row", gap: HB_SPACING.xs },
  footerCancel: { flex: 1 },
  footerConfirm: { flex: 1.6 },
});
