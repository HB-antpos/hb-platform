import { StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 定位使用说明弹层（只读）：打卡页点「了解详情」打开，右上角关闭或「知道了」收起。
 * 班中后台定位已取消，不再有上班扫码前的「同意并继续」披露流程。
 */
export function LocationUsageSheet({
  visible,
  onDismiss,
}: {
  visible: boolean;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["attendance", "common"]);

  const steps = [
    { icon: "qrcode-scan", off: false, title: t("locationNotice.sheet.scanTitle"), body: t("locationNotice.sheet.scanBody") },
    { icon: "stop-circle-outline", off: true, title: t("locationNotice.sheet.otherTitle"), body: t("locationNotice.sheet.otherBody") },
    { icon: "shield-check-outline", off: true, title: t("locationNotice.sheet.permissionTitle"), body: t("locationNotice.sheet.permissionBody") },
  ];

  return (
    <BusinessSheet
      visible={visible}
      title={t("locationNotice.sheet.infoTitle")}
      onDismiss={onDismiss}
      footer={
        <Button mode="contained" onPress={onDismiss}>
          {t("locationNotice.sheet.gotIt")}
        </Button>
      }
    >
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
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  step: { flexDirection: "row", gap: HB_SPACING.sm },
  iconBox: { alignItems: "center", borderRadius: 10, height: 32, justifyContent: "center", width: 32 },
  iconOn: { backgroundColor: "#D1E9FF" },
  iconOff: { backgroundColor: "#EAECF0" },
  stepText: { flex: 1, minWidth: 0 },
  stepTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  stepBody: { color: HB_COLORS.textSecondary, lineHeight: 20 },
});
