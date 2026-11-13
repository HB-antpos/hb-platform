import { useCallback, useState } from "react";
import { Alert, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound } from "@/modules/scanner/scan-sound";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  addCartonToPallet,
  createCarton,
  createPallet,
  fetchPallets,
  reopenCarton,
  resolvePackCode,
  setCartonLocation,
} from "../api";
import { readPackingError } from "../api-normalization";
import { packingErrorMessage, printErrorDetail } from "../packing-errors";
import { destinationRequestOf, formatLocalTime, looksLikePackCode } from "../packing-logic";
import { usePackPreferences } from "../pack-preferences";
import { cartonRoute, palletRoute, printCartonLabel } from "../pack-print";
import type { PackCartonDetail } from "../types";
import { CodeEntrySheet } from "../components/EntrySheets";
import { destinationText } from "../components/destination-text";
import { Chevron, KeyValueRow, PACK_COLORS, SectionTitle, UnitRow, packStyles } from "../components/pack-ui";

/**
 * 封箱完成：继续装下一箱（沿用去向和订货单）、放上托盘、放到货位暂存、补打标签、重新开箱（仅经理）。
 * 设计稿里的「散箱直接发运」属于装车发运，本期不做。
 */
export function CartonSealedView({
  carton,
  focused,
  notice,
  onApply,
  onReload,
  onEditDestination,
  onMessage,
}: {
  carton: PackCartonDetail;
  focused: boolean;
  /** 刚封箱时的打印结果提示；从列表进入已封的箱时为空。 */
  notice: string | null;
  onApply: (carton: PackCartonDetail) => void;
  onReload: () => void;
  onEditDestination: () => void;
  onMessage: (message: string) => void;
}) {
  const { t, language } = useAppTranslation("warehousePacking");
  const router = useRouter();
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const [sheet, setSheet] = useState<"pallet" | "location" | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [sheetError, setSheetError] = useState("");
  const onPallet = Boolean(carton.palletGuid);

  const buildingPallets = useQuery({
    queryKey: ["warehousePacking", "pallets", "building", ""],
    queryFn: ({ signal }) => fetchPallets("building", "", signal),
    enabled: sheet === "pallet",
    staleTime: 10_000,
  });

  const run = async (key: string, task: () => Promise<void>) => {
    if (busy) return;
    setBusy(key);
    try {
      await task();
    } finally {
      setBusy(null);
    }
  };

  /** 把这只箱放上指定托盘；去向不一致时先问，经理才能放行。 */
  const putOnPallet = useCallback(
    async (palletGuid: string, palletNo: string, allowMismatch = false): Promise<void> => {
      try {
        await addCartonToPallet(palletGuid, carton.cartonNo, allowMismatch);
        playScanFeedbackSound("added");
        setSheet(null);
        onMessage(t("sealed.addedToPallet", { palletNo }));
        onReload();
      } catch (error) {
        playScanFeedbackSound("blocked");
        const info = readPackingError(error);
        if (info.code === "DESTINATION_MISMATCH" && !allowMismatch) {
          Alert.alert(t("pallet.mismatchTitle"), t("sealed.mismatchBody", { palletNo }), [
            { text: t("pallet.skip"), style: "cancel" },
            { text: t("pallet.addAnyway"), onPress: () => void putOnPallet(palletGuid, palletNo, true) },
          ]);
          return;
        }
        const message =
          info.code === "DESTINATION_MISMATCH" ? t("pallet.managerOnly") : packingErrorMessage(error, t, language);
        setSheetError(message);
        onMessage(message);
      }
    },
    [carton.cartonNo, language, onMessage, onReload, t],
  );

  const submitPalletCode = (code: string) =>
    run("pallet", async () => {
      setSheetError("");
      try {
        const resolved = await resolvePackCode(code);
        if (resolved.kind !== "pallet") {
          setSheetError(t("sealed.notPallet"));
          return;
        }
        await putOnPallet(resolved.guid, resolved.no);
      } catch (error) {
        setSheetError(
          readPackingError(error).code === "CODE_NOT_FOUND" ? t("sealed.notPallet") : packingErrorMessage(error, t, language),
        );
      }
    });

  const createPalletAndAdd = () =>
    run("newPallet", async () => {
      setSheetError("");
      try {
        // 新托盘沿用这只箱的去向，直接放上去并进入组托盘页继续扫别的箱。
        const pallet = await createPallet(destinationRequestOf(carton.destination));
        await addCartonToPallet(pallet.palletGuid, carton.cartonNo);
        setSheet(null);
        onReload();
        router.push(palletRoute(pallet.palletGuid));
      } catch (error) {
        setSheetError(packingErrorMessage(error, t, language));
      }
    });

  const submitLocation = (code: string) =>
    run("location", async () => {
      setSheetError("");
      try {
        const updated = await setCartonLocation(carton.cartonGuid, code);
        onApply(updated);
        setSheet(null);
        onMessage(t("sealed.locationSaved", { location: updated.locationCode ?? code }));
      } catch (error) {
        setSheetError(packingErrorMessage(error, t, language));
      }
    });

  const continueNext = () =>
    run("next", async () => {
      try {
        const next = await createCarton({ ...destinationRequestOf(carton.destination), orderGuid: carton.orderGuid });
        router.replace(cartonRoute(next.cartonGuid));
      } catch (error) {
        onMessage(packingErrorMessage(error, t, language));
      }
    });

  const reprint = () =>
    run("print", async () => {
      try {
        await printCartonLabel(carton, 1, smallLabel);
        onMessage(t("sealed.reprinted"));
      } catch (error) {
        onMessage(t("print.failed", { detail: printErrorDetail(error) }));
      }
    });

  const reopen = () =>
    Alert.alert(t("sealed.reopenTitle"), t("sealed.reopenBody"), [
      { text: t("actions.cancel"), style: "cancel" },
      {
        text: t("sealed.reopen"),
        onPress: () =>
          void run("reopen", async () => {
            try {
              onApply(await reopenCarton(carton.cartonGuid));
            } catch (error) {
              // 403：只有仓库经理 / 管理员可以重新开箱。
              const info = readPackingError(error);
              onMessage(
                info.status === 403 || info.code === "PACKING_NOT_ALLOWED"
                  ? t("sealed.reopenManagerOnly")
                  : packingErrorMessage(error, t, language),
              );
            }
          }),
      },
    ]);

  // 页面上直接扫：托盘标签 = 放上那个托盘；箱标签 = 打开那只箱；货位码需要先点「放到货位暂存」。
  const handleScan = useCallback(
    async (raw: string) => {
      const code = raw.trim();
      if (!code) return;
      const kind = looksLikePackCode(code);
      if (!kind) {
        playScanFeedbackSound("not_found");
        onMessage(t("sealed.scanHint"));
        return;
      }
      try {
        const resolved = await resolvePackCode(code);
        if (resolved.kind === "carton") {
          playScanFeedbackSound("found");
          if (resolved.guid !== carton.cartonGuid) router.push(cartonRoute(resolved.guid));
          return;
        }
        if (onPallet) {
          playScanFeedbackSound("blocked");
          onMessage(t("errors.CARTON_ON_OTHER_PALLET"));
          return;
        }
        await putOnPallet(resolved.guid, resolved.no);
      } catch (error) {
        playScanFeedbackSound("not_found");
        onMessage(packingErrorMessage(error, t, language));
      }
    },
    [carton.cartonGuid, language, onMessage, onPallet, putOnPallet, router, t],
  );
  const hid = useHidBarcodeScanner({ enabled: focused && sheet === null && !busy, onScan: handleScan });

  const sealedAt = formatLocalTime(carton.sealedAtUtc);
  const where = onPallet
    ? t("sealed.onPallet", { palletNo: carton.palletNo ?? "" })
    : carton.locationCode || t("sealed.noLocation");

  return (
    <>
      <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
        <View style={styles.hero}>
          <View style={styles.check}>
            <MaterialCommunityIcons name="check" size={30} color={PACK_COLORS.success} />
          </View>
          <Text style={styles.cartonNo}>{carton.cartonNo}</Text>
          <Text style={styles.heroSub}>
            {t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount })}
            {notice ? ` · ${notice}` : ""}
          </Text>
        </View>

        <View style={packStyles.cardList}>
          <KeyValueRow
            first
            label={t("fields.destination")}
            value={destinationText(carton.destination, t)}
            onPress={onPallet ? undefined : onEditDestination}
          />
          <KeyValueRow
            label={t("fields.location")}
            value={where}
            onPress={onPallet && carton.palletGuid ? () => router.push(palletRoute(carton.palletGuid as string)) : undefined}
          />
          {carton.orderNo ? <KeyValueRow label={t("fields.order")} value={carton.orderNo} /> : null}
          <KeyValueRow
            label={t("fields.sealedBy")}
            value={[carton.sealedByName, sealedAt].filter(Boolean).join(" · ") || "—"}
          />
        </View>

        <SectionTitle title={t("sealed.next")} />
        <View style={packStyles.cardList}>
          <UnitRow
            icon="package-variant"
            tone="blue"
            title={t("sealed.continueNext")}
            subtitle={
              carton.orderNo
                ? t("sealed.continueNextWithOrder", { destination: destinationText(carton.destination, t), orderNo: carton.orderNo })
                : t("sealed.continueNextHint", { destination: destinationText(carton.destination, t) })
            }
            right={<Chevron />}
            onPress={() => void continueNext()}
          />
          {onPallet ? null : (
            <>
              <UnitRow
                divider
                icon="layers-triple-outline"
                tone="blue"
                title={t("sealed.putOnPallet")}
                subtitle={t("sealed.putOnPalletHint")}
                right={<Chevron />}
                onPress={() => {
                  setSheetError("");
                  setSheet("pallet");
                }}
              />
              <UnitRow
                divider
                icon="map-marker-outline"
                title={t("sealed.putToLocation")}
                subtitle={t("sealed.putToLocationHint")}
                right={<Chevron />}
                onPress={() => {
                  setSheetError("");
                  setSheet("location");
                }}
              />
            </>
          )}
        </View>

        <View style={[packStyles.buttons, styles.bottomButtons]}>
          <Button
            mode="outlined"
            icon="printer"
            loading={busy === "print"}
            disabled={Boolean(busy)}
            onPress={() => void reprint()}
            style={[packStyles.button, packStyles.flex]}
            contentStyle={packStyles.buttonContent}
          >
            {t("sealed.reprint")}
          </Button>
          <Button
            mode="outlined"
            icon="lock-open-variant-outline"
            loading={busy === "reopen"}
            disabled={Boolean(busy)}
            onPress={reopen}
            style={[packStyles.button, packStyles.flex]}
            contentStyle={packStyles.buttonContent}
          >
            {t("sealed.reopen")}
          </Button>
        </View>
      </ScrollView>

      <CodeEntrySheet
        visible={sheet === "pallet"}
        title={t("sealed.putOnPallet")}
        subtitle={t("sealed.palletSheetHint")}
        placeholder={t("sealed.palletPlaceholder")}
        confirmLabel={t("sealed.putOnPalletConfirm")}
        saving={busy === "pallet" || busy === "newPallet"}
        errorMessage={sheetError}
        onDismiss={() => setSheet(null)}
        onSubmit={(code) => void submitPalletCode(code)}
      >
        <Button
          mode="contained-tonal"
          icon="plus"
          disabled={Boolean(busy)}
          onPress={() => void createPalletAndAdd()}
          style={packStyles.button}
          contentStyle={packStyles.buttonContent}
        >
          {t("sealed.newPalletAndAdd")}
        </Button>
        {(buildingPallets.data?.items.length ?? 0) > 0 ? (
          <>
            <SectionTitle title={t("sealed.buildingPallets")} />
            <View style={packStyles.cardList}>
              {(buildingPallets.data?.items ?? []).slice(0, 10).map((pallet, index) => (
                <UnitRow
                  key={pallet.palletGuid}
                  divider={index > 0}
                  icon="layers-triple-outline"
                  tone="blue"
                  title={pallet.palletNo}
                  monoTitle
                  subtitle={`${t("counts.cartonPieces", { cartons: pallet.cartonCount, pieces: pallet.pieceCount })} · ${destinationText(pallet.destination, t)}`}
                  right={<Chevron />}
                  onPress={() =>
                    void run("pallet", async () => {
                      setSheetError("");
                      await putOnPallet(pallet.palletGuid, pallet.palletNo);
                    })
                  }
                />
              ))}
            </View>
          </>
        ) : null}
      </CodeEntrySheet>
      <CodeEntrySheet
        visible={sheet === "location"}
        title={t("sealed.putToLocation")}
        subtitle={t("sealed.locationSheetHint")}
        placeholder={t("sealed.locationPlaceholder")}
        confirmLabel={t("actions.confirm")}
        saving={busy === "location"}
        errorMessage={sheetError}
        onDismiss={() => setSheet(null)}
        onSubmit={(code) => void submitLocation(code)}
      />
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={packStyles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  hero: { alignItems: "center", gap: 4, paddingVertical: 12 },
  check: { width: 56, height: 56, borderRadius: 28, backgroundColor: PACK_COLORS.successBg, alignItems: "center", justifyContent: "center" },
  cartonNo: { fontFamily: "monospace", fontSize: 20, lineHeight: 28, fontWeight: "700", color: PACK_COLORS.ink, marginTop: 8 },
  heroSub: { fontSize: 13, lineHeight: 18, color: PACK_COLORS.textSecondary, textAlign: "center" },
  bottomButtons: { marginTop: "auto", paddingTop: 8 },
});
