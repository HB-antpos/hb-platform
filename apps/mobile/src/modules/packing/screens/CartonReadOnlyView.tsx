import { useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { useRouter } from "expo-router";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ProductThumb } from "@/modules/warehouse-picking/components/ProductThumb";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { printErrorDetail } from "../packing-errors";
import { formatLocalTime, type ViewMode } from "../packing-logic";
import { usePackPreferences } from "../pack-preferences";
import { palletRoute, printCartonLabel, receiveRoute } from "../pack-print";
import type { PackCartonDetail } from "../types";
import { destinationText } from "../components/destination-text";
import { KeyValueRow, PACK_COLORS, SectionTitle, packStyles } from "../components/pack-ui";

/**
 * 只读的箱：已发出（等对方签收）、发给本店在路上（去签收）、对方已签收，以及其他看得到但不归你的箱。
 * 发出后发出方不能再改，后端也会拒绝。
 */
export function CartonReadOnlyView({
  carton,
  mode,
  onMessage,
}: {
  carton: PackCartonDetail;
  mode: Exclude<ViewMode, "editable">;
  onMessage: (message: string) => void;
}) {
  const { t } = useAppTranslation("packing");
  const router = useRouter();
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const [printing, setPrinting] = useState(false);
  const target = destinationText(carton.destination, t, carton.ownerCode);

  const reprint = async () => {
    if (printing) return;
    setPrinting(true);
    try {
      await printCartonLabel(carton, 1, smallLabel);
      onMessage(t("sealed.reprinted"));
    } catch (error) {
      onMessage(t("print.failed", { detail: printErrorDetail(error) }));
    } finally {
      setPrinting(false);
    }
  };

  const banner =
    mode === "outgoing"
      ? t("readonly.outgoing", { target })
      : mode === "incoming"
        ? t("readonly.incoming", { origin: carton.originName || carton.originCode })
        : mode === "received"
          ? t("readonly.received", { owner: carton.ownerName || carton.ownerCode, time: formatLocalTime(carton.receivedAtUtc) })
          : t("readonly.other");

  return (
    <ScrollView contentContainerStyle={packStyles.body}>
      <View style={[packStyles.card, mode === "received" ? styles.doneCard : styles.infoCard]}>
        <MaterialCommunityIcons
          name={mode === "received" ? "truck-check-outline" : "truck-delivery-outline"}
          size={22}
          color={mode === "received" ? PACK_COLORS.success : PACK_COLORS.action}
        />
        <Text style={styles.bannerText}>{banner}</Text>
      </View>

      <View style={packStyles.cardList}>
        <KeyValueRow first label={t("fields.origin")} value={carton.originName || carton.originCode} />
        <KeyValueRow label={t("fields.destination")} value={target} />
        {carton.palletNo && carton.palletGuid ? (
          <KeyValueRow
            label={t("fields.pallet")}
            value={carton.palletNo}
            onPress={() => router.push(palletRoute(carton.palletGuid as string))}
          />
        ) : null}
        {carton.dispatchedAtUtc ? (
          <KeyValueRow
            label={t("fields.dispatchedBy")}
            value={[carton.dispatchedByName, formatLocalTime(carton.dispatchedAtUtc)].filter(Boolean).join(" · ")}
          />
        ) : null}
        {carton.receivedAtUtc ? (
          <KeyValueRow
            label={t("fields.receivedBy")}
            value={[carton.receivedByName, formatLocalTime(carton.receivedAtUtc)].filter(Boolean).join(" · ")}
          />
        ) : null}
      </View>

      <SectionTitle title={t("pack.items")} right={t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })} />
      <View style={packStyles.cardList}>
        {carton.items.map((item, index) => (
          <View key={item.productCode} style={[styles.itemRow, index > 0 ? styles.itemDivider : null]}>
            <ProductThumb uri={item.productImage} size={40} />
            <View style={styles.itemMid}>
              <Text numberOfLines={1} style={styles.itemName}>
                {item.productName || item.productCode}
              </Text>
              <Text numberOfLines={1} style={styles.itemCodes}>
                {item.itemNumber || item.productCode}
              </Text>
            </View>
            <Text style={styles.itemQty}>
              {item.quantity}
              <Text style={styles.itemUnit}>{` ${t("counts.unitPcs")}`}</Text>
            </Text>
          </View>
        ))}
      </View>

      {mode === "incoming" ? (
        <Button
          mode="contained"
          icon="package-down"
          onPress={() => router.push(receiveRoute(carton.cartonNo))}
          style={packStyles.button}
          contentStyle={packStyles.buttonContent}
        >
          {t("readonly.goReceive")}
        </Button>
      ) : null}
      {mode === "outgoing" ? (
        <Button
          mode="outlined"
          icon="printer"
          loading={printing}
          disabled={printing}
          onPress={() => void reprint()}
          style={packStyles.button}
          contentStyle={packStyles.buttonContent}
        >
          {t("sealed.reprint")}
        </Button>
      ) : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  infoCard: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PACK_COLORS.infoBg, borderColor: PACK_COLORS.infoBorder },
  doneCard: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PACK_COLORS.successBg, borderColor: PACK_COLORS.successBorder },
  bannerText: { flex: 1, fontSize: 14, lineHeight: 20, fontWeight: "600", color: PACK_COLORS.ink },
  itemRow: { minHeight: 56, flexDirection: "row", alignItems: "center", gap: 10, paddingVertical: 6 },
  itemDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  itemMid: { flex: 1, minWidth: 0 },
  itemName: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: PACK_COLORS.ink },
  itemCodes: { fontFamily: "monospace", fontSize: 12, lineHeight: 17, color: PACK_COLORS.textSecondary },
  itemQty: { fontSize: 17, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  itemUnit: { fontSize: 12, fontWeight: "400", color: PACK_COLORS.textSecondary },
});
