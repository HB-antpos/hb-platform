import { MaterialCommunityIcons } from "@expo/vector-icons";
import { StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { PreorderGateState } from "./use-preorder-gate";

interface PreorderGateBannerProps {
  gate: PreorderGateState;
  onOpen: () => void;
}

/** 通栏琥珀色提示条：插在页头与列表之间，不遮挡商品行的加减按钮。 */
export function PreorderGateBanner({ gate, onOpen }: PreorderGateBannerProps) {
  const { t } = useAppTranslation(["preorder"]);
  if (!gate.normalOrderBlocked) {
    return null;
  }

  const description = gate.isError
    ? t("gate.unavailable")
    : gate.isChecking
      ? t("gate.checking")
      : t("gate.pending", { count: gate.activations.length });

  return (
    <View style={styles.banner} accessibilityRole="alert">
      <MaterialCommunityIcons name="alert-circle-outline" size={20} color="#B54708" />
      <View style={styles.textWrap}>
        <Text style={styles.title}>{t("gate.title")}</Text>
        <Text numberOfLines={2} style={styles.description}>{description}</Text>
      </View>
      <Button
        compact
        mode="contained"
        onPress={onOpen}
        contentStyle={styles.buttonContent}
        labelStyle={styles.buttonLabel}
      >
        {t("gate.action")}
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
    backgroundColor: "#FFF7E6",
    borderBottomWidth: 1,
    borderBottomColor: "#F5B041",
  },
  textWrap: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  title: {
    color: "#7A3E00",
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "700",
  },
  description: {
    color: "#6B4B24",
    fontSize: 12,
    lineHeight: 16,
  },
  buttonContent: {
    minHeight: 44,
  },
  buttonLabel: {
    fontWeight: "700",
  },
});
