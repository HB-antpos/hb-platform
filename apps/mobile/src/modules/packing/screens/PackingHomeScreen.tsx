import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, FlatList, Pressable, RefreshControl, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickCameraSheet } from "@/modules/warehouse-picking/components/PickCameraSheet";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { usePrinterStore } from "@/modules/printer/state";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAuthStore } from "@/store/auth-store";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  activateCarton,
  createCarton,
  createPallet,
  fetchCarton,
  fetchCartons,
  fetchIncoming,
  fetchPallets,
  lookupForReceive,
  reportLabelsPrinted,
  resolvePackCode,
  updatePalletHeader,
  voidBlankCartons,
} from "../api";
import { readPackingError } from "../api-normalization";
import {
  runLabelQueue,
  selectedCartons,
  splitSelection,
  summarizeVoidResult,
  toggleSelectAll,
  toggleSelected,
} from "../batch-logic";
import { packingErrorMessage, printErrorDetail } from "../packing-errors";
import {
  cartonDisplayState,
  destinationLabel,
  emptyDestinationForm,
  formatLocalTime,
  isUnusedBlank,
  palletDisplayState,
  sameCode,
  toDestinationRequest,
  type CartonDisplayState,
  type PalletDisplayState,
} from "../packing-logic";
import { usePackPreferences } from "../pack-preferences";
import { PACKING_BATCH_LIST, PACKING_LOCATE, cartonRoute, palletRoute, printCartonLabel, receiveRoute } from "../pack-print";
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
  const queryClient = useQueryClient();
  const focused = useIsFocused();
  const user = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const printerStatus = usePrinterStore((state) => state.status);
  const smallLabel = usePackPreferences((state) => state.smallLabel);
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
  // 多选（长按进入）：补打标签、作废空箱标。
  const [selectMode, setSelectMode] = useState(false);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [printProgress, setPrintProgress] = useState<{ done: number; total: number } | null>(null);
  const [voiding, setVoiding] = useState(false);
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
        setKeyword("");
        if (lookup?.canReceive) {
          playScanFeedbackSound("found");
          router.push(receiveRoute(code));
          return;
        }
        if (resolved.kind === "carton") {
          // 扫到还没启用的空箱标：先启用，再进入装箱页（顶部提示「已启用」）。
          const detail =
            lookup?.carton && lookup.carton.cartonGuid.toLowerCase() === resolved.guid.toLowerCase()
              ? lookup.carton
              : await fetchCarton(resolved.guid).catch(() => null);
          if (detail && isUnusedBlank(detail)) {
            try {
              await activateCarton(resolved.guid);
              playScanFeedbackSound("found");
              void queryClient.invalidateQueries({ queryKey: ["packing", "cartons"] });
              router.push(cartonRoute(resolved.guid, { activated: true }));
              return;
            } catch (error) {
              // 已经被别人封箱 / 启用不了：照常打开这只箱，由箱页给出状态；其他错误提示后留在首页。
              if (readPackingError(error).code !== "CARTON_NOT_PACKING") {
                playScanFeedbackSound("error");
                setSnackbar(packingErrorMessage(error, t, language));
                return;
              }
            }
          }
        }
        playScanFeedbackSound("found");
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
    [language, queryClient, router, t],
  );

  const hid = useHidBarcodeScanner({
    enabled: focused && ready && !inputFocused && !cameraVisible && !resolving && !creating && !selectMode,
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

  // ---- 多选：补打标签 / 作废空箱标 ----
  const listItems = cartonsQuery.data?.items;
  const chosen = selectedCartons(listItems ?? [], selected);
  const operatorName = user?.fullName?.trim() || user?.username || null;
  const exitSelect = () => {
    setSelectMode(false);
    setSelected(new Set());
  };
  const startSelect = (carton: PackCartonSummary) => {
    if (printProgress || voiding) return;
    setSelectMode(true);
    setSelected((current) => new Set(current).add(carton.cartonGuid));
  };
  const refreshCartonLists = () => void queryClient.invalidateQueries({ queryKey: ["packing", "cartons"] });

  /** 对选中的箱逐张打印（空箱标用空箱标样式，其余用内容标），每成功一张就上报并从选中里去掉。 */
  const printSelected = async () => {
    if (chosen.length === 0 || printProgress || printerStatus !== "connected") return;
    setPrintProgress({ done: 0, total: chosen.length });
    let done = 0;
    const result = await runLabelQueue(chosen, {
      print: (carton) => printCartonLabel(carton, 1, smallLabel, operatorName),
      report: (guids) => reportLabelsPrinted(guids),
      onPrinted: (carton) => {
        done += 1;
        setPrintProgress({ done, total: chosen.length });
        // 打好的从选中里去掉：失败后再点打印只补剩下的。
        setSelected((current) => {
          const next = new Set(current);
          next.delete(carton.cartonGuid);
          return next;
        });
      },
    });
    setPrintProgress(null);
    refreshCartonLists();
    const parts: string[] = [];
    if (result.failed) {
      parts.push(
        t("select.printStopped", {
          done: result.printed.length,
          left: chosen.length - result.printed.length,
          detail: printErrorDetail(result.failed.error),
        }),
      );
    } else {
      parts.push(t("select.printDone", { count: result.printed.length }));
    }
    if (result.unreported.length > 0) parts.push(t("select.unreported", { count: result.unreported.length }));
    setSnackbar(parts.join(clauseSeparator(language)));
    if (!result.failed && result.unreported.length === 0) exitSelect();
  };

  const confirmVoid = () => {
    if (voiding || printProgress) return;
    const { blanks } = splitSelection(chosen);
    if (blanks.length === 0) {
      setSnackbar(t("select.voidNone"));
      return;
    }
    Alert.alert(t("select.voidTitle", { count: blanks.length }), t("select.voidBody"), [
      { text: t("actions.cancel"), style: "cancel" },
      { text: t("select.void"), style: "destructive", onPress: () => void voidSelected(chosen.length, blanks) },
    ]);
  };
  const voidSelected = async (selectedCount: number, blanks: PackCartonSummary[]) => {
    setVoiding(true);
    try {
      const result = await voidBlankCartons(blanks.map((carton) => carton.cartonGuid));
      const summary = summarizeVoidResult(selectedCount, blanks.length, result);
      const parts = [t("select.voided", { count: summary.voided })];
      if (summary.notBlank > 0) parts.push(t("select.voidSkippedNotBlank", { count: summary.notBlank }));
      if (summary.otherSkipped > 0) parts.push(t("select.voidSkippedOther", { count: summary.otherSkipped }));
      setSnackbar(parts.join(clauseSeparator(language)));
      refreshCartonLists();
      exitSelect();
    } catch (error) {
      setSnackbar(t("select.voidFailed", { detail: packingErrorMessage(error, t, language) }));
    } finally {
      setVoiding(false);
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
  const blankSupported = cartonCounts?.blankCount != null;
  const blankCount = cartonCounts?.blankCount ?? 0;

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
      <View style={styles.entryRow}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("home.receive")}
          onPress={() => router.push(receiveRoute())}
          style={styles.receiveRow}
        >
          <MaterialCommunityIcons name="package-down" size={20} color={PACK_COLORS.action} />
          <View style={styles.entryTextWrap}>
            <Text numberOfLines={1} style={styles.receiveText}>
              {t("home.receive")}
            </Text>
            {incomingCount > 0 ? (
              <Text numberOfLines={1} style={styles.entryHint}>
                {t("home.incomingBadge", { count: incomingCount })}
              </Text>
            ) : null}
          </View>
          <MaterialCommunityIcons name="chevron-right" size={20} color={PACK_COLORS.action} />
        </Pressable>
        {blankSupported ? (
          // 批量箱标入口：后端没有 blankCount（旧版本）时整个隐藏。
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("home.batch")}
            onPress={() => router.push(PACKING_BATCH_LIST)}
            style={[styles.receiveRow, styles.batchRow]}
          >
            <MaterialCommunityIcons name="tag-multiple-outline" size={20} color={PACK_COLORS.action} />
            <View style={styles.entryTextWrap}>
              <Text numberOfLines={1} style={styles.receiveText}>
                {t("home.batch")}
              </Text>
              <Text numberOfLines={1} style={styles.entryHint}>
                {blankCount > 0 ? t("home.batchHint", { count: blankCount }) : t("home.batchHintEmpty")}
              </Text>
            </View>
          </Pressable>
        ) : null}
      </View>
      <Segmented
        label={t("home.tabLabel")}
        value={tab}
        onChange={(next) => {
          exitSelect();
          setTab(next);
        }}
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
              ...(blankSupported ? [{ key: "blank" as const, label: t("home.filterBlank", { count: blankCount }) }] : []),
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
      <PickHeader
        title={selectMode ? t("select.title", { count: chosen.length }) : t("home.title")}
        onBack={selectMode ? exitSelect : goBack}
        backLabel={selectMode ? t("select.exit") : t("actions.back")}
        right={
          selectMode ? (
            <Pressable
              accessibilityRole="button"
              onPress={() => setSelected(toggleSelectAll(listItems ?? [], selected))}
              style={styles.selectAll}
            >
              <Text style={styles.selectAllText}>
                {listItems && listItems.length > 0 && listItems.every((item) => selected.has(item.cartonGuid))
                  ? t("select.clearAll")
                  : t("select.selectAll")}
              </Text>
            </Pressable>
          ) : undefined
        }
      >
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
          extraData={selectMode ? selected : null}
          renderItem={({ item }) => (
            <CartonCard
              carton={item}
              ownerCode={owner.code}
              isWarehouse={isWarehouse}
              selecting={selectMode}
              selected={selected.has(item.cartonGuid)}
              onPress={() =>
                selectMode ? setSelected((current) => toggleSelected(current, item.cartonGuid)) : router.push(cartonRoute(item.cartonGuid))
              }
              onLongPress={() => startSelect(item)}
            />
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

      {selectMode ? (
        <View style={packStyles.bar}>
          {printerConnected ? null : <Text style={packStyles.muted}>{t("select.printerOffline")}</Text>}
          <View style={packStyles.buttons}>
            <Button
              mode="outlined"
              textColor={PACK_COLORS.danger}
              disabled={chosen.length === 0 || voiding || Boolean(printProgress)}
              loading={voiding}
              onPress={confirmVoid}
              style={[packStyles.button, packStyles.flexSmall, styles.voidButton]}
              contentStyle={packStyles.buttonContent}
            >
              {t("select.void")}
            </Button>
            <Button
              mode="contained"
              icon="printer"
              disabled={chosen.length === 0 || !printerConnected || voiding || Boolean(printProgress)}
              loading={Boolean(printProgress)}
              onPress={() => void printSelected()}
              style={[packStyles.button, packStyles.flex]}
              contentStyle={packStyles.buttonContent}
            >
              {printProgress
                ? t("select.printing", { done: printProgress.done, total: printProgress.total })
                : t("select.print", { count: chosen.length })}
            </Button>
          </View>
        </View>
      ) : null}

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
  selecting,
  selected,
  onPress,
  onLongPress,
}: {
  carton: PackCartonSummary;
  ownerCode: string;
  isWarehouse: boolean;
  selecting: boolean;
  selected: boolean;
  onPress: () => void;
  onLongPress: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const state = cartonDisplayState(carton, ownerCode);
  // 还没启用的空箱标：没有商品，副标题显示打印情况，右侧灰色「未启用」。
  const unusedBlank = isUnusedBlank(carton);
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
  const label = unusedBlank
    ? t("cartonList.blankUnused")
    : state === "stored" && isWarehouse
      ? t("cartonState.storedWarehouse")
      : t(`cartonState.${state}`);
  const icon = unusedBlank
    ? "tag-outline"
    : state === "outgoing" ? "truck-delivery-outline" : state === "received" ? "truck-check-outline" : state === "onPallet" ? "layers-triple-outline" : state === "stored" ? "archive-outline" : "package-variant-closed";
  const printedAt = formatLocalTime(carton.labelPrintedAtUtc);
  const subtitle = unusedBlank
    ? carton.labelPrintCount > 0
      ? printedAt
        ? t("cartonList.blankPrinted", { count: carton.labelPrintCount, time: printedAt })
        : t("cartonList.blankPrintedNoTime", { count: carton.labelPrintCount })
      : t("cartonList.blankNotPrinted")
    : `${t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })} · ${where}`;
  const tone: Tone = unusedBlank ? "gray" : CARTON_TONE[state];
  return (
    <View style={[packStyles.cardList, selecting && selected ? styles.cardSelected : null]}>
      <Pressable
        accessibilityRole={selecting ? "checkbox" : "button"}
        accessibilityState={selecting ? { checked: selected } : undefined}
        accessibilityLabel={selecting ? t("cartonList.selectCarton", { cartonNo: carton.cartonNo }) : `${carton.cartonNo} ${label}`}
        onPress={onPress}
        onLongPress={onLongPress}
        delayLongPress={350}
        style={styles.cardPress}
      >
        {selecting ? (
          <MaterialCommunityIcons
            name={selected ? "checkbox-marked" : "checkbox-blank-outline"}
            size={24}
            color={selected ? PACK_COLORS.action : PACK_COLORS.inkMuted}
          />
        ) : null}
        <View style={styles.cardRow}>
          <UnitRow
            icon={icon}
            tone={tone}
            title={carton.cartonNo}
            monoTitle
            subtitle={subtitle}
            right={<Tag tone={tone} label={label} />}
          />
        </View>
      </Pressable>
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
  entryRow: { flexDirection: "row", gap: 8 },
  receiveRow: {
    flex: 1,
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 12,
    borderRadius: 8,
    backgroundColor: PACK_COLORS.infoBg,
  },
  batchRow: { backgroundColor: PACK_COLORS.white, borderWidth: 1, borderColor: PACK_COLORS.infoBorder },
  entryTextWrap: { flex: 1, minWidth: 0 },
  receiveText: { fontSize: 15, fontWeight: "700", color: PACK_COLORS.action },
  entryHint: { fontSize: 12, lineHeight: 16, color: PACK_COLORS.textSecondary },
  cardSelected: { borderColor: PACK_COLORS.infoBorder, backgroundColor: PACK_COLORS.infoBg },
  cardPress: { flexDirection: "row", alignItems: "center", gap: 10 },
  cardRow: { flex: 1, minWidth: 0 },
  selectAll: { minHeight: 44, justifyContent: "center", paddingHorizontal: 8 },
  selectAllText: { fontSize: 15, fontWeight: "700", color: PACK_COLORS.action },
  voidButton: { borderColor: PACK_COLORS.danger },
  filterRow: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  printerTag: { minHeight: 44, justifyContent: "center" },
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PACK_COLORS.background },
  messageText: { textAlign: "center", color: PACK_COLORS.ink },
});
