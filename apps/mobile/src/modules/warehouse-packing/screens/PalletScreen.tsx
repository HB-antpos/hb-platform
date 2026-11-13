import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
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
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  addCartonToPallet,
  fetchPallet,
  removeCartonFromPallet,
  reopenPallet,
  sealPallet,
  setPalletLocation,
  updatePalletHeader,
  voidPallet,
} from "../api";
import { readPackingError } from "../api-normalization";
import { packingErrorMessage, printErrorDetail } from "../packing-errors";
import {
  buildPalletLabelPayload,
  destinationFormOf,
  formatLocalTime,
  formatWeight,
  isCarrierDestination,
  palletBlockReason,
  palletLabelPreview,
  sumCartonWeight,
  toDestinationRequest,
  type PalletBlockReason,
} from "../packing-logic";
import { usePackPreferences } from "../pack-preferences";
import { PACKING_HOME, cartonRoute, printPalletLabel } from "../pack-print";
import {
  PALLET_STATUS,
  type DestinationFormValue,
  type PackCartonSummary,
  type PackPalletDetail,
} from "../types";
import { DestinationSheet } from "../components/DestinationSheet";
import { CodeEntrySheet } from "../components/EntrySheets";
import { SealSheet } from "../components/SealSheet";
import { destinationText } from "../components/destination-text";
import {
  KeyValueRow,
  PACK_COLORS,
  ScanBar,
  SectionTitle,
  Tag,
  UnitRow,
  WarningCard,
  packStyles,
} from "../components/pack-ui";

const palletKey = (palletGuid: string) => ["warehousePacking", "pallet", palletGuid] as const;
/** 托盘标签默认打 2 张，贴相邻两侧。 */
const DEFAULT_PALLET_COPIES = 2;

interface BlockedScan {
  /** managerOnly：已带「仍然加入」再次被拒，说明当前账号不是仓库经理。 */
  reason: PalletBlockReason | "managerOnly";
  code: string;
  carton: PackCartonSummary | null;
}

/** 组托盘：扫箱标签加入；三种拦截当场说明原因；封托并打印托盘标签。已封托时显示补打 / 重新开托 / 放货位。 */
export function PalletScreen({ palletGuid }: { palletGuid: string }) {
  const { t, language } = useAppTranslation("warehousePacking");
  const router = useRouter();
  const focused = useIsFocused();
  const queryClient = useQueryClient();
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const toggleSmallLabel = usePackPreferences((state) => state.toggleSmallLabel);
  const [scanText, setScanText] = useState("");
  const [inputFocused, setInputFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [pending, setPending] = useState(0);
  const [blocked, setBlocked] = useState<BlockedScan | null>(null);
  const [lastAdded, setLastAdded] = useState<string | null>(null);
  const [sheet, setSheet] = useState<"destination" | "seal" | "location" | null>(null);
  const returnToSealRef = useRef(false);
  const [saving, setSaving] = useState(false);
  const [sheetError, setSheetError] = useState("");
  const [snackbar, setSnackbar] = useState("");
  const queueRef = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  const query = useQuery({
    queryKey: palletKey(palletGuid),
    queryFn: ({ signal }) => fetchPallet(palletGuid, signal),
    enabled: focused && Boolean(palletGuid),
    staleTime: 5_000,
  });
  const pallet = query.data ?? null;
  const palletRef = useRef(pallet);
  palletRef.current = pallet;
  const apply = useCallback(
    (next: PackPalletDetail) => {
      palletRef.current = next;
      queryClient.setQueryData(palletKey(palletGuid), next);
      void queryClient.invalidateQueries({ queryKey: ["warehousePacking", "pallets"] });
      void queryClient.invalidateQueries({ queryKey: ["warehousePacking", "cartons"] });
    },
    [palletGuid, queryClient],
  );

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PACKING_HOME));

  // 扫码串行执行：连扫多只箱时不丢码。
  const enqueue = useCallback((task: () => Promise<void>) => {
    setPending((count) => count + 1);
    queueRef.current = queueRef.current
      .then(task)
      .catch(() => undefined)
      .finally(() => setPending((count) => Math.max(0, count - 1)));
  }, []);

  const addCarton = useCallback(
    async (code: string, allowMismatch: boolean) => {
      const before = new Set((palletRef.current?.cartons ?? []).map((carton) => carton.cartonGuid));
      try {
        const next = await addCartonToPallet(palletGuid, code, allowMismatch);
        apply(next);
        setBlocked(null);
        const added = next.cartons.find((carton) => !before.has(carton.cartonGuid));
        if (added) {
          setLastAdded(added.cartonGuid);
          playScanFeedbackSound("added");
        } else {
          // 同一只箱重复扫：后端按成功处理，这里只提示。
          playScanFeedbackSound("found");
          setSnackbar(t("pallet.alreadyOn"));
        }
      } catch (error) {
        const info = readPackingError(error);
        const reason = palletBlockReason(info.code);
        if (reason) {
          playScanFeedbackSound("blocked");
          setBlocked({
            reason: reason === "DESTINATION_MISMATCH" && allowMismatch ? "managerOnly" : reason,
            code,
            carton: info.blockedCarton,
          });
          return;
        }
        playScanFeedbackSound(info.code === "CARTON_NOT_FOUND" ? "not_found" : "error");
        setSnackbar(info.code === "CARTON_NOT_FOUND" ? t("pallet.unknownCarton", { code }) : packingErrorMessage(error, t, language));
      }
    },
    [apply, language, palletGuid, t],
  );

  const handleScan = useCallback(
    (raw: string) => {
      const code = raw.trim();
      if (!code) return;
      setScanText("");
      enqueue(() => addCarton(code, false));
    },
    [addCarton, enqueue],
  );

  const building = pallet?.status === PALLET_STATUS.building;
  const sealed = pallet?.status === PALLET_STATUS.sealed;
  const hid = useHidBarcodeScanner({
    enabled: focused && Boolean(building) && !inputFocused && !cameraVisible && sheet === null,
    onScan: handleScan,
  });

  const runSaving = async (task: () => Promise<void>) => {
    if (saving) return;
    setSaving(true);
    setSheetError("");
    try {
      await task();
    } catch (error) {
      const message = packingErrorMessage(error, t, language);
      setSheetError(message);
      if (sheet === null) setSnackbar(message);
    } finally {
      setSaving(false);
    }
  };

  const closeDestination = () => {
    if (returnToSealRef.current) {
      returnToSealRef.current = false;
      setSheet("seal");
    } else {
      setSheet(null);
    }
  };
  const saveDestination = (value: DestinationFormValue) =>
    runSaving(async () => {
      if (!pallet) return;
      // 托盘头整体覆盖：重量带回现值。
      apply(
        await updatePalletHeader(pallet.palletGuid, {
          ...toDestinationRequest(value),
          consignmentNo: value.consignmentNo.trim() || null,
          weightKg: pallet.weightKg,
          remark: value.remark.trim() || null,
        }),
      );
      closeDestination();
    });

  const confirmSeal = (input: { weightKg: number | null; copies: number; print: boolean }) =>
    runSaving(async () => {
      if (!pallet) return;
      const sealedPallet = await sealPallet(pallet.palletGuid, input.weightKg);
      apply(sealedPallet);
      setSheet(null);
      if (!input.print) {
        setSnackbar(t("seal.pallet.done"));
        return;
      }
      try {
        await printPalletLabel(sealedPallet, input.copies, smallLabel);
        setSnackbar(t("seal.printed", { count: input.copies }));
      } catch (error) {
        // 封托已成功，打印失败不回滚，可在本页补打。
        setSnackbar(t("print.sealedButFailed", { detail: printErrorDetail(error) }));
      }
    });

  const reprint = () =>
    runSaving(async () => {
      if (!pallet) return;
      try {
        await printPalletLabel(pallet, DEFAULT_PALLET_COPIES, smallLabel);
        setSnackbar(t("seal.printed", { count: DEFAULT_PALLET_COPIES }));
      } catch (error) {
        setSnackbar(t("print.failed", { detail: printErrorDetail(error) }));
      }
    });

  const reopen = () =>
    Alert.alert(t("palletSealed.reopenTitle"), t("palletSealed.reopenBody"), [
      { text: t("actions.cancel"), style: "cancel" },
      {
        text: t("palletSealed.reopen"),
        onPress: () => {
          if (!pallet || saving) return;
          setSaving(true);
          reopenPallet(pallet.palletGuid)
            .then(apply)
            .catch((error: unknown) => {
              // 403：只有仓库经理 / 管理员可以重新开托。
              const info = readPackingError(error);
              setSnackbar(
                info.status === 403 || info.code === "PACKING_NOT_ALLOWED"
                  ? t("palletSealed.reopenManagerOnly")
                  : packingErrorMessage(error, t, language),
              );
            })
            .finally(() => setSaving(false));
        },
      },
    ]);

  const removeCarton = (carton: PackCartonSummary) =>
    Alert.alert(t("pallet.removeTitle"), t("pallet.removeBody", { cartonNo: carton.cartonNo }), [
      { text: t("actions.cancel"), style: "cancel" },
      {
        text: t("pallet.remove"),
        style: "destructive",
        onPress: () =>
          enqueue(async () => {
            try {
              apply(await removeCartonFromPallet(palletGuid, carton.cartonGuid));
            } catch (error) {
              setSnackbar(packingErrorMessage(error, t, language));
            }
          }),
      },
    ]);

  const confirmVoid = () =>
    Alert.alert(t("pallet.voidTitle"), t("pallet.voidBody", { palletNo: pallet?.palletNo ?? "" }), [
      { text: t("actions.cancel"), style: "cancel" },
      {
        text: t("pallet.void"),
        style: "destructive",
        onPress: () =>
          enqueue(async () => {
            try {
              apply(await voidPallet(palletGuid));
              goBack();
            } catch (error) {
              setSnackbar(packingErrorMessage(error, t, language));
            }
          }),
      },
    ]);

  const openDestination = (fromSeal: boolean) => {
    returnToSealRef.current = fromSeal;
    setSheetError("");
    setSheet("destination");
  };

  const totalWeight = pallet ? sumCartonWeight(pallet.cartons) : null;
  const summary = pallet
    ? t("counts.palletSummary", { cartons: pallet.cartonCount, sku: pallet.skuCount, pieces: pallet.pieceCount })
    : "";

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader
        title={sealed ? t("palletSealed.title") : t("pallet.title")}
        subtitle={pallet?.palletNo}
        onBack={goBack}
        backLabel={t("actions.back")}
        right={
          pallet ? (
            <Tag
              tone={building ? "blue" : sealed ? "green" : "gray"}
              label={t(`palletState.${building ? "building" : sealed ? "sealed" : "voided"}`)}
            />
          ) : null
        }
      />
      {!pallet ? (
        query.isError ? (
          <EmptyState
            title={packingErrorMessage(query.error, t, language)}
            actionLabel={t("actions.retry")}
            onAction={() => void query.refetch()}
          />
        ) : (
          <ActivityIndicator style={packStyles.loading} />
        )
      ) : !building && !sealed ? (
        <EmptyState title={t("pallet.voided")} actionLabel={t("actions.back")} onAction={goBack} />
      ) : (
        <>
          <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
            <View style={packStyles.cardList}>
              <UnitRow
                icon={isCarrierDestination(pallet.destination) ? "truck-outline" : "storefront-outline"}
                tone="blue"
                title={destinationText(pallet.destination, t)}
                subtitle={
                  [
                    isCarrierDestination(pallet.destination) ? t("pallet.viaCarrier") : t("fields.destination"),
                    pallet.consignmentNo ? t("pallet.consignment", { no: pallet.consignmentNo }) : null,
                    pallet.remark,
                  ]
                    .filter(Boolean)
                    .join(" · ")
                }
                right={
                  <Button compact mode="contained-tonal" onPress={() => openDestination(false)}>
                    {t("pallet.change")}
                  </Button>
                }
              />
            </View>

            {building ? (
              <ScanBar
                value={scanText}
                onChangeText={setScanText}
                onSubmit={handleScan}
                placeholder={t("pallet.scanPlaceholder")}
                inputLabel={t("pallet.scanPlaceholder")}
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
                busy={pending > 0}
              />
            ) : (
              <View style={packStyles.cardList}>
                <KeyValueRow
                  first
                  label={t("fields.location")}
                  value={pallet.locationCode}
                  placeholder={t("sealed.noLocation")}
                  onPress={() => {
                    setSheetError("");
                    setSheet("location");
                  }}
                />
                <KeyValueRow
                  label={t("fields.sealedBy")}
                  value={[pallet.sealedByName, formatLocalTime(pallet.sealedAtUtc)].filter(Boolean).join(" · ") || "—"}
                />
                {pallet.weightKg != null ? (
                  <KeyValueRow label={t("seal.weight")} value={`${formatWeight(pallet.weightKg)} kg`} />
                ) : null}
              </View>
            )}

            {blocked ? (
              <BlockedBanner
                blocked={blocked}
                palletDestination={destinationText(pallet.destination, t)}
                busy={pending > 0}
                onDismiss={() => setBlocked(null)}
                onAddAnyway={() => {
                  const code = blocked.code;
                  enqueue(() => addCarton(code, true));
                }}
                onOpenCarton={(cartonGuid) => router.push(cartonRoute(cartonGuid))}
              />
            ) : null}

            <SectionTitle title={t("pallet.cartons")} right={summary} />
            {pallet.cartons.length === 0 ? (
              <EmptyState title={t("pallet.emptyTitle")} description={t("pallet.emptyHint")} />
            ) : (
              <View style={packStyles.cardList}>
                {pallet.cartons.map((carton, index) => (
                  <UnitRow
                    key={carton.cartonGuid}
                    divider={index > 0}
                    icon="package-variant-closed"
                    tone={carton.cartonGuid === lastAdded ? "green" : "gray"}
                    title={carton.cartonNo}
                    monoTitle
                    subtitle={[
                      t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount }),
                      carton.weightKg != null ? `${formatWeight(carton.weightKg)} kg` : null,
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                    onPress={() => router.push(cartonRoute(carton.cartonGuid))}
                    right={
                      <>
                        {carton.cartonGuid === lastAdded ? <Tag tone="green" icon="check" label={t("pallet.justAdded")} /> : null}
                        {building ? (
                          <Pressable
                            accessibilityRole="button"
                            accessibilityLabel={t("pallet.removeLabel", { cartonNo: carton.cartonNo })}
                            onPress={() => removeCarton(carton)}
                            hitSlop={8}
                            style={styles.removeButton}
                          >
                            <MaterialCommunityIcons name="close" size={20} color={PACK_COLORS.inkMuted} />
                          </Pressable>
                        ) : null}
                      </>
                    }
                  />
                ))}
              </View>
            )}
            {building && pallet.cartons.length === 0 ? (
              <Pressable accessibilityRole="button" onPress={confirmVoid} style={styles.voidLink}>
                <Text style={styles.voidText}>{t("pallet.void")}</Text>
              </Pressable>
            ) : null}
          </ScrollView>

          <View style={packStyles.bar}>
            <View style={packStyles.barRow}>
              <Text style={packStyles.muted}>
                {t("pack.total")}{" "}
                <Text style={packStyles.strong}>
                  {t("counts.cartonPieces", { cartons: pallet.cartonCount, pieces: pallet.pieceCount })}
                </Text>
              </Text>
              {totalWeight != null ? <Text style={packStyles.muted}>{t("pallet.totalWeight", { weight: formatWeight(totalWeight) })}</Text> : null}
            </View>
            {building ? (
              <View style={packStyles.buttons}>
                <Button mode="outlined" onPress={goBack} style={[packStyles.button, packStyles.flexSmall]} contentStyle={packStyles.buttonContent}>
                  {t("pack.leave")}
                </Button>
                <Button
                  mode="contained"
                  icon="printer"
                  disabled={pallet.cartons.length === 0 || pending > 0}
                  onPress={() => {
                    setSheetError("");
                    setSheet("seal");
                  }}
                  style={[packStyles.button, packStyles.flex]}
                  contentStyle={packStyles.buttonContent}
                >
                  {t("pallet.seal")}
                </Button>
              </View>
            ) : (
              <View style={packStyles.buttons}>
                <Button
                  mode="outlined"
                  icon="printer"
                  disabled={saving}
                  onPress={() => void reprint()}
                  style={[packStyles.button, packStyles.flex]}
                  contentStyle={packStyles.buttonContent}
                >
                  {t("sealed.reprint")}
                </Button>
                <Button
                  mode="outlined"
                  icon="lock-open-variant-outline"
                  disabled={saving}
                  onPress={reopen}
                  style={[packStyles.button, packStyles.flex]}
                  contentStyle={packStyles.buttonContent}
                >
                  {t("palletSealed.reopen")}
                </Button>
              </View>
            )}
          </View>

          <DestinationSheet
            visible={sheet === "destination"}
            kind="pallet"
            title={t("destination.palletTitle")}
            confirmLabel={t("actions.confirm")}
            initial={destinationFormOf(pallet.destination, {
              consignmentNo: pallet.consignmentNo ?? "",
              remark: pallet.remark ?? "",
            })}
            saving={saving}
            errorMessage={sheetError}
            onDismiss={closeDestination}
            onConfirm={(value) => void saveDestination(value)}
          />
          <SealSheet
            visible={sheet === "seal"}
            kind="pallet"
            preview={(copies) => palletLabelPreview(buildPalletLabelPayload(pallet, new Date()), copies)}
            destinationText={destinationText(pallet.destination, t)}
            initialWeightKg={pallet.weightKg ?? totalWeight}
            defaultCopies={DEFAULT_PALLET_COPIES}
            smallLabel={smallLabel}
            onToggleSmallLabel={toggleSmallLabel}
            saving={saving}
            errorMessage={sheetError}
            onDismiss={() => setSheet(null)}
            onChangeDestination={() => openDestination(true)}
            onConfirm={(input) => void confirmSeal(input)}
          />
          <CodeEntrySheet
            visible={sheet === "location"}
            title={t("palletSealed.putToLocation")}
            subtitle={t("sealed.locationSheetHint")}
            placeholder={t("sealed.locationPlaceholder")}
            confirmLabel={t("actions.confirm")}
            saving={saving}
            errorMessage={sheetError}
            onDismiss={() => setSheet(null)}
            onSubmit={(code) =>
              void runSaving(async () => {
                const next = await setPalletLocation(pallet.palletGuid, code);
                apply(next);
                setSheet(null);
                setSnackbar(t("sealed.locationSaved", { location: next.locationCode ?? code }));
              })
            }
          />
        </>
      )}

      <PickCameraSheet
        visible={cameraVisible}
        title={t("pallet.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={handleScan}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3500}>
        {snackbar}
      </Snackbar>
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={packStyles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

/** 加入托盘被拦下：用后端带回的那只箱说明原因；去向不一致可「仍然加入」，非经理会再次被拒并照实提示。 */
function BlockedBanner({
  blocked,
  palletDestination,
  busy,
  onDismiss,
  onAddAnyway,
  onOpenCarton,
}: {
  blocked: BlockedScan;
  palletDestination: string;
  busy: boolean;
  onDismiss: () => void;
  onAddAnyway: () => void;
  onOpenCarton: (cartonGuid: string) => void;
}) {
  const { t } = useAppTranslation("warehousePacking");
  const carton = blocked.carton;
  const cartonNo = carton?.cartonNo ?? blocked.code.toUpperCase();
  const message =
    blocked.reason === "CARTON_NOT_SEALED"
      ? t("pallet.blockedNotSealed", { cartonNo })
      : blocked.reason === "CARTON_ON_OTHER_PALLET"
        ? t("pallet.blockedOtherPallet", { cartonNo, palletNo: carton?.palletNo ?? "—" })
        : blocked.reason === "managerOnly"
          ? t("pallet.blockedManagerOnly", { cartonNo })
          : t("pallet.blockedMismatch", {
              cartonNo,
              cartonDestination: carton ? destinationText(carton.destination, t) : "—",
              palletDestination,
            });
  return (
    <WarningCard>
      <View style={styles.bannerRow} accessibilityLiveRegion="assertive">
        <MaterialCommunityIcons name="alert-outline" size={18} color={PACK_COLORS.warning} />
        <Text style={styles.bannerText}>{message}</Text>
      </View>
      <View style={packStyles.buttons}>
        <Button compact mode="outlined" onPress={onDismiss} style={packStyles.flex}>
          {blocked.reason === "DESTINATION_MISMATCH" ? t("pallet.skip") : t("pallet.dismiss")}
        </Button>
        {blocked.reason === "DESTINATION_MISMATCH" ? (
          <Button compact mode="outlined" textColor={PACK_COLORS.warning} disabled={busy} onPress={onAddAnyway} style={packStyles.flex}>
            {t("pallet.addAnywayManager")}
          </Button>
        ) : null}
        {blocked.reason === "CARTON_NOT_SEALED" && carton ? (
          <Button compact mode="outlined" onPress={() => onOpenCarton(carton.cartonGuid)} style={packStyles.flex}>
            {t("pallet.openCarton")}
          </Button>
        ) : null}
      </View>
    </WarningCard>
  );
}

const styles = StyleSheet.create({
  removeButton: { width: 36, height: 36, alignItems: "center", justifyContent: "center" },
  bannerRow: { flexDirection: "row", alignItems: "flex-start", gap: 8 },
  bannerText: { flex: 1, fontSize: 13, lineHeight: 19, color: PACK_COLORS.warningText },
  voidLink: { minHeight: 44, alignItems: "center", justifyContent: "center" },
  voidText: { fontSize: 13, color: PACK_COLORS.danger },
});
