import { ScrollView, StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { formatLocalTime, type ViewMode } from "../packing-logic";
import { CARTON_STATUS, type PackPalletDetail } from "../types";
import { destinationText } from "../components/destination-text";
import { Chevron, KeyValueRow, PACK_COLORS, SectionTitle, Tag, UnitRow, packStyles } from "../components/pack-ui";

/**
 * 只读的托盘：已发出（等对方签收，显示已签收 x / n 箱）、发给本店在路上（去签收）、对方已签收，以及其他看得到但不归你的托盘。
 * 发出后发出方不能再改，后端也会拒绝。
 */
export function PalletReadOnlyView({
  pallet,
  mode,
  saving,
  onReprint,
  onOpenCarton,
  onReceive,
}: {
  pallet: PackPalletDetail;
  mode: Exclude<ViewMode, "editable">;
  saving: boolean;
  onReprint: () => void;
  onOpenCarton: (cartonGuid: string) => void;
  onReceive: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const target = destinationText(pallet.destination, t, pallet.ownerCode);
  const receivedCount = pallet.cartonCount - pallet.inTransitCartonCount;

  const banner =
    mode === "outgoing"
      ? receivedCount > 0
        ? t("readonly.palletPartlyReceived", { target, received: receivedCount, total: pallet.cartonCount })
        : t("readonly.outgoing", { target })
      : mode === "incoming"
        ? t("readonly.incoming", { origin: pallet.originName || pallet.originCode })
        : mode === "received"
          ? t("readonly.received", { owner: pallet.ownerName || pallet.ownerCode, time: formatLocalTime(pallet.receivedAtUtc) })
          : t("readonly.other");

  return (
    <>
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
          <KeyValueRow first label={t("fields.origin")} value={pallet.originName || pallet.originCode} />
          <KeyValueRow label={t("fields.destination")} value={target} />
          {pallet.consignmentNo ? <KeyValueRow label={t("fields.consignmentNo")} value={pallet.consignmentNo} /> : null}
          {pallet.dispatchedAtUtc ? (
            <KeyValueRow
              label={t("fields.dispatchedBy")}
              value={[pallet.dispatchedByName, formatLocalTime(pallet.dispatchedAtUtc)].filter(Boolean).join(" · ")}
            />
          ) : null}
          {pallet.receivedAtUtc ? (
            <KeyValueRow
              label={t("fields.receivedBy")}
              value={[pallet.receivedByName, formatLocalTime(pallet.receivedAtUtc)].filter(Boolean).join(" · ")}
            />
          ) : null}
        </View>

        <SectionTitle
          title={t("pallet.cartons")}
          right={t("counts.palletSummary", { cartons: pallet.cartonCount, sku: pallet.skuCount, pieces: pallet.pieceCount })}
        />
        <View style={packStyles.cardList}>
          {pallet.cartons.map((carton, index) => {
            const inTransit = carton.status === CARTON_STATUS.inTransit;
            return (
              <UnitRow
                key={carton.cartonGuid}
                divider={index > 0}
                icon="package-variant-closed"
                tone={inTransit ? "gray" : "green"}
                title={carton.cartonNo}
                monoTitle
                subtitle={t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })}
                right={
                  mode === "outgoing" || mode === "received" ? (
                    <Tag tone={inTransit ? "orange" : "green"} label={inTransit ? t("incoming.inTransit") : t("receive.cartonReceived")} />
                  ) : (
                    <Chevron />
                  )
                }
                onPress={() => onOpenCarton(carton.cartonGuid)}
              />
            );
          })}
        </View>

        {mode === "incoming" ? (
          <Button
            mode="contained"
            icon="package-down"
            onPress={onReceive}
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
            disabled={saving}
            onPress={onReprint}
            style={packStyles.button}
            contentStyle={packStyles.buttonContent}
          >
            {t("sealed.reprint")}
          </Button>
        ) : null}
      </ScrollView>
    </>
  );
}

const styles = StyleSheet.create({
  infoCard: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PACK_COLORS.infoBg, borderColor: PACK_COLORS.infoBorder },
  doneCard: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PACK_COLORS.successBg, borderColor: PACK_COLORS.successBorder },
  bannerText: { flex: 1, fontSize: 14, lineHeight: 20, fontWeight: "600", color: PACK_COLORS.ink },
});
