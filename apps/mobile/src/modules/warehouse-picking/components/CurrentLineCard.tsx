import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { hasMinOrderQuantity, lineRemaining, lineStatus } from "../pick-math";
import { pickedByParts } from "../pick-view-model";
import type { PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";
import { ProductThumb } from "./ProductThumb";

/**
 * 当前拣货行：货位最醒目（拣货员先找货位再找商品），其次是订货 / 已拣 / 中包三格与“+1 中包”大按钮。
 * 中包未设置时大按钮换成“设置中包数”，扫码也不会计入。
 */
export function CurrentLineCard({
  line,
  myUserGuid,
  scannedChildCode,
  readonly,
  busy,
  onPlus,
  onMinus,
  onManual,
  onSetMinOrder,
}: {
  line: PickSheetLine;
  myUserGuid: string | null;
  scannedChildCode: string | null;
  readonly: boolean;
  busy: boolean;
  onPlus: () => void;
  onMinus: () => void;
  onManual: () => void;
  onSetMinOrder: () => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const hasPack = hasMinOrderQuantity(line);
  const status = lineStatus(line);
  const { remaining, scans } = lineRemaining(line);
  const tone =
    status === "complete"
      ? { bg: PICK_COLORS.successBg, border: PICK_COLORS.successBorder, label: PICK_COLORS.successText, value: PICK_COLORS.success }
      : status === "over"
        ? { bg: PICK_COLORS.warningBg, border: PICK_COLORS.warningBorder, label: PICK_COLORS.warningText, value: PICK_COLORS.warning }
        : { bg: PICK_COLORS.infoBg, border: PICK_COLORS.infoBorder, label: PICK_COLORS.infoText, value: PICK_COLORS.action };
  const hint = !hasPack
    ? { text: t("picking.hintNoPack"), color: PICK_COLORS.textSecondary }
    : status === "complete"
      ? { text: t("picking.hintComplete"), color: PICK_COLORS.success }
      : status === "over"
        ? { text: t("picking.hintOver", { count: -remaining }), color: PICK_COLORS.warning }
        : {
            text: scans ? t("picking.hintRemaining", { remaining, scans }) : t("picking.hintRemainingNoPack", { remaining }),
            color: PICK_COLORS.textSecondary,
          };
  const parts = pickedByParts(line, myUserGuid);

  return (
    <View style={styles.card} accessibilityLabel={line.productName ?? line.productCode}>
      <View style={styles.locationBar}>
        <MaterialCommunityIcons name="map-marker-outline" size={20} color={PICK_COLORS.inkMuted} />
        <Text numberOfLines={1} style={styles.location}>
          {line.locationCode || t("picking.noLocation")}
        </Text>
        <Text style={styles.locationLabel}>{t("picking.pickLocation")}</Text>
      </View>
      <View style={styles.body}>
        <View style={styles.productRow}>
          <ProductThumb uri={line.productImage} size={56} />
          <View style={styles.productText}>
            <View style={styles.nameRow}>
              {line.isSet ? <Text style={styles.setBadge}>{t("picking.setBadge")}</Text> : null}
              <Text style={styles.productName}>{line.productName || line.productCode}</Text>
            </View>
            <Text style={styles.meta}>
              {line.itemNumber || line.productCode}
              {line.barcode ? " · " : ""}
              {line.barcode ? <Text style={styles.mono}>{line.barcode}</Text> : null}
            </Text>
          </View>
        </View>

        {line.isSet && line.setChildren.length > 0 ? (
          <View style={styles.setBox}>
            <Text style={styles.setHint}>{t("picking.setChildren", { count: line.setChildren.length })}</Text>
            <View style={styles.setChips}>
              {line.setChildren.map((child) => {
                const hit =
                  Boolean(scannedChildCode) &&
                  [child.barcode, child.itemNumber, child.productCode].some(
                    (code) => code && code.toUpperCase() === scannedChildCode,
                  );
                return (
                  <View key={child.productCode} style={[styles.setChip, hit ? styles.setChipHit : null]}>
                    <Text numberOfLines={1} style={[styles.setChipText, hit ? styles.setChipTextHit : null]}>
                      {child.productName || child.itemNumber || child.productCode}
                      {hit ? ` · ${t("picking.scannedChild")}` : ""}
                    </Text>
                  </View>
                );
              })}
            </View>
          </View>
        ) : null}

        <View style={styles.grid}>
          <Cell label={t("picking.ordered")} value={String(line.orderedQuantity)} />
          <Cell
            label={t("picking.picked")}
            value={String(line.pickedTotal)}
            tone={{ bg: tone.bg, border: tone.border, label: tone.label, value: tone.value }}
          />
          {hasPack ? (
            <Cell label={t("picking.minOrder")} value={String(line.minOrderQuantity)} unit={t("picking.pcs")} />
          ) : (
            <Cell
              label={t("picking.minOrder")}
              value={t("picking.notSet")}
              small
              tone={{ bg: PICK_COLORS.warningBg, border: PICK_COLORS.warningBorder, label: PICK_COLORS.warningText, value: PICK_COLORS.warning }}
            />
          )}
        </View>

        <View style={styles.pickedBy}>
          <MaterialCommunityIcons name="account-outline" size={14} color={PICK_COLORS.textSecondary} />
          <Text style={styles.pickedByText}>
            {parts.length === 0
              ? t("picking.noPicks")
              : parts
                  .map((part) =>
                    part.isMe
                      ? t("picking.pickedByYou", { count: part.quantity })
                      : t("picking.pickedByOther", { name: part.name, count: part.quantity }),
                  )
                  .join(" · ")}
          </Text>
        </View>

        {!readonly ? (
          <View style={styles.actions}>
            {hasPack ? (
              <>
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={t("picking.removePack")}
                  disabled={busy || line.pickedTotal <= 0}
                  onPress={onMinus}
                  style={[styles.square, busy || line.pickedTotal <= 0 ? styles.disabled : null]}
                >
                  <MaterialCommunityIcons name="minus" size={22} color={PICK_COLORS.ink} />
                </Pressable>
                <Pressable
                  accessibilityRole="button"
                  disabled={busy}
                  onPress={onPlus}
                  style={[styles.primary, busy ? styles.disabled : null]}
                >
                  <MaterialCommunityIcons name="plus" size={20} color={PICK_COLORS.white} />
                  <Text style={styles.primaryText}>{t("picking.addPack")}</Text>
                  <Text style={styles.primarySub}>{t("picking.addPackPieces", { count: line.minOrderQuantity })}</Text>
                </Pressable>
              </>
            ) : (
              <Pressable accessibilityRole="button" onPress={onSetMinOrder} style={styles.warningButton}>
                <Text style={styles.warningButtonText}>{t("picking.setMinOrder")}</Text>
              </Pressable>
            )}
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("picking.manualInput")}
              disabled={busy}
              onPress={onManual}
              style={[styles.square, busy ? styles.disabled : null]}
            >
              <MaterialCommunityIcons name="keyboard-outline" size={22} color={PICK_COLORS.ink} />
            </Pressable>
          </View>
        ) : null}
        <Text style={[styles.hint, { color: hint.color }]}>{hint.text}</Text>
      </View>
    </View>
  );
}

function Cell({
  label,
  value,
  unit,
  small = false,
  tone,
}: {
  label: string;
  value: string;
  unit?: string;
  small?: boolean;
  tone?: { bg: string; border: string; label: string; value: string };
}) {
  return (
    <View style={[styles.cell, tone ? { backgroundColor: tone.bg, borderColor: tone.border } : null]}>
      <Text style={[styles.cellLabel, tone ? { color: tone.label } : null]}>{label}</Text>
      <Text style={[small ? styles.cellValueSmall : styles.cellValue, tone ? { color: tone.value } : null]}>
        {value}
        {unit ? <Text style={styles.cellUnit}> {unit}</Text> : null}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  card: {
    backgroundColor: PICK_COLORS.white,
    borderWidth: 1,
    borderColor: PICK_COLORS.outlineMuted,
    borderRadius: 12,
    overflow: "hidden",
  },
  locationBar: { height: 48, backgroundColor: PICK_COLORS.ink, flexDirection: "row", alignItems: "center", gap: 8, paddingHorizontal: 12 },
  location: { flexShrink: 1, fontFamily: MONO_FONT, fontSize: 24, lineHeight: 28, fontWeight: "700", color: PICK_COLORS.white, letterSpacing: 0.5 },
  locationLabel: { marginLeft: "auto", fontSize: 12, color: PICK_COLORS.outline },
  body: { padding: 12, gap: 10 },
  productRow: { flexDirection: "row", gap: 10, alignItems: "flex-start" },
  productText: { flex: 1, minWidth: 0, gap: 3 },
  nameRow: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: 6 },
  setBadge: {
    fontSize: 11,
    lineHeight: 18,
    fontWeight: "700",
    paddingHorizontal: 6,
    borderRadius: 4,
    overflow: "hidden",
    backgroundColor: PICK_COLORS.neutralChipText,
    color: PICK_COLORS.white,
  },
  productName: { flexShrink: 1, fontSize: 15, lineHeight: 21, fontWeight: "600", color: PICK_COLORS.ink },
  meta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  mono: { fontFamily: MONO_FONT },
  setBox: { borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 8, padding: 8, gap: 6, backgroundColor: PICK_COLORS.cellBg },
  setHint: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  setChips: { flexDirection: "row", gap: 6 },
  setChip: { flex: 1, minHeight: 32, borderRadius: 6, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center", paddingHorizontal: 4 },
  setChipHit: { backgroundColor: PICK_COLORS.successBg, borderColor: PICK_COLORS.successBorder },
  setChipText: { fontSize: 12, color: PICK_COLORS.textSecondary, fontWeight: "500" },
  setChipTextHit: { color: PICK_COLORS.successText, fontWeight: "700" },
  grid: { flexDirection: "row", gap: 8 },
  cell: { flex: 1, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, backgroundColor: PICK_COLORS.cellBg, borderRadius: 8, paddingHorizontal: 10, paddingVertical: 6 },
  cellLabel: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  cellValue: { fontSize: 22, lineHeight: 30, fontWeight: "700", color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  cellValueSmall: { fontSize: 16, lineHeight: 30, fontWeight: "700", color: PICK_COLORS.ink },
  cellUnit: { fontSize: 12, fontWeight: "400", color: PICK_COLORS.textSecondary },
  pickedBy: { flexDirection: "row", alignItems: "center", gap: 6 },
  pickedByText: { flex: 1, fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  actions: { flexDirection: "row", gap: 8 },
  square: { width: 48, height: 48, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  primary: { flex: 1, height: 48, borderRadius: 8, backgroundColor: PICK_COLORS.action, flexDirection: "row", alignItems: "center", justifyContent: "center", gap: 6 },
  primaryText: { fontSize: 16, fontWeight: "700", color: PICK_COLORS.white },
  primarySub: { fontSize: 13, fontWeight: "500", color: "#D6E4FF" },
  warningButton: { flex: 1, height: 48, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.warningStrong, backgroundColor: PICK_COLORS.warningBg, alignItems: "center", justifyContent: "center" },
  warningButtonText: { fontSize: 15, fontWeight: "700", color: PICK_COLORS.warningText },
  disabled: { opacity: 0.45 },
  hint: { fontSize: 12, lineHeight: 16, textAlign: "center" },
});
