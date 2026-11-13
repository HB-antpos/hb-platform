import { useCallback, useEffect, useMemo, useState } from "react";
import { ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickCameraSheet } from "@/modules/warehouse-picking/components/PickCameraSheet";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { ProductThumb } from "@/modules/warehouse-picking/components/ProductThumb";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { locateProduct } from "../api";
import { readPackingError } from "../api-normalization";
import { packingErrorMessage } from "../packing-errors";
import { groupLocateRows } from "../packing-logic";
import { PACKING_HOME, cartonRoute, palletRoute } from "../pack-print";
import { CARTON_STATUS } from "../types";
import { destinationText } from "../components/destination-text";
import { Chevron, PACK_COLORS, ScanBar, UnitRow, packStyles } from "../components/pack-ui";

/** 商品位置查询：扫码或输入条码 / 货号，按位置分组（货位 → 托盘 → 箱）显示数量与去向。 */
export function LocateScreen() {
  const { t, language } = useAppTranslation("warehousePacking");
  const router = useRouter();
  const focused = useIsFocused();
  const [text, setText] = useState("");
  const [code, setCode] = useState("");
  const [inputFocused, setInputFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  const query = useQuery({
    queryKey: ["warehousePacking", "locate", code],
    queryFn: ({ signal }) => locateProduct(code, signal),
    enabled: focused && Boolean(code),
    staleTime: 10_000,
    retry: false,
  });
  // 扫到 / 查到结果时给声音反馈；同一个码的后台刷新不重复响。
  useEffect(() => {
    if (query.isSuccess) playScanFeedbackSound("found");
  }, [code, query.isSuccess]);
  useEffect(() => {
    if (query.isError) playScanFeedbackSound("not_found");
  }, [code, query.isError]);

  const search = useCallback((raw: string) => {
    const next = raw.trim();
    if (!next) return;
    setText(next);
    setCode(next);
  }, []);
  const hid = useHidBarcodeScanner({ enabled: focused && !inputFocused && !cameraVisible, onScan: search });

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PACKING_HOME));
  const result = query.data;
  const groups = useMemo(() => groupLocateRows(result?.rows ?? []), [result?.rows]);
  const notFound = query.isError && readPackingError(query.error).code === "CODE_NOT_FOUND";

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader title={t("locate.title")} onBack={goBack} backLabel={t("actions.back")}>
        <View style={styles.search}>
          <ScanBar
            value={text}
            onChangeText={setText}
            onSubmit={search}
            placeholder={t("locate.placeholder")}
            inputLabel={t("locate.placeholder")}
            cameraLabel={t("actions.openCamera")}
            onCamera={() => setCameraVisible(true)}
            onFocus={() => {
              setInputFocused(true);
              hid.pauseHiddenInputFocus();
            }}
            onBlur={() => {
              setInputFocused(false);
              hid.resumeHiddenInputFocus();
            }}
            busy={query.isFetching}
            icon="magnify"
          />
        </View>
      </PickHeader>

      <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
        {!code ? (
          <EmptyState title={t("locate.idleTitle")} description={t("locate.idleHint")} />
        ) : query.isLoading ? (
          <ActivityIndicator style={packStyles.loading} />
        ) : query.isError ? (
          <EmptyState
            title={notFound ? t("locate.notFound", { code }) : packingErrorMessage(query.error, t, language)}
            actionLabel={notFound ? undefined : t("actions.retry")}
            onAction={notFound ? undefined : () => void query.refetch()}
          />
        ) : result ? (
          <>
            <View style={packStyles.card}>
              <View style={styles.productRow}>
                <ProductThumb uri={result.product.productImage} size={52} />
                <View style={styles.productMid}>
                  <Text numberOfLines={2} style={styles.productName}>
                    {result.product.productName || result.product.productCode}
                  </Text>
                  <Text numberOfLines={1} style={styles.codes}>
                    {[result.product.itemNumber, result.product.barcode].filter(Boolean).join(" · ") || result.product.productCode}
                  </Text>
                </View>
              </View>
              <View style={styles.stats}>
                <Stat label={t("locate.packedTotal")} value={String(result.packedQuantity)} unit={t("counts.unitPcs")} />
                <Stat
                  label={t("locate.spread")}
                  value={String(result.cartonCount)}
                  unit={t("locate.spreadUnit", { pallets: result.palletCount })}
                />
                <Stat label={t("locate.stock")} value={result.stockQuantity == null ? "—" : String(result.stockQuantity)} />
              </View>
            </View>

            {groups.length === 0 ? (
              <EmptyState title={t("locate.emptyTitle")} description={t("locate.emptyHint")} />
            ) : (
              groups.map((group) => (
                <View key={group.key} style={styles.group}>
                  <View style={styles.groupHead}>
                    <MaterialCommunityIcons
                      name={group.locationCode ? "map-marker-outline" : "map-marker-off-outline"}
                      size={16}
                      color={PACK_COLORS.textSecondary}
                    />
                    <Text numberOfLines={1} style={styles.groupTitle}>
                      {[
                        group.locationCode ?? t("locate.noLocation"),
                        group.palletNo ? t("locate.pallet", { palletNo: group.palletNo }) : t("locate.looseCartons"),
                      ].join(" · ")}
                    </Text>
                    <Text style={styles.groupQty}>{t("counts.pieces", { count: group.quantity })}</Text>
                  </View>
                  <View style={packStyles.cardList}>
                    {group.palletGuid ? (
                      <UnitRow
                        icon="layers-triple-outline"
                        tone="blue"
                        title={group.palletNo ?? ""}
                        monoTitle
                        subtitle={t("locate.openPallet")}
                        right={<Chevron />}
                        onPress={() => router.push(palletRoute(group.palletGuid as string))}
                      />
                    ) : null}
                    {group.rows.map((row, index) => (
                      <UnitRow
                        key={row.cartonGuid}
                        divider={index > 0 || Boolean(group.palletGuid)}
                        icon={row.cartonStatus === CARTON_STATUS.packing ? "package-variant" : "package-variant-closed"}
                        tone={row.cartonStatus === CARTON_STATUS.packing ? "blue" : "gray"}
                        title={row.cartonNo}
                        monoTitle
                        subtitle={[
                          row.cartonStatus === CARTON_STATUS.packing ? t("cartonState.packing") : null,
                          destinationText(row.destination, t),
                          row.orderNo ? t("pack.orderScope", { orderNo: row.orderNo }) : null,
                        ]
                          .filter(Boolean)
                          .join(" · ")}
                        right={<Text style={styles.rowQty}>{row.quantity}</Text>}
                        onPress={() => router.push(cartonRoute(row.cartonGuid))}
                        accessibilityLabel={`${row.cartonNo} ${t("counts.pieces", { count: row.quantity })}`}
                      />
                    ))}
                  </View>
                </View>
              ))
            )}
          </>
        ) : null}
      </ScrollView>

      <PickCameraSheet
        visible={cameraVisible}
        title={t("locate.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={search}
      />
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={packStyles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

function Stat({ label, value, unit }: { label: string; value: string; unit?: string }) {
  return (
    <View style={styles.stat}>
      <Text numberOfLines={1} style={packStyles.muted}>
        {label}
      </Text>
      <Text numberOfLines={1} style={styles.statValue}>
        {value}
        {unit ? <Text style={styles.statUnit}>{` ${unit}`}</Text> : null}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  search: { paddingHorizontal: 12, paddingBottom: 10 },
  productRow: { flexDirection: "row", alignItems: "center", gap: 10 },
  productMid: { flex: 1, minWidth: 0 },
  productName: { fontSize: 15, lineHeight: 20, fontWeight: "700", color: PACK_COLORS.ink },
  codes: { fontFamily: "monospace", fontSize: 12, lineHeight: 17, color: PACK_COLORS.textSecondary },
  stats: {
    flexDirection: "row",
    gap: 8,
    marginTop: 12,
    paddingTop: 10,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: PACK_COLORS.outlineMuted,
  },
  stat: { flex: 1, minWidth: 0 },
  statValue: { fontSize: 20, lineHeight: 26, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  statUnit: { fontSize: 12, fontWeight: "500", color: PACK_COLORS.textSecondary },
  group: { gap: 6 },
  groupHead: { flexDirection: "row", alignItems: "center", gap: 5, marginTop: 4 },
  groupTitle: { flex: 1, minWidth: 0, fontSize: 13, lineHeight: 18, fontWeight: "700", color: PACK_COLORS.textSecondary },
  groupQty: { fontSize: 13, lineHeight: 18, color: PACK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  rowQty: { fontSize: 17, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
});
