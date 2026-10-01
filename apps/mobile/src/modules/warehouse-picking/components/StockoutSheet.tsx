import { useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { hasLocation, primaryLocation } from "../pick-math";
import { stockoutReasonKey } from "../pick-view-model";
import { PICK_STOCKOUT_REASON, type PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";
import { ProductThumb } from "./ProductThumb";

const REASONS = [PICK_STOCKOUT_REASON.locationEmpty, PICK_STOCKOUT_REASON.wrongProduct, PICK_STOCKOUT_REASON.damaged];

/**
 * 确认“货位没货”：已拣的保留，剩余记为缺货；选原因后标记并跳到下一个货位。
 * 未绑定货位的行叫“找不到货”，第一个原因说成“仓库里找不到”。
 */
export function StockoutSheet({
  visible,
  line,
  saving,
  onDismiss,
  onConfirm,
}: {
  visible: boolean;
  line: PickSheetLine;
  saving: boolean;
  onDismiss: () => void;
  onConfirm: (reason: number) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const located = hasLocation(line);
  const [reason, setReason] = useState<number>(line.stockout?.reason ?? PICK_STOCKOUT_REASON.locationEmpty);
  const short = Math.max(0, line.orderedQuantity - line.pickedTotal);

  return (
    <BusinessSheet
      visible={visible}
      title={located ? t("stockout.title") : t("stockout.titleNoLocation")}
      subtitle={located ? primaryLocation(line.locationCode) : undefined}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <Button
          mode="contained"
          loading={saving}
          disabled={saving}
          onPress={() => onConfirm(reason)}
          buttonColor={PICK_COLORS.danger}
          contentStyle={styles.buttonContent}
          style={styles.button}
        >
          {t("stockout.confirm")}
        </Button>
      }
    >
      <View style={styles.body}>
        <View style={styles.productRow}>
          <ProductThumb uri={line.productImage} size={48} />
          <View style={styles.productText}>
            <Text style={styles.productName}>{line.productName || line.productCode}</Text>
            <Text style={styles.meta}>
              {line.itemNumber || line.productCode}
              {located ? " · " : ""}
              {located ? <Text style={styles.mono}>{line.locationCode}</Text> : null}
            </Text>
          </View>
        </View>

        <View style={styles.facts}>
          <Fact label={t("stockout.ordered")} value={line.orderedQuantity} />
          <Fact label={t("stockout.picked")} value={line.pickedTotal} />
          <Fact label={t("stockout.short")} value={short} danger />
        </View>

        <Text style={styles.label}>{located ? t("stockout.reasonLabel") : t("stockout.reasonLabelNoLocation")}</Text>
        <View accessibilityRole="radiogroup" style={styles.reasons}>
          {REASONS.map((value) => {
            const selected = value === reason;
            return (
              <Pressable
                key={value}
                accessibilityRole="radio"
                accessibilityState={{ checked: selected }}
                disabled={saving}
                onPress={() => setReason(value)}
                style={[styles.reason, selected ? styles.reasonOn : null]}
              >
                <MaterialCommunityIcons
                  name={selected ? "radiobox-marked" : "radiobox-blank"}
                  size={20}
                  color={selected ? PICK_COLORS.danger : PICK_COLORS.inkMuted}
                />
                <Text style={[styles.reasonText, selected ? styles.reasonTextOn : null]}>{t(stockoutReasonKey(value, located))}</Text>
              </Pressable>
            );
          })}
        </View>

        <View style={styles.hint}>
          <MaterialCommunityIcons name="barcode-scan" size={16} color={PICK_COLORS.textSecondary} />
          <Text style={styles.hintText}>{t("stockout.hint")}</Text>
        </View>
      </View>
    </BusinessSheet>
  );
}

function Fact({ label, value, danger = false }: { label: string; value: number; danger?: boolean }) {
  return (
    <View style={[styles.fact, danger ? styles.factDanger : null]}>
      <Text style={[styles.factLabel, danger ? { color: PICK_COLORS.dangerText } : null]}>{label}</Text>
      <Text style={[styles.factValue, danger ? { color: PICK_COLORS.danger } : null]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { gap: 12 },
  productRow: { flexDirection: "row", alignItems: "center", gap: 10 },
  productText: { flex: 1, minWidth: 0, gap: 2 },
  productName: { fontSize: 15, lineHeight: 21, fontWeight: "600", color: PICK_COLORS.ink },
  meta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  mono: { fontFamily: MONO_FONT, fontWeight: "700", color: PICK_COLORS.ink },
  facts: { flexDirection: "row", gap: 8 },
  fact: { flex: 1, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, backgroundColor: PICK_COLORS.cellBg, borderRadius: 8, paddingHorizontal: 10, paddingVertical: 6 },
  factDanger: { backgroundColor: PICK_COLORS.dangerBg, borderColor: PICK_COLORS.dangerBorder },
  factLabel: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  factValue: { fontSize: 20, lineHeight: 28, fontWeight: "700", color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  label: { fontSize: 13, lineHeight: 18, fontWeight: "700", color: PICK_COLORS.ink },
  reasons: { gap: 6 },
  reason: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 12,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: PICK_COLORS.outline,
    backgroundColor: PICK_COLORS.white,
  },
  reasonOn: { borderWidth: 2, paddingHorizontal: 11, borderColor: PICK_COLORS.danger, backgroundColor: PICK_COLORS.dangerBg },
  reasonText: { flex: 1, fontSize: 14, lineHeight: 20, color: PICK_COLORS.ink },
  reasonTextOn: { fontWeight: "600", color: PICK_COLORS.dangerText },
  hint: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    padding: 10,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PICK_COLORS.outlineMuted,
    backgroundColor: PICK_COLORS.cellBg,
  },
  hintText: { flex: 1, fontSize: 12, lineHeight: 17, color: PICK_COLORS.textSecondary },
  button: { borderRadius: 8 },
  buttonContent: { height: 48 },
});
