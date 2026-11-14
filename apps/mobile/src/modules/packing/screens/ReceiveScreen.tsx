import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickCameraSheet } from "@/modules/warehouse-picking/components/PickCameraSheet";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { fetchIncoming, lookupForReceive, receiveCarton, receivePallet } from "../api";
import { readPackingError } from "../api-normalization";
import { packingErrorMessage } from "../packing-errors";
import {
  allCartonsChecked,
  findPalletCartonByCode,
  formatLocalTime,
  inTransitCartons,
  receiveBlockKind,
  receiveOwnerCode,
  validCheckedGuids,
} from "../packing-logic";
import { PACKING_HOME, cartonRoute, palletRoute } from "../pack-print";
import { usePackScope } from "../use-pack-scope";
import { CARTON_STATUS, type PackIncomingItem, type PackReceiveLookup } from "../types";
import { LocationPickerSheet } from "../components/LocationPickerSheet";
import { OwnerBar } from "../components/OwnerBar";
import { ReceiveSheet } from "../components/ReceiveSheet";
import { KeyValueRow, PACK_COLORS, ScanBar, SectionTitle, Tag, UnitRow, packStyles } from "../components/pack-ui";

type SheetKind = "via" | "count" | "location" | null;

interface ReceiveDone {
  title: string;
  detail: string;
}

/**
 * 签收到货：扫托盘标签或箱标签（也可从待签收列表点进来）。
 * - 托盘标签：带出整托的箱，逐箱扫箱标签打勾核对；整托签收要确认实到箱数，或只签已核对的箱；
 * - 托盘标签丢了：扫托盘上任意一只箱的标签，同样找到整个托盘（弹层里数箱后整托签收，或只签这一箱）；
 * - 散箱：直接签收。签收后可选放到哪个存放位置。
 */
export function ReceiveScreen({ initialCode }: { initialCode: string }) {
  const { t, language } = useAppTranslation("packing");
  const router = useRouter();
  const focused = useIsFocused();
  const queryClient = useQueryClient();
  const user = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const allowed = !(review || sessionKind === "iosReview") && Boolean(user?.userGUID || user?.userGuid);
  const scope = usePackScope(allowed);
  const owner = scope.owner;

  const [scanText, setScanText] = useState("");
  const [inputFocused, setInputFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [current, setCurrent] = useState<PackReceiveLookup | null>(null);
  const [checked, setChecked] = useState<ReadonlySet<string>>(new Set());
  const [loading, setLoading] = useState(false);
  const [lookupError, setLookupError] = useState("");
  const [sheet, setSheet] = useState<SheetKind>(null);
  const returnSheetRef = useRef<SheetKind>(null);
  const [location, setLocation] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [sheetError, setSheetError] = useState("");
  const [done, setDone] = useState<ReceiveDone | null>(null);
  const [snackbar, setSnackbar] = useState("");
  const busyRef = useRef(false);
  const initialDone = useRef(false);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  const incomingQuery = useQuery({
    queryKey: ["packing", "incoming", owner?.code ?? ""],
    queryFn: ({ signal }) => fetchIncoming(owner?.code ?? "", signal),
    enabled: focused && allowed && Boolean(owner),
    staleTime: 10_000,
    refetchInterval: focused && allowed && Boolean(owner) ? 30_000 : false,
  });

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PACKING_HOME));

  const refreshLists = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ["packing", "incoming"] });
    void queryClient.invalidateQueries({ queryKey: ["packing", "cartons"] });
    void queryClient.invalidateQueries({ queryKey: ["packing", "pallets"] });
  }, [queryClient]);

  /** 按码查找要签收的托盘 / 箱；托盘标签丢了、扫到托盘上的箱时自动弹出整托签收弹层。 */
  const lookup = useCallback(
    async (code: string, options?: { keepChecked?: boolean }) => {
      setLoading(true);
      setLookupError("");
      setDone(null);
      try {
        const result = await lookupForReceive(code);
        playScanFeedbackSound(result.canReceive ? "found" : "blocked");
        setCurrent(result);
        setLocation(null);
        setSheetError("");
        setChecked((previous) => {
          const next = new Set(options?.keepChecked ? previous : []);
          // 扫的就是箱标签：这只箱本身已经核对过了。
          if (result.viaCartonLabel && result.scannedCartonGuid) next.add(result.scannedCartonGuid);
          return next;
        });
        if (result.viaCartonLabel && result.canReceive && result.kind === "pallet") setSheet("via");
      } catch (error) {
        playScanFeedbackSound("not_found");
        setCurrent(null);
        const info = readPackingError(error);
        setLookupError(
          info.code === "CODE_NOT_FOUND" || info.code === "PALLET_NOT_FOUND" || info.code === "CARTON_NOT_FOUND"
            ? t("receive.notFound", { code })
            : packingErrorMessage(error, t, language),
        );
      } finally {
        setLoading(false);
      }
    },
    [language, t],
  );

  // 首页扫到发给本店的箱 / 托盘进来时，带着码直接查找。
  useEffect(() => {
    if (initialDone.current || !initialCode || !allowed || !owner) return;
    initialDone.current = true;
    void lookup(initialCode);
  }, [allowed, initialCode, lookup, owner]);

  const handleScan = useCallback(
    (raw: string) => {
      const code = raw.trim();
      if (!code || busyRef.current) return;
      setScanText("");
      const pallet = current?.pallet;
      // 正在核对一个托盘：扫到它上面的箱就打勾，不再发请求。
      if (pallet && current?.canReceive && !sheet) {
        const carton = findPalletCartonByCode(pallet, code);
        if (carton) {
          if (carton.status !== CARTON_STATUS.inTransit) {
            playScanFeedbackSound("blocked");
            setSnackbar(t("receive.alreadyReceived", { cartonNo: carton.cartonNo }));
            return;
          }
          playScanFeedbackSound("added");
          setChecked((previous) => new Set(previous).add(carton.cartonGuid));
          return;
        }
      }
      busyRef.current = true;
      void lookup(code).finally(() => {
        busyRef.current = false;
      });
    },
    [current, lookup, sheet, t],
  );

  const hid = useHidBarcodeScanner({
    enabled: focused && allowed && Boolean(owner) && !inputFocused && !cameraVisible && sheet === null && !saving,
    onScan: handleScan,
  });

  const pallet = current?.pallet ?? null;
  const looseCarton = current?.kind === "carton" ? current.carton : null;
  const transit = pallet ? inTransitCartons(pallet) : [];
  const checkedGuids = pallet ? validCheckedGuids(pallet, checked) : [];
  const everyChecked = pallet ? allCartonsChecked(pallet, checked) : false;
  const blockKind = current ? receiveBlockKind(current) : null;
  const receiveOwner = current ? receiveOwnerCode(current) ?? owner?.code ?? "" : owner?.code ?? "";
  const scannedInTransit = Boolean(
    pallet && current?.scannedCartonGuid && transit.some((carton) => carton.cartonGuid === current.scannedCartonGuid),
  );

  const openSheet = (next: Exclude<SheetKind, null>) => {
    setSheetError("");
    setSheet(next);
  };

  /** 位置弹层是另一个原生 Modal：先收起当前弹层，选完（或取消）再回到它。 */
  const chooseLocation = () => {
    returnSheetRef.current = sheet === "location" ? null : sheet;
    setSheet("location");
  };
  const closeLocation = () => {
    setSheet(returnSheetRef.current);
    returnSheetRef.current = null;
  };

  const handleReceiveError = (error: unknown, enteredCount?: number) => {
    const info = readPackingError(error);
    playScanFeedbackSound("blocked");
    if (info.code === "COUNT_MISMATCH") {
      // 实到箱数和系统记录对不上：不能整托签收，改为逐箱扫码核对。
      setSheet(null);
      setSnackbar(
        t("receive.countMismatch", {
          entered: enteredCount ?? 0,
          actual: info.actualInTransitCartonCount ?? current?.inTransitCartonCount ?? 0,
        }),
      );
      return;
    }
    const message = packingErrorMessage(error, t, language);
    setSheetError(message);
    setSnackbar(message);
    if (info.code === "NOT_IN_TRANSIT" || info.code === "CARTON_NOT_FOUND") {
      // 别人刚签收完：刷新这一屏的状态。
      const no = pallet?.palletNo ?? looseCarton?.cartonNo;
      if (no) void lookup(no, { keepChecked: true });
    }
  };

  const afterReceive = (title: string, detail: string, remaining: number, reloadCode: string | null) => {
    playScanFeedbackSound("added");
    setSheet(null);
    refreshLists();
    if (remaining > 0 && reloadCode) {
      // 还有箱没签收：留在这一屏继续核对，只提示这次签了几箱。
      setSnackbar(`${title} ${detail}`);
      void lookup(reloadCode, { keepChecked: false });
      return;
    }
    setCurrent(null);
    setChecked(new Set());
    setLocation(null);
    setDone({ title, detail });
  };

  const receiveWhole = async (actualCount: number) => {
    if (!pallet || saving) return;
    setSaving(true);
    setSheetError("");
    try {
      const result = await receivePallet(pallet.palletGuid, {
        expectedCartonCount: actualCount,
        locationCode: location,
        viaCartonNo: current?.viaCartonLabel ? current.scannedCartonNo : null,
      });
      afterReceive(
        t("receive.doneTitle", { no: result.palletNo }),
        t("receive.doneWhole", { cartons: actualCount }),
        inTransitCartons(result).length,
        result.palletNo,
      );
    } catch (error) {
      handleReceiveError(error, actualCount);
    } finally {
      setSaving(false);
    }
  };

  const receiveChecked = async () => {
    if (!pallet || saving || checkedGuids.length === 0) return;
    setSaving(true);
    setSheetError("");
    try {
      const result = await receivePallet(pallet.palletGuid, {
        cartonGuids: checkedGuids,
        locationCode: location,
        viaCartonNo: current?.viaCartonLabel ? current.scannedCartonNo : null,
      });
      afterReceive(
        t("receive.doneTitle", { no: result.palletNo }),
        t("receive.doneChecked", { cartons: checkedGuids.length }),
        inTransitCartons(result).length,
        result.palletNo,
      );
    } catch (error) {
      handleReceiveError(error);
    } finally {
      setSaving(false);
    }
  };

  /** 签收一只箱：散箱，或托盘上「只签收这一箱」。 */
  const receiveOne = async (cartonGuid: string, cartonNo: string) => {
    if (saving) return;
    setSaving(true);
    setSheetError("");
    try {
      await receiveCarton(cartonGuid, location);
      const remaining = pallet ? Math.max(0, transit.length - 1) : 0;
      afterReceive(t("receive.doneTitle", { no: cartonNo }), t("receive.doneOne"), remaining, pallet?.palletNo ?? null);
    } catch (error) {
      handleReceiveError(error);
    } finally {
      setSaving(false);
    }
  };

  const confirmWhole = () => {
    if (!pallet) return;
    if (everyChecked) {
      // 逐箱核对完：实到箱数就是核对过的箱数，直接整托签收。
      void receiveWhole(transit.length);
      return;
    }
    openSheet("count");
  };

  const confirmChecked = () =>
    Alert.alert(t("receive.checkedConfirmTitle"), t("receive.checkedConfirmBody", { checked: checkedGuids.length, total: transit.length }), [
      { text: t("actions.cancel"), style: "cancel" },
      { text: t("receive.receiveChecked", { count: checkedGuids.length }), onPress: () => void receiveChecked() },
    ]);

  if (!allowed || (!scope.isLoading && !owner)) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.message}>
        <Text accessibilityLiveRegion="polite" style={styles.messageText}>
          {!allowed ? t("messages.accountRequired") : scope.error ? packingErrorMessage(scope.error, t, language) : t("messages.noOwner")}
        </Text>
        <Button onPress={goBack}>{t("actions.back")}</Button>
      </SafeAreaView>
    );
  }

  const pending = incomingQuery.data?.pending ?? [];
  const recent = incomingQuery.data?.recent ?? [];
  const palletSummary = pallet
    ? t("counts.palletSummary", { cartons: pallet.cartonCount, sku: pallet.skuCount, pieces: pallet.pieceCount })
    : "";

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader title={t("receive.title")} onBack={goBack} backLabel={t("actions.back")}>
        <OwnerBar owners={scope.owners} owner={owner} onChange={scope.setOwner} suffix={t("receive.ownerSuffix")} />
      </PickHeader>

      <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
        <ScanBar
          value={scanText}
          onChangeText={setScanText}
          onSubmit={handleScan}
          placeholder={t("receive.scanPlaceholder")}
          inputLabel={t("receive.scanPlaceholder")}
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
          busy={loading}
        />

        {done ? (
          <View style={[packStyles.card, styles.doneCard]}>
            <MaterialCommunityIcons name="check-circle" size={28} color={PACK_COLORS.success} />
            <View style={styles.doneText}>
              <Text style={styles.doneTitle}>{done.title}</Text>
              <Text style={packStyles.muted}>{done.detail}</Text>
            </View>
          </View>
        ) : null}

        {lookupError ? (
          <View style={[packStyles.card, styles.errorCard]}>
            <MaterialCommunityIcons name="alert-outline" size={20} color={PACK_COLORS.danger} />
            <Text style={styles.errorCardText}>{lookupError}</Text>
          </View>
        ) : null}

        {current && pallet ? (
          <>
            <View style={[packStyles.card, styles.palletCard]}>
              <UnitRow
                icon="layers-triple-outline"
                tone="blue"
                title={pallet.palletNo}
                monoTitle
                subtitle={t("receive.palletFrom", {
                  name: pallet.originName || pallet.originCode,
                  cartons: pallet.cartonCount,
                  pieces: pallet.pieceCount,
                })}
                right={<Tag tone={blockKind ? "gray" : "orange"} label={blockKind ? t(`receive.blockedTag.${blockKind}`) : t("incoming.inTransit")} />}
                onPress={() => router.push(palletRoute(pallet.palletGuid))}
              />
              <View style={styles.palletMeta}>
                <Text style={packStyles.muted}>{palletSummary}</Text>
                <Text numberOfLines={1} style={[packStyles.muted, styles.metaRight]}>
                  {[
                    pallet.destination.carrierName,
                    pallet.consignmentNo ? t("receive.consignment", { no: pallet.consignmentNo }) : null,
                    pallet.dispatchedAtUtc ? t("receive.dispatchedAt", { time: formatLocalTime(pallet.dispatchedAtUtc) }) : null,
                  ]
                    .filter(Boolean)
                    .join(" · ")}
                </Text>
              </View>
            </View>

            {blockKind ? (
              <BlockedCard
                kind={blockKind}
                target={pallet.destination.name || pallet.destination.code || "—"}
                owner={owner?.name ?? ""}
                onOpen={() => router.push(palletRoute(pallet.palletGuid))}
              />
            ) : null}

            <SectionTitle
              title={t("receive.palletCartons")}
              right={current.canReceive ? t("receive.checkedProgress", { checked: checkedGuids.length, total: transit.length }) : null}
            />
            <View style={packStyles.cardList}>
              {pallet.cartons.map((carton, index) => {
                const inTransit = carton.status === CARTON_STATUS.inTransit;
                const isChecked = checked.has(carton.cartonGuid);
                return (
                  <UnitRow
                    key={carton.cartonGuid}
                    divider={index > 0}
                    icon={isChecked || !inTransit ? "check" : "package-variant-closed"}
                    tone={isChecked || !inTransit ? "green" : "gray"}
                    title={carton.cartonNo}
                    monoTitle
                    subtitle={t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })}
                    right={
                      <Tag
                        tone={isChecked || !inTransit ? "green" : "gray"}
                        label={!inTransit ? t("receive.cartonReceived") : isChecked ? t("receive.cartonChecked") : t("receive.cartonPending")}
                      />
                    }
                  />
                );
              })}
            </View>

            {current.canReceive ? (
              <View style={packStyles.cardList}>
                <KeyValueRow first label={t("receive.putAfter")} value={location} placeholder={t("receive.noLocation")} onPress={() => openSheet("location")} />
              </View>
            ) : null}
          </>
        ) : null}

        {current && looseCarton ? (
          <>
            <View style={[packStyles.card, styles.palletCard]}>
              <UnitRow
                icon="package-variant-closed"
                tone="blue"
                title={looseCarton.cartonNo}
                monoTitle
                subtitle={t("receive.cartonFrom", {
                  name: looseCarton.originName || looseCarton.originCode,
                  sku: looseCarton.skuCount,
                  pieces: looseCarton.pieceCount,
                })}
                right={<Tag tone={blockKind ? "gray" : "orange"} label={blockKind ? t(`receive.blockedTag.${blockKind}`) : t("incoming.inTransit")} />}
                onPress={() => router.push(cartonRoute(looseCarton.cartonGuid))}
              />
              <View style={styles.palletMeta}>
                <Text numberOfLines={1} style={packStyles.muted}>
                  {[
                    looseCarton.destination.carrierName,
                    looseCarton.dispatchedAtUtc ? t("receive.dispatchedAt", { time: formatLocalTime(looseCarton.dispatchedAtUtc) }) : null,
                  ]
                    .filter(Boolean)
                    .join(" · ")}
                </Text>
              </View>
            </View>
            {blockKind ? (
              <BlockedCard
                kind={blockKind}
                target={looseCarton.destination.name || looseCarton.destination.code || "—"}
                owner={owner?.name ?? ""}
                onOpen={() => router.push(cartonRoute(looseCarton.cartonGuid))}
              />
            ) : (
              <View style={packStyles.cardList}>
                <KeyValueRow first label={t("receive.putAfter")} value={location} placeholder={t("receive.noLocation")} onPress={() => openSheet("location")} />
              </View>
            )}
          </>
        ) : null}

        {!current && !loading ? (
          <>
            <SectionTitle title={t("receive.pending")} right={pending.length ? String(pending.length) : null} />
            {incomingQuery.isLoading ? (
              <ActivityIndicator style={packStyles.loading} />
            ) : incomingQuery.isError ? (
              <EmptyState
                title={packingErrorMessage(incomingQuery.error, t, language)}
                actionLabel={t("actions.retry")}
                onAction={() => void incomingQuery.refetch()}
              />
            ) : pending.length === 0 ? (
              <EmptyState title={t("receive.emptyTitle")} description={t("receive.emptyHint")} />
            ) : (
              <View style={packStyles.cardList}>
                {pending.map((item, index) => (
                  <IncomingRow key={`${item.kind}:${item.guid}`} item={item} divider={index > 0} onPress={() => void lookup(item.no)} />
                ))}
              </View>
            )}
            {recent.length > 0 ? (
              <>
                <SectionTitle title={t("receive.recent")} />
                <View style={packStyles.cardList}>
                  {recent.map((item, index) => (
                    <IncomingRow key={`${item.kind}:${item.guid}`} item={item} divider={index > 0} received />
                  ))}
                </View>
              </>
            ) : null}
          </>
        ) : null}
        {loading ? <ActivityIndicator style={packStyles.loading} /> : null}
      </ScrollView>

      {current && current.canReceive ? (
        <View style={packStyles.bar}>
          {pallet ? (
            <View style={packStyles.buttons}>
              <Button
                mode="outlined"
                disabled={saving || checkedGuids.length === 0}
                onPress={confirmChecked}
                style={[packStyles.button, packStyles.flex]}
                contentStyle={packStyles.buttonContent}
              >
                {t("receive.receiveChecked", { count: checkedGuids.length })}
              </Button>
              <Button
                mode="contained"
                icon="check-all"
                loading={saving}
                disabled={saving}
                onPress={confirmWhole}
                style={[packStyles.button, packStyles.flex]}
                contentStyle={packStyles.buttonContent}
              >
                {t("receive.receiveWhole", { count: transit.length })}
              </Button>
            </View>
          ) : looseCarton ? (
            <Button
              mode="contained"
              icon="check-all"
              loading={saving}
              disabled={saving}
              onPress={() => void receiveOne(looseCarton.cartonGuid, looseCarton.cartonNo)}
              style={packStyles.button}
              contentStyle={packStyles.buttonContent}
            >
              {t("receive.receiveCarton")}
            </Button>
          ) : null}
        </View>
      ) : null}

      <ReceiveSheet
        visible={sheet === "via" || sheet === "count"}
        mode={sheet === "via" ? "via" : "count"}
        pallet={pallet}
        scannedCartonNo={current?.scannedCartonNo ?? null}
        inTransitCount={transit.length || current?.inTransitCartonCount || 0}
        canReceiveScanned={scannedInTransit}
        locationName={location}
        saving={saving}
        errorMessage={sheetError}
        onDismiss={() => setSheet(null)}
        onChooseLocation={chooseLocation}
        onReceiveWhole={(count) => void receiveWhole(count)}
        onReceiveOne={() => {
          if (current?.scannedCartonGuid && current.scannedCartonNo) {
            void receiveOne(current.scannedCartonGuid, current.scannedCartonNo);
          }
        }}
      />
      <LocationPickerSheet
        visible={sheet === "location"}
        ownerCode={receiveOwner}
        isManager={scope.isManager}
        title={t("receive.locationTitle")}
        subtitle={t("receive.locationSubtitle")}
        selected={location}
        allowClear={Boolean(location)}
        saving={false}
        onDismiss={closeLocation}
        onSelect={(code) => {
          setLocation(code);
          closeLocation();
        }}
      />
      <PickCameraSheet
        visible={cameraVisible}
        title={t("receive.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={handleScan}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={4500}>
        {snackbar}
      </Snackbar>
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={packStyles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

/** 不能签收（NOT_FOR_YOU / NOT_IN_TRANSIT 等）时用中文说明原因，不显示错误码。 */
function BlockedCard({
  kind,
  target,
  owner,
  onOpen,
}: {
  kind: "notForYou" | "notInTransit" | "empty";
  target: string;
  owner: string;
  onOpen: () => void;
}) {
  const { t } = useAppTranslation("packing");
  return (
    <View style={[packStyles.card, styles.warnCard]}>
      <View style={styles.warnRow} accessibilityLiveRegion="polite">
        <MaterialCommunityIcons name="alert-outline" size={18} color={PACK_COLORS.warning} />
        <Text style={styles.warnText}>{t(`receive.blocked.${kind}`, { target, owner })}</Text>
      </View>
      <Button compact mode="outlined" onPress={onOpen}>
        {t("receive.openDetail")}
      </Button>
    </View>
  );
}

function IncomingRow({
  item,
  divider,
  received = false,
  onPress,
}: {
  item: PackIncomingItem;
  divider: boolean;
  received?: boolean;
  onPress?: () => void;
}) {
  const { t } = useAppTranslation("packing");
  const isPallet = item.kind === "pallet";
  const partial = isPallet && !received && item.receivedCartonCount > 0;
  const detail = [
    t("incoming.from", { name: item.originName || item.originCode }),
    isPallet ? t("counts.cartonPieces", { cartons: item.cartonCount, pieces: item.pieceCount }) : t("counts.pieces", { count: item.pieceCount }),
    item.carrierName,
    received
      ? t("incoming.receivedAt", { time: formatLocalTime(item.receivedAtUtc) })
      : item.dispatchedAtUtc
        ? t("incoming.dispatchedAt", { time: formatLocalTime(item.dispatchedAtUtc) })
        : null,
  ]
    .filter(Boolean)
    .join(" · ");
  return (
    <UnitRow
      divider={divider}
      icon={isPallet ? "layers-triple-outline" : "package-variant-closed"}
      tone={received ? "green" : "blue"}
      title={item.no}
      monoTitle
      subtitle={detail}
      right={
        received ? (
          <Tag tone="green" label={t("incoming.received")} />
        ) : (
          <Tag tone="orange" label={partial ? t("incoming.partly", { received: item.receivedCartonCount, total: item.cartonCount }) : t("incoming.inTransit")} />
        )
      }
      onPress={onPress}
    />
  );
}

const styles = StyleSheet.create({
  palletCard: { backgroundColor: PACK_COLORS.infoBg, borderColor: PACK_COLORS.infoBorder },
  palletMeta: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 8,
    marginTop: 8,
    paddingTop: 8,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: PACK_COLORS.infoBorder,
  },
  metaRight: { flexShrink: 1, textAlign: "right" },
  warnCard: { backgroundColor: PACK_COLORS.warningBg, borderColor: PACK_COLORS.warningBorder, gap: 8 },
  warnRow: { flexDirection: "row", alignItems: "flex-start", gap: 8 },
  warnText: { flex: 1, fontSize: 13, lineHeight: 19, color: PACK_COLORS.warningText },
  doneCard: { flexDirection: "row", alignItems: "center", gap: 10, backgroundColor: PACK_COLORS.successBg, borderColor: PACK_COLORS.successBorder },
  doneText: { flex: 1, minWidth: 0 },
  doneTitle: { fontSize: 15, fontWeight: "700", color: PACK_COLORS.successText },
  errorCard: { flexDirection: "row", alignItems: "center", gap: 8, backgroundColor: PACK_COLORS.dangerBg, borderColor: PACK_COLORS.dangerBorder },
  errorCardText: { flex: 1, fontSize: 13, lineHeight: 19, color: PACK_COLORS.dangerText },
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PACK_COLORS.background },
  messageText: { textAlign: "center", color: PACK_COLORS.ink },
});
