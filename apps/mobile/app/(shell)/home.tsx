import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  FlatList,
  Pressable,
  StyleSheet,
  TextInput,
  useWindowDimensions,
  View,
} from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQuery } from "@tanstack/react-query";
import { CameraView } from "expo-camera";
import { type Href, useRouter } from "expo-router";
import { useFocusEffect, useIsFocused } from "@react-navigation/native";
import {
  Button,
  Card,
  Menu,
  Modal,
  Portal,
  Searchbar,
  Text,
  TextInput as PaperTextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { AnimatedEmptyStateGraphic } from "@/components/ui/AnimatedEmptyStateGraphic";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { EmptyState } from "@/components/ui/EmptyState";
import { LoadingOverlay } from "@/components/ui/LoadingOverlay";
import { CameraScanSheet } from "@/components/ui/CameraScanSheet";
import {
  OrderBarIconButton,
  OrderBarPrimaryButton,
  OrderBottomBar,
} from "@/components/order/OrderBottomBar";
import { OrderProductRow } from "@/components/order/OrderProductRow";
import { GradeTag, OrderThumbnail } from "@/components/order/OrderTags";
import { QuantityPresetRow } from "@/components/order/QuantityPresetRow";
import {
  mapScanFeedbackToNotice,
  ORDER_NOTICE_DURATION_MS,
  type OrderNotice,
} from "@/components/order/order-notice";
import {
  formatOrderMoney,
  ORDER_COLORS,
  resolveTotalPages,
} from "@/components/order/order-ui";
import { ORDER_MONO_FONT } from "@/components/order/order-fonts";
import { getCategoryTree } from "@/modules/shop/api";
import {
  canSubmitCartQuantityEdit,
  parseCartQuantityInput,
  shouldSubmitCartQuantityUpdate,
} from "@/modules/shop/cart-quantity-input";
import { shouldClearActiveCartMutation } from "@/modules/shop/cart-mutation-state";
import {
  resolveMinimumOrderQuantity,
  useAddToCart,
} from "@/modules/shop/use-add-to-cart";
import { useCartSummary } from "@/modules/shop/use-cart-summary";
import { useProductGrades } from "@/modules/shop/use-product-grades";
import { isLocationLookupEnabled } from "@/modules/shop/location-lookup";
import { resolveHomeProductColumns } from "@/modules/shop/home-layout";
import { useProducts } from "@/modules/shop/use-products";
import { useStores } from "@/modules/shop/use-stores";
import { useUpdateCartQuantity } from "@/modules/shop/use-update-cart-quantity";
import {
  buildCategoryNameMap,
  buildHomeProductQuery,
  flattenVisibleCategories,
  resolveHomeSearchPageState,
  type HomeSearchPageAction,
  type VisibleCategoryRow,
} from "@/modules/shop/home-filters";
import {
  useCameraScan,
  type CameraScanMode,
} from "@/modules/scanner/use-camera-scan";
import {
  createCameraSheetSession,
  isCameraSheetSessionActive,
  reduceCameraSheetSession,
} from "@/modules/scanner/camera-sheet-session";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { useScanResult } from "@/modules/scanner/use-scan-result";
import { useVisibleSearchScannerInput } from "@/modules/scanner/use-visible-search-scanner-input";
import { ScanResultPicker } from "@/components/ui/ScanResultPicker";
import type {
  StoreOrderCategoryNode,
  StoreOrderProductItem,
} from "@/modules/shop/types";
import { useCartStore } from "@/store/cart-store";
import { isPreorderRequiredError } from "@/modules/preorder/api";
import { canBypassPreorderGate } from "@/modules/preorder/gate";
import { PreorderGateBanner } from "@/modules/preorder/preorder-gate-banner";
import { SupplyRestockedBanner } from "@/modules/supply-notice/supply-restocked-banner";
import { SupplyStatusCard } from "@/modules/supply-notice/supply-status-card";
import { SupplyWatchesSheet } from "@/modules/supply-notice/supply-watches-sheet";
import type { StoreSupplyStatus } from "@/modules/supply-notice/types";
import { useSupplyStatusLookup, useSupplyWatchSummary } from "@/modules/supply-notice/use-supply-status";
import { usePreorderGate } from "@/modules/preorder/use-preorder-gate";
import { useAuthStore } from "@/store/auth-store";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

const HOME_PAGE_SIZE = 18;

function resolveDisplayCategories(tree: StoreOrderCategoryNode[]) {
  const allNode = tree.find((item) =>
    item.categoryName.toLowerCase().includes("all"),
  );
  return allNode?.children?.length ? allNode.children : tree;
}

function normalizeGradeValue(value: string | null | undefined) {
  return value?.trim().toUpperCase() || "";
}

function normalizeStoreCode(value: string | null | undefined) {
  const normalized = value?.trim();
  return normalized ? normalized : null;
}

interface FilterChipProps {
  label: string;
  value: string;
  active: boolean;
  onPress: () => void;
}

/** 页头筛选芯片：左侧是筛选维度，右侧是当前取值，生效时换成浅蓝底。 */
function FilterChip({ label, value, active, onPress }: FilterChipProps) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${label} ${value}`}
      onPress={onPress}
      style={({ pressed }) => [
        styles.filterChip,
        active ? styles.filterChipActive : null,
        pressed ? styles.filterChipPressed : null,
      ]}
    >
      <Text style={styles.filterChipLabel}>{label}</Text>
      <Text numberOfLines={1} style={[styles.filterChipValue, active ? styles.filterChipValueActive : null]}>
        {value}
      </Text>
      <MaterialCommunityIcons name="chevron-down" size={16} color={HB_COLORS.textSecondary} />
    </Pressable>
  );
}

interface AutoAddToggleProps {
  label: string;
  value: boolean;
  accessibilityLabel: string;
  onToggle: () => void;
}

/** 自动加购决定扫码后会发生什么，单独做成开关，状态始终可见。 */
function AutoAddToggle({ label, value, accessibilityLabel, onToggle }: AutoAddToggleProps) {
  return (
    <Pressable
      accessibilityRole="switch"
      accessibilityState={{ checked: value }}
      accessibilityLabel={accessibilityLabel}
      onPress={onToggle}
      style={({ pressed }) => [
        styles.autoAdd,
        value ? styles.autoAddOn : null,
        pressed ? styles.filterChipPressed : null,
      ]}
    >
      <Text style={[styles.autoAddText, value ? styles.autoAddTextOn : null]}>{label}</Text>
      <View style={[styles.switchTrack, value ? styles.switchTrackOn : null]}>
        <View style={[styles.switchKnob, value ? styles.switchKnobOn : null]} />
      </View>
    </Pressable>
  );
}

export default function Home() {
  const isFocused = useIsFocused();
  const { t, language } = useAppTranslation(["home", "common", "supplyNotice"]);
  const { width: windowWidth } = useWindowDimensions();
  const productColumns = resolveHomeProductColumns(windowWidth);
  const router = useRouter();
  const {
    stores,
    selectedStore,
    selectedStoreCode,
    selectStore,
    isLoading: storesLoading,
    isError: storesLoadFailed,
    error: storesError,
    refetch: refetchStores,
  } = useStores();
  const cartSummary = useCartStore((state) => state.cartSummary);
  const access = useAuthStore((state) => state.access);
  const locationLookupEnabled = isLocationLookupEnabled(access);
  const [cameraScanMode, setCameraScanMode] =
    useState<CameraScanMode>("single");
  const [cameraSession, setCameraSession] = useState(() =>
    createCameraSheetSession("single"),
  );
  const cameraSheetSessionRef = useRef(cameraSession);
  const cameraScanModeRef = useRef(cameraScanMode);
  const isFocusedRef = useRef(isFocused);
  const cameraResultGenerationRef = useRef<number | null>(null);
  const [cameraScanHandling, setCameraScanHandling] = useState(false);
  const [cameraSelectionConfirming, setCameraSelectionConfirming] =
    useState(false);
  const [lastCameraScan, setLastCameraScan] = useState<{
    product: StoreOrderProductItem;
    barcode: string;
  } | null>(null);
  const [lastCameraBarcode, setLastCameraBarcode] = useState<string | null>(
    null,
  );
  const cameraVisible = cameraSession.visible;
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [filtersVisible, setFiltersVisible] = useState(false);
  const [gradeFilterVisible, setGradeFilterVisible] = useState(false);
  const [searchInput, setSearchInput] = useState("");
  const [autoAddWhenSingle, setAutoAddWhenSingle] = useState(true);
  const [keyword, setKeyword] = useState("");
  const [scannedProducts, setScannedProducts] = useState<
    StoreOrderProductItem[] | null
  >(null);
  const [scannedProductTraceIds, setScannedProductTraceIds] = useState<
    Record<string, string>
  >({});
  const [selectedCategoryGUID, setSelectedCategoryGUID] = useState<
    string | undefined
  >();
  const [selectedGrade, setSelectedGrade] = useState<string | undefined>();
  const [expandedCategoryGUIDs, setExpandedCategoryGUIDs] = useState<string[]>(
    [],
  );
  const [pageNumber, setPageNumber] = useState(1);
  isFocusedRef.current = isFocused;
  cameraScanModeRef.current = cameraScanMode;
  const searchReturnPageRef = useRef<number | null>(null);
  const [notice, setNotice] = useState<OrderNotice | null>(null);
  const [scanBarcode, setScanBarcode] = useState<string | null>(null);
  const [activeCartMutationProductCode, setActiveCartMutationProductCode] =
    useState<string | null>(null);
  const [quantityEditorProduct, setQuantityEditorProduct] =
    useState<StoreOrderProductItem | null>(null);
  const [quantityEditorStoreCode, setQuantityEditorStoreCode] = useState<
    string | null
  >(null);
  const [quantityDraft, setQuantityDraft] = useState("");
  const [quantityEditorError, setQuantityEditorError] = useState("");
  const [preorderPromptVisible, setPreorderPromptVisible] = useState(false);
  const promptedPreorderKeyRef = useRef("");
  const getErrorMessage = useCallback(
    (error: unknown, fallbackKey: string) =>
      resolveLocalizedErrorMessage(error, {
        language,
        t,
        fallbackKey,
      }),
    [language, t],
  );
  const addToCart = useAddToCart(selectedStoreCode);
  const updateCartQuantity = useUpdateCartQuantity(selectedStoreCode);
  const quantityEditorBusy = Boolean(
    quantityEditorProduct && updateCartQuantity.isPending,
  );
  const selectedStoreCodeRef = useRef<string | null>(
    normalizeStoreCode(selectedStoreCode),
  );
  const resumeHiddenScannerFocusTimerRef = useRef<ReturnType<
    typeof setTimeout
  > | null>(null);
  const preorderGate = usePreorderGate(
    selectedStoreCode,
    canBypassPreorderGate(access),
  );

  const openPreorder = useCallback(() => {
    setPreorderPromptVisible(false);
    const firstActivation = preorderGate.activations[0];
    if (firstActivation && selectedStoreCode) {
      router.push({
        pathname: "/preorders/[activationGuid]",
        params: {
          activationGuid: firstActivation.activationGuid,
          storeCode: selectedStoreCode,
        },
      } as unknown as Href);
      return;
    }
    router.push("/preorders" as Href);
  }, [preorderGate.activations, router, selectedStoreCode]);

  const handleNormalOrderError = useCallback(
    (error: unknown, fallbackKey: string) => {
      if (isPreorderRequiredError(error)) {
        void preorderGate.refresh();
        openPreorder();
        return;
      }
      setNotice({ tone: "error", title: getErrorMessage(error, fallbackKey) });
    },
    [getErrorMessage, openPreorder, preorderGate],
  );

  useEffect(() => {
    if (
      !selectedStoreCode ||
      !preorderGate.normalOrderBlocked ||
      !preorderGate.activations.length
    ) {
      return;
    }
    const promptKey = `${selectedStoreCode}:${preorderGate.activations.map((item) => item.activationGuid).join(",")}`;
    if (promptedPreorderKeyRef.current === promptKey) return;
    promptedPreorderKeyRef.current = promptKey;
    setPreorderPromptVisible(true);
  }, [
    preorderGate.activations,
    preorderGate.normalOrderBlocked,
    selectedStoreCode,
  ]);

  selectedStoreCodeRef.current = normalizeStoreCode(selectedStoreCode);

  useCartSummary(selectedStoreCode);
  const clearAppliedSearchForScan = useCallback(() => {
    if (keyword) {
      // 扫码退出文字搜索时保留既有行为：回到第一页，不复用文字搜索的返回页。
      setPageNumber(1);
    }
    searchReturnPageRef.current = null;
    setKeyword("");
  }, [keyword]);
  const handleScanLookupProduct = useCallback(
    async (
      product: StoreOrderProductItem,
      barcode?: string,
      source?: unknown,
      scanTraceId?: string,
      scanStoreCode?: string | null,
    ) => {
      const expectedStoreCode = normalizeStoreCode(
        scanStoreCode ?? selectedStoreCodeRef.current,
      );
      if (selectedStoreCodeRef.current !== expectedStoreCode) {
        return;
      }

      setSearchInput("");
      clearAppliedSearchForScan();
      setScanBarcode(barcode || product.productCode);
      setScannedProducts([product]);
      setScannedProductTraceIds(
        scanTraceId ? { [product.productCode]: scanTraceId } : {},
      );
      setSelectedCategoryGUID(undefined);
      setSelectedGrade(undefined);
      if (source === "camera") {
        setLastCameraScan({ product, barcode: barcode || product.productCode });
      }

      if (!autoAddWhenSingle) {
        return;
      }

      setActiveCartMutationProductCode(product.productCode);
      try {
        await addToCart.mutateAsync({
          product,
          quantity: resolveMinimumOrderQuantity(product),
          scanTraceId,
        });
        if (selectedStoreCodeRef.current !== expectedStoreCode) {
          return;
        }

        setNotice({
          tone: "success",
          title: t("common:orderNotice.addedNamed", {
            name: product.productName || product.productCode,
          }),
          detail: t("common:orderNotice.addedQuantity", {
            quantity: resolveMinimumOrderQuantity(product),
          }),
        });
      } catch (error) {
        if (selectedStoreCodeRef.current !== expectedStoreCode) {
          return;
        }

        handleNormalOrderError(error, "messages.scanAddFailed");
      } finally {
        if (
          shouldClearActiveCartMutation(
            selectedStoreCodeRef.current,
            expectedStoreCode,
          )
        ) {
          setActiveCartMutationProductCode(null);
        }
      }
    },
    [
      addToCart,
      autoAddWhenSingle,
      clearAppliedSearchForScan,
      handleNormalOrderError,
      t,
    ],
  );
  const handleScanAddedProduct = useCallback(
    (
      product: StoreOrderProductItem,
      barcode?: string,
      source?: unknown,
      scanTraceId?: string,
    ) => {
      setSearchInput("");
      clearAppliedSearchForScan();
      setScanBarcode(barcode || product.productCode);
      setScannedProducts([product]);
      setScannedProductTraceIds(
        scanTraceId ? { [product.productCode]: scanTraceId } : {},
      );
      setSelectedCategoryGUID(undefined);
      setSelectedGrade(undefined);
      if (source === "camera") {
        setLastCameraScan({ product, barcode: barcode || product.productCode });
      }
    },
    [clearAppliedSearchForScan],
  );
  const scanResult = useScanResult({
    autoAddWhenSingle,
    mode: autoAddWhenSingle ? "add-to-cart" : "lookup",
    onAddedToCart: handleScanAddedProduct,
    onProductFound: handleScanLookupProduct,
    storeCode: selectedStoreCode,
  });
  const updateCameraSheetSession = useCallback(
    (
      event: Parameters<typeof reduceCameraSheetSession>[1],
      mode = cameraScanModeRef.current,
    ) => {
      const next = reduceCameraSheetSession(
        cameraSheetSessionRef.current,
        event,
        mode,
      );
      cameraSheetSessionRef.current = next;
      setCameraSession(next);
      return next;
    },
    [],
  );
  const cameraScan = useCameraScan({
    disabled: !isFocused || !cameraSession.visible,
    ignoreWhileProcessing: cameraScanMode === "continuous",
    resetKey: `${isFocused ? "focused" : "blurred"}:${cameraSession.generation}:${cameraScanMode}:${selectedStoreCode ?? ""}:${autoAddWhenSingle ? "add-to-cart" : "lookup"}`,
    onBarcode: async (barcode) => {
      const session = cameraSheetSessionRef.current;
      if (
        !isFocusedRef.current ||
        !isCameraSheetSessionActive(session, session.generation)
      ) {
        return;
      }
      const generation = session.generation;
      // 新一笔相机任务不能继续显示上一笔命中；队列的失败反馈会在 sheet 内按条码呈现。
      setLastCameraBarcode(barcode);
      setLastCameraScan(null);
      // Native sheet 会遮住扫码结果弹层；每次命中先卸载相机，连续模式仅在结果处理完成后恢复。
      setCameraScanHandling(true);
      cameraResultGenerationRef.current = generation;
      const captured = updateCameraSheetSession(
        { type: "capture", generation },
        cameraScanModeRef.current,
      );
      if (captured === session) {
        setCameraScanHandling(false);
        return;
      }
      try {
        await scanResult.handleBarcode(barcode, "camera");
      } finally {
        if (cameraSheetSessionRef.current.generation === generation) {
          setCameraScanHandling(false);
          updateCameraSheetSession(
            {
              type: "foreground-complete",
              focused: isFocusedRef.current,
              generation,
            },
            cameraScanModeRef.current,
          );
        }
      }
    },
  });
  const handleScanBarcode = scanResult.handleBarcode;
  const lastHidScanRef = useRef<{ barcode: string; time: number } | null>(
    null,
  );
  const handleHidScan = useCallback(
    async (barcode: string) => {
      const now = Date.now();
      const lastScan = lastHidScanRef.current;
      // 同一次扫码可能同时被原生按键监听和可见搜索框收到；加购模式无去重，必须在此拦截避免重复加购。
      if (lastScan?.barcode === barcode && now - lastScan.time < 100) {
        return;
      }
      lastHidScanRef.current = { barcode, time: now };
      await handleScanBarcode(barcode, "hid");
    },
    [handleScanBarcode],
  );
  const hidScanner = useHidBarcodeScanner({
    enabled: isFocused,
    onScan: handleHidScan,
  });
  const pauseHiddenScannerFocus = useCallback(() => {
    if (resumeHiddenScannerFocusTimerRef.current) {
      clearTimeout(resumeHiddenScannerFocusTimerRef.current);
      resumeHiddenScannerFocusTimerRef.current = null;
    }

    // 手动输入搜索或数量时暂停隐藏扫码输入，避免它定时抢走当前输入框焦点。
    hidScanner.pauseHiddenInputFocus();
  }, [hidScanner.pauseHiddenInputFocus]);
  const resumeHiddenScannerFocusSoon = useCallback(() => {
    if (resumeHiddenScannerFocusTimerRef.current) {
      clearTimeout(resumeHiddenScannerFocusTimerRef.current);
    }

    // 给 iOS 输入收尾留出短暂时间，再恢复扫码隐藏输入焦点。
    resumeHiddenScannerFocusTimerRef.current = setTimeout(() => {
      resumeHiddenScannerFocusTimerRef.current = null;
      hidScanner.resumeHiddenInputFocus();
    }, 250);
  }, [hidScanner.resumeHiddenInputFocus]);
  const handleSearchFocus = pauseHiddenScannerFocus;
  const handleSearchBlur = resumeHiddenScannerFocusSoon;

  useFocusEffect(
    useCallback(() => {
      if (hidScanner.focusHiddenInput) {
        hidScanner.focusHiddenInput();
      }
    }, [hidScanner.focusHiddenInput]),
  );

  useEffect(() => {
    if (!isFocused) {
      // 离开订货页时卸载相机 sheet，防止连续扫码会话在后台继续占用相机。
      cameraResultGenerationRef.current = null;
      setCameraScanHandling(false);
      updateCameraSheetSession({ type: "blur" }, cameraScanModeRef.current);
    }
  }, [isFocused, updateCameraSheetSession]);

  useEffect(
    () => () => {
      if (resumeHiddenScannerFocusTimerRef.current) {
        clearTimeout(resumeHiddenScannerFocusTimerRef.current);
      }
    },
    [],
  );

  const categoriesQuery = useQuery({
    queryKey: ["shopCategories"],
    queryFn: getCategoryTree,
    staleTime: 30 * 60 * 1000,
  });

  const categoryOptions = useMemo(
    () => resolveDisplayCategories(categoriesQuery.data ?? []),
    [categoriesQuery.data],
  );
  const categoryNameMap = useMemo(
    () => buildCategoryNameMap(categoryOptions),
    [categoryOptions],
  );
  const visibleCategoryRows = useMemo(
    () => flattenVisibleCategories(categoryOptions, expandedCategoryGUIDs),
    [categoryOptions, expandedCategoryGUIDs],
  );
  const selectedCategoryName = useMemo(
    () =>
      (selectedCategoryGUID
        ? categoryNameMap.get(selectedCategoryGUID)
        : undefined) ?? t("filters.all"),
    [categoryNameMap, selectedCategoryGUID, t],
  );
  const productGradesQuery = useProductGrades();
  const gradeOptions = useMemo(
    () => productGradesQuery.data ?? [],
    [productGradesQuery.data],
  );
  const selectedGradeLabel = useMemo(() => {
    if (!selectedGrade) {
      return t("filters.all");
    }

    return (
      gradeOptions.find(
        (item) => normalizeGradeValue(item.value) === selectedGrade,
      )?.label ?? selectedGrade
    );
  }, [gradeOptions, selectedGrade, t]);
  const productQuery = useMemo(
    () =>
      buildHomeProductQuery({
        storeCode: selectedStoreCode,
        keyword,
        categoryGUID: selectedCategoryGUID,
        grade: selectedGrade,
        pageNumber,
        pageSize: HOME_PAGE_SIZE,
      }),
    [
      keyword,
      pageNumber,
      selectedCategoryGUID,
      selectedGrade,
      selectedStoreCode,
    ],
  );
  const productsQuery = useProducts(productQuery, locationLookupEnabled);

  useEffect(() => {
    searchReturnPageRef.current = null;
    setPageNumber(1);
  }, [selectedCategoryGUID, selectedGrade, selectedStoreCode]);

  useEffect(() => {
    // 门店切换后清空旧门店扫码结果，避免迟到的加购反馈落到新门店界面。
    setScannedProducts(null);
    setScannedProductTraceIds({});
  }, [selectedStoreCode]);

  useEffect(() => {
    const currentStoreCode = normalizeStoreCode(selectedStoreCode);
    if (
      !quantityEditorStoreCode ||
      quantityEditorStoreCode === currentStoreCode
    ) {
      return;
    }

    // 门店切换后关闭旧门店数量编辑器，防止旧商品数量写入新门店订货车。
    setQuantityEditorProduct(null);
    setQuantityEditorStoreCode(null);
    setQuantityDraft("");
    setQuantityEditorError("");
    resumeHiddenScannerFocusSoon();
  }, [
    quantityEditorStoreCode,
    resumeHiddenScannerFocusSoon,
    selectedStoreCode,
  ]);

  useEffect(() => {
    const nextNotice = mapScanFeedbackToNotice(scanResult.feedback, t);
    if (nextNotice) {
      setNotice(nextNotice);
    }
  }, [scanResult.feedback, t]);

  useEffect(() => {
    if (!storesLoadFailed) {
      return;
    }

    setNotice({ tone: "error", title: getErrorMessage(storesError, "messages.storesLoadFailed") });
  }, [getErrorMessage, storesError, storesLoadFailed]);

  useEffect(() => {
    if (!productsQuery.isError) {
      return;
    }

    setNotice({
      tone: "error",
      title: getErrorMessage(productsQuery.error, "messages.productsLoadFailed"),
    });
  }, [getErrorMessage, productsQuery.error, productsQuery.isError]);

  useEffect(() => {
    if (!notice) {
      return;
    }

    // 提示在操作栏停留 2.5 秒后回到购物车汇总，位置固定不推动列表。
    const dismissTimer = setTimeout(() => {
      setNotice(null);
    }, ORDER_NOTICE_DURATION_MS);

    return () => {
      clearTimeout(dismissTimer);
    };
  }, [notice]);

  const canGoNextPage = useMemo(() => {
    const total = productsQuery.data?.total ?? 0;
    return pageNumber * HOME_PAGE_SIZE < total;
  }, [pageNumber, productsQuery.data?.total]);
  const hasNoAssignedStores =
    !storesLoading &&
    !storesLoadFailed &&
    stores.length === 0 &&
    !selectedStoreCode;
  const displayProducts = useMemo(() => {
    if (scannedProducts?.length) {
      return selectedGrade
        ? scannedProducts.filter(
            (product) => normalizeGradeValue(product.grade) === selectedGrade,
          )
        : scannedProducts;
    }

    return productsQuery.data?.items ?? [];
  }, [productsQuery.data?.items, scannedProducts, selectedGrade]);
  // 关注恢复订货：汇总用于横幅与入口角标；搜索零结果时补查暂停供货商品，让分店知道是暂停供货而不是搜错。
  const supplyWatchSummary = useSupplyWatchSummary(selectedStoreCode ?? null);
  const [supplyWatchesVisible, setSupplyWatchesVisible] = useState(false);
  const supplyLookupEnabled =
    Boolean(selectedStoreCode) &&
    Boolean(keyword.trim()) &&
    !scannedProducts?.length &&
    productsQuery.isSuccess &&
    (productsQuery.data?.items.length ?? 0) === 0;
  const supplyLookup = useSupplyStatusLookup(selectedStoreCode ?? null, keyword, supplyLookupEnabled);
  const handleSupplyWatchToggle = useCallback(
    async (status: StoreSupplyStatus, watch: boolean) => {
      const ok = await supplyLookup.toggleWatch(status, watch);
      setNotice(
        ok
          ? { tone: "success", title: t(watch ? "supplyNotice:watchDone" : "supplyNotice:unwatchDone") }
          : { tone: "error", title: t("supplyNotice:actionFailed") },
      );
    },
    [supplyLookup, t],
  );
  const displayDynamicDataMap = useMemo(() => {
    const mergedMap = { ...productsQuery.dynamicDataMap };

    if (!scannedProducts?.length || !cartSummary?.items?.length) {
      return mergedMap;
    }

    scannedProducts.forEach((product) => {
      const cartItem = cartSummary.items.find(
        (item) => item.productCode === product.productCode,
      );
      if (!cartItem) {
        return;
      }

      mergedMap[product.productCode] = {
        ...mergedMap[product.productCode],
        productCode: product.productCode,
        cartQuantity: cartItem.quantity ?? 0,
      };
    });

    return mergedMap;
  }, [cartSummary?.items, productsQuery.dynamicDataMap, scannedProducts]);
  const applySearchPageAction = useCallback(
    (action: HomeSearchPageAction) => {
      const nextState = resolveHomeSearchPageState(
        {
          keyword,
          pageNumber,
          returnPageNumber: searchReturnPageRef.current,
        },
        action,
      );

      searchReturnPageRef.current = nextState.returnPageNumber;
      setKeyword(nextState.keyword);
      setPageNumber(nextState.pageNumber);
    },
    [keyword, pageNumber],
  );
  const handleApplySearch = useCallback(() => {
    setScannedProducts(null);
    // 搜索框可能接收到同一扫码枪输入，保留商品 trace 让同商品数量调整继续走 scan-update。
    if (!searchInput.trim()) {
      setSearchInput("");
    }
    applySearchPageAction({ type: "apply", input: searchInput });
  }, [applySearchPageAction, searchInput]);
  const handleSearchInputChange = useCallback(
    (value: string) => {
      setSearchInput(value);
      setScannedProducts(null);
      if (!value.trim()) {
        applySearchPageAction({ type: "clear" });
      }
    },
    [applySearchPageAction],
  );
  const handleSupplyOrder = useCallback(
    (status: StoreSupplyStatus) => {
      // 商品已恢复订货：按货号搜索即可在正常列表里命中。
      const code = status.itemNumber || status.productCode;
      setScannedProducts(null);
      setSearchInput(code);
      applySearchPageAction({ type: "apply", input: code });
    },
    [applySearchPageAction],
  );
  // 扫到暂停供货的商品：把条码转成搜索词，零结果处展示后续计划与关注按钮。
  const scanFeedback = scanResult.feedback;
  useEffect(() => {
    if (scanFeedback.status !== "supply_paused" || !scanFeedback.barcode) {
      return;
    }
    setScannedProducts(null);
    setSearchInput(scanFeedback.barcode);
    applySearchPageAction({ type: "apply", input: scanFeedback.barcode });
  }, [applySearchPageAction, scanFeedback]);
  const handleVisibleSearchScan = useCallback(
    (barcode: string) => {
      // 扫码不作为文字关键字：清空搜索框（原生文本由 hook 清空），避免与旧关键字拼接。
      setSearchInput("");
      void handleHidScan(barcode);
    },
    [handleHidScan],
  );
  const visibleSearchScanner = useVisibleSearchScannerInput<
    React.ElementRef<typeof Searchbar>
  >({
    value: searchInput,
    onChangeText: handleSearchInputChange,
    onScannerInput: handleVisibleSearchScan,
  });
  const { blurSearchInput } = visibleSearchScanner;
  const handleSearchSubmit = useCallback(() => {
    // 提交后释放焦点，让隐藏扫码输入框恢复接收下一次扫码。
    blurSearchInput();
    handleApplySearch();
  }, [blurSearchInput, handleApplySearch]);
  const handleClearSearchAndScan = useCallback(() => {
    setScannedProducts(null);
    setScannedProductTraceIds({});
    setScanBarcode(null);
    setSearchInput("");
    setKeyword("");
    setSelectedGrade(undefined);
  }, []);
  const handleCameraScanModeChange = useCallback((mode: CameraScanMode) => {
    cameraScanModeRef.current = mode;
    setCameraScanMode(mode);
    const next = {
      ...cameraSheetSessionRef.current,
      resumeRequested: mode === "continuous",
    };
    cameraSheetSessionRef.current = next;
    setCameraSession(next);
  }, []);

  const handleToggleAutoAdd = useCallback(() => {
    const nextValue = !autoAddWhenSingle;
    setAutoAddWhenSingle(nextValue);
    // 切换后在操作栏说明扫码会发生什么，避免店员误以为扫码没反应。
    setNotice({
      tone: "info",
      title: nextValue ? t("autoAddOn") : t("autoAddOff"),
      detail: nextValue ? t("autoAddOnDetail") : t("autoAddOffDetail"),
    });
  }, [autoAddWhenSingle, t]);

  const toggleCategoryExpanded = useCallback((categoryGUID: string) => {
    setExpandedCategoryGUIDs((currentValue) =>
      currentValue.includes(categoryGUID)
        ? currentValue.filter((item) => item !== categoryGUID)
        : [...currentValue, categoryGUID],
    );
  }, []);

  const handleSelectCategoryFilter = useCallback((categoryGUID?: string) => {
    setSelectedCategoryGUID((currentValue) =>
      currentValue === categoryGUID ? undefined : categoryGUID,
    );
    // 与分类同一次更新里回到第一页：等 effect 再重置会先按旧页码多发一次商品请求。
    searchReturnPageRef.current = null;
    setPageNumber(1);
    // 分类选择完成后回到商品列表；展开箭头不走这个回调，保持只展开分类树。
    setFiltersVisible(false);
  }, []);

  const renderCategoryRow = useCallback(
    ({ item }: { item: VisibleCategoryRow }) => {
      const { node, depth, hasChildren, isExpanded } = item;
      const isSelected = selectedCategoryGUID === node.categoryGUID;

      return (
        <View
          key={node.categoryGUID}
          style={[
            styles.categoryTreeRow,
            isSelected ? styles.categoryRowSelected : null,
          ]}
        >
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ selected: isSelected }}
            onPress={() => handleSelectCategoryFilter(node.categoryGUID)}
            style={[styles.categoryTreeLabelButton, { paddingLeft: 12 + depth * 20 }]}
          >
            <Text
              numberOfLines={1}
              style={[
                styles.categoryLabel,
                depth === 0 ? styles.categoryLabelRoot : null,
                isSelected ? styles.categoryLabelSelected : null,
              ]}
            >
              {node.categoryName}
            </Text>
            {isSelected ? (
              <MaterialCommunityIcons name="check" size={18} color={HB_COLORS.action} />
            ) : null}
          </Pressable>
          {hasChildren ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={node.categoryName}
              accessibilityState={{ expanded: isExpanded }}
              onPress={() => toggleCategoryExpanded(node.categoryGUID)}
              style={styles.categoryToggle}
            >
              <MaterialCommunityIcons
                name={isExpanded ? "chevron-down" : "chevron-right"}
                size={20}
                color={HB_COLORS.textSecondary}
              />
            </Pressable>
          ) : null}
        </View>
      );
    },
    [handleSelectCategoryFilter, selectedCategoryGUID, toggleCategoryExpanded],
  );

  async function handleAddToCart(product: StoreOrderProductItem) {
    const mutationStoreCode = selectedStoreCodeRef.current;
    setActiveCartMutationProductCode(product.productCode);
    try {
      await addToCart.mutateAsync({
        product,
        // 扫码结果手动加购继续透传 trace，方便后端日志串起同一次扫码链路。
        scanTraceId: scannedProductTraceIds[product.productCode],
      });
      setNotice({
        tone: "success",
        title: t("common:orderNotice.addedNamed", {
          name: product.productName || product.productCode,
        }),
        detail: t("common:orderNotice.addedQuantity", {
          quantity: resolveMinimumOrderQuantity(product),
        }),
      });
    } catch (error) {
      handleNormalOrderError(error, "messages.addFailed");
    } finally {
      if (
        shouldClearActiveCartMutation(
          selectedStoreCodeRef.current,
          mutationStoreCode,
        )
      ) {
        setActiveCartMutationProductCode(null);
      }
    }
  }

  function getCurrentCartQuantity(productCode: string) {
    return displayDynamicDataMap[productCode]?.cartQuantity ?? 0;
  }

  function handleEditCartQuantity(
    product: StoreOrderProductItem,
    currentQuantity: number,
  ) {
    pauseHiddenScannerFocus();
    setQuantityEditorProduct(product);
    setQuantityEditorStoreCode(selectedStoreCodeRef.current);
    setQuantityDraft(String(currentQuantity));
    setQuantityEditorError("");
  }

  function resetQuantityEditor() {
    setQuantityEditorProduct(null);
    setQuantityEditorStoreCode(null);
    setQuantityDraft("");
    setQuantityEditorError("");
    resumeHiddenScannerFocusSoon();
  }

  function handleDismissQuantityEditor() {
    if (quantityEditorBusy) {
      return;
    }

    resetQuantityEditor();
  }

  async function handleConfirmQuantityEdit() {
    if (!quantityEditorProduct || quantityEditorBusy) {
      return;
    }

    if (
      !canSubmitCartQuantityEdit({
        currentStoreCode: selectedStoreCodeRef.current,
        editorStoreCode: quantityEditorStoreCode,
        isPending: updateCartQuantity.isPending,
      })
    ) {
      // 分店已切换或已有提交在路上时不继续写后端，避免编辑弹窗串门店或重复提交。
      if (!updateCartQuantity.isPending) {
        resetQuantityEditor();
      }
      return;
    }

    const nextQuantity = parseCartQuantityInput(quantityDraft);
    if (nextQuantity === null) {
      setQuantityEditorError(t("quantityEditor.invalidQuantity"));
      return;
    }

    const product = quantityEditorProduct;
    const currentQuantity = getCurrentCartQuantity(product.productCode);
    if (!shouldSubmitCartQuantityUpdate(currentQuantity, nextQuantity)) {
      // 数量没有变化时不触发后端写入，避免 0 -> 0 这类无意义更新落成零数量明细。
      resetQuantityEditor();
      return;
    }

    const mutationStoreCode = quantityEditorStoreCode;
    setQuantityEditorError("");
    setActiveCartMutationProductCode(product.productCode);

    try {
      // 复用 useUpdateCartQuantity 的乐观快照：onMutate 立即改数量，失败由 onError 回滚。
      await updateCartQuantity.mutateAsync({
        nextQuantity,
        product,
        scanTraceId: scannedProductTraceIds[product.productCode],
      });
      resetQuantityEditor();
    } catch (error) {
      if (isPreorderRequiredError(error)) {
        resetQuantityEditor();
        void preorderGate.refresh();
        openPreorder();
      } else {
        const message = getErrorMessage(error, "messages.updateQtyFailed");
        setQuantityEditorError(message);
        setNotice({ tone: "error", title: message });
      }
    } finally {
      if (
        shouldClearActiveCartMutation(
          selectedStoreCodeRef.current,
          mutationStoreCode,
        )
      ) {
        setActiveCartMutationProductCode(null);
      }
    }
  }

  async function handleIncreaseCartQuantity(product: StoreOrderProductItem) {
    const mutationStoreCode = selectedStoreCodeRef.current;
    const step = resolveMinimumOrderQuantity(product);
    const nextQuantity = getCurrentCartQuantity(product.productCode) + step;
    setActiveCartMutationProductCode(product.productCode);

    try {
      await updateCartQuantity.mutateAsync({
        nextQuantity,
        product,
        scanTraceId: scannedProductTraceIds[product.productCode],
      });
    } catch (error) {
      handleNormalOrderError(error, "messages.updateQtyFailed");
    } finally {
      if (
        shouldClearActiveCartMutation(
          selectedStoreCodeRef.current,
          mutationStoreCode,
        )
      ) {
        setActiveCartMutationProductCode(null);
      }
    }
  }

  async function handleDecreaseCartQuantity(
    product: StoreOrderProductItem,
    currentQuantity: number,
  ) {
    const mutationStoreCode = selectedStoreCodeRef.current;
    const step = resolveMinimumOrderQuantity(product);
    const nextQuantity = Math.max(0, currentQuantity - step);
    setActiveCartMutationProductCode(product.productCode);

    try {
      await updateCartQuantity.mutateAsync({
        nextQuantity,
        product,
        scanTraceId: scannedProductTraceIds[product.productCode],
      });
    } catch (error) {
      handleNormalOrderError(error, "messages.updateQtyFailed");
    } finally {
      if (
        shouldClearActiveCartMutation(
          selectedStoreCodeRef.current,
          mutationStoreCode,
        )
      ) {
        setActiveCartMutationProductCode(null);
      }
    }
  }

  const totalProducts = productsQuery.data?.total ?? 0;
  const totalPages = resolveTotalPages(totalProducts, HOME_PAGE_SIZE);
  // Zebra TC26 等 360dp 窄屏收紧缩略图和步进器宽度，保证货号与价格不被挤掉。
  const compactRows = windowWidth <= 390;
  const selectedStoreName =
    selectedStore?.storeName || t("common:labels.selectStore");
  const showScanResultHeader = Boolean(scannedProducts?.length);
  const gradeChipValue = selectedGrade
    ? t("gradeMenu.gradeValue", { grade: selectedGradeLabel })
    : t("filters.all");
  const cartSkuCount = cartSummary?.totalSku ?? 0;
  const cartQuantityTotal = cartSummary?.totalQuantity ?? 0;
  const cartImportTotal = Number(cartSummary?.totalImportAmount ?? 0);

  const restockedCount = supplyWatchSummary.data?.restockedCount ?? 0;

  const handleOpenCameraSheet = () => {
    cameraResultGenerationRef.current = null;
    setCameraScanHandling(false);
    updateCameraSheetSession({ type: "open" }, cameraScanModeRef.current);
  };

  const listHeader = showScanResultHeader ? (
    <View style={styles.resultHeader}>
      <MaterialCommunityIcons name="barcode-scan" size={20} color={HB_COLORS.action} />
      <View style={styles.resultHeaderCopy}>
        <Text style={styles.resultHeaderTitle}>
          {t("scanResult.title", { count: displayProducts.length })}
        </Text>
        {scanBarcode ? (
          <Text numberOfLines={1} style={styles.resultHeaderBarcode}>
            {scanBarcode}
          </Text>
        ) : null}
      </View>
      <Button
        compact
        icon="close"
        onPress={handleClearSearchAndScan}
        contentStyle={styles.resultHeaderButton}
      >
        {t("scanResult.clear")}
      </Button>
    </View>
  ) : displayProducts.length ? (
    <View style={styles.listMeta}>
      <Text style={styles.listMetaText}>
        {t("listMeta.total", { count: totalProducts })}
      </Text>
      <Text style={styles.listMetaText}>
        {t("listMeta.page", { page: pageNumber, pages: totalPages })}
      </Text>
    </View>
  ) : null;

  const renderCategoryPickerRow = (row: VisibleCategoryRow) => renderCategoryRow({ item: row });

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container}>
      <View style={styles.header}>
        <View style={styles.headerTopRow}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("storeSwitchAccessibility", { store: selectedStoreName })}
            onPress={() => setStorePickerVisible(true)}
            style={({ pressed }) => [styles.storeButton, pressed ? styles.pressed : null]}
          >
            <View style={styles.storeIcon}>
              <MaterialCommunityIcons name="storefront-outline" size={18} color={HB_COLORS.action} />
            </View>
            <View style={styles.storeCopy}>
              <Text style={styles.storeCaption}>{t("storeCaption")}</Text>
              <View style={styles.storeNameRow}>
                <Text numberOfLines={1} style={styles.storeName}>
                  {selectedStoreName}
                </Text>
                <MaterialCommunityIcons name="chevron-down" size={18} color={HB_COLORS.textPrimary} />
              </View>
            </View>
          </Pressable>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={
              restockedCount
                ? `${t("supplyNotice:watchesTitle")} ${t("supplyNotice:restockedBanner", { count: restockedCount })}`
                : t("supplyNotice:watchesTitle")
            }
            onPress={() => setSupplyWatchesVisible(true)}
            style={({ pressed }) => [styles.watchButton, pressed ? styles.pressed : null]}
          >
            <MaterialCommunityIcons
              name={restockedCount ? "bell-ring-outline" : "bell-outline"}
              size={22}
              color={restockedCount ? HB_COLORS.success : HB_COLORS.textSecondary}
            />
            {restockedCount ? (
              <View style={styles.watchBadge}>
                <Text style={styles.watchBadgeText}>{restockedCount > 99 ? "99+" : restockedCount}</Text>
              </View>
            ) : null}
          </Pressable>
          {hidScanner.mode === "textInput" ? (
            <Button
              compact
              icon="barcode-scan"
              textColor={HB_COLORS.textSecondary}
              accessibilityLabel={t("resetScanFocus")}
              onPress={() => hidScanner.focusHiddenInput?.()}
              contentStyle={styles.focusButtonContent}
            >
              {t("resetScanFocusShort")}
            </Button>
          ) : null}
        </View>
        <Searchbar
          ref={visibleSearchScanner.searchInputRef}
          placeholder={t(
            locationLookupEnabled
              ? "locationSearchPlaceholder"
              : "searchPlaceholder",
          )}
          value={searchInput}
          onChangeText={visibleSearchScanner.handleChangeText}
          onSubmitEditing={handleSearchSubmit}
          onIconPress={handleSearchSubmit}
          onFocus={handleSearchFocus}
          onBlur={handleSearchBlur}
          elevation={0}
          style={styles.searchInput}
          inputStyle={styles.searchInputText}
        />
        <View style={styles.filterRow}>
          <FilterChip
            label={t("filters.category")}
            value={selectedCategoryName}
            active={Boolean(selectedCategoryGUID)}
            onPress={() => setFiltersVisible(true)}
          />
          <Menu
            visible={gradeFilterVisible}
            onDismiss={() => setGradeFilterVisible(false)}
            anchorPosition="bottom"
            contentStyle={styles.gradeMenu}
            anchor={
              <FilterChip
                label={t("filters.grade")}
                value={gradeChipValue}
                active={Boolean(selectedGrade)}
                onPress={() => setGradeFilterVisible(true)}
              />
            }
          >
            <Menu.Item
              title={t("filters.allGrades")}
              trailingIcon={!selectedGrade ? "check" : undefined}
              onPress={() => {
                searchReturnPageRef.current = null;
                setPageNumber(1);
                setSelectedGrade(undefined);
                setGradeFilterVisible(false);
              }}
            />
            {gradeOptions.map((option) => {
              const grade = normalizeGradeValue(option.value);
              const isSelected = selectedGrade === grade;

              return (
                <Menu.Item
                  key={option.value}
                  title={t("gradeMenu.gradeValue", { grade: option.label })}
                  trailingIcon={isSelected ? "check" : undefined}
                  onPress={() => {
                    // 与等级同一次更新里回到第一页，避免按旧页码多发一次请求。
                    searchReturnPageRef.current = null;
                    setPageNumber(1);
                    setSelectedGrade(isSelected ? undefined : grade);
                    setGradeFilterVisible(false);
                  }}
                />
              );
            })}
            {productGradesQuery.isError ? (
              <Menu.Item disabled title={t("filters.gradesLoadFailed")} />
            ) : null}
            {!gradeOptions.length ? (
              <Menu.Item
                disabled
                title={productGradesQuery.isLoading ? t("common:loading") : t("filters.noGrades")}
              />
            ) : null}
          </Menu>
          <View style={styles.filterSpacer} />
          <AutoAddToggle
            label={t("common:labels.autoAddShort")}
            value={autoAddWhenSingle}
            accessibilityLabel={autoAddWhenSingle ? t("autoAddOn") : t("autoAddOff")}
            onToggle={handleToggleAutoAdd}
          />
        </View>
      </View>
      <PreorderGateBanner gate={preorderGate} onOpen={openPreorder} />
      <SupplyRestockedBanner
        restockedCount={restockedCount}
        onOpen={() => setSupplyWatchesVisible(true)}
      />
      <SupplyWatchesSheet
        visible={supplyWatchesVisible}
        storeCode={selectedStoreCode ?? null}
        onDismiss={() => setSupplyWatchesVisible(false)}
        onOrder={handleSupplyOrder}
        onChanged={() => void supplyWatchSummary.refetch()}
        onError={(message) => setNotice({ tone: "error", title: message })}
      />
      <FlatList
        style={styles.content}
        data={displayProducts}
        key={`product-grid-${productColumns}`}
        keyExtractor={(item) => item.productCode}
        numColumns={productColumns}
        columnWrapperStyle={productColumns > 1 ? styles.columnWrapper : undefined}
        contentContainerStyle={styles.listContent}
        keyboardShouldPersistTaps="handled"
        ListHeaderComponent={listHeader}
        ListEmptyComponent={
          supplyLookup.items.length ? (
            <View testID="home-supply-status-list">
              <View style={styles.pausedHeader}>
                <MaterialCommunityIcons name="package-variant-remove" size={20} color={ORDER_COLORS.paused} />
                <View style={styles.resultHeaderCopy}>
                  <Text style={styles.pausedHeaderTitle}>{t("supplyNotice:pausedHeading")}</Text>
                  <Text numberOfLines={2} style={styles.pausedHeaderHint}>{t("supplyNotice:pausedHint")}</Text>
                </View>
                <Button
                  compact
                  icon="close"
                  onPress={() => handleSearchInputChange("")}
                  contentStyle={styles.resultHeaderButton}
                >
                  {t("scanResult.clear")}
                </Button>
              </View>
              <View style={styles.supplyStatusList}>
                {supplyLookup.items.map((status) => (
                  <SupplyStatusCard
                    key={status.productCode}
                    status={status}
                    busy={supplyLookup.busyCode === status.productCode}
                    onWatch={(item) => void handleSupplyWatchToggle(item, true)}
                    onUnwatch={(item) => void handleSupplyWatchToggle(item, false)}
                  />
                ))}
              </View>
            </View>
          ) : (
            <View style={styles.homeEmptyState}>
              <AnimatedEmptyStateGraphic />
              <EmptyState
                title={
                  productsQuery.isError
                    ? t("empty.productsLoadFailedTitle")
                    : hasNoAssignedStores
                      ? t("empty.noAssignedStoresTitle")
                      : selectedStoreCode
                        ? t("empty.noProductsTitle")
                        : t("empty.selectStoreTitle")
                }
                description={
                  productsQuery.isError
                    ? t("empty.productsLoadFailedDescription")
                    : hasNoAssignedStores
                      ? t("empty.noAssignedStoresDescription")
                      : selectedStoreCode
                        ? t("empty.noProductsDescription")
                        : t("empty.selectStoreDescription")
                }
                actionLabel={
                  productsQuery.isError ? t("common:actions.retry") : undefined
                }
                onAction={
                  productsQuery.isError
                    ? () => void productsQuery.refetch()
                    : undefined
                }
              />
            </View>
          )
        }
        ListFooterComponent={
          showScanResultHeader ? (
            <Text style={styles.resultHint}>{t("scanResultHint")}</Text>
          ) : displayProducts.length ? (
            <View style={styles.paginationRow}>
              <Button
                mode="outlined"
                icon="chevron-left"
                disabled={pageNumber <= 1}
                onPress={() => setPageNumber((value) => value - 1)}
                style={styles.paginationButton}
                contentStyle={styles.paginationButtonContent}
              >
                {t("pagination.previous")}
              </Button>
              <Text style={styles.paginationText}>
                {t("pagination.pageOf", { page: pageNumber, pages: totalPages })}
              </Text>
              <Button
                mode="outlined"
                icon="chevron-right"
                disabled={!canGoNextPage}
                onPress={() => setPageNumber((value) => value + 1)}
                style={styles.paginationButton}
                contentStyle={[styles.paginationButtonContent, styles.paginationNextContent]}
              >
                {t("pagination.next")}
              </Button>
            </View>
          ) : null
        }
        renderItem={({ item }) => (
          <OrderProductRow
            product={item}
            cartQuantity={getCurrentCartQuantity(item.productCode)}
            compact={compactRows}
            disabled={!selectedStoreCode}
            isUpdating={activeCartMutationProductCode === item.productCode}
            onAddToCart={handleAddToCart}
            onDecreaseCartQuantity={handleDecreaseCartQuantity}
            onEditCartQuantity={handleEditCartQuantity}
            onIncreaseCartQuantity={handleIncreaseCartQuantity}
          />
        )}
      />

      <OrderBottomBar
        leading={
          <OrderBarIconButton
            icon="camera-outline"
            accessibilityLabel={t("cameraQuery")}
            onPress={handleOpenCameraSheet}
          />
        }
        notice={notice}
        title={t("actionBar.summary", { sku: cartSkuCount, quantity: cartQuantityTotal })}
        subtitle={t("actionBar.importTotal", { amount: formatOrderMoney(cartImportTotal) })}
        action={
          <OrderBarPrimaryButton
            label={t("actionBar.cart")}
            icon="chevron-right"
            onPress={() => router.push("/(shell)/cart")}
          />
        }
      />

      {productsQuery.isFetching || storesLoading ? <LoadingOverlay /> : null}

      <Portal>
        <Modal
          visible={preorderPromptVisible}
          onDismiss={() => setPreorderPromptVisible(false)}
          contentContainerStyle={styles.preorderPromptModal}
        >
          <Text variant="titleMedium" style={styles.preorderPromptTitle}>
            {t("preorder:gate.dialogTitle")}
          </Text>
          <Text variant="bodyMedium" style={styles.secondaryText}>
            {t("preorder:gate.dialogMessage", {
              count: preorderGate.activations.length,
            })}
          </Text>
          <View style={styles.preorderPromptActions}>
            <Button
              mode="outlined"
              contentStyle={styles.preorderPromptButtonContent}
              onPress={() => setPreorderPromptVisible(false)}
            >
              {t("preorder:gate.dialogLater")}
            </Button>
            <Button
              mode="contained"
              contentStyle={styles.preorderPromptButtonContent}
              onPress={openPreorder}
            >
              {t("preorder:gate.dialogOpen")}
            </Button>
          </View>
        </Modal>
      </Portal>

      <BusinessSheet
        visible={Boolean(quantityEditorProduct)}
        title={t("quantityEditor.title")}
        onDismiss={handleDismissQuantityEditor}
        dismissable={!quantityEditorBusy}
        footer={
          <View style={styles.sheetActions}>
            <Button
              mode="outlined"
              onPress={handleDismissQuantityEditor}
              disabled={quantityEditorBusy}
              style={styles.sheetActionButton}
              contentStyle={styles.sheetActionContent}
            >
              {t("common:actions.cancel")}
            </Button>
            <Button
              mode="contained"
              onPress={() => void handleConfirmQuantityEdit()}
              loading={quantityEditorBusy}
              disabled={quantityEditorBusy}
              style={[styles.sheetActionButton, styles.sheetActionPrimary]}
              contentStyle={styles.sheetActionContent}
            >
              {t("common:actions.confirm")}
            </Button>
          </View>
        }
      >
        {quantityEditorProduct ? (
          <>
            <View style={styles.editorProduct}>
              <OrderThumbnail uri={quantityEditorProduct.productImage} size={44} />
              <View style={styles.editorProductCopy}>
                <Text numberOfLines={2} style={styles.editorProductName}>
                  {quantityEditorProduct.productName || quantityEditorProduct.productCode}
                </Text>
                <View style={styles.editorProductMeta}>
                  <GradeTag grade={quantityEditorProduct.grade} />
                  <Text numberOfLines={1} style={styles.editorItemNumber}>
                    {quantityEditorProduct.itemNumber || quantityEditorProduct.productCode}
                  </Text>
                  <Text numberOfLines={1} style={styles.editorMetaMuted}>
                    · {t("common:orderRow.minOrder", { quantity: resolveMinimumOrderQuantity(quantityEditorProduct) })}
                  </Text>
                </View>
              </View>
            </View>
            {/* 编辑面板只收集覆盖数量；确认时统一走后端订货数量 mutation。 */}
            <PaperTextInput
              mode="outlined"
              label={t("quantityEditor.inputLabel")}
              value={quantityDraft}
              onChangeText={(value) => {
                setQuantityDraft(value);
                setQuantityEditorError("");
              }}
              keyboardType="number-pad"
              autoFocus
              selectTextOnFocus
              disabled={quantityEditorBusy}
              error={Boolean(quantityEditorError)}
              onFocus={pauseHiddenScannerFocus}
              onBlur={() => {
                if (!quantityEditorProduct) {
                  resumeHiddenScannerFocusSoon();
                }
              }}
              style={styles.editorInput}
              contentStyle={styles.editorInputContent}
            />
            <Text style={styles.editorHelper}>
              {t("quantityEditor.helper", {
                current: getCurrentCartQuantity(quantityEditorProduct.productCode),
              })}
            </Text>
            {quantityEditorError ? (
              <Text
                accessibilityLiveRegion="polite"
                accessibilityRole="alert"
                style={styles.quantityEditorError}
              >
                {quantityEditorError}
              </Text>
            ) : null}
            <QuantityPresetRow
              step={resolveMinimumOrderQuantity(quantityEditorProduct)}
              value={quantityDraft}
              disabled={quantityEditorBusy}
              onSelect={(quantity) => {
                setQuantityDraft(String(quantity));
                setQuantityEditorError("");
              }}
            />
          </>
        ) : null}
      </BusinessSheet>

      <BusinessSheet
        visible={storePickerVisible}
        title={t("storePicker.title")}
        subtitle={t("filters.currentStore", {
          store: selectedStore?.storeName || t("common:na"),
        })}
        onDismiss={() => setStorePickerVisible(false)}
        footer={<Text style={styles.sheetNote}>{t("storePicker.note")}</Text>}
      >
        <View style={styles.sheetToolbar}>
          <Text style={styles.sheetSectionLabel}>{t("filters.store")}</Text>
          <Button compact icon="refresh" onPress={() => void refetchStores()}>
            {t("common:actions.refresh")}
          </Button>
        </View>
        {storesLoading ? (
          <Text variant="bodyMedium">{t("common:loading")}</Text>
        ) : storesLoadFailed ? (
          <View style={styles.storeErrorWrap}>
            <Text variant="bodyMedium">
              {getErrorMessage(storesError, "messages.storesLoadFailed")}
            </Text>
            <Button mode="outlined" onPress={() => void refetchStores()}>
              {t("common:actions.retry")}
            </Button>
          </View>
        ) : stores.length ? (
          <View accessibilityRole="radiogroup" style={styles.optionList}>
            {stores.map((item) => {
              const selected = selectedStoreCode === item.storeCode;

              return (
                <Pressable
                  key={item.storeCode}
                  accessibilityRole="radio"
                  accessibilityState={{ checked: selected }}
                  onPress={() => {
                    searchReturnPageRef.current = null;
                    setPageNumber(1);
                    void selectStore(item);
                    setStorePickerVisible(false);
                  }}
                  style={({ pressed }) => [
                    styles.optionRow,
                    selected ? styles.optionRowSelected : null,
                    pressed ? styles.optionRowPressed : null,
                  ]}
                >
                  <View style={[styles.radio, selected ? styles.radioSelected : null]}>
                    {selected ? <View style={styles.radioDot} /> : null}
                  </View>
                  <Text
                    numberOfLines={1}
                    style={[styles.optionLabel, selected ? styles.optionLabelSelected : null]}
                  >
                    {item.storeName}
                  </Text>
                  {selected ? (
                    <View style={styles.currentTag}>
                      <Text style={styles.currentTagText}>{t("storePicker.current")}</Text>
                    </View>
                  ) : null}
                </Pressable>
              );
            })}
          </View>
        ) : (
          <Text variant="bodyMedium">{t("filters.noStores")}</Text>
        )}
      </BusinessSheet>

      <BusinessSheet
        visible={filtersVisible}
        title={t("filterTitle")}
        subtitle={t("filters.currentCategory", {
          category: selectedCategoryName,
        })}
        onDismiss={() => setFiltersVisible(false)}
        footer={
          selectedCategoryGUID ? (
            <Button
              mode="outlined"
              icon="filter-remove-outline"
              onPress={() => handleSelectCategoryFilter(undefined)}
              contentStyle={styles.sheetActionContent}
            >
              {t("filters.clearCategory")}
            </Button>
          ) : undefined
        }
      >
        <View style={styles.categoryList}>
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ selected: !selectedCategoryGUID }}
            onPress={() => handleSelectCategoryFilter(undefined)}
            style={({ pressed }) => [
              styles.categoryRow,
              !selectedCategoryGUID ? styles.categoryRowSelected : null,
              pressed ? styles.optionRowPressed : null,
            ]}
          >
            <MaterialCommunityIcons
              name="view-grid-outline"
              size={18}
              color={!selectedCategoryGUID ? HB_COLORS.action : HB_COLORS.textSecondary}
            />
            <Text
              style={[
                styles.categoryLabel,
                styles.categoryLabelRoot,
                !selectedCategoryGUID ? styles.categoryLabelSelected : null,
              ]}
            >
              {t("filters.allCategories")}
            </Text>
            {!selectedCategoryGUID ? (
              <MaterialCommunityIcons name="check" size={18} color={HB_COLORS.action} />
            ) : null}
          </Pressable>
          <View style={styles.categoryDivider} />
          {categoriesQuery.isLoading ? (
            <Text variant="bodyMedium">{t("common:loading")}</Text>
          ) : (
            visibleCategoryRows.map(renderCategoryPickerRow)
          )}
        </View>
      </BusinessSheet>

      <CameraScanSheet
        visible={
          cameraVisible &&
          !scanResult.selectionState &&
          !cameraSelectionConfirming &&
          !cameraScanHandling
        }
        title={t("camera.title")}
        subtitle={t("camera.currentStore", {
          store: selectedStore?.storeName || t("common:na"),
        })}
        mode={cameraScanMode}
        onModeChange={handleCameraScanModeChange}
        onDismiss={() => {
          cameraResultGenerationRef.current = null;
          setCameraScanHandling(false);
          updateCameraSheetSession(
            { type: "dismiss" },
            cameraScanModeRef.current,
          );
        }}
      >
        {lastCameraBarcode &&
        scanResult.feedback.barcode === lastCameraBarcode &&
        ["not_found", "blocked", "error", "supply_paused"].includes(
          scanResult.feedback.status,
        ) ? (
          <View
            style={[
              styles.cameraFeedbackBar,
              scanResult.feedback.status === "supply_paused" ? styles.cameraFeedbackBarPaused : null,
            ]}
          >
            <View style={styles.cameraHitCopy}>
              <Text variant="labelLarge">{scanResult.feedback.message}</Text>
              <Text
                variant="bodySmall"
                style={styles.secondaryText}
                numberOfLines={1}
              >
                {lastCameraBarcode}
              </Text>
            </View>
          </View>
        ) : null}
        {lastCameraScan ? (
          <View style={styles.cameraHitBar}>
            <View style={styles.cameraHitCopy}>
              <Text variant="labelLarge" numberOfLines={1}>
                {lastCameraScan.product.productName ||
                  lastCameraScan.product.productCode}
              </Text>
              <Text
                variant="bodySmall"
                style={styles.secondaryText}
                numberOfLines={1}
              >
                {lastCameraScan.barcode}
              </Text>
            </View>
            <Button
              compact
              mode="contained-tonal"
              onPress={() => {
                cameraResultGenerationRef.current = null;
                setCameraScanHandling(false);
                updateCameraSheetSession(
                  { type: "dismiss" },
                  cameraScanModeRef.current,
                );
              }}
            >
              {t("common:actions.viewDetail")}
            </Button>
          </View>
        ) : null}
        {cameraScan.permission?.granted ? (
          <CameraView style={styles.cameraView} {...cameraScan.cameraProps} />
        ) : (
          <Card style={styles.permissionCard}>
            <Card.Content style={styles.permissionCardContent}>
              <Text variant="titleMedium">
                {t("camera.needPermissionTitle")}
              </Text>
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("camera.needPermissionDescription")}
              </Text>
              <Button
                mode="contained"
                onPress={() => void cameraScan.requestPermission()}
              >
                {t("camera.grantPermission")}
              </Button>
            </Card.Content>
          </Card>
        )}
      </CameraScanSheet>

      <ScanResultPicker
        visible={Boolean(scanResult.selectionState)}
        barcode={scanResult.selectionState?.barcode}
        items={scanResult.selectionState?.items ?? []}
        selectLabel={t("common:actions.select")}
        cancelLabel={t("common:actions.cancel")}
        title={t("productQuery:lookup.title")}
        tip={t("productQuery:lookup.query", {
          value: scanResult.selectionState?.barcode || t("common:na"),
        })}
        onDismiss={() => {
          scanResult.clearSelection();
          const generation = cameraResultGenerationRef.current;
          if (generation !== null) {
            updateCameraSheetSession(
              {
                type: "foreground-complete",
                focused: isFocusedRef.current,
                generation,
              },
              cameraScanModeRef.current,
            );
          }
        }}
        onSelect={async (product) => {
          setCameraSelectionConfirming(true);
          try {
            await scanResult.confirmSelection(product);
          } finally {
            setCameraSelectionConfirming(false);
            const generation = cameraResultGenerationRef.current;
            if (generation !== null) {
              updateCameraSheetSession(
                {
                  type: "foreground-complete",
                  focused: isFocusedRef.current,
                  generation,
                },
                cameraScanModeRef.current,
              );
            }
          }
        }}
      />

      {hidScanner.mode === "textInput" && hidScanner.textInputProps ? (
        <TextInput style={styles.hiddenInput} {...hidScanner.textInputProps} />
      ) : null}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: HB_COLORS.background,
  },
  pressed: {
    opacity: 0.7,
  },
  header: {
    paddingHorizontal: 12,
    paddingBottom: 8,
    gap: 8,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outline,
  },
  headerTopRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  storeButton: {
    flex: 1,
    minWidth: 0,
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  storeIcon: {
    width: 32,
    height: 32,
    borderRadius: 8,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  storeCopy: {
    flex: 1,
    minWidth: 0,
  },
  storeCaption: {
    color: ORDER_COLORS.subtleText,
    fontSize: 11,
    lineHeight: 14,
  },
  storeNameRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
  },
  storeName: {
    flexShrink: 1,
    color: HB_COLORS.textPrimary,
    fontSize: 17,
    lineHeight: 22,
    fontWeight: "700",
  },
  watchButton: {
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
  },
  watchBadge: {
    position: "absolute",
    top: 4,
    right: 2,
    minWidth: 18,
    height: 18,
    paddingHorizontal: 4,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: HB_COLORS.success,
  },
  watchBadgeText: {
    color: HB_COLORS.white,
    fontSize: 11,
    fontWeight: "700",
  },
  focusButtonContent: {
    minHeight: 44,
  },
  searchInput: {
    height: 44,
    borderRadius: 10,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  searchInputText: {
    minHeight: 0,
    fontSize: 15,
  },
  filterRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  filterSpacer: {
    flex: 1,
  },
  filterChip: {
    height: 40,
    maxWidth: 150,
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    paddingLeft: 10,
    paddingRight: 6,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  filterChipActive: {
    borderColor: HB_COLORS.action,
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  filterChipPressed: {
    opacity: 0.75,
  },
  filterChipLabel: {
    color: ORDER_COLORS.subtleText,
    fontSize: 13,
  },
  filterChipValue: {
    flexShrink: 1,
    color: HB_COLORS.textPrimary,
    fontSize: 13,
    fontWeight: "600",
  },
  filterChipValueActive: {
    color: ORDER_COLORS.tonalText,
  },
  gradeMenu: {
    backgroundColor: HB_COLORS.white,
  },
  autoAdd: {
    height: 40,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingLeft: 10,
    paddingRight: 8,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  autoAddOn: {
    borderColor: ORDER_COLORS.stepperBorder,
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  autoAddText: {
    color: HB_COLORS.textSecondary,
    fontSize: 13,
    fontWeight: "600",
  },
  autoAddTextOn: {
    color: ORDER_COLORS.tonalText,
  },
  switchTrack: {
    width: 32,
    height: 20,
    borderRadius: 10,
    padding: 2,
    backgroundColor: ORDER_COLORS.placeholderIcon,
  },
  switchTrackOn: {
    backgroundColor: HB_COLORS.action,
  },
  switchKnob: {
    width: 16,
    height: 16,
    borderRadius: 8,
    backgroundColor: HB_COLORS.white,
  },
  switchKnobOn: {
    transform: [{ translateX: 12 }],
  },
  content: {
    flex: 1,
  },
  listContent: {
    flexGrow: 1,
    paddingBottom: 12,
  },
  columnWrapper: {
    gap: StyleSheet.hairlineWidth,
  },
  listMeta: {
    flexDirection: "row",
    justifyContent: "space-between",
    paddingHorizontal: 12,
    paddingTop: 10,
    paddingBottom: 6,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outline,
  },
  listMetaText: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
    fontVariant: ["tabular-nums"],
  },
  resultHeader: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingLeft: 12,
    paddingRight: 4,
    backgroundColor: ORDER_COLORS.tonalBackground,
    borderBottomWidth: 1,
    borderBottomColor: ORDER_COLORS.stepperBorder,
  },
  resultHeaderCopy: {
    flex: 1,
    minWidth: 0,
  },
  resultHeaderTitle: {
    color: ORDER_COLORS.tonalText,
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "700",
  },
  resultHeaderBarcode: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
    fontFamily: ORDER_MONO_FONT,
  },
  resultHeaderButton: {
    minHeight: 44,
  },
  pausedHeader: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingLeft: 12,
    paddingRight: 4,
    paddingVertical: 6,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderBottomWidth: 1,
    borderBottomColor: HB_COLORS.outline,
  },
  pausedHeaderTitle: {
    color: ORDER_COLORS.paused,
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "700",
  },
  pausedHeaderHint: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
  },
  supplyStatusList: {
    padding: 12,
    gap: 10,
  },
  resultHint: {
    paddingHorizontal: 24,
    paddingVertical: 16,
    color: ORDER_COLORS.subtleText,
    fontSize: 12,
    lineHeight: 18,
    textAlign: "center",
  },
  homeEmptyState: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 16,
    paddingVertical: 24,
  },
  paginationRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    padding: 12,
  },
  paginationButton: {
    flex: 1,
    borderRadius: 8,
  },
  paginationButtonContent: {
    minHeight: 44,
  },
  paginationNextContent: {
    flexDirection: "row-reverse",
  },
  paginationText: {
    minWidth: 72,
    textAlign: "center",
    color: HB_COLORS.textSecondary,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
  },
  preorderPromptModal: {
    marginHorizontal: 24,
    borderRadius: 12,
    backgroundColor: HB_COLORS.white,
    padding: 20,
    gap: 12,
  },
  preorderPromptTitle: {
    color: "#7A3E00",
    fontWeight: "700",
  },
  preorderPromptActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    flexWrap: "wrap",
    gap: 8,
  },
  preorderPromptButtonContent: {
    minHeight: 44,
  },
  secondaryText: {
    color: HB_COLORS.textSecondary,
  },
  sheetActions: {
    flexDirection: "row",
    gap: 10,
  },
  sheetActionButton: {
    flex: 1,
    borderRadius: 10,
  },
  sheetActionPrimary: {
    flex: 2,
  },
  sheetActionContent: {
    minHeight: 48,
  },
  sheetToolbar: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sheetSectionLabel: {
    color: HB_COLORS.textSecondary,
    fontSize: 13,
    fontWeight: "600",
  },
  sheetNote: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 18,
    paddingBottom: 4,
  },
  editorProduct: {
    flexDirection: "row",
    gap: 10,
    padding: 10,
    borderRadius: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    backgroundColor: ORDER_COLORS.mutedRow,
  },
  editorProductCopy: {
    flex: 1,
    minWidth: 0,
    gap: 4,
  },
  editorProductName: {
    color: HB_COLORS.textPrimary,
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "600",
  },
  editorProductMeta: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  editorItemNumber: {
    flexShrink: 1,
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    fontFamily: ORDER_MONO_FONT,
  },
  editorMetaMuted: {
    color: ORDER_COLORS.subtleText,
    fontSize: 12,
  },
  editorInput: {
    backgroundColor: HB_COLORS.white,
  },
  editorInputContent: {
    fontSize: 24,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  editorHelper: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
  },
  quantityEditorError: {
    color: HB_COLORS.danger,
    fontSize: 12,
    lineHeight: 16,
  },
  storeErrorWrap: {
    gap: 12,
  },
  optionList: {
    borderRadius: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    overflow: "hidden",
  },
  optionRow: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  optionRowSelected: {
    backgroundColor: ORDER_COLORS.inCartRow,
  },
  optionRowPressed: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  radio: {
    width: 20,
    height: 20,
    borderRadius: 10,
    borderWidth: 2,
    borderColor: ORDER_COLORS.placeholderIcon,
    alignItems: "center",
    justifyContent: "center",
  },
  radioSelected: {
    borderColor: HB_COLORS.action,
  },
  radioDot: {
    width: 10,
    height: 10,
    borderRadius: 5,
    backgroundColor: HB_COLORS.action,
  },
  optionLabel: {
    flex: 1,
    color: HB_COLORS.textPrimary,
    fontSize: 15,
    fontWeight: "500",
  },
  optionLabelSelected: {
    color: ORDER_COLORS.tonalText,
    fontWeight: "700",
  },
  currentTag: {
    paddingHorizontal: 8,
    paddingVertical: 2,
    borderRadius: 10,
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  currentTagText: {
    color: HB_COLORS.action,
    fontSize: 12,
    fontWeight: "600",
  },
  categoryList: {
    gap: 2,
  },
  categoryRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 12,
    borderRadius: 8,
  },
  categoryRowSelected: {
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  categoryDivider: {
    height: StyleSheet.hairlineWidth,
    marginVertical: 4,
    backgroundColor: HB_COLORS.outline,
  },
  categoryTreeRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    borderRadius: 8,
  },
  categoryTreeLabelButton: {
    flex: 1,
    minWidth: 0,
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingRight: 8,
  },
  categoryLabel: {
    flexShrink: 1,
    color: HB_COLORS.textPrimary,
    fontSize: 14,
  },
  categoryLabelRoot: {
    flex: 1,
    fontSize: 15,
    fontWeight: "600",
  },
  categoryLabelSelected: {
    color: ORDER_COLORS.tonalText,
    fontWeight: "700",
  },
  categoryToggle: {
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
  },
  cameraView: {
    width: "100%",
    height: 420,
    borderRadius: 12,
    overflow: "hidden",
  },
  cameraFeedbackBar: {
    minHeight: 52,
    borderRadius: 8,
    backgroundColor: "#FFF4E5",
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  cameraFeedbackBarPaused: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  cameraHitBar: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderRadius: 8,
    backgroundColor: ORDER_COLORS.tonalBackground,
    paddingHorizontal: 12,
    paddingVertical: 6,
  },
  cameraHitCopy: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  permissionCard: {
    marginTop: 4,
  },
  permissionCardContent: {
    gap: 12,
  },
  hiddenInput: {
    position: "absolute",
    width: 1,
    height: 1,
    opacity: 0,
  },
});
