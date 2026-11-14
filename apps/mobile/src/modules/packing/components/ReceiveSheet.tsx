import { useEffect, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { parseQuantityInput } from "../packing-logic";
import type { PackPalletDetail } from "../types";
import { KeyValueRow, MONO_FONT, PACK_COLORS, UnitRow, packStyles } from "./pack-ui";

/**
 * 整托签收前的「实到箱数」确认：收货人数过托盘上的箱，数完才能签收，防止没清点就整托签收。
 * - 托盘标签丢了、是扫箱标签找到托盘时（via）：同一个弹层里还有「只签收这一箱」；
 * - 没逐箱核对完就点「整托签收」时（count）：只有数箱和签收。
 * 步进器初始为 0、不预填，数完才可点。
 */
export function ReceiveSheet({
  visible,
  mode,
  pallet,
  scannedCartonNo,
  inTransitCount,
  canReceiveScanned,
  locationName,
  saving,
  errorMessage,
  onDismiss,
  onChooseLocation,
  onReceiveWhole,
  onReceiveOne,
}: {
  visible: boolean;
  mode: "via" | "count";
  pallet: PackPalletDetail | null;
  scannedCartonNo: string | null;
  /** 系统记录里托盘上还在路上的箱数（按钮文字里的 N）。 */
  inTransitCount: number;
  /** 扫到的那只箱还在路上，可以单独签收。 */
  canReceiveScanned: boolean;
  locationName: string | null;
  saving: boolean;
  errorMessage?: string | null;
  onDismiss: () => void;
  onChooseLocation: () => void;
  onReceiveWhole: (actualCount: number) => void;
  onReceiveOne: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const [text, setText] = useState("0");
  useEffect(() => {
    if (visible) setText("0");
  }, [visible]);
  const count = parseQuantityInput(text, true) ?? 0;
  const step = (delta: number) => setText(String(Math.max(0, Math.min(999, count + delta))));

  return (
    <BusinessSheet
      visible={visible}
      title={mode === "via" ? t("receive.viaTitle") : t("receive.countTitle")}
      subtitle={mode === "via" && scannedCartonNo ? t("receive.viaSubtitle", { cartonNo: scannedCartonNo }) : undefined}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <View style={styles.footer}>
          <Button
            mode="contained"
            icon="check-all"
            loading={saving}
            disabled={saving || count < 1}
            onPress={() => onReceiveWhole(count)}
            style={packStyles.button}
            contentStyle={packStyles.buttonContent}
          >
            {t("receive.receiveWhole", { count: inTransitCount })}
          </Button>
          {mode === "via" ? (
            <Button
              mode="outlined"
              disabled={saving || !canReceiveScanned}
              onPress={onReceiveOne}
              style={packStyles.button}
              contentStyle={packStyles.buttonContent}
            >
              {t("receive.receiveOne")}
            </Button>
          ) : null}
          <Text style={[packStyles.muted, styles.caption]}>{t("receive.mismatchCaption", { count: inTransitCount })}</Text>
        </View>
      }
    >
      <View style={packStyles.sheetBody}>
        {pallet ? (
          <View style={[packStyles.card, styles.palletCard]}>
            <UnitRow
              icon="layers-triple-outline"
              tone="blue"
              title={pallet.palletNo}
              monoTitle
              subtitle={t("receive.palletFrom", {
                name: pallet.originName || pallet.originCode,
                cartons: pallet.cartonCount,
                pieces: pallet.pieceCount,
              })}
            />
          </View>
        ) : null}
        {mode === "via" ? (
          <View style={styles.note}>
            <MaterialCommunityIcons name="information-outline" size={16} color={PACK_COLORS.action} />
            <Text style={styles.noteText}>{t("receive.viaNote")}</Text>
          </View>
        ) : null}

        <View style={styles.countRow}>
          <View style={styles.countLabel}>
            <Text style={styles.countTitle}>{t("receive.actualCount")}</Text>
            <Text style={packStyles.muted}>{t("receive.actualCountHint")}</Text>
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

        <View style={packStyles.cardList}>
          <KeyValueRow
            first
            label={t("receive.putAfter")}
            value={locationName}
            placeholder={t("receive.noLocation")}
            onPress={saving ? undefined : onChooseLocation}
          />
        </View>
        {errorMessage ? <Text style={packStyles.errorText}>{errorMessage}</Text> : null}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  footer: { gap: 8, paddingBottom: 4 },
  caption: { textAlign: "center" },
  palletCard: { backgroundColor: PACK_COLORS.infoBg, borderColor: PACK_COLORS.infoBorder },
  note: { flexDirection: "row", gap: 7, padding: 10, borderRadius: 8, backgroundColor: PACK_COLORS.neutralChipBg },
  noteText: { flex: 1, fontSize: 12.5, lineHeight: 19, color: PACK_COLORS.textSecondary },
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
