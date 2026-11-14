import { useCallback, useEffect, useMemo, useState } from "react";
import { ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Switch, Text } from "react-native-paper";
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
import { groupLocateRows, groupLocateSections, type LocateSection } from "../packing-logic";
import { PACKING_HOME, cartonRoute, palletRoute } from "../pack-print";
import { usePackScope } from "../use-pack-scope";
import { CARTON_STATUS, type LocateGroupKey, type PackLocateRow } from "../types";
import { OwnerBar } from "../components/OwnerBar";
import { destinationText } from "../components/destination-text";
import { Chevron, PACK_COLORS, ScanBar, UnitRow, packStyles } from "../components/pack-ui";

const GROUP_ICON: Record<LocateGroupKey, "archive-outline" | "truck-delivery-outline" | "truck-check-outline" | "storefront-outline"> = {
  stored: "archive-outline",
  transit: "truck-delivery-outline",
  elsewhere: "truck-check-outline",
  other: "storefront-outline",
};

/**
 * 商品位置查询：扫码或输入条码 / 货号，按「本店存放 / 在路上 / 对方已签收」分组（存放里再按位置 → 托盘 → 箱），
 * 显示数量与去向。店长多一个「查看全部分店」开关；店员只看本店。
 */
export function LocateScreen() {
  const { t, language } = useAppTranslation("packing");
  const router = useRouter();
  const focused = useIsFocused();
  const scope = usePackScope();
  const owner = scope.owner;
  const [text, setText] = useState("");
  const [code, setCode] = useState("");
  const [allStores, setAllStores] = useState(false);
  const [inputFocused, setInputFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  // 切换地点后「查看全部分店」回到关闭，避免带着上一个地点的口径。
  useEffect(() => {
    setAllStores(false);
  }, [owner?.code]);

  const query = useQuery({
    queryKey: ["packing", "locate", owner?.code ?? "", code, allStores && scope.isManager],
    queryFn: ({ signal }) => locateProduct(owner?.code ?? "", code, allStores && scope.isManager, signal),
    enabled: focused && Boolean(code) && Boolean(owner),
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
  const sections = useMemo(() => groupLocateSections(result?.rows ?? []), [result?.rows]);
  const notFound = query.isError && readPackingError(query.error).code === "CODE_NOT_FOUND";
  const showStock = Boolean(owner?.isWarehouse) && result?.stockQuantity != null;

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader title={t("locate.title")} onBack={goBack} backLabel={t("actions.back")}>
        <OwnerBar owners={scope.owners} owner={owner} onChange={scope.setOwner} />
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
          {scope.isManager ? (
            <View style={styles.allRow}>
              <Text style={styles.allText}>{t("locate.allStores")}</Text>
              <Switch
                accessibilityLabel={t("locate.allStores")}
                value={allStores}
                onValueChange={setAllStores}
                color={PACK_COLORS.action}
              />
            </View>
          ) : null}
        </View>
      </PickHeader>

      <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
        {!owner ? (
          scope.error ? (
            <EmptyState title={packingErrorMessage(scope.error, t, language)} actionLabel={t("actions.retry")} onAction={scope.refetch} />
          ) : (
            <ActivityIndicator style={packStyles.loading} />
          )
        ) : !code ? (
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
                {showStock ? <Stat label={t("locate.stock")} value={String(result.stockQuantity)} /> : null}
              </View>
            </View>

            {sections.length === 0 ? (
              <EmptyState title={t("locate.emptyTitle")} description={owner.isWarehouse ? t("locate.emptyHintWarehouse") : t("locate.emptyHint")} />
            ) : (
              sections.map((section) => (
                <SectionBlock
                  key={section.group}
                  section={section}
                  onOpenCarton={(guid) => router.push(cartonRoute(guid))}
                  onOpenPallet={(guid) => router.push(palletRoute(guid))}
                />
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

function SectionBlock({
  section,
  onOpenCarton,
  onOpenPallet,
}: {
  section: LocateSection;
  onOpenCarton: (guid: string) => void;
  onOpenPallet: (guid: string) => void;
}) {
  const { t } = useAppTranslation("packing");
  // 本店存放里再按「位置 → 托盘」分组；其他分段直接列箱。
  const groups = section.group === "stored" ? groupLocateRows(section.rows) : null;

  return (
    <View style={styles.group}>
      <View style={styles.groupHead}>
        <MaterialCommunityIcons name={GROUP_ICON[section.group]} size={16} color={PACK_COLORS.textSecondary} />
        <Text numberOfLines={1} style={styles.groupTitle}>
          {t(`locate.group.${section.group}`)}
        </Text>
        <Text style={styles.groupQty}>{t("counts.pieces", { count: section.quantity })}</Text>
      </View>
      {groups ? (
        groups.map((group) => (
          <View key={group.key} style={styles.subGroup}>
            <Text numberOfLines={1} style={styles.subTitle}>
              {[
                group.locationCode ?? t("locate.noLocation"),
                group.palletNo ? t("locate.pallet", { palletNo: group.palletNo }) : t("locate.looseCartons"),
              ].join(" · ")}
              {`  ${t("counts.pieces", { count: group.quantity })}`}
            </Text>
            <View style={packStyles.cardList}>
              {group.palletGuid ? (
                <UnitRow
                  icon="layers-triple-outline"
                  tone="blue"
                  title={group.palletNo ?? ""}
                  monoTitle
                  subtitle={t("locate.openPallet")}
                  right={<Chevron />}
                  onPress={() => onOpenPallet(group.palletGuid as string)}
                />
              ) : null}
              {group.rows.map((row, index) => (
                <LocateRow key={row.cartonGuid} row={row} divider={index > 0 || Boolean(group.palletGuid)} onPress={() => onOpenCarton(row.cartonGuid)} />
              ))}
            </View>
          </View>
        ))
      ) : (
        <View style={packStyles.cardList}>
          {section.rows.map((row, index) => (
            <LocateRow key={row.cartonGuid} row={row} divider={index > 0} onPress={() => onOpenCarton(row.cartonGuid)} showOwner />
          ))}
        </View>
      )}
    </View>
  );
}

function LocateRow({
  row,
  divider,
  showOwner = false,
  onPress,
}: {
  row: PackLocateRow;
  divider: boolean;
  showOwner?: boolean;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const subtitle = [
    row.cartonStatus === CARTON_STATUS.packing ? t("cartonState.packing") : null,
    showOwner && row.group === "other" ? t("locate.ownedBy", { name: row.ownerName || row.ownerCode }) : null,
    showOwner && row.group === "elsewhere" ? t("locate.nowAt", { name: row.ownerName || row.ownerCode }) : null,
    row.group === "transit" ? t("locate.sendTo", { target: destinationText(row.destination, t, row.ownerCode) }) : null,
    row.group === "transit" && row.originName ? t("incoming.from", { name: row.originName }) : null,
    row.palletNo && row.group !== "stored" ? t("locate.pallet", { palletNo: row.palletNo }) : null,
    row.group === "stored" ? destinationText(row.destination, t, row.ownerCode) : null,
    row.orderNo ? t("pack.orderScope", { orderNo: row.orderNo }) : null,
  ]
    .filter(Boolean)
    .join(" · ");
  return (
    <UnitRow
      divider={divider}
      icon={row.cartonStatus === CARTON_STATUS.packing ? "package-variant" : "package-variant-closed"}
      tone={row.cartonStatus === CARTON_STATUS.packing ? "blue" : "gray"}
      title={row.cartonNo}
      monoTitle
      subtitle={subtitle}
      right={<Text style={styles.rowQty}>{row.quantity}</Text>}
      onPress={onPress}
      accessibilityLabel={`${row.cartonNo} ${t("counts.pieces", { count: row.quantity })}`}
    />
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
  search: { paddingHorizontal: 12, paddingBottom: 10, gap: 6 },
  allRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", minHeight: 40 },
  allText: { fontSize: 13, fontWeight: "600", color: PACK_COLORS.neutralChipText },
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
  subGroup: { gap: 4 },
  subTitle: { fontSize: 12, lineHeight: 17, fontWeight: "600", color: PACK_COLORS.neutralChipText, fontVariant: ["tabular-nums"] },
  rowQty: { fontSize: 17, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
});
