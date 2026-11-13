import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { ScanResultPicker } from "@/components/ui/ScanResultPicker";
import { PickCameraSheet } from "@/modules/warehouse-picking/components/PickCameraSheet";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { ProductThumb } from "@/modules/warehouse-picking/components/ProductThumb";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import type { StoreOrderProductItem } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  fetchCarton,
  scanIntoCarton,
  sealCarton,
  setCartonItemQuantity,
  updateCartonHeader,
  voidCarton,
} from "../api";
import { readPackingError } from "../api-normalization";
import { packingErrorMessage, printErrorDetail } from "../packing-errors";
import {
  buildCartonLabelPayload,
  cartonLabelPreview,
  destinationFormOf,
  findCartonItemByCode,
  formatLocalTime,
  middlePackOf,
  orderCartonItems,
  orderProgress,
  packTopUp,
  resolveScanQuantity,
  toDestinationRequest,
} from "../packing-logic";
import { usePackPreferences } from "../pack-preferences";
import { PACKING_HOME, printCartonLabel } from "../pack-print";
import {
  CARTON_STATUS,
  type DestinationFormValue,
  type PackCartonDetail,
  type PackCartonItem,
  type PackProductCandidate,
  type ScanCountMode,
} from "../types";
import { DestinationSheet } from "../components/DestinationSheet";
import { QuantitySheet } from "../components/EntrySheets";
import { SealSheet } from "../components/SealSheet";
import { destinationText } from "../components/destination-text";
import { ChipRow, PACK_COLORS, ScanBar, SectionTitle, Stepper, Tag, packStyles } from "../components/pack-ui";
import { CartonSealedView } from "./CartonSealedView";

const cartonKey = (cartonGuid: string) => ["warehousePacking", "carton", cartonGuid] as const;

/** 箱页：装箱中显示扫码装箱，已封箱显示封箱完成与下一步。箱号不变，两个状态共用一个路由。 */
export function CartonScreen({ cartonGuid }: { cartonGuid: string }) {
  const { t, language } = useAppTranslation("warehousePacking");
  const router = useRouter();
  const focused = useIsFocused();
  const queryClient = useQueryClient();
  const [snackbar, setSnackbar] = useState("");
  const [destinationVisible, setDestinationVisible] = useState(false);
  const [destinationSaving, setDestinationSaving] = useState(false);
  const [destinationError, setDestinationError] = useState("");
  // 从封箱弹层点「去向」进来时，改完要回到封箱弹层。
  const [sealVisible, setSealVisible] = useState(false);
  const returnToSealRef = useRef(false);
  const [sealNotice, setSealNotice] = useState<string | null>(null);

  const query = useQuery({
    queryKey: cartonKey(cartonGuid),
    queryFn: ({ signal }) => fetchCarton(cartonGuid, signal),
    enabled: focused && Boolean(cartonGuid),
    staleTime: 5_000,
  });
  const carton = query.data ?? null;
  const apply = useCallback(
    (next: PackCartonDetail) => {
      queryClient.setQueryData(cartonKey(cartonGuid), next);
      // 首页列表与计数跟着变。
      void queryClient.invalidateQueries({ queryKey: ["warehousePacking", "cartons"] });
    },
    [cartonGuid, queryClient],
  );

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PACKING_HOME));

  const closeDestination = () => {
    setDestinationVisible(false);
    if (returnToSealRef.current) {
      returnToSealRef.current = false;
      setSealVisible(true);
    }
  };
  const saveDestination = async (value: DestinationFormValue) => {
    if (!carton || destinationSaving) return;
    setDestinationSaving(true);
    setDestinationError("");
    try {
      // 箱头整体覆盖：重量、备注带回现值，避免改去向时被清掉。
      apply(
        await updateCartonHeader(carton.cartonGuid, {
          ...toDestinationRequest(value),
          orderGuid: value.orderGuid,
          weightKg: carton.weightKg,
          remark: carton.remark,
        }),
      );
      closeDestination();
    } catch (error) {
      setDestinationError(packingErrorMessage(error, t, language));
    } finally {
      setDestinationSaving(false);
    }
  };

  const packing = carton?.status === CARTON_STATUS.packing;
  const sealed = carton?.status === CARTON_STATUS.sealed;

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader
        title={sealed ? t("sealed.title") : t("pack.title")}
        subtitle={carton?.cartonNo}
        onBack={goBack}
        backLabel={t("actions.back")}
        right={
          carton ? (
            <Tag
              tone={packing ? "blue" : sealed ? "green" : "gray"}
              label={t(`cartonState.${packing ? "packing" : sealed ? (carton.palletGuid ? "onPallet" : "sealed") : "voided"}`)}
            />
          ) : null
        }
      />
      {!carton ? (
        query.isError ? (
          <EmptyState
            title={packingErrorMessage(query.error, t, language)}
            actionLabel={t("actions.retry")}
            onAction={() => void query.refetch()}
          />
        ) : (
          <ActivityIndicator style={packStyles.loading} />
        )
      ) : packing ? (
        <CartonPackingView
          carton={carton}
          focused={focused && !destinationVisible}
          sealVisible={sealVisible}
          onSealVisibleChange={setSealVisible}
          onApply={apply}
          onMessage={setSnackbar}
          onEditDestination={(fromSeal) => {
            returnToSealRef.current = fromSeal;
            if (fromSeal) setSealVisible(false);
            setDestinationError("");
            setDestinationVisible(true);
          }}
          onSealed={setSealNotice}
          onLeave={goBack}
        />
      ) : sealed ? (
        <CartonSealedView
          carton={carton}
          focused={focused && !destinationVisible}
          notice={sealNotice}
          onApply={(next) => {
            setSealNotice(null);
            apply(next);
          }}
          onReload={() => void query.refetch()}
          onEditDestination={() => {
            returnToSealRef.current = false;
            setDestinationError("");
            setDestinationVisible(true);
          }}
          onMessage={setSnackbar}
        />
      ) : (
        <EmptyState title={t("pack.voided")} actionLabel={t("actions.back")} onAction={goBack} />
      )}

      {carton ? (
        <DestinationSheet
          visible={destinationVisible}
          kind="carton"
          title={t("destination.cartonTitle")}
          confirmLabel={t("actions.confirm")}
          initial={destinationFormOf(carton.destination, { orderGuid: carton.orderGuid, orderNo: carton.orderNo })}
          // 已封箱的箱不能更换关联订货单（后端会拒绝），只让改去向。
          showOrder={packing}
          saving={destinationSaving}
          errorMessage={destinationError}
          onDismiss={closeDestination}
          onConfirm={(value) => void saveDestination(value)}
        />
      ) : null}
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3500}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

interface AmbiguousScan {
  code: string;
  mode: ScanCountMode;
  asked: number | null;
  candidates: PackProductCandidate[];
}

/**
 * 装箱扫码：PDA 扫码枪 + 相机；三种计数方式；刚扫入的商品置顶高亮并可加减 / 输入数量。
 * 每次扫码即时保存到服务端，换人换设备可以接着装。
 */
function CartonPackingView({
  carton,
  focused,
  sealVisible,
  onSealVisibleChange,
  onApply,
  onMessage,
  onEditDestination,
  onSealed,
  onLeave,
}: {
  carton: PackCartonDetail;
  focused: boolean;
  sealVisible: boolean;
  onSealVisibleChange: (visible: boolean) => void;
  onApply: (carton: PackCartonDetail) => void;
  onMessage: (message: string) => void;
  onEditDestination: (fromSeal: boolean) => void;
  onSealed: (notice: string | null) => void;
  onLeave: () => void;
}) {
  const { t, language } = useAppTranslation("warehousePacking");
  const mode = usePackPreferences((state) => state.scanMode);
  const setMode = usePackPreferences((state) => state.setScanMode);
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const toggleSmallLabel = usePackPreferences((state) => state.toggleSmallLabel);
  const [scanText, setScanText] = useState("");
  const [inputFocused, setInputFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [highlightCode, setHighlightCode] = useState<string | null>(carton.changedProductCode);
  const [justScanned, setJustScanned] = useState(false);
  const [pending, setPending] = useState(0);
  const [askScan, setAskScan] = useState<{ code: string } | null>(null);
  const [editItem, setEditItem] = useState<PackCartonItem | null>(null);
  const [ambiguous, setAmbiguous] = useState<AmbiguousScan | null>(null);
  const [sealing, setSealing] = useState(false);
  const [sealError, setSealError] = useState("");
  const cartonRef = useRef(carton);
  cartonRef.current = carton;
  // 扫码和改数量串行执行：扫码枪连扫时不丢码，也不会用过期数量互相覆盖。
  const queueRef = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  const enqueue = useCallback((task: () => Promise<void>) => {
    setPending((count) => count + 1);
    queueRef.current = queueRef.current
      .then(task)
      .catch(() => undefined)
      .finally(() => setPending((count) => Math.max(0, count - 1)));
  }, []);

  const failScan = useCallback(
    (error: unknown) => {
      const info = readPackingError(error);
      playScanFeedbackSound(info.code === "CODE_NOT_FOUND" ? "not_found" : "error");
      onMessage(packingErrorMessage(error, t, language));
    },
    [language, onMessage, t],
  );

  /** 提交一次扫码；productCode 有值表示一码多品时用户已选定商品。 */
  const submitScan = useCallback(
    async (input: { code: string; productCode?: string | null; mode: ScanCountMode; asked?: number | null }) => {
      const current = cartonRef.current;
      const known = input.productCode
        ? current.items.find((item) => item.productCode === input.productCode) ?? null
        : findCartonItemByCode(current.items, input.code);
      const quantity = resolveScanQuantity(input.mode, known, input.asked);
      try {
        let detail = await scanIntoCarton(current.cartonGuid, {
          code: input.productCode ? null : input.code,
          productCode: input.productCode ?? null,
          quantity,
        });
        if (input.mode === "pack" && !middlePackOf(known)) {
          // 商品第一次进这只箱：扫码前不知道中包数，先加 1 件拿到资料，再把这一行补到整中包。
          const changed = detail.items.find((item) => item.productCode === detail.changedProductCode);
          const target = packTopUp(changed, quantity);
          if (changed && target != null) {
            detail = await setCartonItemQuantity(current.cartonGuid, changed.productCode, target);
          }
        }
        cartonRef.current = detail;
        onApply(detail);
        setHighlightCode(detail.changedProductCode);
        setJustScanned(true);
        const changed = detail.items.find((item) => item.productCode === detail.changedProductCode);
        if (detail.orderGuid && changed && changed.orderQuantity == null) {
          // 订货单外的商品：照常装入，只提示。
          playScanFeedbackSound("blocked");
          onMessage(t("pack.outsideOrderNotice"));
        } else {
          playScanFeedbackSound("added");
        }
      } catch (error) {
        const info = readPackingError(error);
        if (info.code === "CODE_AMBIGUOUS" && info.candidates.length > 0 && !input.productCode) {
          playScanFeedbackSound("multiple");
          setAmbiguous({ code: input.code, mode: input.mode, asked: input.asked ?? null, candidates: info.candidates });
          return;
        }
        failScan(error);
      }
    },
    [failScan, onApply, onMessage, t],
  );

  const handleScan = useCallback(
    (raw: string) => {
      const code = raw.trim();
      if (!code) return;
      setScanText("");
      if (mode === "ask") {
        // 扫后输数量：先弹数量，再一次提交。
        setAskScan({ code });
        return;
      }
      enqueue(() => submitScan({ code, mode }));
    },
    [enqueue, mode, submitScan],
  );

  const setQuantity = useCallback(
    (item: PackCartonItem, quantity: number) => {
      enqueue(async () => {
        try {
          const detail = await setCartonItemQuantity(cartonRef.current.cartonGuid, item.productCode, quantity);
          cartonRef.current = detail;
          onApply(detail);
          setHighlightCode(quantity > 0 ? item.productCode : null);
          setJustScanned(false);
        } catch (error) {
          failScan(error);
        }
      });
    },
    [enqueue, failScan, onApply],
  );

  const changeBy = (item: PackCartonItem, delta: number) => {
    // 以服务端最新数量为准：连续点击时前一次还没返回也不会少算。
    const latest = cartonRef.current.items.find((entry) => entry.productCode === item.productCode) ?? item;
    const next = latest.quantity + delta;
    if (next <= 0) {
      Alert.alert(t("pack.removeTitle"), t("pack.removeBody", { name: item.productName || item.itemNumber || item.productCode }), [
        { text: t("actions.cancel"), style: "cancel" },
        { text: t("pack.remove"), style: "destructive", onPress: () => setQuantity(item, 0) },
      ]);
      return;
    }
    setQuantity(item, next);
  };

  const confirmVoid = () =>
    Alert.alert(t("pack.voidTitle"), t("pack.voidBody", { cartonNo: carton.cartonNo }), [
      { text: t("actions.cancel"), style: "cancel" },
      {
        text: t("pack.void"),
        style: "destructive",
        onPress: () =>
          enqueue(async () => {
            try {
              onApply(await voidCarton(carton.cartonGuid));
              onLeave();
            } catch (error) {
              onMessage(packingErrorMessage(error, t, language));
            }
          }),
      },
    ]);

  const confirmSeal = async (input: { weightKg: number | null; copies: number; print: boolean }) => {
    if (sealing) return;
    setSealing(true);
    setSealError("");
    try {
      const sealedCarton = await sealCarton(carton.cartonGuid, input.weightKg);
      let notice: string | null = t("seal.notPrinted");
      if (input.print) {
        try {
          await printCartonLabel(sealedCarton, input.copies, smallLabel);
          notice = t("seal.printed", { count: input.copies });
        } catch (error) {
          // 封箱已成功，打印失败不回滚：在完成页提示并可补打。
          notice = t("seal.printFailedShort");
          onMessage(t("print.sealedButFailed", { detail: printErrorDetail(error) }));
        }
      }
      onSealVisibleChange(false);
      onSealed(notice);
      onApply(sealedCarton);
    } catch (error) {
      setSealError(packingErrorMessage(error, t, language));
    } finally {
      setSealing(false);
    }
  };

  const sheetsOpen = sealVisible || Boolean(askScan) || Boolean(editItem) || Boolean(ambiguous) || cameraVisible;
  const hid = useHidBarcodeScanner({ enabled: focused && !inputFocused && !sheetsOpen, onScan: handleScan });

  const { highlighted, rest } = orderCartonItems(carton.items, highlightCode);
  const totals = t("counts.skuPieces", { sku: carton.skuCount, pieces: carton.pieceCount });
  const scopeText = [
    destinationText(carton.destination, t),
    carton.orderNo ? t("pack.orderScope", { orderNo: carton.orderNo }) : null,
  ]
    .filter(Boolean)
    .join(" · ");
  const askKnown = askScan ? findCartonItemByCode(carton.items, askScan.code) : null;

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${t("fields.destination")} ${scopeText}`}
        onPress={() => onEditDestination(false)}
        style={styles.scope}
      >
        <MaterialCommunityIcons name="map-marker-outline" size={16} color={PACK_COLORS.textSecondary} />
        <Text numberOfLines={1} style={styles.scopeText}>
          {scopeText}
        </Text>
        <MaterialCommunityIcons name="chevron-right" size={18} color={PACK_COLORS.inkMuted} />
      </Pressable>

      <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
        <ScanBar
          value={scanText}
          onChangeText={setScanText}
          onSubmit={handleScan}
          placeholder={t("pack.scanPlaceholder")}
          inputLabel={t("pack.scanPlaceholder")}
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
        <ChipRow
          label={t("pack.modeLabel")}
          value={mode}
          onChange={setMode}
          options={[
            { key: "each", label: t("pack.modeEach") },
            {
              key: "pack",
              label: middlePackOf(highlighted)
                ? t("pack.modePackWith", { count: middlePackOf(highlighted) })
                : t("pack.modePack"),
            },
            { key: "ask", label: t("pack.modeAsk") },
          ]}
        />

        {highlighted ? (
          <HighlightCard
            item={highlighted}
            justScanned={justScanned}
            linked={Boolean(carton.orderGuid)}
            onDecrease={() => changeBy(highlighted, -1)}
            onIncrease={() => changeBy(highlighted, 1)}
            onPressValue={() => setEditItem(highlighted)}
          />
        ) : null}

        <SectionTitle title={t("pack.items")} right={totals} />
        {carton.items.length === 0 ? (
          <EmptyState title={t("pack.emptyTitle")} description={t("pack.emptyHint")} />
        ) : rest.length > 0 ? (
          <View style={packStyles.cardList}>
            {rest.map((item, index) => (
              <ItemRow
                key={item.productCode}
                item={item}
                linked={Boolean(carton.orderGuid)}
                divider={index > 0}
                onPress={() => {
                  setHighlightCode(item.productCode);
                  setJustScanned(false);
                }}
              />
            ))}
          </View>
        ) : null}
        <Pressable accessibilityRole="button" onPress={confirmVoid} style={styles.voidLink}>
          <Text style={styles.voidText}>{t("pack.void")}</Text>
        </Pressable>
      </ScrollView>

      <View style={packStyles.bar}>
        <View style={packStyles.barRow}>
          <Text style={packStyles.muted}>
            {t("pack.total")} <Text style={packStyles.strong}>{totals}</Text>
          </Text>
          <Text style={packStyles.muted}>{t("pack.savedAt", { time: formatLocalTime(carton.updatedAtUtc) })}</Text>
        </View>
        <View style={packStyles.buttons}>
          <Button mode="outlined" onPress={onLeave} style={[packStyles.button, packStyles.flexSmall]} contentStyle={packStyles.buttonContent}>
            {t("pack.leave")}
          </Button>
          <Button
            mode="contained"
            icon="printer"
            disabled={carton.items.length === 0 || pending > 0}
            onPress={() => onSealVisibleChange(true)}
            style={[packStyles.button, packStyles.flex]}
            contentStyle={packStyles.buttonContent}
          >
            {t("pack.seal")}
          </Button>
        </View>
      </View>

      <SealSheet
        visible={sealVisible}
        kind="carton"
        preview={() => cartonLabelPreview(buildCartonLabelPayload(carton, new Date()))}
        destinationText={destinationText(carton.destination, t)}
        initialWeightKg={carton.weightKg}
        defaultCopies={1}
        smallLabel={smallLabel}
        onToggleSmallLabel={toggleSmallLabel}
        saving={sealing}
        errorMessage={sealError}
        onDismiss={() => onSealVisibleChange(false)}
        onChangeDestination={() => onEditDestination(true)}
        onConfirm={(input) => void confirmSeal(input)}
      />
      <QuantitySheet
        visible={Boolean(askScan)}
        title={t("quantity.askTitle")}
        productName={askKnown?.productName ?? askScan?.code}
        hint={middlePackOf(askKnown) ? t("pack.packInfo", { count: middlePackOf(askKnown) }) : null}
        initial={1}
        allowZero={false}
        saving={false}
        onDismiss={() => setAskScan(null)}
        onConfirm={(quantity) => {
          const code = askScan?.code;
          setAskScan(null);
          if (code) enqueue(() => submitScan({ code, mode: "ask", asked: quantity }));
        }}
      />
      <QuantitySheet
        visible={Boolean(editItem)}
        title={t("quantity.editTitle")}
        productName={editItem?.productName ?? editItem?.itemNumber}
        hint={t("quantity.editHint")}
        initial={editItem?.quantity ?? 0}
        allowZero
        saving={false}
        onDismiss={() => setEditItem(null)}
        onConfirm={(quantity) => {
          const item = editItem;
          setEditItem(null);
          if (item) setQuantity(item, quantity);
        }}
      />
      <ScanResultPicker
        visible={Boolean(ambiguous)}
        barcode={ambiguous?.code}
        hideOrderMeta
        title={t("pack.ambiguousTitle")}
        tip={t("pack.ambiguousTip", { code: ambiguous?.code ?? "" })}
        items={(ambiguous?.candidates ?? []).map(toPickerItem)}
        onDismiss={() => setAmbiguous(null)}
        onSelect={(product) => {
          const scan = ambiguous;
          setAmbiguous(null);
          if (scan) {
            enqueue(() => submitScan({ code: scan.code, productCode: product.productCode, mode: scan.mode, asked: scan.asked }));
          }
        }}
      />
      <PickCameraSheet
        visible={cameraVisible}
        title={t("pack.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={handleScan}
      />
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={packStyles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </>
  );
}

/** 候选商品转成 ScanResultPicker 的条目；订货相关字段在打包场景不用（已隐藏）。 */
function toPickerItem(candidate: PackProductCandidate): StoreOrderProductItem {
  return {
    productCode: candidate.productCode,
    itemNumber: candidate.itemNumber ?? undefined,
    barcode: candidate.barcode ?? undefined,
    productName: candidate.productName ?? undefined,
    productImage: candidate.productImage ?? undefined,
    minOrderQuantity: 1,
    stockQuantity: 0,
    isInStock: true,
  };
}

function itemCodes(item: PackCartonItem) {
  return [item.itemNumber, item.barcode].filter(Boolean).join(" · ") || item.productCode;
}

function HighlightCard({
  item,
  justScanned,
  linked,
  onDecrease,
  onIncrease,
  onPressValue,
}: {
  item: PackCartonItem;
  justScanned: boolean;
  linked: boolean;
  onDecrease: () => void;
  onIncrease: () => void;
  onPressValue: () => void;
}) {
  const { t } = useAppTranslation("warehousePacking");
  const progress = orderProgress(item);
  const packInfo = [
    middlePackOf(item) ? t("pack.packInfo", { count: middlePackOf(item) }) : null,
    item.packingQuantity && item.packingQuantity > 1 ? t("pack.cartonSpec", { count: item.packingQuantity }) : null,
  ]
    .filter(Boolean)
    .join(" · ");
  return (
    <View style={[packStyles.card, styles.highlight]}>
      <View style={styles.highlightTop}>
        <ProductThumb uri={item.productImage} size={52} />
        <View style={styles.highlightMid}>
          <Text numberOfLines={2} style={styles.highlightName}>
            {item.productName || item.productCode}
          </Text>
          <Text numberOfLines={1} style={styles.codes}>
            {itemCodes(item)}
          </Text>
        </View>
        {justScanned ? <Tag tone="green" icon="check" label={t("pack.justScanned")} /> : null}
      </View>
      <Stepper
        value={item.quantity}
        onDecrease={onDecrease}
        onIncrease={onIncrease}
        onPressValue={onPressValue}
        decreaseLabel={t("quantity.decrease")}
        increaseLabel={t("quantity.increase")}
        valueLabel={t("quantity.valueLabel", { count: item.quantity })}
      />
      <View style={styles.highlightFoot}>
        {packInfo ? <Text style={packStyles.muted}>{packInfo}</Text> : null}
        {progress ? (
          <Text style={[packStyles.muted, progress.over > 0 ? styles.warn : null]}>
            {progress.over > 0
              ? t("pack.orderOver", { ordered: progress.ordered, packed: progress.packed, over: progress.over })
              : t("pack.orderProgress", { ordered: progress.ordered, packed: progress.packed, remaining: progress.remaining })}
          </Text>
        ) : linked ? (
          <Text style={[packStyles.muted, styles.warn]}>{t("pack.outsideOrder")}</Text>
        ) : null}
      </View>
    </View>
  );
}

function ItemRow({
  item,
  linked,
  divider,
  onPress,
}: {
  item: PackCartonItem;
  linked: boolean;
  divider: boolean;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("warehousePacking");
  const progress = orderProgress(item);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${item.productName || item.productCode} ${t("quantity.valueLabel", { count: item.quantity })}`}
      onPress={onPress}
      style={[styles.itemRow, divider ? styles.itemDivider : null]}
    >
      <ProductThumb uri={item.productImage} size={40} />
      <View style={styles.highlightMid}>
        <Text numberOfLines={1} style={styles.itemName}>
          {item.productName || item.productCode}
        </Text>
        <Text numberOfLines={1} style={styles.codes}>
          {item.itemNumber || item.productCode}
          {progress ? ` · ${t("pack.ordered", { count: progress.ordered })}` : ""}
          {!progress && linked ? <Text style={styles.warn}>{` · ${t("pack.outsideOrder")}`}</Text> : null}
        </Text>
      </View>
      <Text style={styles.itemQty}>
        {item.quantity}
        <Text style={styles.itemUnit}>{` ${t("counts.unitPcs")}`}</Text>
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  scope: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 12,
    backgroundColor: PACK_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: PACK_COLORS.outlineMuted,
  },
  scopeText: { flex: 1, minWidth: 0, fontSize: 13, lineHeight: 18, color: PACK_COLORS.textSecondary },
  highlight: { gap: 10, borderColor: PACK_COLORS.successBorder, backgroundColor: PACK_COLORS.successBg },
  highlightTop: { flexDirection: "row", alignItems: "center", gap: 10 },
  highlightMid: { flex: 1, minWidth: 0 },
  highlightName: { fontSize: 15, lineHeight: 20, fontWeight: "700", color: PACK_COLORS.ink },
  highlightFoot: { flexDirection: "row", flexWrap: "wrap", justifyContent: "space-between", gap: 6 },
  codes: { fontFamily: "monospace", fontSize: 12, lineHeight: 17, color: PACK_COLORS.textSecondary },
  warn: { color: PACK_COLORS.warning, fontWeight: "600" },
  itemRow: { minHeight: 56, flexDirection: "row", alignItems: "center", gap: 10, paddingVertical: 6 },
  itemDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  itemName: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: PACK_COLORS.ink },
  itemQty: { fontSize: 17, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  itemUnit: { fontSize: 12, fontWeight: "400", color: PACK_COLORS.textSecondary },
  voidLink: { minHeight: 44, alignItems: "center", justifyContent: "center" },
  voidText: { fontSize: 13, color: PACK_COLORS.danger },
});
