import { MaterialCommunityIcons } from "@expo/vector-icons";
import { StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

interface SupplyRestockedBannerProps {
  restockedCount: number;
  onOpen: () => void;
}

/** 订货首页通栏提示：关注的商品已恢复订货。与 Preorder 提示条同一形态，用成功色区分。 */
export function SupplyRestockedBanner({ restockedCount, onOpen }: SupplyRestockedBannerProps) {
  const { t } = useAppTranslation("supplyNotice");
  if (restockedCount <= 0) {
    return null;
  }
  return (
    <View style={styles.banner} testID="supply-restocked-banner" accessibilityRole="alert">
      <MaterialCommunityIcons name="bell-ring-outline" size={20} color={HB_COLORS.success} />
      <View style={styles.textWrap}>
        <Text numberOfLines={2} style={styles.title}>{t("restockedBanner", { count: restockedCount })}</Text>
      </View>
      <Button compact mode="contained" onPress={onOpen} contentStyle={styles.buttonContent} labelStyle={styles.buttonLabel}>
        {t("restockedBannerAction")}
      </Button>
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingLeft: 12,
    paddingRight: 8,
    paddingVertical: 8,
    backgroundColor: "#E7F6EC",
    borderBottomWidth: 1,
    borderBottomColor: "#7BC89A",
  },
  textWrap: { flex: 1, minWidth: 0 },
  title: { color: "#054F31", fontSize: 14, lineHeight: 18, fontWeight: "700" },
  buttonContent: { minHeight: 44 },
  buttonLabel: { fontWeight: "700" },
});
