import { useCallback, useEffect, useRef, useState } from "react";
import { Keyboard, StyleSheet, TextInput, View } from "react-native";
import { CameraView } from "expo-camera";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { ProductInsightsView } from "@/components/product-insights/ProductInsightsView";
import { BranchSalesSheet } from "@/components/product-insights/BranchSalesSheet";
import { LookupResultSheet } from "@/components/product-maintenance/LookupResultSheet";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { lookupProducts } from "@/modules/product-maintenance/api";
import type { ProductLookupItem } from "@/modules/product-maintenance/types";
import { useStores } from "@/modules/shop/use-stores";
import type { Store } from "@/modules/shop/types";
import { useCameraScan } from "@/modules/scanner/use-camera-scan";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { useAuthStore } from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { HB_COLORS } from "@/shared/theme/tokens";
import {
  fetchProductInsightBranchSales,
  fetchStoreProductInsight,
} from "./api";
import {
  canViewProductInsightBranches,
  findProductInsightStore,
  isValidProductInsightRange,
} from "./logic";
import {
  DEFAULT_PRODUCT_INSIGHT_PRESET,
  resolvePeriodRange,
  type ProductInsightPeriod,
} from "./movement-summary";
import { createProductInsightRequestGate } from "./request-gate";
import type {
  ProductBranchSales,
  ProductInsightRange,
  StoreProductInsight,
} from "./types";

const first = (value: string | string[] | undefined) =>
  (Array.isArray(value) ? value[0] : value)?.trim() || "";

export function ProductInsightsScreen() {
  const { t } = useAppTranslation("productInsights");
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const permissionScope = useAuthStore((state) =>
    JSON.stringify([state.user?.permissions, state.user?.roleNames]),
  );
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const device = useDeviceStore((state) => state.session);
  const params = useLocalSearchParams<{
    productCode?: string | string[];
    storeCode?: string | string[];
  }>();
  const goBack = () =>
    router.canGoBack() ? router.back() : router.replace("/(shell)/workbench");
  if (review || sessionKind === "iosReview") {
    return (
      <ScreenMessage
        message={t("messages.reviewUnavailable")}
        onBack={goBack}
      />
    );
  }
  // 账号/设备切换时销毁页面私有状态和在途请求，不复用上一身份的进销数据。
  const identity = [
    userGuid,
    sessionKind,
    isAuthenticated,
    device?.hardwareId,
    device?.storeCode,
    permissionScope,
  ].join(":");
  return (
    <StoreScopedInsights
      key={`${identity}:${first(params.productCode)}:${first(params.storeCode)}`}
      initialProductCode={first(params.productCode)}
      initialStoreCode={first(params.storeCode)}
      onBack={goBack}
    />
  );
}

function ScreenMessage({
  message,
  loading = false,
  onBack,
  onRetry,
}: {
  message: string;
  loading?: boolean;
  onBack: () => void;
  onRetry?: () => void;
}) {
  const { t } = useAppTranslation("productInsights");
  return (
    <SafeAreaView style={styles.message}>
      {loading ? <ActivityIndicator /> : null}
      <Text accessibilityLiveRegion="polite">{message}</Text>
      {onRetry ? <Button onPress={onRetry}>{t("actions.retry")}</Button> : null}
      <Button onPress={onBack}>{t("actions.back")}</Button>
    </SafeAreaView>
  );
}

function StoreScopedInsights({
  initialProductCode,
  initialStoreCode,
  onBack,
}: {
  initialProductCode: string;
  initialStoreCode: string;
  onBack: () => void;
}) {
  const { t } = useAppTranslation("productInsights");
  const {
    stores,
    selectedStore,
    isDeviceMode,
    isStoreSelectionReady,
    isLoading,
    error,
    refetch,
  } = useStores();
  const [overrideStoreCode, setOverrideStoreCode] = useState(initialStoreCode);
  const [productCode, setProductCode] = useState(initialProductCode);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const store = isDeviceMode
    ? selectedStore
    : overrideStoreCode
      ? findProductInsightStore(stores, overrideStoreCode)
      : selectedStore;
  const requestedStoreRejected = Boolean(
    initialStoreCode &&
    isDeviceMode &&
    selectedStore?.storeCode.toLowerCase() !== initialStoreCode.toLowerCase(),
  );
  const available = isStoreSelectionReady && !isLoading;
  const picker = (
    <StorePickerModal
      visible={storePickerVisible}
      presentation="sheet"
      stores={stores}
      selectedStoreCode={store?.storeCode}
      title={t("actions.selectStore")}
      cancelLabel={t("actions.close")}
      onDismiss={() => setStorePickerVisible(false)}
      onSelectStore={(next) => {
        if (next) setOverrideStoreCode(next.storeCode);
        setStorePickerVisible(false);
      }}
    />
  );

  if (!available || !store || requestedStoreRejected) {
    const message = error
      ? t("messages.storesFailed")
      : !available
        ? t("common:loading")
        : overrideStoreCode || requestedStoreRejected
          ? t("messages.storeNotAllowed")
          : t("messages.selectStore");
    return (
      <>
        <ScreenMessage
          message={message}
          loading={!available && !error}
          onBack={onBack}
          onRetry={
            error
              ? () => {
                  void refetch();
                }
              : undefined
          }
        />
        {available && !isDeviceMode && stores.length ? (
          <Button onPress={() => setStorePickerVisible(true)}>
            {t("actions.selectStore")}
          </Button>
        ) : null}
        {picker}
      </>
    );
  }
  return (
    <>
      {/* 门店变化使用独立组件实例，第一帧就不再显示上一门店的数据。 */}
      <ProductInsightsContent
        key={store.storeCode}
        store={store}
        initialProductCode={productCode}
        onSelectedProductCode={setProductCode}
        onBack={onBack}
        storePickerVisible={storePickerVisible}
        onStorePress={
          !isDeviceMode && stores.length > 1
            ? () => setStorePickerVisible(true)
            : undefined
        }
      />
      {picker}
    </>
  );
}

function ProductInsightsContent({
  store,
  initialProductCode,
  onSelectedProductCode,
  onBack,
  onStorePress,
  storePickerVisible,
}: {
  store: Store;
  initialProductCode: string;
  onSelectedProductCode: (code: string) => void;
  onBack: () => void;
  onStorePress?: () => void;
  storePickerVisible: boolean;
}) {
  const { t, language } = useAppTranslation("productInsights");
  const focused = useIsFocused();
  const access = useAuthStore((state) => state.access);
  const authenticated = useAuthStore((state) => state.isAuthenticated);
  const [query, setQuery] = useState("");
  const [data, setData] = useState<StoreProductInsight | null>(null);
  const [loading, setLoading] = useState(false);
  // 切换周期时保留当前内容，只在周期条上显示加载中，避免整页闪成空白。
  const [periodLoading, setPeriodLoading] = useState(false);
  const [period, setPeriod] = useState<ProductInsightPeriod>({
    kind: "preset",
    days: DEFAULT_PRODUCT_INSIGHT_PRESET,
  });
  // 门店「今天」：取自最近一次默认区间响应的结束日，用来推算 30 / 180 天。
  const [anchorEndDate, setAnchorEndDate] = useState<string | null>(null);
  const periodRef = useRef(period);
  const anchorRef = useRef(anchorEndDate);
  const [error, setError] = useState<string | null>(null);
  const [emptyState, setEmptyState] = useState<"initial" | "not-found">(
    "initial",
  );
  const [candidates, setCandidates] = useState<ProductLookupItem[]>([]);
  const [candidateCode, setCandidateCode] = useState<string>();
  const [cameraVisible, setCameraVisible] = useState(false);
  const [cameraGeneration, setCameraGeneration] = useState(0);
  const [queryFocused, setQueryFocused] = useState(false);
  const [detailVisible, setDetailVisible] = useState(false);
  const [branchVisible, setBranchVisible] = useState(false);
  const [branchData, setBranchData] = useState<ProductBranchSales | null>(null);
  const [branchLoading, setBranchLoading] = useState(false);
  const [branchError, setBranchError] = useState<string | null>(null);
  const [branchRange, setBranchRange] = useState<ProductInsightRange>({
    startDate: "",
    endDate: "",
  });
  const [branchRetry, setBranchRetry] = useState(0);
  const gate = useRef(createProductInsightRequestGate()).current;
  const branchGate = useRef(createProductInsightRequestGate()).current;
  const retryProduct = useRef(initialProductCode);
  const scanGeneration = useRef(0);
  const cameraOpen = useRef(false);
  cameraOpen.current = cameraVisible;
  const canViewBranches = canViewProductInsightBranches(
    authenticated,
    access.hasPermission,
    false,
  );
  const formatError = useCallback(
    (value: unknown, fallbackKey: string) =>
      resolveLocalizedErrorMessage(value, {
        t,
        language,
        fallbackKey,
        allowRawMessageInChinese: false,
      }),
    [language, t],
  );

  const loadProduct = useCallback(
    async (code: string, options: { keepContent?: boolean } = {}) => {
      const lease = gate.begin();
      const range = resolvePeriodRange(periodRef.current, anchorRef.current);
      retryProduct.current = code;
      onSelectedProductCode(code);
      if (options.keepContent) setPeriodLoading(true);
      else {
        setData(null);
        setLoading(true);
      }
      setError(null);
      setCandidates([]);
      setBranchVisible(false);
      try {
        const result = await fetchStoreProductInsight(
          store.storeCode,
          code,
          lease.signal,
          range,
        );
        if (!lease.isCurrent()) return;
        if (!range) {
          anchorRef.current = result.range.endDate;
          setAnchorEndDate(result.range.endDate);
        }
        setData(result);
        setQuery(result.product.itemNumber || result.product.barcode || code);
      } catch (cause) {
        if (lease.isCurrent()) {
          setData(null);
          setError(formatError(cause, "messages.loadFailed"));
        }
      } finally {
        if (lease.isCurrent()) {
          setLoading(false);
          setPeriodLoading(false);
        }
      }
    },
    [formatError, gate, onSelectedProductCode, store.storeCode],
  );

  const search = useCallback(
    async (value: string) => {
      const keyword = value.trim();
      if (!keyword) return;
      const lease = gate.begin();
      retryProduct.current = "";
      onSelectedProductCode("");
      Keyboard.dismiss();
      setQueryFocused(false);
      setQuery(keyword);
      setData(null);
      setCandidates([]);
      setError(null);
      setLoading(true);
      setPeriodLoading(false);
      setBranchVisible(false);
      setEmptyState("initial");
      try {
        const items = await lookupProducts({
          keyword,
          storeCode: store.storeCode,
        });
        if (!lease.isCurrent()) return;
        const unique = [
          ...new Map(items.map((item) => [item.productCode, item])).values(),
        ];
        if (unique.length === 1) {
          await loadProduct(unique[0].productCode);
          return;
        }
        if (!unique.length) setEmptyState("not-found");
        else {
          setCandidates(unique);
          setCandidateCode(unique[0].productCode);
        }
      } catch (cause) {
        if (lease.isCurrent())
          setError(formatError(cause, "messages.lookupFailed"));
      } finally {
        if (lease.isCurrent()) setLoading(false);
      }
    },
    [formatError, gate, loadProduct, onSelectedProductCode, store.storeCode],
  );

  const initialLoad = useRef(loadProduct);
  const initialCode = useRef(initialProductCode);
  const invalidateCamera = useCallback(() => {
    scanGeneration.current++;
    cameraOpen.current = false;
  }, []);
  useEffect(() => {
    if (initialCode.current) void initialLoad.current(initialCode.current);
    return () => {
      gate.cancel();
      branchGate.cancel();
      invalidateCamera();
    };
    // 初始货品只在当前门店实例挂载时加载，后续输入与重试由事件驱动。
  }, [branchGate, gate, invalidateCamera]);

  const camera = useCameraScan({
    disabled: !focused || !cameraVisible || loading || periodLoading,
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
      const permission = camera.permission?.granted
        ? camera.permission
        : await camera.requestPermission();
      if (generation !== scanGeneration.current) return;
      if (!permission.granted) {
        setError(t("messages.cameraPermission"));
        return;
      }
      setCameraGeneration((value) => value + 1);
      setCameraVisible(true);
    } catch (cause) {
      if (generation === scanGeneration.current)
        setError(formatError(cause, "messages.cameraUnavailable"));
    }
  };
  const hid = useHidBarcodeScanner({
    enabled:
      focused &&
      !queryFocused &&
      !detailVisible &&
      !cameraVisible &&
      !branchVisible &&
      !storePickerVisible &&
      !candidates.length &&
      !loading &&
      !periodLoading,
    onScan: search,
  });
  useEffect(() => {
    if (!focused || storePickerVisible) {
      scanGeneration.current++;
      cameraOpen.current = false;
      setCameraVisible(false);
    }
  }, [focused, storePickerVisible]);

  useEffect(() => {
    if (
      !branchVisible ||
      !canViewBranches ||
      !data ||
      !isValidProductInsightRange(branchRange)
    ) {
      branchGate.cancel();
      return;
    }
    const lease = branchGate.begin();
    setBranchLoading(true);
    setBranchData(null);
    setBranchError(null);
    void fetchProductInsightBranchSales(
      data.product.productCode,
      branchRange,
      lease.signal,
    )
      .then((result) => {
        if (lease.isCurrent()) setBranchData(result);
      })
      .catch((cause) => {
        if (lease.isCurrent())
          setBranchError(formatError(cause, "messages.branchSalesFailed"));
      })
      .finally(() => {
        if (lease.isCurrent()) setBranchLoading(false);
      });
    return () => branchGate.cancel();
  }, [
    branchGate,
    branchRange,
    branchRetry,
    branchVisible,
    canViewBranches,
    data,
    formatError,
  ]);

  return (
    <View style={styles.root}>
      <ProductInsightsView
        query={query}
        onQueryChange={(value) => {
          gate.cancel();
          retryProduct.current = "";
          onSelectedProductCode("");
          setQuery(value);
          setData(null);
          setLoading(false);
          setPeriodLoading(false);
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
        onSearch={() => {
          void search(query);
        }}
        onScan={() => {
          void openCamera();
        }}
        store={store}
        onStorePress={onStorePress}
        data={data}
        loading={loading}
        periodLoading={periodLoading}
        period={period}
        presetEndDate={anchorEndDate}
        onPeriodChange={(next) => {
          periodRef.current = next;
          setPeriod(next);
          if (retryProduct.current)
            void loadProduct(retryProduct.current, { keepContent: true });
        }}
        error={error}
        emptyState={emptyState}
        onRetry={() => {
          if (retryProduct.current) void loadProduct(retryProduct.current);
          else void search(query);
        }}
        onBack={onBack}
        onDetailVisibilityChange={setDetailVisible}
        canViewBranchSales={canViewBranches}
        onOpenBranchSales={() => {
          if (!data || !canViewBranches) return;
          setBranchRange(data.range);
          setBranchData(null);
          setBranchError(null);
          setBranchVisible(true);
        }}
      />
      {hid.textInputProps ? (
        <TextInput
          {...hid.textInputProps}
          style={styles.hiddenInput}
          accessible={false}
          importantForAccessibility="no-hide-descendants"
        />
      ) : null}
      <LookupResultSheet
        visible={candidates.length > 0}
        queryText={query}
        items={candidates}
        selectedValue={candidateCode}
        onSelect={setCandidateCode}
        onClose={() => setCandidates([])}
        onConfirm={() => {
          if (candidateCode) void loadProduct(candidateCode);
        }}
      />
      <BranchSalesSheet
        visible={branchVisible && canViewBranches}
        onClose={() => {
          branchGate.cancel();
          setBranchVisible(false);
          setBranchData(null);
        }}
        product={data?.product ?? null}
        currentStoreCode={store.storeCode}
        data={branchData}
        loading={branchLoading}
        error={branchError}
        onRetry={() => setBranchRetry((value) => value + 1)}
        range={branchRange}
        onRangeChange={(range) => {
          if (!isValidProductInsightRange(range)) return;
          branchGate.cancel();
          setBranchData(null);
          setBranchRange(range);
        }}
      />
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
        {cameraVisible && focused && camera.permission?.granted ? (
          <CameraView style={styles.camera} {...camera.cameraProps} />
        ) : null}
      </BusinessSheet>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: HB_COLORS.background },
  message: {
    flex: 1,
    gap: 16,
    padding: 24,
    justifyContent: "center",
    backgroundColor: HB_COLORS.background,
  },
  camera: { height: 300 },
  hiddenInput: {
    position: "absolute",
    width: 1,
    height: 1,
    opacity: 0,
    left: -100,
  },
});
