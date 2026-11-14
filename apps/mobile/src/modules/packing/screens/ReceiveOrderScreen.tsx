import { useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { playScanFeedbackSound } from "@/modules/scanner/scan-sound";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { fetchOrderReceive, receiveOrder } from "../api";
import { readPackingError } from "../api-normalization";
import {
  canReceiveOrder,
  countMismatchCounts,
  orderProductsByVariance,
  orderReceiveKey,
  productShipDiff,
  productShipStatus,
  splitOrderCartons,
} from "../order-receive-logic";
import { packingErrorMessage } from "../packing-errors";
import { PACKING_HOME } from "../pack-print";
import { OrderReceiveSheet } from "../components/OrderReceiveSheet";
import { PACK_COLORS, SectionTitle, Tag, UnitRow, packStyles } from "../components/pack-ui";

/**
 * 分店按订单签收：把一张订单发给本店、还在路上的全部箱（含托盘上的）一次签收。
 * 页面先展示箱列表（在途 / 已签收）和每个商品「订货 / 配货 / 本次发出」的对照，数清箱数后再签收；
 * 实到箱数和系统记录对不上时一只都不签，改为逐箱扫码核对（回「签收到货」逐箱扫）。
 */
export function ReceiveOrderScreen({ orderGuid }: { orderGuid: string }) {
  const { t, language } = useAppTranslation("packing");
  const router = useRouter();
  const focused = useIsFocused();
  const queryClient = useQueryClient();
  const user = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const allowed = !(review || sessionKind === "iosReview") && Boolean(user?.userGUID || user?.userGuid) && Boolean(orderGuid);
  const [sheetVisible, setSheetVisible] = useState(false);
  const [saving, setSaving] = useState(false);
  const [sheetError, setSheetError] = useState("");
  const [snackbar, setSnackbar] = useState("");
  const [done, setDone] = useState<{ cartons: number } | null>(null);

  const query = useQuery({
    queryKey: orderReceiveKey(orderGuid),
    queryFn: ({ signal }) => fetchOrderReceive(orderGuid, signal),
    enabled: focused && allowed,
    staleTime: 5_000,
  });
  const detail = query.data ?? null;

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PACKING_HOME));

  const refreshLists = () => {
    void queryClient.invalidateQueries({ queryKey: ["packing", "incoming"] });
    void queryClient.invalidateQueries({ queryKey: ["packing", "cartons"] });
    void queryClient.invalidateQueries({ queryKey: ["packing", "pallets"] });
  };

  const receiveAll = async (actualCount: number) => {
    if (saving) return;
    setSaving(true);
    setSheetError("");
    try {
      const result = await receiveOrder(orderGuid, actualCount);
      queryClient.setQueryData(orderReceiveKey(orderGuid), result);
      refreshLists();
      playScanFeedbackSound("added");
      setSheetVisible(false);
      setDone({ cartons: actualCount });
    } catch (error) {
      playScanFeedbackSound("blocked");
      const info = readPackingError(error);
      if (info.code === "COUNT_MISMATCH") {
        // 实到箱数和系统记录对不上：一只都没签，提示实际在途几箱并刷新页面，改为逐箱扫码核对。
        setSheetVisible(false);
        const counts = countMismatchCounts(actualCount, info.actualInTransitCartonCount, detail?.inTransitCount ?? 0);
        setSnackbar(t("orderReceive.countMismatch", counts));
        void query.refetch();
        return;
      }
      const message = info.code === "ORDER_NOT_FOUND" ? t("orderReceive.notFound") : packingErrorMessage(error, t, language);
      setSheetError(message);
      setSnackbar(message);
      // 别人刚签收完 / 订单不在本店范围：刷新这一屏的状态。
      if (info.code === "NOT_IN_TRANSIT" || info.code === "ORDER_NOT_FOUND") void query.refetch();
    } finally {
      setSaving(false);
    }
  };

  if (!allowed) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.message}>
        <Text accessibilityLiveRegion="polite" style={styles.messageText}>
          {t("messages.accountRequired")}
        </Text>
        <Button onPress={goBack}>{t("actions.back")}</Button>
      </SafeAreaView>
    );
  }

  const groups = detail ? splitOrderCartons(detail.cartons) : null;
  const products = detail ? orderProductsByVariance(detail.products) : [];
  const receivable = detail ? canReceiveOrder(detail) : false;

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader
        title={t("orderReceive.title")}
        subtitle={detail?.orderNo ? t("orderReceive.subtitle", { orderNo: detail.orderNo }) : null}
        onBack={goBack}
        backLabel={t("actions.back")}
      />
      {!detail ? (
        query.isError ? (
          <EmptyState
            title={readPackingError(query.error).code === "ORDER_NOT_FOUND" ? t("orderReceive.notFound") : packingErrorMessage(query.error, t, language)}
            actionLabel={t("actions.retry")}
            onAction={() => void query.refetch()}
          />
        ) : (
          <ActivityIndicator style={packStyles.loading} />
        )
      ) : (
        <ScrollView contentContainerStyle={packStyles.body}>
          {done ? (
            <View style={[packStyles.card, styles.doneCard]}>
              <MaterialCommunityIcons name="check-circle" size={28} color={PACK_COLORS.success} />
              <View style={styles.doneText}>
                <Text style={styles.doneTitle}>{t("orderReceive.doneTitle", { orderNo: detail.orderNo ?? "" })}</Text>
                <Text style={packStyles.muted}>{t("orderReceive.doneDetail", { cartons: done.cartons })}</Text>
              </View>
            </View>
          ) : null}

          <View style={[packStyles.card, styles.summary]}>
            <Text style={styles.summaryText}>
              {t("orderReceive.summary", { inTransit: detail.inTransitCount, received: detail.receivedCount })}
            </Text>
            {!receivable ? <Text style={packStyles.muted}>{t("orderReceive.nothingToReceive")}</Text> : null}
          </View>

          {groups && groups.inTransit.length > 0 ? (
            <>
              <SectionTitle title={t("orderReceive.inTransitTitle")} right={String(groups.inTransit.length)} />
              <View style={packStyles.cardList}>
                {groups.inTransit.map((carton, index) => (
                  <UnitRow
                    key={carton.cartonGuid}
                    divider={index > 0}
                    icon="package-variant-closed"
                    tone="blue"
                    title={carton.cartonNo}
                    monoTitle
                    subtitle={[
                      t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount }),
                      carton.palletNo ? t("orderReceive.onPallet", { palletNo: carton.palletNo }) : t("orderReceive.looseCarton"),
                    ].join(" · ")}
                    right={<Tag tone="orange" label={t("incoming.inTransit")} />}
                  />
                ))}
              </View>
            </>
          ) : null}
          {groups && groups.received.length > 0 ? (
            <>
              <SectionTitle title={t("orderReceive.receivedTitle")} right={String(groups.received.length)} />
              <View style={packStyles.cardList}>
                {groups.received.map((carton, index) => (
                  <UnitRow
                    key={carton.cartonGuid}
                    divider={index > 0}
                    icon="check"
                    tone="green"
                    title={carton.cartonNo}
                    monoTitle
                    subtitle={t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })}
                    right={<Tag tone="green" label={t("incoming.received")} />}
                  />
                ))}
              </View>
            </>
          ) : null}

          {products.length > 0 ? (
            <>
              <SectionTitle title={t("orderReceive.productsTitle")} />
              <View style={packStyles.cardList}>
                {products.map((product, index) => {
                  const status = productShipStatus(product);
                  const diff = productShipDiff(product);
                  return (
                    <View key={product.productCode} style={[styles.productRow, index > 0 ? styles.productDivider : null]}>
                      <View style={styles.productText}>
                        <Text numberOfLines={2} style={styles.productName}>
                          {product.productName || product.itemNumber || product.productCode}
                        </Text>
                        <Text style={packStyles.muted}>
                          {[
                            product.itemNumber || product.productCode,
                            t("orderReceive.ordered", { count: product.orderedPieces }),
                            product.allocPieces == null ? t("orderReceive.noAlloc") : t("orderReceive.alloc", { count: product.allocPieces }),
                          ].join(" · ")}
                        </Text>
                      </View>
                      <View style={styles.productRight}>
                        <Text style={styles.shipped}>{t("orderReceive.shipped", { count: product.shippedPieces })}</Text>
                        <Text
                          style={[
                            styles.shipStatus,
                            status === "short" ? styles.shortText : status === "over" ? styles.overText : styles.matchText,
                          ]}
                        >
                          {status === "match"
                            ? t("orderReceive.shipMatch")
                            : status === "short"
                              ? t("orderReceive.shipShort", { count: -diff })
                              : t("orderReceive.shipOver", { count: diff })}
                        </Text>
                      </View>
                    </View>
                  );
                })}
              </View>
              <Text style={packStyles.muted}>{t("orderReceive.productsHint")}</Text>
            </>
          ) : null}
        </ScrollView>
      )}

      {detail && receivable ? (
        <View style={packStyles.bar}>
          <Button
            mode="contained"
            icon="check-all"
            disabled={saving}
            onPress={() => {
              setSheetError("");
              setSheetVisible(true);
            }}
            style={packStyles.button}
            contentStyle={packStyles.buttonContent}
          >
            {t("orderReceive.receiveAll", { count: detail.inTransitCount })}
          </Button>
        </View>
      ) : null}

      <OrderReceiveSheet
        visible={sheetVisible}
        orderNo={detail?.orderNo ?? null}
        inTransitCount={detail?.inTransitCount ?? 0}
        saving={saving}
        errorMessage={sheetError}
        onDismiss={() => setSheetVisible(false)}
        onConfirm={(count) => void receiveAll(count)}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={4500}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PACK_COLORS.background },
  messageText: { textAlign: "center", color: PACK_COLORS.ink },
  summary: { backgroundColor: PACK_COLORS.infoBg, borderColor: PACK_COLORS.infoBorder, gap: 4 },
  summaryText: { fontSize: 15, fontWeight: "700", color: PACK_COLORS.infoText },
  doneCard: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PACK_COLORS.successBg, borderColor: PACK_COLORS.successBorder },
  doneText: { flex: 1, minWidth: 0 },
  doneTitle: { fontSize: 15, fontWeight: "700", color: PACK_COLORS.successText },
  productRow: { minHeight: 56, flexDirection: "row", alignItems: "center", gap: 10, paddingVertical: 8 },
  productDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  productText: { flex: 1, minWidth: 0, gap: 2 },
  productName: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: PACK_COLORS.ink },
  productRight: { alignItems: "flex-end", gap: 2 },
  shipped: { fontSize: 15, lineHeight: 20, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  shipStatus: { fontSize: 12, lineHeight: 16, fontWeight: "600", fontVariant: ["tabular-nums"] },
  matchText: { color: PACK_COLORS.success },
  shortText: { color: PACK_COLORS.warning },
  overText: { color: PACK_COLORS.danger },
});
