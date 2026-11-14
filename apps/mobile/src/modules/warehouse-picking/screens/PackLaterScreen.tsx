import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { fetchCarton } from "../../packing/api";
import { QuantitySheet } from "../../packing/components/EntrySheets";
import { ChipRow, SectionTitle, Stepper, Tag, ScanBar, packStyles } from "../../packing/components/pack-ui";
import { fetchPickSheet } from "../api";
import { readPickingError } from "../api-normalization";
import { buildPickCodeIndex, resolvePickScan } from "../code-resolver";
import { sameGuid } from "../pack-carton-logic";
import { setOrderCartonItem } from "../pack-api";
import {
  distinctProducts,
  nextCartonQuantity,
  overPackBy,
  packLaterTotals,
  packScanStep,
  splitPackLines,
  type PackLaterLine,
  type PackScanStep,
} from "../pack-later-logic";
import type { PackProgress } from "../pack-types";
import { usePickerStore } from "../picker-store";
import { PICKER_RECONFIRM_CODES, pickingErrorMessage } from "../picking-errors";
import type { PickSheet, PickSheetLine } from "../types";
import { useOrderCarton } from "../use-order-carton";
import { LineChooserSheet } from "../components/LineChooserSheet";
import { PickCameraSheet } from "../components/PickCameraSheet";
import { PickHeader } from "../components/PickHeader";
import { PickSealSheet } from "../components/PickSealSheet";
import { ProductThumb } from "../components/ProductThumb";
import { MONO_FONT, PICK_COLORS } from "../components/pick-theme";
import { PICKING_HOME } from "./PickOrderListView";

const sameCode = (left: string, right: string) => left.trim().toLowerCase() === right.trim().toLowerCase();

/**
 * 先拣后装：拣货时没有装箱（或没装完），拣完后在这里按已拣数量扫码装箱。
 * - 列表「还没装完的商品」排前（待装 = 已拣 − 已装），装完的沉到下面折叠；
 * - 扫商品码：当前箱里该商品 +1 件（或 +1 中包，与拣货一致），请求带的是绝对件数，重复提交结果相同；
 * - 超出已拣数量先确认，不拦截；封箱并打印标签；暂存离开随时可以回来接着装。
 */
export function PackLaterScreen({ orderGuid, focusProductCode }: { orderGuid: string; focusProductCode: string | null }) {
  const { t, language } = useAppTranslation("warehousePicking");
  const router = useRouter();
  const focused = useIsFocused();
  const clearPicker = usePickerStore((state) => state.clearPicker);
  const [sheet, setSheet] = useState<PickSheet | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [snackbar, setSnackbar] = useState("");
  const [scanText, setScanText] = useState("");
  const [inputFocused, setInputFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [stepMode, setStepMode] = useState<PackScanStep>("pack");
  const [highlight, setHighlight] = useState<string | null>(focusProductCode);
  const [chooser, setChooser] = useState<{ code: string; lines: PickSheetLine[] } | null>(null);
  const [editProduct, setEditProduct] = useState<string | null>(null);
  const [showDone, setShowDone] = useState(false);
  const [pending, setPending] = useState(0);
  const [confirming, setConfirming] = useState(false);
  const [baselineFailed, setBaselineFailed] = useState<string | null>(null);
  const queueRef = useRef<Promise<void>>(Promise.resolve());
  const pendingRef = useRef(0);
  const progressRef = useRef<PackProgress | null>(null);
  const sheetRef = useRef<PickSheet | null>(null);
  sheetRef.current = sheet;

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  const handleAuthError = useCallback(
    (error: unknown) => {
      const { code } = readPickingError(error);
      if (code && PICKER_RECONFIRM_CODES.has(code)) {
        // 拣货人凭证失效或员工已不能拣货：回到入口重新确认。
        clearPicker();
        router.replace(PICKING_HOME);
        return true;
      }
      return false;
    },
    [clearPicker, router],
  );

  useEffect(() => {
    let cancelled = false;
    setLoadError(null);
    fetchPickSheet(orderGuid)
      .then((result) => {
        if (!cancelled) setSheet(result);
      })
      .catch((error) => {
        if (cancelled || handleAuthError(error)) return;
        setLoadError(pickingErrorMessage(error, t, language));
      });
    return () => {
      cancelled = true;
    };
    // 只按订单加载；文案与回调经 handleAuthError / t 的最新值使用即可。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [orderGuid, reloadKey]);

  const pack = useOrderCarton({
    orderGuid,
    enabled: focused,
    productOf: (detailGuid) => sheetRef.current?.lines.find((line) => line.detailGuid === detailGuid)?.productCode ?? null,
    onAuthError: handleAuthError,
    notify: setSnackbar,
  });
  const { carton, progress, applyDetail, refresh: refreshPack, getCartonGuid, cartonPieces, handleWriteError } = pack;
  progressRef.current = progress;

  // 接着用以前装过的箱：先读箱内明细当基线（设置数量是绝对值，不知道现有件数就不能写，否则会覆盖）。
  const cartonGuid = carton?.cartonGuid ?? null;
  const cartonComplete = carton?.itemsComplete ?? true;
  useEffect(() => {
    if (!cartonGuid || cartonComplete) return;
    let cancelled = false;
    fetchCarton(cartonGuid)
      .then((detail) => {
        if (!cancelled) applyDetail(detail);
      })
      .catch(() => {
        if (!cancelled) setBaselineFailed(cartonGuid);
      });
    return () => {
      cancelled = true;
    };
  }, [applyDetail, cartonComplete, cartonGuid]);
  const baselineUnknown = Boolean(carton && !carton.itemsComplete && baselineFailed && sameGuid(baselineFailed, carton.cartonGuid));

  const enqueue = useCallback(
    (task: () => Promise<void>) => {
      pendingRef.current += 1;
      setPending(pendingRef.current);
      queueRef.current = queueRef.current
        .then(task)
        .catch(() => undefined)
        .finally(() => {
          pendingRef.current -= 1;
          setPending(pendingRef.current);
        });
    },
    [],
  );

  const confirmOver = (over: number) =>
    new Promise<boolean>((resolve) => {
      setConfirming(true);
      Alert.alert(
        t("packLater.overTitle"),
        t("packLater.overBody", { count: over }),
        [
          {
            text: t("actions.cancel"),
            style: "cancel",
            onPress: () => {
              setConfirming(false);
              resolve(false);
            },
          },
          {
            text: t("packLater.overConfirm"),
            onPress: () => {
              setConfirming(false);
              resolve(true);
            },
          },
        ],
        { cancelable: false },
      );
    });

  /**
   * 改某商品在当前箱里的件数（compute 拿箱内现有件数算出新的绝对值）；排队串行执行，
   * 超出已拣数量先确认，成功后以服务端返回的完整箱为准并刷新进度。
   */
  const changeQuantity = useCallback(
    (productCode: string, compute: (baseline: number) => number, fromScan = false) =>
      enqueue(async () => {
        const targetCarton = getCartonGuid();
        if (!targetCarton) {
          playScanFeedbackSound("blocked");
          setSnackbar(t("packLater.needCarton"));
          return;
        }
        const baseline = cartonPieces(productCode);
        if (baseline === null) {
          playScanFeedbackSound("blocked");
          setSnackbar(t("packLater.unknownBaseline"));
          return;
        }
        const next = compute(baseline);
        if (next === baseline) return;
        const line = progressRef.current?.lines.find((item) => sameCode(item.productCode, productCode));
        const over = next > baseline ? overPackBy(line ?? { pickedPieces: 0, packedPieces: 0 }, baseline, next) : 0;
        if (over > 0 && !(await confirmOver(over))) return;
        try {
          const result = await setOrderCartonItem(orderGuid, targetCarton, productCode, next);
          applyDetail(result.carton);
          setHighlight(productCode);
          if (result.overPacked) {
            playScanFeedbackSound("blocked");
            setSnackbar(t("packLater.overNotice", { count: result.overPackedBy }));
          } else if (fromScan) {
            playScanFeedbackSound("added");
          }
          const fresh = await refreshPack();
          if (fresh) progressRef.current = fresh;
        } catch (error) {
          if (handleAuthError(error)) return;
          playScanFeedbackSound("error");
          setSnackbar(handleWriteError(error) ?? pickingErrorMessage(error, t, language));
        }
      }),
    // confirmOver 只依赖 setConfirming 与 t，随渲染更新即可。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [applyDetail, cartonPieces, enqueue, getCartonGuid, handleAuthError, handleWriteError, language, orderGuid, refreshPack, t],
  );

  const codeIndex = useMemo(() => buildPickCodeIndex(sheet?.codes ?? []), [sheet]);
  const addScanned = useCallback(
    (line: PickSheetLine) => {
      const step = packScanStep(stepMode, line);
      changeQuantity(line.productCode, (baseline) => nextCartonQuantity(baseline, step), true);
    },
    [changeQuantity, stepMode],
  );

  const handleScan = useCallback(
    (raw: string) => {
      const code = raw.trim();
      if (!code || !sheet) return;
      setScanText("");
      if (!pack.carton) {
        playScanFeedbackSound("blocked");
        setSnackbar(t("packLater.needCarton"));
        return;
      }
      const resolution = resolvePickScan(code, codeIndex, null);
      switch (resolution.kind) {
        case "line": {
          const line = sheet.lines.find((item) => item.detailGuid === resolution.detailGuid);
          if (line) addScanned(line);
          return;
        }
        case "multiple": {
          const candidates = distinctProducts(
            resolution.detailGuids
              .map((guid) => sheet.lines.find((item) => item.detailGuid === guid))
              .filter((item): item is PickSheetLine => Boolean(item)),
          );
          if (candidates.length === 1) {
            addScanned(candidates[0]);
            return;
          }
          playScanFeedbackSound("multiple");
          setChooser({ code: resolution.code, lines: candidates });
          return;
        }
        case "none":
          playScanFeedbackSound("not_found");
          setSnackbar(t("packLater.notInOrder", { code: resolution.code }));
          return;
        default:
          // 货位码 / 订单码 / 分单码：这里只扫商品码。
          playScanFeedbackSound("blocked");
          setSnackbar(t("packLater.notProductCode"));
      }
    },
    [addScanned, codeIndex, pack.carton, sheet, t],
  );

  const sheetsOpen = pack.sealVisible || Boolean(chooser) || Boolean(editProduct) || cameraVisible || confirming;
  const hid = useHidBarcodeScanner({ enabled: focused && !inputFocused && !sheetsOpen && Boolean(sheet), onScan: handleScan });

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PICKING_HOME));

  if (!sheet || !progress) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.center}>
        {loadError || (pack.loaded && pack.available && !progress) ? (
          <>
            <Text accessibilityLiveRegion="polite" style={styles.centerText}>
              {loadError ?? t("messages.loadFailed")}
            </Text>
            <Button
              mode="contained"
              onPress={() => {
                setReloadKey((key) => key + 1);
                void refreshPack();
              }}
            >
              {t("actions.retry")}
            </Button>
            <Button onPress={goBack}>{t("actions.back")}</Button>
          </>
        ) : !pack.available ? (
          <>
            <Text style={styles.centerText}>{t("review.unavailable")}</Text>
            <Button onPress={goBack}>{t("actions.back")}</Button>
          </>
        ) : (
          <ActivityIndicator />
        )}
      </SafeAreaView>
    );
  }

  const groups = splitPackLines(progress.lines, focusProductCode);
  const totals = packLaterTotals(progress.lines);
  const ratio = totals.picked > 0 ? Math.min(1, totals.packed / totals.picked) : 0;
  const sheetLineOf = (productCode: string) => sheet.lines.find((line) => sameCode(line.productCode, productCode)) ?? null;
  const highlightLine = highlight ? progress.lines.find((line) => sameCode(line.productCode, highlight)) ?? null : null;
  const highlightInCarton = highlightLine ? pack.cartonPieces(highlightLine.productCode) : null;
  const packMode = stepMode;
  const editLine = editProduct ? progress.lines.find((line) => sameCode(line.productCode, editProduct)) ?? null : null;
  const canSeal = Boolean(carton) && (carton?.pieceCount ?? 0) > 0 && pending === 0 && !baselineUnknown;

  const renderRow = (line: PackLaterLine, divider: boolean, done: boolean) => {
    const inCarton = carton ? pack.cartonPieces(line.productCode) : null;
    const sheetLine = sheetLineOf(line.productCode);
    return (
      <Pressable
        key={line.productCode}
        accessibilityRole="button"
        onPress={() => setHighlight(line.productCode)}
        style={[styles.row, divider ? styles.rowDivider : null, done ? styles.rowDone : null]}
      >
        <ProductThumb uri={sheetLine?.productImage ?? null} size={40} />
        <View style={styles.rowText}>
          <Text numberOfLines={1} style={styles.rowName}>
            {line.productName || sheetLine?.productName || line.productCode}
          </Text>
          <Text numberOfLines={1} style={styles.rowMeta}>
            {line.itemNumber || line.productCode}
            {` · ${t("packLater.picked", { count: line.pickedPieces })}`}
          </Text>
        </View>
        <View style={styles.rowRight}>
          <Text style={styles.rowQty}>
            {line.packedPieces}
            <Text style={styles.rowQtyTotal}>{` / ${line.pickedPieces}`}</Text>
          </Text>
          {inCarton ? <Text style={styles.rowCarton}>{t("carton.rowInCarton", { count: inCarton })}</Text> : null}
          {done && line.remaining < 0 ? <Text style={styles.rowOver}>{t("packLater.overBy", { count: -line.remaining })}</Text> : null}
          {done && line.remaining >= 0 ? <Text style={styles.rowDoneTag}>{t("packLater.done")}</Text> : null}
        </View>
      </Pressable>
    );
  };

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
      <PickHeader
        title={t("packLater.title")}
        subtitle={progress.orderNo}
        onBack={goBack}
        backLabel={t("actions.back")}
        right={<Tag tone="blue" label={t("packLater.tag")} />}
      />
      <View style={styles.scope}>
        <MaterialCommunityIcons name="clipboard-text-outline" size={16} color={PICK_COLORS.textSecondary} />
        <Text numberOfLines={1} style={styles.scopeText}>
          {t("packLater.scope", { orderNo: progress.orderNo ?? "—", store: progress.storeName || progress.storeCode || "—" })}
        </Text>
      </View>

      <ScrollView contentContainerStyle={packStyles.body} keyboardShouldPersistTaps="handled">
        {totals.packed === 0 ? (
          <View style={styles.info}>
            <MaterialCommunityIcons name="information-outline" size={16} color={PICK_COLORS.action} />
            <Text style={styles.infoText}>{t("packLater.intro")}</Text>
          </View>
        ) : null}

        <ScanBar
          value={scanText}
          onChangeText={setScanText}
          onSubmit={handleScan}
          placeholder={t("packLater.scanPlaceholder")}
          inputLabel={t("packLater.scanPlaceholder")}
          cameraLabel={t("picking.cameraLabel")}
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
          label={t("packLater.stepLabel")}
          value={packMode}
          onChange={setStepMode}
          options={[
            { key: "pack", label: t("packLater.stepPack") },
            { key: "each", label: t("packLater.stepEach") },
          ]}
        />

        <View style={styles.progressCard}>
          <View style={styles.progressRow}>
            <Text style={styles.progressText}>{t("packLater.progress", { packed: totals.packed, picked: totals.picked })}</Text>
            <Text style={styles.progressText}>{t("packLater.remaining", { count: totals.remaining, products: totals.pendingProducts })}</Text>
          </View>
          <View style={styles.track}>
            <View style={[styles.fill, { width: `${Math.round(ratio * 100)}%` }]} />
          </View>
        </View>

        {carton ? (
          <View style={styles.cartonRow}>
            <MaterialCommunityIcons name="package-variant-closed" size={18} color={PICK_COLORS.action} />
            <Text numberOfLines={1} style={styles.cartonText}>
              {t("packLater.currentCarton", { cartonNo: carton.cartonNo, sku: carton.skuCount, pieces: carton.pieceCount })}
            </Text>
          </View>
        ) : (
          <View style={styles.newCarton}>
            <Text style={styles.newCartonText}>{t("packLater.noCarton")}</Text>
            <Button mode="contained" loading={pack.creating} disabled={pack.creating} onPress={() => void pack.startCarton()}>
              {t("packLater.newCarton")}
            </Button>
          </View>
        )}
        {baselineUnknown ? (
          <View style={[styles.info, styles.warning]} accessibilityLiveRegion="polite">
            <MaterialCommunityIcons name="alert-outline" size={16} color={PICK_COLORS.warning} />
            <View style={styles.warningBody}>
              <Text style={styles.warningText}>{t("packLater.baselineUnknown")}</Text>
              <Button compact mode="outlined" loading={pack.creating} disabled={pack.creating} onPress={() => void pack.startCarton()}>
                {t("packLater.newCarton")}
              </Button>
            </View>
          </View>
        ) : null}

        {highlightLine && carton && !baselineUnknown ? (
          <View style={[packStyles.card, styles.highlight]}>
            <Text numberOfLines={2} style={styles.highlightName}>
              {highlightLine.productName || highlightLine.itemNumber || highlightLine.productCode}
            </Text>
            <Text style={styles.rowMeta}>
              {t("packLater.highlightMeta", { packed: highlightLine.packedPieces, picked: highlightLine.pickedPieces })}
            </Text>
            <Stepper
              value={highlightInCarton ?? 0}
              disabled={highlightInCarton === null}
              onDecrease={() => changeQuantity(highlightLine.productCode, (baseline) => nextCartonQuantity(baseline, -1))}
              onIncrease={() => changeQuantity(highlightLine.productCode, (baseline) => nextCartonQuantity(baseline, 1))}
              onPressValue={() => setEditProduct(highlightLine.productCode)}
              decreaseLabel={t("packLater.decrease")}
              increaseLabel={t("packLater.increase")}
              valueLabel={t("packLater.inCartonLabel", { count: highlightInCarton ?? 0 })}
            />
            <Text style={packStyles.muted}>{t("packLater.inCartonHint")}</Text>
          </View>
        ) : null}

        <SectionTitle title={t("packLater.pendingTitle")} right={groups.pending.length > 0 ? String(groups.pending.length) : null} />
        {groups.pending.length === 0 ? (
          <Text style={packStyles.muted}>{t("packLater.pendingEmpty")}</Text>
        ) : (
          <View style={packStyles.cardList}>{groups.pending.map((line, index) => renderRow(line, index > 0, false))}</View>
        )}

        {groups.done.length > 0 ? (
          <>
            <Pressable accessibilityRole="button" accessibilityState={{ expanded: showDone }} onPress={() => setShowDone((value) => !value)} style={styles.doneHeader}>
              <Text style={styles.doneTitle}>{t("packLater.doneTitle", { count: groups.done.length })}</Text>
              <MaterialCommunityIcons name={showDone ? "chevron-up" : "chevron-down"} size={20} color={PICK_COLORS.textSecondary} />
            </Pressable>
            {showDone ? <View style={packStyles.cardList}>{groups.done.map((line, index) => renderRow(line, index > 0, true))}</View> : null}
          </>
        ) : null}
      </ScrollView>

      <View style={packStyles.bar}>
        <View style={packStyles.buttons}>
          <Button mode="outlined" onPress={goBack} style={[packStyles.button, packStyles.flexSmall]} contentStyle={packStyles.buttonContent}>
            {t("packLater.leave")}
          </Button>
          <Button
            mode="contained"
            icon="printer"
            disabled={!canSeal}
            onPress={() => pack.setSealVisible(true)}
            style={[packStyles.button, packStyles.flex]}
            contentStyle={packStyles.buttonContent}
          >
            {t("packLater.sealAndPrint")}
          </Button>
        </View>
      </View>

      <PickSealSheet pack={pack} />
      <QuantitySheet
        visible={Boolean(editLine)}
        title={t("packLater.editTitle")}
        productName={editLine?.productName ?? editLine?.itemNumber}
        hint={t("packLater.editHint")}
        initial={editLine ? pack.cartonPieces(editLine.productCode) ?? 0 : 0}
        allowZero
        saving={false}
        onDismiss={() => setEditProduct(null)}
        onConfirm={(quantity) => {
          const code = editProduct;
          setEditProduct(null);
          if (code) changeQuantity(code, () => quantity);
        }}
      />
      <LineChooserSheet
        visible={Boolean(chooser)}
        code={chooser?.code}
        lines={chooser?.lines ?? []}
        onDismiss={() => setChooser(null)}
        onPick={(detailGuid) => {
          const line = chooser?.lines.find((item) => item.detailGuid === detailGuid);
          setChooser(null);
          if (line) addScanned(line);
        }}
      />
      <PickCameraSheet
        visible={cameraVisible}
        title={t("packLater.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={handleScan}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3500} style={styles.snackbar}>
        {snackbar}
      </Snackbar>
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={packStyles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: PICK_COLORS.background },
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PICK_COLORS.background },
  centerText: { textAlign: "center", color: PICK_COLORS.ink },
  scope: {
    minHeight: 40,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingHorizontal: 12,
    backgroundColor: PICK_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: PICK_COLORS.outlineMuted,
  },
  scopeText: { flex: 1, minWidth: 0, fontSize: 13, lineHeight: 18, color: PICK_COLORS.textSecondary },
  info: { flexDirection: "row", gap: 8, padding: 10, borderRadius: 10, backgroundColor: PICK_COLORS.infoBg, borderWidth: 1, borderColor: PICK_COLORS.infoBorder },
  infoText: { flex: 1, fontSize: 13, lineHeight: 19, color: PICK_COLORS.infoText },
  warning: { backgroundColor: PICK_COLORS.warningBg, borderColor: PICK_COLORS.warningBorder },
  warningBody: { flex: 1, gap: 8 },
  warningText: { fontSize: 13, lineHeight: 19, color: PICK_COLORS.warningText },
  progressCard: { gap: 6, padding: 12, borderRadius: 12, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, backgroundColor: PICK_COLORS.white },
  progressRow: { flexDirection: "row", justifyContent: "space-between", gap: 8 },
  progressText: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  track: { height: 6, borderRadius: 3, backgroundColor: PICK_COLORS.outlineMuted, overflow: "hidden" },
  fill: { height: 6, backgroundColor: PICK_COLORS.action },
  cartonRow: { flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: 4 },
  cartonText: { flex: 1, minWidth: 0, fontSize: 13, lineHeight: 18, fontWeight: "600", color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  newCarton: { gap: 8, padding: 12, borderRadius: 12, borderWidth: 1, borderStyle: "dashed", borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white },
  newCartonText: { fontSize: 13, lineHeight: 19, color: PICK_COLORS.textSecondary },
  highlight: { gap: 8, borderColor: PICK_COLORS.successBorder, backgroundColor: PICK_COLORS.successBg },
  highlightName: { fontSize: 15, lineHeight: 20, fontWeight: "700", color: PICK_COLORS.ink },
  row: { minHeight: 60, flexDirection: "row", alignItems: "center", gap: 10, paddingVertical: 8 },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PICK_COLORS.outlineMuted },
  rowDone: { opacity: 0.75 },
  rowText: { flex: 1, minWidth: 0, gap: 2 },
  rowName: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: PICK_COLORS.ink },
  rowMeta: { fontFamily: MONO_FONT, fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  rowRight: { alignItems: "flex-end", gap: 2 },
  rowQty: { fontSize: 18, lineHeight: 24, fontWeight: "700", color: PICK_COLORS.ink, fontVariant: ["tabular-nums"] },
  rowQtyTotal: { fontSize: 13, fontWeight: "400", color: PICK_COLORS.textSecondary },
  rowCarton: { fontSize: 12, lineHeight: 16, fontWeight: "600", color: PICK_COLORS.action },
  rowOver: { fontSize: 12, lineHeight: 16, fontWeight: "600", color: PICK_COLORS.warning },
  rowDoneTag: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.success },
  doneHeader: { minHeight: 44, flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  doneTitle: { fontSize: 13, lineHeight: 18, fontWeight: "700", color: PICK_COLORS.textSecondary },
  snackbar: { marginBottom: 72 },
});
