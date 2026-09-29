import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { PickSheetLine } from "../types";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";
import { ProductThumb } from "./ProductThumb";

/** 同一个码命中本单多行（同码多品、重复商品）时让拣货员选定；也用作“全部明细”列表。 */
export function LineChooserSheet({
  visible,
  title,
  code,
  lines,
  onDismiss,
  onPick,
}: {
  visible: boolean;
  title?: string;
  code?: string | null;
  lines: PickSheetLine[];
  onDismiss: () => void;
  onPick: (detailGuid: string) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  return (
    <BusinessSheet visible={visible} title={title ?? t("picking.chooseLine")} subtitle={code ?? undefined} onDismiss={onDismiss}>
      <View style={styles.list}>
        {lines.map((line) => (
          <Pressable key={line.detailGuid} accessibilityRole="button" onPress={() => onPick(line.detailGuid)} style={styles.row}>
            <ProductThumb uri={line.productImage} size={44} />
            <View style={styles.text}>
              <Text numberOfLines={1} style={styles.name}>
                {line.productName || line.productCode}
              </Text>
              <Text numberOfLines={1} style={styles.meta}>
                <Text style={styles.mono}>{line.locationCode || "—"}</Text> · {line.itemNumber || line.productCode}
              </Text>
            </View>
            <Text style={styles.qty}>
              {line.pickedTotal}/{line.orderedQuantity}
            </Text>
          </Pressable>
        ))}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  list: { gap: 8 },
  row: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 8,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: PICK_COLORS.outlineMuted,
    backgroundColor: PICK_COLORS.white,
  },
  text: { flex: 1, minWidth: 0, gap: 2 },
  name: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: PICK_COLORS.ink },
  meta: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  mono: { fontFamily: MONO_FONT, fontWeight: "700", color: PICK_COLORS.ink },
  qty: { fontSize: 13, fontWeight: "600", color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
});
