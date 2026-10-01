import { Fragment, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { hasLocation, isOpenLine, isStockout } from "../pick-math";
import { stockoutReasonKey } from "../pick-view-model";
import type { PickRoute, PickScope, PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";
import { PickScopeTabs } from "./PickScopeTabs";
import { ProductThumb } from "./ProductThumb";
import { RouteChip, useRouteTurnLabel } from "./UpNextList";

/**
 * 全部明细：顶部同样可切范围与走位；分三组——待拣（按走位顺序，换排处有提示）、货位没货、已拣齐（默认收起）。
 * lines 须已按当前走位排好序、并已按范围过滤。
 */
export function AllLinesSheet({
  visible,
  lines,
  scope,
  scopeCounts,
  scopes,
  helpName,
  route,
  onScopeChange,
  onRoutePress,
  onDismiss,
  onPick,
}: {
  visible: boolean;
  lines: PickSheetLine[];
  scope: PickScope;
  scopeCounts: Record<PickScope, number>;
  /** 可选的范围（有分配时多一个“我的”）。 */
  scopes?: PickScope[];
  helpName?: string;
  route: PickRoute;
  onScopeChange: (scope: PickScope) => void;
  onRoutePress: () => void;
  onDismiss: () => void;
  onPick: (detailGuid: string) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const turnLabel = useRouteTurnLabel(route);
  const [doneVisible, setDoneVisible] = useState(false);
  const open = lines.filter(isOpenLine);
  const stockouts = lines.filter(isStockout);
  const done = lines.filter((line) => !isOpenLine(line) && !isStockout(line));

  const row = (line: PickSheetLine, note?: { text: string; color: string }) => (
    <Pressable key={line.detailGuid} accessibilityRole="button" onPress={() => onPick(line.detailGuid)} style={[styles.row, isStockout(line) ? styles.rowStockout : null]}>
      <ProductThumb uri={line.productImage} size={44} />
      <View style={styles.text}>
        <Text numberOfLines={1} style={styles.name}>
          {line.productName || line.productCode}
        </Text>
        <Text numberOfLines={1} style={styles.meta}>
          {hasLocation(line) ? <Text style={styles.mono}>{line.locationCode}</Text> : t("picking.scopeUnlocated")} · {line.itemNumber || line.productCode}
        </Text>
        {note ? (
          <Text numberOfLines={1} style={[styles.note, { color: note.color }]}>
            {note.text}
          </Text>
        ) : null}
      </View>
      <View style={styles.qtyBlock}>
        <Text style={styles.qty}>
          {line.pickedTotal}/{line.orderedQuantity}
        </Text>
        {isStockout(line) ? <Text style={styles.short}>-{line.orderedQuantity - line.pickedTotal}</Text> : null}
      </View>
    </Pressable>
  );

  return (
    <BusinessSheet visible={visible} title={t("picking.allLines")} onDismiss={onDismiss}>
      <View style={styles.body}>
        <View style={styles.controls}>
          <View style={styles.tabs}>
            <PickScopeTabs value={scope} counts={scopeCounts} scopes={scopes} helpName={helpName} onChange={onScopeChange} />
          </View>
          {scope === "unlocated" ? null : <RouteChip route={route} onPress={onRoutePress} toggle />}
        </View>
        {lines.length === 0 ? <Text style={styles.empty}>{t("picking.scopeEmpty")}</Text> : null}

        {open.length > 0 ? <GroupHeader label={t("allLines.groupOpen")} count={open.length} /> : null}
        {open.map((line, index) => {
          const turn = scope === "unlocated" ? null : turnLabel(index === 0 ? null : open[index - 1]!, line);
          return (
            <Fragment key={line.detailGuid}>
              {turn ? <Text style={styles.turn}>{turn}</Text> : null}
              {row(line)}
            </Fragment>
          );
        })}

        {stockouts.length > 0 ? <GroupHeader label={t("allLines.groupStockout")} count={stockouts.length} tone="danger" /> : null}
        {stockouts.map((line) =>
          row(line, {
            text: t("picking.noteStockout", {
              reason: t(stockoutReasonKey(line.stockout!.reason, hasLocation(line), true)),
              name: line.stockout!.markedByName,
            }),
            color: PICK_COLORS.danger,
          }),
        )}

        {done.length > 0 ? (
          <>
            <GroupHeader label={t("allLines.groupDone")} count={done.length} tone="success" />
            <Pressable accessibilityRole="button" onPress={() => setDoneVisible((value) => !value)} style={styles.more}>
              <Text style={styles.moreText}>{doneVisible ? t("allLines.hideDone") : t("allLines.showDone", { count: done.length })}</Text>
              <MaterialCommunityIcons name={doneVisible ? "chevron-up" : "chevron-down"} size={16} color={PICK_COLORS.textSecondary} />
            </Pressable>
            {doneVisible ? done.map((line) => row(line)) : null}
          </>
        ) : null}
      </View>
    </BusinessSheet>
  );
}

function GroupHeader({ label, count, tone }: { label: string; count: number; tone?: "danger" | "success" }) {
  const color = tone === "danger" ? PICK_COLORS.danger : tone === "success" ? PICK_COLORS.success : PICK_COLORS.ink;
  return (
    <View style={styles.group}>
      <Text style={[styles.groupLabel, { color }]}>{label}</Text>
      <Text style={styles.groupCount}>{count}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  body: { gap: 8 },
  controls: { flexDirection: "row", alignItems: "center", gap: 8 },
  tabs: { flex: 1 },
  empty: { fontSize: 13, color: PICK_COLORS.textSecondary, textAlign: "center", paddingVertical: 16 },
  group: { flexDirection: "row", alignItems: "center", gap: 6, marginTop: 4 },
  groupLabel: { fontSize: 13, fontWeight: "700" },
  groupCount: {
    fontSize: 11,
    lineHeight: 18,
    fontWeight: "700",
    paddingHorizontal: 7,
    borderRadius: 9,
    overflow: "hidden",
    backgroundColor: PICK_COLORS.outlineMuted,
    color: PICK_COLORS.neutralChipText,
    fontVariant: ["tabular-nums"],
  },
  turn: { fontSize: 11, lineHeight: 15, color: PICK_COLORS.textSecondary, textAlign: "center" },
  row: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 8,
    paddingVertical: 6,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: PICK_COLORS.outlineMuted,
    backgroundColor: PICK_COLORS.white,
  },
  rowStockout: { borderColor: PICK_COLORS.dangerBorder, backgroundColor: "#FFFBFA" },
  text: { flex: 1, minWidth: 0, gap: 2 },
  name: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: PICK_COLORS.ink },
  meta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  note: { fontSize: 11, lineHeight: 15 },
  mono: { fontFamily: MONO_FONT, fontWeight: "700", color: PICK_COLORS.ink },
  qtyBlock: { alignItems: "flex-end" },
  qty: { fontSize: 13, fontWeight: "600", color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  short: { fontSize: 11, fontWeight: "700", color: PICK_COLORS.danger, fontVariant: ["tabular-nums"] },
  more: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
    borderRadius: 10,
    borderWidth: 1,
    borderStyle: "dashed",
    borderColor: PICK_COLORS.outline,
  },
  moreText: { fontSize: 13, color: PICK_COLORS.textSecondary },
});
