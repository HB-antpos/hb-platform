import { useCallback, useEffect, useRef, useState } from "react";
import { Keyboard, StyleSheet, TextInput, View } from "react-native";
import { CameraView } from "expo-camera";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { SeasonalProductInsightsView } from "@/components/seasonal-product-insights/SeasonalProductInsightsView";
import { SeasonalRangeSheet } from "@/components/seasonal-product-insights/SeasonalRangeSheet";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { findProductInsightStore } from "@/modules/product-insights/logic";
import { createProductInsightRequestGate } from "@/modules/product-insights/request-gate";
import { useCameraScan } from "@/modules/scanner/use-camera-scan";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { fetchSeasonalProductInsight, lookupSeasonalProducts } from "./api";
import { canViewSeasonalProductInsights } from "./logic";
import type { SeasonalLookupResult, SeasonalProductInsight, SeasonalRanges } from "./types";

const first = (value: string | string[] | undefined) =>
  (Array.isArray(value) ? value[0] : value)?.trim() || "";

export function SeasonalProductInsightsScreen() {
  const { t } = useAppTranslation("seasonalProductInsights");
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const permissionScope = useAuthStore((state) => JSON.stringify([state.user?.permissions, state.user?.roleNames]));
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const params = useLocalSearchParams<{ productCode?: string | string[]; storeCode?: string | string[] }>();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));

  if (review || sessionKind === "iosReview") {
    return <ScreenMessage message={t("messages.reviewUnavailable")} onBack={goBack} />;
  }
  if (!canViewSeasonalProductInsights(isAuthenticated, hasPermission, false)) {
    return <ScreenMessage message={t("messages.notAllowed")} onBack={goBack} />;
  }

  // 账号或权限变化时销毁页面私有状态与在途请求，不复用上一身份的进销数据。
  const identity = [userGuid, sessionKind, isAuthenticated, permissionScope].join(":");
  return (
    <StoreScoped
      key={`${identity}:${first(params.productCode)}:${first(params.storeCode)}`}
      initialProductCode={first(params.productCode)}
      initialStoreCode={first(params.storeCode)}
      onBack={goBack}
    />
  );
}

function ScreenMessage({ message, loading = false, onBack, onRetry }: { message: string; loading?: boolean; onBack: () => void; onRetry?: () => void }) {
  const { t } = useAppTranslation("seasonalProductInsights");
  return (
    <SafeAreaView style={styles.message}>
      {loading ? <ActivityIndicator /> : null}
      <Text accessibilityLiveRegion="polite">{message}</Text>
      {onRetry ? <Button onPress={onRetry}>{t("actions.retry")}</Button> : null}
      <Button onPress={onBack}>{t("actions.back")}</Button>
    </SafeAreaView>
  );
}

function StoreScoped({ initialProductCode, initialStoreCode, onBack }: { initialProductCode: string; initialStoreCode: string; onBack: () => void }) {
  const { t } = useAppTranslation("seasonalProductInsights");
  const { stores, selectedStore, isStoreSelectionReady, isLoading, error, refetch } = useStores();
  const [overrideStoreCode, setOverrideStoreCode] = useState(initialStoreCode);
  const [productCode, setProductCode] = useState(initialProductCode);
  const [pickerVisible, setPickerVisible] = useState(false);
  const store = overrideStoreCode ? findProductInsightStore(stores, overrideStoreCode) : selectedStore;
  const available = isStoreSelectionReady && !isLoading;
  const picker = (
    <StorePickerModal
      visible={pickerVisible}
      presentation="sheet"
      stores={stores}
      selectedStoreCode={store?.storeCode}
      title={t("actions.selectStore")}
      cancelLabel={t("actions.close")}
      onDismiss={() => setPickerVisible(false)}
      onSelectStore={(next) => {
        if (next) setOverrideStoreCode(next.storeCode);
        setPickerVisible(false);
      }}
    />
  );

  if (!available || !store) {
    const message = error
      ? t("messages.storesFailed")
      : !available
        ? t("common:loading")
        : overrideStoreCode
          ? t("messages.storeNotAllowed")
          : t("messages.selectStore");
    return (
      <>
        <ScreenMessage
          message={message}
          loading={!available && !error}
          onBack={onBack}
          onRetry={error ? () => void refetch() : undefined}
        />
        {available && stores.length ? <Button onPress={() => setPickerVisible(true)}>{t("actions.selectStore")}</Button> : null}
        {picker}
      </>
    );
  }
  return (
    <>
      {/* 门店变化使用独立组件实例，第一帧就不再显示上一门店的数据。 */}
      <Content
        key={store.storeCode}
        store={store}
        initialProductCode={productCode}
        onSelectedProductCode={setProductCode}
        onBack={onBack}
        pickerVisible={pickerVisible}
        onStorePress={stores.length > 1 ? () => setPickerVisible(true) : undefined}
      />
      {picker}
    </>
  );
}

function Content({
  store,
  initialProductCode,
  onSelectedProductCode,
  onBack,
  onStorePress,
  pickerVisible,
}: {
  store: Store;
  initialProductCode: string;
  onSelectedProductCode: (code: string) => void;
  onBack: () => void;
  onStorePress?: () => void;
  pickerVisible: boolean;
}) {
  const { t, language } = useAppTranslation("seasonalProductInsights");
  const focused = useIsFocused();
  const [query, setQuery] = useState("");
  const [data, setData] = useState<SeasonalProductInsight | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [emptyState, setEmptyState] = useState<"initial" | "not-found">("initial");
  const [lookup, setLookup] = useState<SeasonalLookupResult | null>(null);
  const [lookupKeyword, setLookupKeyword] = useState("");
  const [matchMode, setMatchMode] = useState<"barcode" | "itemNumber" | null>(null);
  // null = 让后端按门店时区给默认区间（8 月 1 日至门店当地今天）；首次返回后记住实际区间与「今天」。
  const [ranges, setRanges] = useState<SeasonalRanges | null>(null);
  const [today, setToday] = useState<string | null>(null);
  const [rangeVisible, setRangeVisible] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [cameraGeneration, setCameraGeneration] = useState(0);
  const [queryFocused, setQueryFocused] = useState(false);
  const gate = useRef(createProductInsightRequestGate()).current;
  const retryProduct = useRef(initialProductCode);
  const scanGeneration = useRef(0);
  const cameraOpen = useRef(false);
  cameraOpen.current = cameraVisible;

  const formatError = useCallback(
    (value: unknown, fallbackKey: string) =>
      resolveLocalizedErrorMessage(value, { t, language, fallbackKey, allowRawMessageInChinese: false }),
    [language, t],
  );

  const rememberRanges = useCallback((next: SeasonalRanges, requested: SeasonalRanges | null) => {
    setRanges(next);
    // 未指定区间时后端默认截止到门店当地今天，借此得到门店「今天」，不依赖设备时区。
    if (!requested) setToday(next.sales.endDate);
  }, []);

  const loadProduct = useCallback(
    async (code: string, requested: SeasonalRanges | null) => {
      const lease = gate.begin();
      retryProduct.current = code;
      onSelectedProductCode(code);
      setData(null);
      setError(null);
      setLoading(true);
      setLookup(null);
      try {
        const result = await fetchSeasonalProductInsight(store.storeCode, code, requested, lease.signal);
        if (!lease.isCurrent()) return;
        rememberRanges(result.ranges, requested);
        setData(result);
        setQuery(result.product.itemNumber || result.product.barcode || code);
      } catch (cause) {
        if (lease.isCurrent()) setError(formatError(cause, "messages.loadFailed"));
      } finally {
        if (lease.isCurrent()) setLoading(false);
      }
    },
    [formatError, gate, onSelectedProductCode, rememberRanges, store.storeCode],
  );

  const search = useCallback(
    async (value: string, overrideRanges?: SeasonalRanges) => {
      const keyword = value.trim();
      // 刚在区间弹层里改了区间时，state 还没更新，用调用方传入的新区间。
      const effectiveRanges = overrideRanges ?? ranges;
      if (!keyword) return;
      const lease = gate.begin();
      retryProduct.current = "";
      onSelectedProductCode("");
      Keyboard.dismiss();
      setQueryFocused(false);
      setQuery(keyword);
      setData(null);
      setLookup(null);
      setMatchMode(null);
      setError(null);
      setLoading(true);
      setEmptyState("initial");
      try {
        const result = await lookupSeasonalProducts(store.storeCode, keyword, effectiveRanges, lease.signal);
        if (!lease.isCurrent()) return;
        rememberRanges(result.ranges, effectiveRanges);
        setMatchMode(result.matchMode);
        setLookupKeyword(keyword);
        // 唯一命中直接加载详情；多个命中在页面里列成进销卡片供选择。
        if (result.items.length === 1) {
          await loadProduct(result.items[0].productCode, result.ranges);
          return;
        }
        if (!result.items.length) setEmptyState("not-found");
        else setLookup(result);
      } catch (cause) {
        if (lease.isCurrent()) setError(formatError(cause, "messages.lookupFailed"));
      } finally {
        if (lease.isCurrent()) setLoading(false);
      }
    },
    [formatError, gate, loadProduct, onSelectedProductCode, ranges, rememberRanges, store.storeCode],
  );

  const initialLoad = useRef(loadProduct);
  const initialCode = useRef(initialProductCode);
  const invalidateCamera = useCallback(() => {
    scanGeneration.current++;
    cameraOpen.current = false;
  }, []);
  useEffect(() => {
    if (initialCode.current) void initialLoad.current(initialCode.current, null);
    return () => {
      gate.cancel();
      invalidateCamera();
    };
    // 初始商品只在当前门店实例挂载时加载，后续查询与重试由事件驱动。
  }, [gate, invalidateCamera]);

  const camera = useCameraScan({
    disabled: !focused || !cameraVisible || loading,
    resetKey: `${store.storeCode}:${cameraGeneration}:${cameraVisible}`,
    singleScanUntilReset: true,
    onBarcode: async (barcode) => {
      if (!cameraOpen.current) return;
      cameraOpen.current = false;
      setCameraVisible(false);
      await search(barcode);
    },
  });
  const openCamera = async () => {
    const generation = ++scanGeneration.current;
    Keyboard.dismiss();
    setQueryFocused(false);
    try {
      const permission = camera.permission?.granted ? camera.permission : await camera.requestPermission();
      if (generation !== scanGeneration.current) return;
      if (!permission.granted) {
        setError(t("messages.cameraPermission"));
        return;
      }
      setCameraGeneration((value) => value + 1);
      setCameraVisible(true);
    } catch (cause) {
      if (generation === scanGeneration.current) setError(formatError(cause, "messages.cameraUnavailable"));
    }
  };
  const hid = useHidBarcodeScanner({
    enabled: focused && !queryFocused && !cameraVisible && !rangeVisible && !pickerVisible && !loading,
    onScan: search,
  });
  useEffect(() => {
    if (!focused || pickerVisible) {
      scanGeneration.current++;
      cameraOpen.current = false;
      setCameraVisible(false);
    }
  }, [focused, pickerVisible]);

  const applyRanges = (next: SeasonalRanges) => {
    setRangeVisible(false);
    setRanges(next);
    if (retryProduct.current) void loadProduct(retryProduct.current, next);
    // 还停在候选列表上：按新区间重新检索，进货 / 销量 / 售罄率随之刷新。
    else if (lookup && lookupKeyword) void search(lookupKeyword, next);
  };

  return (
    <View style={styles.root}>
      <SeasonalProductInsightsView
        storeName={store.storeName || store.storeCode}
        onStorePress={onStorePress}
        query={query}
        matchMode={matchMode}
        onQueryChange={(value) => {
          gate.cancel();
          retryProduct.current = "";
          onSelectedProductCode("");
          setQuery(value);
          setData(null);
          setLookup(null);
          setMatchMode(null);
          setLoading(false);
          setError(null);
          setEmptyState("initial");
        }}
        onQueryFocus={() => {
          setQueryFocused(true);
          hid.pauseHiddenInputFocus();
        }}
        onQueryBlur={() => {
          setQueryFocused(false);
          hid.resumeHiddenInputFocus();
        }}
        onSearch={() => void search(query)}
        onScan={() => void openCamera()}
        data={data}
        lookup={lookup}
        lookupKeyword={lookupKeyword}
        onSelectCandidate={(code) => {
          const selectedRanges = lookup?.ranges ?? ranges;
          void loadProduct(code, selectedRanges);
        }}
        today={today}
        loading={loading}
        error={error}
        emptyState={emptyState}
        onRetry={() => {
          if (retryProduct.current) void loadProduct(retryProduct.current, ranges);
          else void search(query);
        }}
        onBack={onBack}
        onOpenRanges={() => setRangeVisible(true)}
        onAlignRanges={() => {
          if (data) applyRanges({ inbound: data.ranges.sales, sales: data.ranges.sales });
        }}
      />
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={styles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
      {(data || lookup) && today ? (
        <SeasonalRangeSheet
          visible={rangeVisible}
          ranges={(data ?? lookup)!.ranges}
          today={today}
          onClose={() => setRangeVisible(false)}
          onApply={applyRanges}
        />
      ) : null}
      <BusinessSheet
        visible={cameraVisible && focused}
        title={t("camera.title")}
        onDismiss={() => {
          scanGeneration.current++;
          cameraOpen.current = false;
          setCameraVisible(false);
        }}
        footer={
          <Button
            onPress={() => {
              cameraOpen.current = false;
              setCameraVisible(false);
            }}
          >
            {t("actions.close")}
          </Button>
        }
      >
        {cameraVisible && focused && camera.permission?.granted ? <CameraView style={styles.camera} {...camera.cameraProps} /> : null}
      </BusinessSheet>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: HB_COLORS.background },
  message: { flex: 1, gap: 16, padding: 24, justifyContent: "center", backgroundColor: HB_COLORS.background },
  camera: { height: 300 },
  hiddenInput: { position: "absolute", width: 1, height: 1, opacity: 0, left: -100 },
});
