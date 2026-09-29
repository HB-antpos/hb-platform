import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { hasMinOrderQuantity } from "../pick-math";
import type { PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";

/** “接下来”：按货位顺序的未拣齐行；同事正在拣的行标出来，避免两个人走到同一个货位。 */
export function UpNextList({
  lines,
  teammateByLine,
  onSelect,
}: {
  lines: PickSheetLine[];
  teammateByLine: Map<string, string>;
  onSelect: (detailGuid: string) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  return (
    <View style={styles.section} accessibilityLabel={t("picking.upNext")}>
      <View style={styles.header}>
        <Text style={styles.title}>{t("picking.upNext")}</Text>
        <Text style={styles.caption}>{t("picking.byLocation")}</Text>
      </View>
      {lines.length === 0 ? <Text style={styles.caption}>{t("picking.allDone")}</Text> : null}
      {lines.map((line) => {
        const teammate = teammateByLine.get(line.detailGuid);
        const note = teammate
          ? { text: t("picking.notePickerHere", { name: teammate }), color: PICK_COLORS.action }
          : !hasMinOrderQuantity(line)
            ? { text: t("picking.noteMinOrderMissing"), color: PICK_COLORS.warning }
            : line.isSet
              ? { text: t("picking.noteSet", { count: line.minOrderQuantity }), color: PICK_COLORS.textSecondary }
              : { text: t("picking.noteMinOrder", { count: line.minOrderQuantity }), color: PICK_COLORS.textSecondary };
        return (
          <Pressable
            key={line.detailGuid}
            accessibilityRole="button"
            onPress={() => onSelect(line.detailGuid)}
            style={styles.row}
          >
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
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  section: { gap: 6 },
  header: { minHeight: 22, flexDirection: "row", justifyContent: "space-between", alignItems: "center" },
  title: { fontSize: 13, fontWeight: "700", color: PICK_COLORS.ink },
  caption: { fontSize: 12, color: PICK_COLORS.textSecondary },
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
