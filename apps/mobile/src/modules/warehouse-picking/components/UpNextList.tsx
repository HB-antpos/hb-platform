import { Fragment } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { hasMinOrderQuantity, routeTurn } from "../pick-math";
import type { PickRoute, PickScope, PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";

/** 换排提示文案：M 型是折返进下一排，S 型标出这一排的列号走向。 */
export function useRouteTurnLabel(route: PickRoute) {
  const { t } = useAppTranslation("warehousePicking");
  return (previous: PickSheetLine | null, next: PickSheetLine) => {
    const turn = routeTurn(previous, next, route);
    if (!turn) return null;
    const values = { zone: turn.zone, row: turn.rowLabel };
    if (route === "m") return t("picking.turnM", values);
    return turn.descending ? t("picking.turnSDesc", values) : t("picking.turnSAsc", values);
  };
}

/** 走位方式入口：显示当前方式；toggle 时点一下直接在 M / S 之间切换（弹层里不再叠弹层）。 */
export function RouteChip({ route, onPress, toggle = false }: { route: PickRoute; onPress: () => void; toggle?: boolean }) {
  const { t } = useAppTranslation("warehousePicking");
  const label = route === "m" ? t("picking.routeM") : t("picking.routeS");
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("picking.routeSwitch", { route: label })}
      onPress={onPress}
      hitSlop={6}
      style={styles.routeChip}
    >
      <MaterialCommunityIcons name={route === "m" ? "alpha-m-box-outline" : "alpha-s-box-outline"} size={16} color={PICK_COLORS.infoText} />
      <Text style={styles.routeText}>{label}</Text>
      <MaterialCommunityIcons name={toggle ? "swap-horizontal" : "chevron-down"} size={16} color={PICK_COLORS.infoText} />
    </Pressable>
  );
}

/** “接下来”：按走位顺序的待拣行；同事正在拣的行标出来，避免两个人走到同一个货位；换排处插一条提示。 */
export function UpNextList({
  current,
  lines,
  route,
  scope,
  teammateByLine,
  onSelect,
  onRoutePress,
}: {
  current: PickSheetLine | null;
  lines: PickSheetLine[];
  route: PickRoute;
  scope: PickScope;
  teammateByLine: Map<string, string>;
  onSelect: (detailGuid: string) => void;
  onRoutePress: () => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const turnLabel = useRouteTurnLabel(route);
  return (
    <View style={styles.section} accessibilityLabel={t("picking.upNext")}>
      <View style={styles.header}>
        <Text style={styles.title}>{t("picking.upNext")}</Text>
        {scope === "unlocated" ? (
          <Text style={styles.caption}>{t("picking.byItemNumber")}</Text>
        ) : (
          <RouteChip route={route} onPress={onRoutePress} />
        )}
      </View>
      {lines.length === 0 ? <Text style={styles.caption}>{t("picking.allDone")}</Text> : null}
      {lines.map((line, index) => {
        const teammate = teammateByLine.get(line.detailGuid);
        const note = teammate
          ? { text: t("picking.notePickerHere", { name: teammate }), color: PICK_COLORS.action }
          : !hasMinOrderQuantity(line)
            ? { text: t("picking.noteMinOrderMissing"), color: PICK_COLORS.warning }
            : line.isSet
              ? { text: t("picking.noteSet", { count: line.minOrderQuantity }), color: PICK_COLORS.textSecondary }
              : { text: t("picking.noteMinOrder", { count: line.minOrderQuantity }), color: PICK_COLORS.textSecondary };
        const turn = turnLabel(index === 0 ? current : lines[index - 1]!, line);
        return (
          <Fragment key={line.detailGuid}>
            {turn ? (
              <View style={styles.turn}>
                <View style={styles.turnLine} />
                <Text style={styles.turnText}>{turn}</Text>
                <View style={styles.turnLine} />
              </View>
            ) : null}
            <Pressable accessibilityRole="button" onPress={() => onSelect(line.detailGuid)} style={styles.row}>
              <Text numberOfLines={1} style={styles.location}>
                {line.locationCode || "—"}
              </Text>
              <View style={styles.textBlock}>
                <Text numberOfLines={1} style={styles.name}>
                  {line.productName || line.productCode}
                </Text>
                <Text numberOfLines={1} style={[styles.note, { color: note.color }]}>
                  {note.text}
                </Text>
              </View>
              <Text style={styles.qty}>
                {line.pickedTotal}/{line.orderedQuantity}
              </Text>
            </Pressable>
          </Fragment>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  section: { gap: 6 },
  header: { minHeight: 32, flexDirection: "row", justifyContent: "space-between", alignItems: "center" },
  title: { fontSize: 13, fontWeight: "700", color: PICK_COLORS.ink },
  caption: { fontSize: 12, color: PICK_COLORS.textSecondary },
  routeChip: {
    minHeight: 32,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingLeft: 8,
    paddingRight: 6,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: PICK_COLORS.infoBorder,
    backgroundColor: PICK_COLORS.infoBg,
  },
  routeText: { fontSize: 12, fontWeight: "600", color: PICK_COLORS.infoText },
  turn: { flexDirection: "row", alignItems: "center", gap: 8, paddingHorizontal: 4 },
  turnLine: { flex: 1, height: StyleSheet.hairlineWidth, backgroundColor: PICK_COLORS.outline },
  turnText: { fontSize: 11, lineHeight: 15, color: PICK_COLORS.textSecondary },
  row: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 12,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: PICK_COLORS.outlineMuted,
    backgroundColor: PICK_COLORS.white,
  },
  location: { width: 92, fontFamily: MONO_FONT, fontSize: 13, fontWeight: "700", color: PICK_COLORS.ink },
  textBlock: { flex: 1, minWidth: 0 },
  name: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.ink },
  note: { fontSize: 11, lineHeight: 15 },
  qty: { fontSize: 13, fontWeight: "600", color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
});
