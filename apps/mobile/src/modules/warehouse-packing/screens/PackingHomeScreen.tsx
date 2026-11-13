import { useCallback, useEffect, useRef, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery } from "@tanstack/react-query";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickCameraSheet } from "@/modules/warehouse-picking/components/PickCameraSheet";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { usePrinterStore } from "@/modules/printer/state";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { createCarton, createPallet, fetchCartons, fetchPallets, resolvePackCode, updatePalletHeader } from "../api";
import { readPackingError } from "../api-normalization";
import { packingErrorMessage } from "../packing-errors";
import { cartonDisplayState, emptyDestinationForm, toDestinationRequest } from "../packing-logic";
import { PACKING_LOCATE, cartonRoute, palletRoute } from "../pack-print";
import {
  PALLET_STATUS,
  type CartonFilter,
  type DestinationFormValue,
  type PackCartonSummary,
  type PackPalletSummary,
  type PalletFilter,
} from "../types";
import { DestinationSheet } from "../components/DestinationSheet";
import { destinationText } from "../components/destination-text";
import { Chip, ChipRow, PACK_COLORS, ScanBar, Segmented, Tag, UnitRow, packStyles, type Tone } from "../components/pack-ui";

type HomeTab = "cartons" | "pallets";

/**
 * 打包首页：扫箱标 / 托盘标直接打开；三个入口（新建箱、组托盘、查位置）；箱与托盘两个列表。
 * 打包接口只接受登录账号（要记操作人），纯设备会话和审核模式不提供。
 */
export function PackingHomeScreen() {
  const { t, language } = useAppTranslation("warehousePacking");
  const router = useRouter();
  const focused = useIsFocused();
  const user = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const printerStatus = usePrinterStore((state) => state.status);
  const [tab, setTab] = useState<HomeTab>("cartons");
  const [cartonFilter, setCartonFilter] = useState<CartonFilter>("all");
  const [palletFilter, setPalletFilter] = useState<PalletFilter>("all");
  const [mine, setMine] = useState(false);
  const [keyword, setKeyword] = useState("");
  const [debouncedKeyword, setDebouncedKeyword] = useState("");
  const [inputFocused, setInputFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [resolving, setResolving] = useState(false);
  const [creating, setCreating] = useState<"carton" | "pallet" | null>(null);
  const [saving, setSaving] = useState(false);
  const [sheetError, setSheetError] = useState("");
  const [snackbar, setSnackbar] = useState("");
  const resolvingRef = useRef(false);

  const allowed = !(review || sessionKind === "iosReview") && Boolean(user?.userGUID || user?.userGuid);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedKeyword(keyword.trim()), 300);
    return () => clearTimeout(timer);
  }, [keyword]);

  const cartonsQuery = useQuery({
    queryKey: ["warehousePacking", "cartons", cartonFilter, mine, debouncedKeyword],
    queryFn: ({ signal }) => fetchCartons(cartonFilter, debouncedKeyword, mine, signal),
    enabled: focused && allowed,
    staleTime: 10_000,
    // 多人同时打包：列表在前台时定期刷新。
    refetchInterval: focused && allowed ? 30_000 : false,
  });
  const palletsQuery = useQuery({
    queryKey: ["warehousePacking", "pallets", palletFilter, debouncedKeyword],
    queryFn: ({ signal }) => fetchPallets(palletFilter, debouncedKeyword, signal),
    enabled: focused && allowed,
    staleTime: 10_000,
    refetchInterval: focused && allowed ? 30_000 : false,
  });

  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));

  const handleScan = useCallback(
    async (raw: string) => {
      const code = raw.trim();
      if (!code || resolvingRef.current) return;
      resolvingRef.current = true;
      setResolving(true);
      try {
        const resolved = await resolvePackCode(code);
        playScanFeedbackSound("found");
        setKeyword("");
        router.push(resolved.kind === "carton" ? cartonRoute(resolved.guid) : palletRoute(resolved.guid));
      } catch (error) {
        playScanFeedbackSound("not_found");
        // 404：不是箱号也不是托盘号（多半是扫到了商品码）。
        setSnackbar(
          readPackingError(error).code === "CODE_NOT_FOUND"
            ? t("home.unknownCode", { code })
            : packingErrorMessage(error, t, language),
        );
      } finally {
        resolvingRef.current = false;
        setResolving(false);
      }
    },
    [language, router, t],
  );

  const hid = useHidBarcodeScanner({
    enabled: focused && allowed && !inputFocused && !cameraVisible && !resolving && !creating,
    onScan: handleScan,
  });

  const confirmCreate = async (value: DestinationFormValue) => {
    if (!creating || saving) return;
    setSaving(true);
    setSheetError("");
    try {
      if (creating === "carton") {
        const carton = await createCarton({ ...toDestinationRequest(value), orderGuid: value.orderGuid });
        setCreating(null);
        router.push(cartonRoute(carton.cartonGuid));
      } else {
        const pallet = await createPallet(toDestinationRequest(value));
        setCreating(null);
        router.push(palletRoute(pallet.palletGuid));
        const consignmentNo = value.consignmentNo.trim();
        const remark = value.remark.trim();
        if (consignmentNo || remark) {
          // 新建接口没有运单号 / 备注字段：托盘建好后再补写表头；失败不影响已建好的托盘，只提示去托盘页补填。
          try {
            await updatePalletHeader(pallet.palletGuid, {
              ...toDestinationRequest(value),
              consignmentNo: consignmentNo || null,
              weightKg: null,
              remark: remark || null,
            });
          } catch {
            setSnackbar(t("home.palletHeaderNotSaved"));
          }
        }
      }
    } catch (error) {
      setSheetError(packingErrorMessage(error, t, language));
    } finally {
      setSaving(false);
    }
  };

  if (!allowed) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.message}>
        <Text accessibilityLiveRegion="polite" style={styles.messageText}>
          {review || sessionKind === "iosReview" ? t("messages.reviewUnavailable") : t("messages.accountRequired")}
        </Text>
        <Button onPress={goBack}>{t("actions.back")}</Button>
      </SafeAreaView>
    );
  }

  const cartonCounts = cartonsQuery.data;
  const palletCounts = palletsQuery.data;
  const printerConnected = printerStatus === "connected";
  const activeQuery = tab === "cartons" ? cartonsQuery : palletsQuery;

  const header = (
    <View style={styles.headerBlock}>
      <ScanBar
        value={keyword}
        onChangeText={setKeyword}
        onSubmit={(value) => void handleScan(value)}
        placeholder={t("home.scanPlaceholder")}
        inputLabel={t("home.scanPlaceholder")}
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
        busy={resolving}
      />
      <View style={styles.stats}>
        <Stat label={t("home.statPacking")} value={cartonCounts?.packingCount} />
        <Stat label={t("home.statLoose")} value={cartonCounts?.looseSealedCount} />
        <Stat label={t("home.statBuilding")} value={palletCounts?.buildingCount} />
      </View>
      <View style={packStyles.buttons}>
        <Button
          mode="contained"
          icon="package-variant-closed"
          onPress={() => {
            setSheetError("");
            setCreating("carton");
          }}
          style={[packStyles.button, packStyles.flex]}
          contentStyle={packStyles.buttonContent}
        >
          {t("home.newCarton")}
        </Button>
        <Button
          mode="contained-tonal"
          icon="layers-triple-outline"
          onPress={() => {
            setSheetError("");
            setCreating("pallet");
          }}
          style={[packStyles.button, packStyles.flex]}
          contentStyle={packStyles.buttonContent}
        >
          {t("home.newPallet")}
        </Button>
        <Button
          mode="outlined"
          icon="map-marker-question-outline"
          onPress={() => router.push(PACKING_LOCATE)}
          style={[packStyles.button, packStyles.flex]}
          contentStyle={packStyles.buttonContent}
        >
          {t("home.locate")}
        </Button>
      </View>
      <Segmented
        label={t("home.tabLabel")}
        value={tab}
        onChange={setTab}
        options={[
          {
            key: "cartons",
            label: t("home.tabCartons", { count: (cartonCounts?.packingCount ?? 0) + (cartonCounts?.sealedCount ?? 0) }),
          },
          {
            key: "pallets",
            label: t("home.tabPallets", { count: (palletCounts?.buildingCount ?? 0) + (palletCounts?.sealedCount ?? 0) }),
          },
        ]}
      />
      {tab === "cartons" ? (
        <View style={styles.filterRow}>
          <ChipRow
            label={t("home.filterLabel")}
            value={cartonFilter}
            onChange={setCartonFilter}
            options={[
              { key: "all", label: t("home.filterAll") },
              { key: "packing", label: t("home.filterPacking", { count: cartonCounts?.packingCount ?? 0 }) },
              { key: "sealed", label: t("home.filterSealed", { count: cartonCounts?.sealedCount ?? 0 }) },
              { key: "loose", label: t("home.filterLoose", { count: cartonCounts?.looseSealedCount ?? 0 }) },
            ]}
          />
          <Chip label={t("home.filterMine")} selected={mine} onPress={() => setMine((current) => !current)} />
        </View>
      ) : (
        <ChipRow
          label={t("home.filterLabel")}
          value={palletFilter}
          onChange={setPalletFilter}
          options={[
            { key: "all", label: t("home.filterAll") },
            { key: "building", label: t("home.filterBuilding", { count: palletCounts?.buildingCount ?? 0 }) },
            { key: "sealed", label: t("home.filterPalletSealed", { count: palletCounts?.sealedCount ?? 0 }) },
          ]}
        />
      )}
    </View>
  );

  const empty = activeQuery.isLoading ? (
    <ActivityIndicator style={packStyles.loading} />
  ) : activeQuery.isError ? (
    <EmptyState
      title={packingErrorMessage(activeQuery.error, t, language)}
      actionLabel={t("actions.retry")}
      onAction={() => void activeQuery.refetch()}
    />
  ) : (
    <EmptyState
      title={tab === "cartons" ? t("home.emptyCartons") : t("home.emptyPallets")}
      description={t("home.emptyHint")}
    />
  );

  const listProps = {
    contentContainerStyle: packStyles.body,
    keyboardShouldPersistTaps: "handled" as const,
    ListHeaderComponent: header,
    ListEmptyComponent: empty,
    refreshControl: (
      <RefreshControl
        refreshing={cartonsQuery.isRefetching || palletsQuery.isRefetching}
        onRefresh={() => {
          void cartonsQuery.refetch();
          void palletsQuery.refetch();
        }}
      />
    ),
  };

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader
        title={t("home.title")}
        onBack={goBack}
        backLabel={t("actions.back")}
        right={
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={printerConnected ? t("home.printerConnected") : t("home.printerDisconnected")}
            onPress={() => router.push("/(shell)/settings")}
            style={styles.printerTag}
          >
            <Tag
              tone={printerConnected ? "green" : "orange"}
              icon={printerConnected ? "printer-check" : "printer-alert"}
              label={printerConnected ? t("home.printerConnected") : t("home.printerDisconnected")}
            />
          </Pressable>
        }
      />
      {tab === "cartons" ? (
        <FlatList
          {...listProps}
          data={cartonsQuery.data?.items ?? []}
          keyExtractor={(item) => item.cartonGuid}
          renderItem={({ item }) => <CartonCard carton={item} onPress={() => router.push(cartonRoute(item.cartonGuid))} />}
        />
      ) : (
        <FlatList
          {...listProps}
          data={palletsQuery.data?.items ?? []}
          keyExtractor={(item) => item.palletGuid}
          renderItem={({ item }) => <PalletCard pallet={item} onPress={() => router.push(palletRoute(item.palletGuid))} />}
        />
      )}

      <DestinationSheet
        visible={creating !== null}
        kind={creating === "pallet" ? "pallet" : "carton"}
        title={creating === "pallet" ? t("home.newPalletTitle") : t("home.newCartonTitle")}
        confirmLabel={creating === "pallet" ? t("home.startPallet") : t("home.startCarton")}
        initial={emptyDestinationForm()}
        showOrder={creating === "carton"}
        saving={saving}
        errorMessage={sheetError}
        onDismiss={() => setCreating(null)}
        onConfirm={(value) => void confirmCreate(value)}
      />
      <PickCameraSheet
        visible={cameraVisible}
        title={t("home.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={(barcode) => void handleScan(barcode)}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3000}>
        {snackbar}
      </Snackbar>
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={packStyles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

function Stat({ label, value }: { label: string; value: number | undefined }) {
  return (
    <View style={[packStyles.card, styles.stat]}>
      <Text numberOfLines={1} style={packStyles.muted}>
        {label}
      </Text>
      <Text style={styles.statValue}>{value ?? "—"}</Text>
    </View>
  );
}

const CARTON_TONE: Record<ReturnType<typeof cartonDisplayState>, Tone> = {
  packing: "blue",
  sealed: "green",
  onPallet: "gray",
  voided: "gray",
};

function CartonCard({ carton, onPress }: { carton: PackCartonSummary; onPress: () => void }) {
  const { t } = useAppTranslation("warehousePacking");
  const state = cartonDisplayState(carton);
  const where =
    state === "onPallet" && carton.palletNo
      ? t("cartonList.onPallet", { palletNo: carton.palletNo })
      : destinationText(carton.destination, t);
  return (
    <View style={packStyles.cardList}>
      <UnitRow
        icon={state === "onPallet" ? "layers-triple-outline" : state === "packing" ? "package-variant" : "package-variant-closed"}
        tone={CARTON_TONE[state]}
        title={carton.cartonNo}
        monoTitle
        subtitle={`${t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })} · ${where}`}
        right={<Tag tone={CARTON_TONE[state]} label={t(`cartonState.${state}`)} />}
        onPress={onPress}
        accessibilityLabel={`${carton.cartonNo} ${t(`cartonState.${state}`)}`}
      />
    </View>
  );
}

function PalletCard({ pallet, onPress }: { pallet: PackPalletSummary; onPress: () => void }) {
  const { t } = useAppTranslation("warehousePacking");
  const building = pallet.status === PALLET_STATUS.building;
  const stateLabel = building ? t("palletState.building") : t("palletState.sealed");
  return (
    <View style={packStyles.cardList}>
      <UnitRow
        icon="layers-triple-outline"
        tone={building ? "blue" : "green"}
        title={pallet.palletNo}
        monoTitle
        subtitle={`${t("counts.cartonPieces", { cartons: pallet.cartonCount, pieces: pallet.pieceCount })} · ${destinationText(pallet.destination, t)}`}
        right={<Tag tone={building ? "blue" : "green"} label={stateLabel} />}
        onPress={onPress}
        accessibilityLabel={`${pallet.palletNo} ${stateLabel}`}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  headerBlock: { gap: 10 },
  stats: { flexDirection: "row", gap: 8 },
  stat: { flex: 1, paddingVertical: 8, paddingHorizontal: 10 },
  statValue: { fontSize: 22, lineHeight: 28, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  filterRow: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  printerTag: { minHeight: 44, justifyContent: "center" },
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PACK_COLORS.background },
  messageText: { textAlign: "center", color: PACK_COLORS.ink },
});
