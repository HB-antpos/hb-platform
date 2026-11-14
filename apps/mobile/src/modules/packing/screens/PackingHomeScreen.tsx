import { useCallback, useEffect, useRef, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickCameraSheet } from "@/modules/warehouse-picking/components/PickCameraSheet";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { usePrinterStore } from "@/modules/printer/state";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  createCarton,
  createPallet,
  fetchCartons,
  fetchIncoming,
  fetchPallets,
  lookupForReceive,
  resolvePackCode,
  updatePalletHeader,
} from "../api";
import { readPackingError } from "../api-normalization";
import { packingErrorMessage } from "../packing-errors";
import {
  cartonDisplayState,
  destinationLabel,
  emptyDestinationForm,
  formatLocalTime,
  palletDisplayState,
  sameCode,
  toDestinationRequest,
  type CartonDisplayState,
  type PalletDisplayState,
} from "../packing-logic";
import { PACKING_LOCATE, cartonRoute, palletRoute, receiveRoute } from "../pack-print";
import { usePackScope } from "../use-pack-scope";
import {
  WAREHOUSE_OWNER_CODE,
  type CartonFilter,
  type DestinationFormValue,
  type PackCartonSummary,
  type PackIncomingItem,
  type PackPalletSummary,
  type PalletFilter,
} from "../types";
import { DestinationSheet } from "../components/DestinationSheet";
import { OwnerBar } from "../components/OwnerBar";
import { destinationText } from "../components/destination-text";
import { Chip, ChipRow, PACK_COLORS, ScanBar, Segmented, Tag, UnitRow, packStyles, type Tone } from "../components/pack-ui";

type HomeTab = "cartons" | "pallets" | "incoming";

/**
 * 打包首页（分店 / 总仓共用）：当前地点与切换、扫箱标 / 托盘标直接打开、三个入口（新建箱、组托盘、查位置）、
 * 签收到货入口，以及 箱 / 托盘 / 待签收 三个列表。打包接口只接受登录账号（要记操作人），纯设备会话和审核模式不提供。
 */
export function PackingHomeScreen() {
  const { t, language } = useAppTranslation("packing");
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
  const scope = usePackScope(allowed);
  const owner = scope.owner;
  const ownerCode = owner?.code ?? "";
  const ready = allowed && Boolean(owner);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedKeyword(keyword.trim()), 300);
    return () => clearTimeout(timer);
  }, [keyword]);

  const cartonsQuery = useQuery({
    queryKey: ["packing", "cartons", ownerCode, cartonFilter, mine, debouncedKeyword],
    queryFn: ({ signal }) => fetchCartons(ownerCode, cartonFilter, debouncedKeyword, mine, signal),
    enabled: focused && ready,
    staleTime: 10_000,
    // 多人同时打包 / 对方发货：列表在前台时定期刷新。
    refetchInterval: focused && ready ? 30_000 : false,
  });
  const palletsQuery = useQuery({
    queryKey: ["packing", "pallets", ownerCode, palletFilter, debouncedKeyword],
    queryFn: ({ signal }) => fetchPallets(ownerCode, palletFilter, debouncedKeyword, signal),
    enabled: focused && ready,
    staleTime: 10_000,
    refetchInterval: focused && ready ? 30_000 : false,
  });
  const incomingQuery = useQuery({
    queryKey: ["packing", "incoming", ownerCode],
    queryFn: ({ signal }) => fetchIncoming(ownerCode, signal),
    enabled: focused && ready,
    staleTime: 10_000,
    refetchInterval: focused && ready ? 30_000 : false,
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
        // 发给本店、还在路上的托盘 / 箱（含托盘上的箱，托盘标签丢了的场景）直接进签收页；其余打开详情。
        const lookup = await lookupForReceive(code).catch(() => null);
        playScanFeedbackSound("found");
        setKeyword("");
        if (lookup?.canReceive) {
          router.push(receiveRoute(code));
        } else {
          router.push(resolved.kind === "carton" ? cartonRoute(resolved.guid) : palletRoute(resolved.guid));
        }
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
    enabled: focused && ready && !inputFocused && !cameraVisible && !resolving && !creating,
    onScan: handleScan,
  });

  const confirmCreate = async (value: DestinationFormValue) => {
    if (!creating || saving || !owner) return;
    setSaving(true);
    setSheetError("");
    try {
      if (creating === "carton") {
        const carton = await createCarton({ ownerCode: owner.code, ...toDestinationRequest(value) });
        setCreating(null);
        router.push(cartonRoute(carton.cartonGuid));
      } else {
        const pallet = await createPallet({ ownerCode: owner.code, ...toDestinationRequest(value) });
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

  if (!allowed || (!scope.isLoading && !owner)) {
    const message = review || sessionKind === "iosReview"
      ? t("messages.reviewUnavailable")
      : !allowed
        ? t("messages.accountRequired")
        : scope.error
          ? packingErrorMessage(scope.error, t, language)
          : t("messages.noOwner");
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.message}>
        <Text accessibilityLiveRegion="polite" style={styles.messageText}>
          {message}
        </Text>
        {scope.error ? <Button onPress={scope.refetch}>{t("actions.retry")}</Button> : null}
        <Button onPress={goBack}>{t("actions.back")}</Button>
      </SafeAreaView>
    );
  }

  if (!owner) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
        <PickHeader title={t("home.title")} onBack={goBack} backLabel={t("actions.back")} />
        <ActivityIndicator style={packStyles.loading} />
      </SafeAreaView>
    );
  }

  const cartonCounts = cartonsQuery.data;
  const palletCounts = palletsQuery.data;
  const incoming = incomingQuery.data?.pending ?? [];
  const incomingCount = incomingQuery.data ? incoming.length : cartonCounts?.incomingCount ?? 0;
  const printerConnected = printerStatus === "connected";
  const activeQuery = tab === "cartons" ? cartonsQuery : tab === "pallets" ? palletsQuery : incomingQuery;
  const isWarehouse = owner.isWarehouse || sameCode(owner.code, WAREHOUSE_OWNER_CODE);

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
        <Stat label={isWarehouse ? t("home.statStoredWarehouse") : t("home.statStored")} value={cartonCounts?.storedCount} unit={t("home.unitCarton")} />
        <Stat label={t("home.statIncoming")} value={incomingCount} highlight onPress={() => router.push(receiveRoute())} />
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
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("home.receive")}
        onPress={() => router.push(receiveRoute())}
        style={styles.receiveRow}
      >
        <MaterialCommunityIcons name="package-down" size={20} color={PACK_COLORS.action} />
        <Text style={styles.receiveText}>{t("home.receive")}</Text>
        {incomingCount > 0 ? <Tag tone="blue" label={t("home.incomingBadge", { count: incomingCount })} /> : null}
        <MaterialCommunityIcons name="chevron-right" size={20} color={PACK_COLORS.action} />
      </Pressable>
      <Segmented
        label={t("home.tabLabel")}
        value={tab}
        onChange={setTab}
        options={[
          { key: "cartons", label: t("home.tabCartons", { count: (cartonCounts?.packingCount ?? 0) + (cartonCounts?.storedCount ?? 0) + (cartonCounts?.outgoingCount ?? 0) }) },
          { key: "pallets", label: t("home.tabPallets", { count: (palletCounts?.buildingCount ?? 0) + (palletCounts?.storedCount ?? 0) }) },
          { key: "incoming", label: t("home.tabIncoming", { count: incomingCount }) },
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
              { key: "stored", label: t("home.filterStored", { count: cartonCounts?.storedCount ?? 0 }) },
              { key: "outgoing", label: t("home.filterOutgoing", { count: cartonCounts?.outgoingCount ?? 0 }) },
            ]}
          />
          <Chip label={t("home.filterMine")} selected={mine} onPress={() => setMine((current) => !current)} />
        </View>
      ) : null}
      {tab === "pallets" ? (
        <ChipRow
          label={t("home.filterLabel")}
          value={palletFilter}
          onChange={setPalletFilter}
          options={[
            { key: "all", label: t("home.filterAll") },
            { key: "building", label: t("home.filterBuilding", { count: palletCounts?.buildingCount ?? 0 }) },
            { key: "stored", label: t("home.filterStored", { count: palletCounts?.storedCount ?? 0 }) },
            { key: "outgoing", label: t("home.filterPalletOutgoing") },
          ]}
        />
      ) : null}
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
      title={tab === "cartons" ? t("home.emptyCartons") : tab === "pallets" ? t("home.emptyPallets") : t("home.emptyIncoming")}
      description={tab === "incoming" ? t("home.emptyIncomingHint") : t("home.emptyHint")}
    />
  );

  const listProps = {
    contentContainerStyle: packStyles.body,
    keyboardShouldPersistTaps: "handled" as const,
    ListHeaderComponent: header,
    ListEmptyComponent: empty,
    refreshControl: (
      <RefreshControl
        refreshing={cartonsQuery.isRefetching || palletsQuery.isRefetching || incomingQuery.isRefetching}
        onRefresh={() => {
          void cartonsQuery.refetch();
          void palletsQuery.refetch();
          void incomingQuery.refetch();
        }}
      />
    ),
  };

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader title={t("home.title")} onBack={goBack} backLabel={t("actions.back")}>
        <OwnerBar
          owners={scope.owners}
          owner={owner}
          onChange={scope.setOwner}
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
      </PickHeader>
      {tab === "cartons" ? (
        <FlatList
          {...listProps}
          data={cartonsQuery.data?.items ?? []}
          keyExtractor={(item) => item.cartonGuid}
          renderItem={({ item }) => (
            <CartonCard carton={item} ownerCode={owner.code} isWarehouse={isWarehouse} onPress={() => router.push(cartonRoute(item.cartonGuid))} />
          )}
        />
      ) : tab === "pallets" ? (
        <FlatList
          {...listProps}
          data={palletsQuery.data?.items ?? []}
          keyExtractor={(item) => item.palletGuid}
          renderItem={({ item }) => (
            <PalletCard pallet={item} ownerCode={owner.code} isWarehouse={isWarehouse} onPress={() => router.push(palletRoute(item.palletGuid))} />
          )}
        />
      ) : (
        <FlatList
          {...listProps}
          data={incoming}
          keyExtractor={(item) => `${item.kind}:${item.guid}`}
          renderItem={({ item }) => <IncomingCard item={item} onPress={() => router.push(receiveRoute(item.no))} />}
        />
      )}

      <DestinationSheet
        visible={creating !== null}
        kind={creating === "pallet" ? "pallet" : "carton"}
        ownerCode={owner.code}
        title={creating === "pallet" ? t("home.newPalletTitle") : t("home.newCartonTitle")}
        confirmLabel={creating === "pallet" ? t("home.startPallet") : t("home.startCarton")}
        initial={emptyDestinationForm()}
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

function Stat({
  label,
  value,
  unit,
  highlight = false,
  onPress,
}: {
  label: string;
  value: number | undefined;
  unit?: string;
  highlight?: boolean;
  onPress?: () => void;
}) {
  const body = (
    <View style={[packStyles.card, styles.stat, highlight ? styles.statHighlight : null]}>
      <Text numberOfLines={1} style={[packStyles.muted, highlight ? styles.statLabelHighlight : null]}>
        {label}
      </Text>
      <Text style={[styles.statValue, highlight ? styles.statLabelHighlight : null]}>
        {value ?? "—"}
        {unit ? <Text style={styles.statUnit}>{` ${unit}`}</Text> : null}
      </Text>
    </View>
  );
  return onPress ? (
    <Pressable accessibilityRole="button" accessibilityLabel={`${label} ${value ?? 0}`} onPress={onPress} style={styles.statWrap}>
      {body}
    </Pressable>
  ) : (
    <View style={styles.statWrap}>{body}</View>
  );
}

const CARTON_TONE: Record<CartonDisplayState, Tone> = {
  packing: "blue",
  stored: "green",
  toDispatch: "blue",
  onPallet: "gray",
  outgoing: "orange",
  received: "gray",
  voided: "gray",
};

function CartonCard({
  carton,
  ownerCode,
  isWarehouse,
  onPress,
}: {
  carton: PackCartonSummary;
  ownerCode: string;
  isWarehouse: boolean;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const state = cartonDisplayState(carton, ownerCode);
  const where =
    state === "onPallet"
      ? t("cartonList.onPallet", { palletNo: carton.palletNo ?? "" })
      : state === "stored" && carton.locationCode
        ? t("cartonList.atLocation", { location: carton.locationCode })
        : state === "received"
          ? t("cartonList.receivedBy", { name: carton.ownerName || destinationLabel(carton.destination) || "—" })
          : state === "outgoing" || state === "toDispatch"
            ? t("cartonList.sendTo", { target: destinationText(carton.destination, t, carton.ownerCode) })
            : destinationText(carton.destination, t, carton.ownerCode);
  const label = state === "stored" && isWarehouse ? t("cartonState.storedWarehouse") : t(`cartonState.${state}`);
  const icon =
    state === "outgoing" ? "truck-delivery-outline" : state === "received" ? "truck-check-outline" : state === "onPallet" ? "layers-triple-outline" : state === "stored" ? "archive-outline" : "package-variant-closed";
  return (
    <View style={packStyles.cardList}>
      <UnitRow
        icon={icon}
        tone={CARTON_TONE[state]}
        title={carton.cartonNo}
        monoTitle
        subtitle={`${t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })} · ${where}`}
        right={<Tag tone={CARTON_TONE[state]} label={label} />}
        onPress={onPress}
        accessibilityLabel={`${carton.cartonNo} ${label}`}
      />
    </View>
  );
}

const PALLET_TONE: Record<PalletDisplayState, Tone> = {
  building: "blue",
  stored: "green",
  toDispatch: "blue",
  outgoing: "orange",
  received: "gray",
  voided: "gray",
};

function PalletCard({
  pallet,
  ownerCode,
  isWarehouse,
  onPress,
}: {
  pallet: PackPalletSummary;
  ownerCode: string;
  isWarehouse: boolean;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const state = palletDisplayState(pallet, ownerCode);
  const partial = state === "outgoing" && pallet.inTransitCartonCount < pallet.cartonCount;
  const label = partial
    ? t("palletState.partlyReceived", { received: pallet.cartonCount - pallet.inTransitCartonCount, total: pallet.cartonCount })
    : state === "stored" && isWarehouse
      ? t("palletState.storedWarehouse")
      : t(`palletState.${state}`);
  const where =
    state === "received"
      ? t("cartonList.receivedBy", { name: pallet.ownerName || "—" })
      : state === "outgoing" || state === "toDispatch"
        ? t("cartonList.sendTo", { target: destinationText(pallet.destination, t, pallet.ownerCode) })
        : state === "stored" && pallet.locationCode
          ? t("cartonList.atLocation", { location: pallet.locationCode })
          : destinationText(pallet.destination, t, pallet.ownerCode);
  return (
    <View style={packStyles.cardList}>
      <UnitRow
        icon="layers-triple-outline"
        tone={PALLET_TONE[state]}
        title={pallet.palletNo}
        monoTitle
        subtitle={`${t("counts.cartonPieces", { cartons: pallet.cartonCount, pieces: pallet.pieceCount })} · ${where}`}
        right={<Tag tone={PALLET_TONE[state]} label={label} />}
        onPress={onPress}
        accessibilityLabel={`${pallet.palletNo} ${label}`}
      />
    </View>
  );
}

function IncomingCard({ item, onPress }: { item: PackIncomingItem; onPress: () => void }) {
  const { t } = useAppTranslation("packing");
  const isPallet = item.kind === "pallet";
  const partial = isPallet && item.receivedCartonCount > 0;
  const detail = [
    t("incoming.from", { name: item.originName || item.originCode }),
    isPallet ? t("counts.cartonPieces", { cartons: item.cartonCount, pieces: item.pieceCount }) : t("counts.pieces", { count: item.pieceCount }),
    item.carrierName,
    item.dispatchedAtUtc ? t("incoming.dispatchedAt", { time: formatLocalTime(item.dispatchedAtUtc) }) : null,
  ]
    .filter(Boolean)
    .join(" · ");
  return (
    <View style={packStyles.cardList}>
      <UnitRow
        icon={isPallet ? "layers-triple-outline" : "package-variant-closed"}
        tone="blue"
        title={item.no}
        monoTitle
        subtitle={detail}
        right={
          <Tag
            tone="orange"
            label={partial ? t("incoming.partly", { received: item.receivedCartonCount, total: item.cartonCount }) : t("incoming.inTransit")}
          />
        }
        onPress={onPress}
        accessibilityLabel={`${item.no} ${t("incoming.inTransit")}`}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  headerBlock: { gap: 10 },
  stats: { flexDirection: "row", gap: 8 },
  statWrap: { flex: 1 },
  stat: { paddingVertical: 8, paddingHorizontal: 10 },
  statHighlight: { backgroundColor: PACK_COLORS.infoBg, borderColor: PACK_COLORS.infoBorder },
  statLabelHighlight: { color: PACK_COLORS.action },
  statValue: { fontSize: 22, lineHeight: 28, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  statUnit: { fontSize: 12, fontWeight: "500", color: PACK_COLORS.textSecondary },
  receiveRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 14,
    borderRadius: 8,
    backgroundColor: PACK_COLORS.infoBg,
  },
  receiveText: { flex: 1, fontSize: 15, fontWeight: "700", color: PACK_COLORS.action },
  filterRow: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  printerTag: { minHeight: 44, justifyContent: "center" },
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PACK_COLORS.background },
  messageText: { textAlign: "center", color: PACK_COLORS.ink },
});
