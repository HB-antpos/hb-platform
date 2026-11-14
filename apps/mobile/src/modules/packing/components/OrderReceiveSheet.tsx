import { useEffect, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { clampCartonCount } from "../order-receive-logic";
import { parseQuantityInput } from "../packing-logic";
import { MONO_FONT, PACK_COLORS, packStyles } from "./pack-ui";

/**
 * 按订单签收前的「实到箱数」确认：收货人数过这张订单发来的箱，数完才能签收，防止没清点就整单签收。
 * 与整托签收的箱数确认同一做法：步进器初始为 0、不预填，数完才可点；对不对由服务端判断，
 * 不一致时一只都不签（409 COUNT_MISMATCH），改为逐箱扫码核对。
 */
export function OrderReceiveSheet({
  visible,
  orderNo,
  inTransitCount,
  saving,
  errorMessage,
  onDismiss,
  onConfirm,
}: {
  visible: boolean;
  orderNo: string | null;
  /** 系统记录里这张订单发给本店、还在路上的箱数（按钮文字里的 N）。 */
  inTransitCount: number;
  saving: boolean;
  errorMessage?: string | null;
  onDismiss: () => void;
  onConfirm: (actualCount: number) => void;
}) {
  const { t } = useAppTranslation("packing");
  const [text, setText] = useState("0");
  useEffect(() => {
    if (visible) setText("0");
  }, [visible]);
  const count = clampCartonCount(parseQuantityInput(text, true) ?? 0);
  const step = (delta: number) => setText(String(clampCartonCount(count + delta)));

  return (
    <BusinessSheet
      visible={visible}
      title={t("orderReceive.countTitle")}
      subtitle={orderNo ? t("orderReceive.countSubtitle", { orderNo }) : undefined}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <View style={styles.footer}>
          <Button
            mode="contained"
            icon="check-all"
            loading={saving}
            disabled={saving || count < 1}
            onPress={() => onConfirm(count)}
            style={packStyles.button}
            contentStyle={packStyles.buttonContent}
          >
            {t("orderReceive.receiveAll", { count: inTransitCount })}
          </Button>
          <Text style={[packStyles.muted, styles.caption]}>{t("orderReceive.mismatchCaption", { count: inTransitCount })}</Text>
        </View>
      }
    >
      <View style={packStyles.sheetBody}>
        <View style={styles.countRow}>
          <View style={styles.countLabel}>
            <Text style={styles.countTitle}>{t("receive.actualCount")}</Text>
            <Text style={packStyles.muted}>{t("orderReceive.actualCountHint")}</Text>
          </View>
          <View style={styles.stepper}>
            <Pressable accessibilityRole="button" accessibilityLabel={t("receive.countDecrease")} onPress={() => step(-1)} style={styles.stepButton}>
              <MaterialCommunityIcons name="minus" size={20} color={PACK_COLORS.action} />
            </Pressable>
            <TextInput
              accessibilityLabel={t("receive.actualCount")}
              value={text}
              onChangeText={(next) => setText(next.replace(/\D/g, "").slice(0, 3) || "0")}
              keyboardType="number-pad"
              selectTextOnFocus
              style={styles.stepInput}
            />
            <Pressable accessibilityRole="button" accessibilityLabel={t("receive.countIncrease")} onPress={() => step(1)} style={styles.stepButton}>
              <MaterialCommunityIcons name="plus" size={20} color={PACK_COLORS.action} />
            </Pressable>
          </View>
        </View>
        {errorMessage ? <Text style={packStyles.errorText}>{errorMessage}</Text> : null}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  footer: { gap: 8, paddingBottom: 4 },
  caption: { textAlign: "center" },
  countRow: { minHeight: 56, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 12 },
  countLabel: { flex: 1, minWidth: 0 },
  countTitle: { fontSize: 15, fontWeight: "700", color: PACK_COLORS.ink },
  stepper: {
    height: 44,
    flexDirection: "row",
    alignItems: "center",
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PACK_COLORS.infoBorder,
    backgroundColor: PACK_COLORS.white,
  },
  stepButton: { width: 44, height: 42, alignItems: "center", justifyContent: "center" },
  stepInput: {
    width: 56,
    height: 42,
    textAlign: "center",
    fontFamily: MONO_FONT,
    fontSize: 20,
    fontWeight: "700",
    color: PACK_COLORS.ink,
    borderLeftWidth: StyleSheet.hairlineWidth,
    borderRightWidth: StyleSheet.hairlineWidth,
    borderColor: PACK_COLORS.outline,
    paddingVertical: 0,
  },
});
