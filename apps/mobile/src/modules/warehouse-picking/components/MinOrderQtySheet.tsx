import { useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { splitIntoPacks } from "../pick-math";
import type { PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";
import { ProductThumb } from "./ProductThumb";

const MAX_PIECES = 100000;

/**
 * 补录中包数：中包数同时是分店订货的最小数量与步长，保存即写回商品资料。
 * 由扫码触发时，保存后本次扫码按新中包数计入；也可以这次先按 1 件计入、不改商品资料。
 */
export function MinOrderQtySheet({
  visible,
  line,
  withPendingScan,
  saving,
  onDismiss,
  onSave,
  onCountOne,
}: {
  visible: boolean;
  line: PickSheetLine;
  withPendingScan: boolean;
  saving: boolean;
  onDismiss: () => void;
  onSave: (minOrderQuantity: number) => void;
  onCountOne: () => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const [text, setText] = useState(line.minOrderQuantity && line.minOrderQuantity > 0 ? String(line.minOrderQuantity) : "");
  const value = Number.parseInt(text, 10);
  const valid = Number.isFinite(value) && value > 0 && value <= MAX_PIECES;
  const split = valid ? splitIntoPacks(line.orderedQuantity, value) : null;
  const calc = !valid
    ? t("minOrder.invalid")
    : split && split.rest > 0
      ? t("minOrder.calcWithRest", { ordered: line.orderedQuantity, packs: split.packs, rest: split.rest })
      : t("minOrder.calc", { ordered: line.orderedQuantity, packs: split?.packs ?? 0 });
  const step = (delta: number) => {
    const next = Math.min(MAX_PIECES, Math.max(1, (valid ? value : 0) + delta));
    setText(String(next));
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t("minOrder.title")}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <View style={styles.footer}>
          <Button
            mode="contained"
            loading={saving}
            disabled={!valid || saving}
            onPress={() => onSave(value)}
            contentStyle={styles.buttonContent}
            style={styles.button}
          >
            {withPendingScan ? t("minOrder.save", { count: valid ? value : 0 }) : t("minOrder.saveOnly")}
          </Button>
          {withPendingScan ? (
            <Button mode="text" disabled={saving} onPress={onCountOne} contentStyle={styles.buttonContent}>
              {t("minOrder.countOne")}
            </Button>
          ) : null}
        </View>
      }
    >
      <View style={styles.body}>
        <View style={styles.warning}>
          <MaterialCommunityIcons name="alert-outline" size={18} color={PICK_COLORS.warningText} />
          <Text style={styles.warningText}>{t("minOrder.missing")}</Text>
        </View>
        <View style={styles.product}>
          <ProductThumb uri={line.productImage} size={40} />
          <View style={styles.productText}>
            <View style={styles.productNameRow}>
              {line.locationCode ? <Text style={styles.locationChip}>{line.locationCode}</Text> : null}
              <Text numberOfLines={1} style={styles.productName}>
                {line.productName || line.productCode}
              </Text>
            </View>
            <Text style={styles.meta}>
              {line.itemNumber || line.productCode}
              {line.barcode ? " · " : ""}
              {line.barcode ? <Text style={styles.mono}>{line.barcode}</Text> : null}
            </Text>
          </View>
        </View>
        <Text nativeID="min-order-qty-label" style={styles.label}>
          {t("minOrder.label")}
        </Text>
        <View style={styles.stepper}>
          <Pressable accessibilityRole="button" accessibilityLabel={t("minOrder.decrease")} onPress={() => step(-1)} style={styles.stepButton}>
            <MaterialCommunityIcons name="minus" size={22} color={PICK_COLORS.ink} />
          </Pressable>
          <TextInput
            accessibilityLabelledBy="min-order-qty-label"
            accessibilityLabel={t("minOrder.label")}
            value={text}
            onChangeText={(next) => setText(next.replace(/\D/g, "").slice(0, 6))}
            keyboardType="number-pad"
            autoFocus
            selectTextOnFocus
            style={styles.input}
          />
          <Pressable accessibilityRole="button" accessibilityLabel={t("minOrder.increase")} onPress={() => step(1)} style={styles.stepButton}>
            <MaterialCommunityIcons name="plus" size={22} color={PICK_COLORS.ink} />
          </Pressable>
        </View>
        <Text style={[styles.calc, !valid ? { color: PICK_COLORS.warning } : null]}>{calc}</Text>
        <Text style={styles.note}>{t("minOrder.note")}</Text>
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  body: { gap: 12 },
  warning: { flexDirection: "row", gap: 8, alignItems: "flex-start", backgroundColor: PICK_COLORS.warningBg, borderWidth: 1, borderColor: PICK_COLORS.warningBorder, borderRadius: 8, paddingHorizontal: 10, paddingVertical: 8 },
  warningText: { flex: 1, fontSize: 13, lineHeight: 19, color: PICK_COLORS.warningText },
  product: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PICK_COLORS.cellBg, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 8, paddingHorizontal: 10, paddingVertical: 8 },
  productText: { flex: 1, minWidth: 0, gap: 2 },
  productNameRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  locationChip: { fontFamily: MONO_FONT, fontSize: 12, lineHeight: 20, fontWeight: "700", paddingHorizontal: 6, borderRadius: 4, overflow: "hidden", backgroundColor: PICK_COLORS.ink, color: PICK_COLORS.white },
  productName: { flexShrink: 1, fontSize: 14, lineHeight: 20, fontWeight: "600", color: PICK_COLORS.ink },
  meta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  mono: { fontFamily: MONO_FONT },
  label: { fontSize: 13, lineHeight: 18, fontWeight: "600", color: PICK_COLORS.ink },
  stepper: { flexDirection: "row", gap: 8, height: 60 },
  stepButton: { width: 60, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  input: { flex: 1, minWidth: 0, borderRadius: 8, borderWidth: 2, borderColor: PICK_COLORS.action, textAlign: "center", fontFamily: MONO_FONT, fontSize: 28, fontWeight: "700", color: PICK_COLORS.ink, backgroundColor: PICK_COLORS.white },
  calc: { fontSize: 13, lineHeight: 18, textAlign: "center", color: PICK_COLORS.textSecondary },
  note: { fontSize: 12, lineHeight: 18, color: PICK_COLORS.textSecondary },
  footer: { gap: 4 },
  button: { borderRadius: 8 },
  buttonContent: { minHeight: 48 },
});
