import { useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { hasMinOrderQuantity, splitIntoPacks } from "../pick-math";
import type { PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";

const MAX_PIECES = 100000;

/**
 * 手动改本行合计：可按件或按中包输入；提交的是“本行总数”，服务端按与当前合计的差额记在当前拣货人名下，
 * 期间若同事改过这一行会被拒绝并刷新，避免按过期合计算错差额。
 */
export function ManualQtySheet({
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
  onConfirm: (totalPieces: number) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const pack = hasMinOrderQuantity(line) ? (line.minOrderQuantity as number) : null;
  const [unit, setUnit] = useState<"pcs" | "pack">("pcs");
  const [text, setText] = useState(String(line.pickedTotal));
  const parsed = Number.parseInt(text, 10);
  const value = Number.isFinite(parsed) ? parsed : 0;
  const total = unit === "pack" && pack ? value * pack : value;
  const valid = text.length > 0 && total >= 0 && total <= MAX_PIECES;
  const split = splitIntoPacks(total, pack);
  const convert =
    unit === "pack"
      ? t("manual.convertPieces", { count: total })
      : split
        ? split.rest > 0
          ? t("manual.convertPacksRest", { packs: split.packs, rest: split.rest })
          : t("manual.convertPacks", { packs: split.packs })
        : null;
  const diff = total - line.orderedQuantity;
  const diffText = diff < 0 ? t("manual.short", { count: -diff }) : diff === 0 ? t("manual.exact") : t("manual.over", { count: diff });
  const diffColor = diff < 0 ? PICK_COLORS.textSecondary : diff === 0 ? PICK_COLORS.success : PICK_COLORS.warning;

  const switchUnit = (next: "pcs" | "pack") => {
    if (next === unit || !pack) return;
    // 切换单位时保持总件数不变：件→中包向下取整，避免悄悄多算。
    setText(String(next === "pack" ? Math.floor(total / pack) : total));
    setUnit(next);
  };
  const step = (delta: number) => setText(String(Math.max(0, value + delta)));

  return (
    <BusinessSheet
      visible={visible}
      title={t("manual.title")}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <Button
          mode="contained"
          loading={saving}
          disabled={!valid || saving}
          onPress={() => onConfirm(total)}
          contentStyle={styles.buttonContent}
          style={styles.button}
        >
          {t("manual.confirm", { count: total })}
        </Button>
      }
    >
      <View style={styles.body}>
        <View style={styles.productRow}>
          {line.locationCode ? <Text style={styles.locationChip}>{line.locationCode}</Text> : null}
          <Text numberOfLines={1} style={styles.productName}>
            {line.productName || line.productCode}
          </Text>
        </View>
        <View style={styles.grid}>
          <Info label={t("manual.ordered")} value={`${line.orderedQuantity} ${t("manual.unitPcs")}`} />
          <Info label={t("manual.minOrder")} value={pack ? `${pack} ${t("manual.unitPcs")}` : "—"} />
          <Info label={t("manual.pickedNow")} value={`${line.pickedTotal} ${t("manual.unitPcs")}`} />
        </View>
        {pack ? (
          <View accessibilityRole="radiogroup" accessibilityLabel={t("manual.unitGroup")} style={styles.segment}>
            {(["pcs", "pack"] as const).map((option) => (
              <Pressable
                key={option}
                accessibilityRole="radio"
                accessibilityState={{ checked: unit === option }}
                onPress={() => switchUnit(option)}
                style={[styles.segmentItem, option === "pack" ? styles.segmentDivider : null, unit === option ? styles.segmentActive : null]}
              >
                <Text style={[styles.segmentText, unit === option ? styles.segmentTextActive : null]}>
                  {option === "pcs" ? t("manual.unitPieces") : t("manual.unitPacks")}
                </Text>
              </Pressable>
            ))}
          </View>
        ) : null}
        <Text nativeID="manual-qty-label" style={styles.label}>
          {t("manual.label", { unit: unit === "pack" ? t("manual.unitPack") : t("manual.unitPcs") })}
        </Text>
        <View style={styles.stepper}>
          <Pressable accessibilityRole="button" accessibilityLabel={t("manual.decrease")} onPress={() => step(-1)} style={styles.stepButton}>
            <MaterialCommunityIcons name="minus" size={22} color={PICK_COLORS.ink} />
          </Pressable>
          <TextInput
            accessibilityLabelledBy="manual-qty-label"
            accessibilityLabel={t("manual.label", { unit: unit === "pack" ? t("manual.unitPack") : t("manual.unitPcs") })}
            value={text}
            onChangeText={(next) => setText(next.replace(/\D/g, "").slice(0, 6))}
            keyboardType="number-pad"
            selectTextOnFocus
            style={styles.input}
          />
          <Pressable accessibilityRole="button" accessibilityLabel={t("manual.increase")} onPress={() => step(1)} style={styles.stepButton}>
            <MaterialCommunityIcons name="plus" size={22} color={PICK_COLORS.ink} />
          </Pressable>
        </View>
        <Text style={styles.convert}>
          {convert ? `${convert} · ` : ""}
          <Text style={[styles.diff, { color: diffColor }]}>{diffText}</Text>
        </Text>
        <View style={styles.chips}>
          <Pressable
            accessibilityRole="button"
            onPress={() => {
              setUnit("pcs");
              setText(String(line.orderedQuantity));
            }}
            style={styles.chip}
          >
            <Text style={styles.chipText}>{t("manual.matchOrder", { count: line.orderedQuantity })}</Text>
          </Pressable>
          <Pressable accessibilityRole="button" onPress={() => setText("0")} style={styles.chip}>
            <Text style={styles.chipText}>{t("manual.clear")}</Text>
          </Pressable>
        </View>
        <Text style={styles.note}>{t("manual.note")}</Text>
      </View>
    </BusinessSheet>
  );
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.cell}>
      <Text style={styles.cellLabel}>{label}</Text>
      <Text style={styles.cellValue}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { gap: 12 },
  productRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  locationChip: { fontFamily: MONO_FONT, fontSize: 12, lineHeight: 20, fontWeight: "700", paddingHorizontal: 6, borderRadius: 4, overflow: "hidden", backgroundColor: PICK_COLORS.ink, color: PICK_COLORS.white },
  productName: { flexShrink: 1, fontSize: 14, lineHeight: 20, fontWeight: "600", color: PICK_COLORS.ink },
  grid: { flexDirection: "row", gap: 8 },
  cell: { flex: 1, backgroundColor: PICK_COLORS.cellBg, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 8, paddingHorizontal: 10, paddingVertical: 6 },
  cellLabel: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  cellValue: { fontSize: 17, lineHeight: 24, fontWeight: "700", color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  segment: { flexDirection: "row", height: 44, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, overflow: "hidden" },
  segmentItem: { flex: 1, alignItems: "center", justifyContent: "center", backgroundColor: PICK_COLORS.white },
  segmentDivider: { borderLeftWidth: 1, borderLeftColor: PICK_COLORS.outline },
  segmentActive: { backgroundColor: PICK_COLORS.infoBg },
  segmentText: { fontSize: 14, fontWeight: "500", color: PICK_COLORS.neutralChipText },
  segmentTextActive: { fontWeight: "700", color: PICK_COLORS.action },
  label: { fontSize: 13, lineHeight: 18, fontWeight: "600", color: PICK_COLORS.ink },
  stepper: { flexDirection: "row", gap: 8, height: 60 },
  stepButton: { width: 60, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  input: { flex: 1, minWidth: 0, borderRadius: 8, borderWidth: 2, borderColor: PICK_COLORS.action, textAlign: "center", fontFamily: MONO_FONT, fontSize: 28, fontWeight: "700", color: PICK_COLORS.ink, backgroundColor: PICK_COLORS.white },
  convert: { fontSize: 13, lineHeight: 18, textAlign: "center", color: PICK_COLORS.textSecondary },
  diff: { fontWeight: "600" },
  chips: { flexDirection: "row", gap: 8 },
  chip: { minHeight: 44, paddingHorizontal: 14, borderRadius: 22, borderWidth: 1, borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  chipText: { fontSize: 13, fontWeight: "500", color: PICK_COLORS.neutralChipText },
  note: { fontSize: 12, lineHeight: 18, color: PICK_COLORS.textSecondary },
  button: { borderRadius: 8 },
  buttonContent: { minHeight: 48 },
});
