import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  FlatList,
  ScrollView,
  StyleSheet,
  TextInput,
  useWindowDimensions,
  View,
} from "react-native";
import { useQuery } from "@tanstack/react-query";
import { CameraView } from "expo-camera";
import { type Href, useRouter } from "expo-router";
import { useFocusEffect, useIsFocused } from "@react-navigation/native";
import {
  Badge,
  Button,
  Card,
  Chip,
  IconButton,
  Modal,
  Portal,
  Searchbar,
  Text,
  TextInput as PaperTextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { AnimatedEmptyStateGraphic } from "@/components/ui/AnimatedEmptyStateGraphic";
import { EmptyState } from "@/components/ui/EmptyState";
import { LoadingOverlay } from "@/components/ui/LoadingOverlay";
import { ProductCard } from "@/components/ui/ProductCard";
import { CameraScanSheet } from "@/components/ui/CameraScanSheet";
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
  isSameKeywordResubmit,
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

export default function Home() {
  const isFocused = useIsFocused();
  const { t, language } = useAppTranslation(["home", "common", "supplyNotice"]);
  const { height: windowHeight, width: windowWidth } = useWindowDimensions();
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
  const [noticeMessage, setNoticeMessage] = useState("");
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
      setNoticeMessage(getErrorMessage(error, fallbackKey));
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

        setNoticeMessage(
          t("messages.addedToCart", {
            name: product.productName || product.productCode,
          }),
        );
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
      setScannedProducts([product]);
      setScannedProductTraceIds(
        scanTraceId ? { [product.productCode]: scanTraceId } : {},
      );
      setSelectedCategoryGUID(undefined);
      setSelectedGrade(undefined);
      if (source === "camera") {
        setLastCameraScan({ product, barcode: barcode || product.productCode });
      }
      setNoticeMessage(
        t("messages.addedToCart", {
          name: product.productName || product.productCode,
        }),
      );
    },
    [clearAppliedSearchForScan, t],
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
        pageSize: 18,
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
  const { refetch: refetchProducts } = productsQuery;

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
    if (
      scanResult.feedback.status === "ready" ||
      scanResult.feedback.status === "scanning" ||
      scanResult.feedback.status === "found" ||
      scanResult.feedback.status === "multiple"
    ) {
      return;
    }

    setNoticeMessage(scanResult.feedback.message);
  }, [scanResult.feedback.message, scanResult.feedback.status]);

  useEffect(() => {
    if (!storesLoadFailed) {
      return;
    }

    setNoticeMessage(getErrorMessage(storesError, "messages.storesLoadFailed"));
  }, [getErrorMessage, storesError, storesLoadFailed]);

  useEffect(() => {
    if (!productsQuery.isError) {
      return;
    }

    setNoticeMessage(
      getErrorMessage(productsQuery.error, "messages.productsLoadFailed"),
    );
  }, [getErrorMessage, productsQuery.error, productsQuery.isError]);

  useEffect(() => {
    if (!noticeMessage) {
      return;
    }

    const dismissTimer = setTimeout(() => {
      setNoticeMessage("");
    }, 2500);

    return () => {
      clearTimeout(dismissTimer);
    };
  }, [noticeMessage]);

  const canGoNextPage = useMemo(() => {
    const total = productsQuery.data?.total ?? 0;
    return pageNumber * 18 < total;
  }, [pageNumber, productsQuery.data?.total]);
  const hasNoAssignedStores =
    !storesLoading &&
    !storesLoadFailed &&
    stores.length === 0 &&
    !selectedStoreCode;
  const isCompactLayout = windowHeight < 780;
  const productListContentStyle = useMemo(
    () => [
      styles.listContent,
      isCompactLayout ? styles.listContentCompact : null,
    ],
    [isCompactLayout],
  );
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
  // 关注恢复订货：汇总用于横幅与入口角标；搜索零结果时补查暂停供货商品，让分店知道是下架而不是搜错。
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
      setNoticeMessage(t(ok ? (watch ? "supplyNotice:watchDone" : "supplyNotice:unwatchDone") : "supplyNotice:actionFailed"));
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
  // 用户主动提交搜索的入口：同词重搜不会换查询键，需显式重新请求，确保上架/恢复订货后再搜能拿到最新结果。
  // 扫码回填关键词的 effect 不走这里，避免它因依赖变化重跑时重复请求。
  const submitSearchKeyword = useCallback(
    (input: string) => {
      if (isSameKeywordResubmit(keyword, input)) {
        void refetchProducts();
      }
      applySearchPageAction({ type: "apply", input });
    },
    [applySearchPageAction, keyword, refetchProducts],
  );
  const handleApplySearch = useCallback(() => {
    setScannedProducts(null);
    // 搜索框可能接收到同一扫码枪输入，保留商品 trace 让同商品数量调整继续走 scan-update。
    if (!searchInput.trim()) {
      setSearchInput("");
    }
    submitSearchKeyword(searchInput);
  }, [searchInput, submitSearchKeyword]);
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
      submitSearchKeyword(code);
    },
    [submitSearchKeyword],
  );
  // 扫到暂停供货的商品：把条码转成搜索词，让零结果处展示恢复计划与关注按钮。
  const scanFeedback = scanResult.feedback;
  useEffect(() => {
    if (scanFeedback.status !== "not_found" || !scanFeedback.pausedSupply || !scanFeedback.barcode) {
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
    // 分类选择完成后回到商品列表；展开箭头不走这个回调，保持只展开分类树。
    setFiltersVisible(false);
  }, []);

  const renderCategoryRow = useCallback(
    ({ item }: { item: VisibleCategoryRow }) => {
      const { node, depth, hasChildren, isExpanded } = item;
      const isSelected = selectedCategoryGUID === node.categoryGUID;

      return (
        <View
          style={[
            styles.categoryTreeNode,
            depth ? { marginLeft: depth * 14 } : null,
          ]}
        >
          <View
            style={[
              styles.categoryTreeRow,
              isSelected ? styles.categoryTreeRowSelected : null,
            ]}
          >
            <Button
              compact
              mode={isSelected ? "contained-tonal" : "text"}
              onPress={() => handleSelectCategoryFilter(node.categoryGUID)}
              style={styles.categoryTreeButton}
              contentStyle={styles.categoryTreeButtonContent}
              labelStyle={styles.categoryTreeButtonLabel}
            >
              {node.categoryName}
            </Button>
            {hasChildren ? (
              <IconButton
                icon={isExpanded ? "chevron-up" : "chevron-down"}
                size={18}
                onPress={() => toggleCategoryExpanded(node.categoryGUID)}
                style={styles.categoryTreeToggle}
              />
            ) : null}
          </View>
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
      setNoticeMessage(
        t("messages.addedToCart", {
          name: product.productName || product.productCode,
        }),
      );
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
        setNoticeMessage(message);
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

  const fixedHeaderContent = (
    <View
      style={[styles.header, isCompactLayout ? styles.headerCompact : null]}
    >
      <View
        style={[
          styles.headerTopRow,
          isCompactLayout ? styles.headerTopRowCompact : null,
        ]}
      >
        <View style={styles.headerTitleWrap}>
          <Text variant="titleLarge" style={styles.headerTitle}>
            {t("title")}
          </Text>
          <Text variant="bodySmall" style={styles.headerSubtitle}>
            {selectedStore?.storeName || t("common:labels.selectStore")}
          </Text>
        </View>
        <View style={styles.cartBox}>
          <IconButton
            icon="cart-outline"
            size={18}
            onPress={() => router.push("/(shell)/cart")}
            style={styles.cartButton}
          />
          {cartSummary?.totalQuantity ? (
            <Badge style={styles.badge}>{cartSummary.totalQuantity}</Badge>
          ) : null}
          <Text variant="labelMedium" style={styles.cartText}>
            {t("cartSummary.total")} {cartSummary?.totalQuantity ?? 0}
          </Text>
        </View>
      </View>
      <View
        style={[
          styles.searchRow,
          isCompactLayout ? styles.searchRowCompact : null,
        ]}
      >
        <View style={styles.searchInputWrap}>
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
            style={styles.searchInput}
            inputStyle={styles.searchInputText}
          />
        </View>
        <IconButton
          icon="camera-outline"
          mode="contained-tonal"
          accessibilityLabel={t("cameraQuery")}
          onPress={() => {
            cameraResultGenerationRef.current = null;
            setCameraScanHandling(false);
            updateCameraSheetSession(
              { type: "open" },
              cameraScanModeRef.current,
            );
          }}
          style={styles.cameraQueryButton}
        />
        <IconButton
          icon="filter-variant"
          mode="outlined"
          accessibilityLabel={t("common:actions.openFilters")}
          onPress={() => setFiltersVisible(true)}
          style={styles.filterToggleButton}
        />
      </View>
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        contentContainerStyle={styles.utilityRow}
      >
        <Chip
          compact
          mode="outlined"
          icon="storefront-outline"
          onPress={() => setStorePickerVisible(true)}
          style={styles.utilityChip}
          textStyle={styles.utilityChipText}
        >
          {selectedStore?.storeName || t("common:labels.selectStore")}
        </Chip>
        <Chip
          compact
          mode={selectedCategoryGUID ? "flat" : "outlined"}
          icon="shape-outline"
          onPress={() => setFiltersVisible(true)}
          style={[
            styles.utilityChip,
            selectedCategoryGUID ? styles.utilityChipActive : null,
          ]}
          textStyle={[
            styles.utilityChipText,
            selectedCategoryGUID ? styles.utilityChipTextActive : null,
          ]}
        >
          {selectedCategoryName}
        </Chip>
        <Chip
          compact
          mode={selectedGrade ? "flat" : "outlined"}
          icon="label-outline"
          onPress={() => setGradeFilterVisible(true)}
          style={[
            styles.utilityChip,
            selectedGrade ? styles.utilityChipActive : null,
          ]}
          textStyle={[
            styles.utilityChipText,
            selectedGrade ? styles.utilityChipTextActive : null,
          ]}
        >
          {selectedGrade
            ? t("common:grade", { grade: selectedGrade })
            : t("filters.grade")}
        </Chip>
        <Chip
          compact
          mode={autoAddWhenSingle ? "flat" : "outlined"}
          selected={autoAddWhenSingle}
          icon={autoAddWhenSingle ? "cart-check" : "cart-off"}
          onPress={() => setAutoAddWhenSingle((currentValue) => !currentValue)}
          accessibilityLabel={
            autoAddWhenSingle ? t("autoAddOn") : t("autoAddOff")
          }
          style={[
            styles.utilityChip,
            autoAddWhenSingle ? styles.utilityChipActive : null,
          ]}
          textStyle={[
            styles.utilityChipText,
            autoAddWhenSingle ? styles.utilityChipTextActive : null,
          ]}
        >
          {t("common:labels.autoAddShort")}
        </Chip>
        <Chip
          compact
          mode={supplyWatchSummary.data?.restockedCount ? "flat" : "outlined"}
          icon="bell-outline"
          onPress={() => setSupplyWatchesVisible(true)}
          accessibilityLabel={t("supplyNotice:watchesTitle")}
          style={[
            styles.utilityChip,
            supplyWatchSummary.data?.restockedCount ? styles.utilityChipActive : null,
          ]}
          textStyle={[
            styles.utilityChipText,
            supplyWatchSummary.data?.restockedCount ? styles.utilityChipTextActive : null,
          ]}
        >
          {supplyWatchSummary.data?.restockedCount
            ? `${t("supplyNotice:watchesEntry")} ${supplyWatchSummary.data.restockedCount}`
            : t("supplyNotice:watchesEntry")}
        </Chip>
        {scannedProducts?.length ? (
          <Chip
            compact
            mode="outlined"
            icon="barcode-scan"
            onClose={handleClearSearchAndScan}
            style={styles.utilityChip}
            textStyle={styles.utilityChipText}
          >
            {t("common:labels.scanResults")}
          </Chip>
        ) : null}
        {hidScanner.mode === "textInput" ? (
          <IconButton
            icon="barcode-scan"
            mode="outlined"
            accessibilityLabel={t("resetScanFocus")}
            onPress={() => hidScanner.focusHiddenInput?.()}
            style={styles.scanFocusButton}
          />
        ) : null}
      </ScrollView>
      {scannedProducts?.length ? (
        <View style={styles.scanHintBanner}>
          <Text variant="bodySmall" style={styles.scanHintText}>
            {t("scanResultHint")}
          </Text>
        </View>
      ) : null}
      {noticeMessage ? (
        <View style={styles.headerNotice}>
          {/* 页头内联提示用于避免遮挡商品数量加减按钮。 */}
          <Text variant="bodySmall" style={styles.headerNoticeText}>
            {noticeMessage}
          </Text>
          <IconButton
            icon="close"
            size={16}
            accessibilityLabel={t("common:actions.close")}
            onPress={() => setNoticeMessage("")}
            style={styles.headerNoticeClose}
          />
        </View>
      ) : null}
    </View>
  );
  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container}>
      {fixedHeaderContent}
      <PreorderGateBanner gate={preorderGate} onOpen={openPreorder} />
      <SupplyRestockedBanner
        restockedCount={supplyWatchSummary.data?.restockedCount ?? 0}
        onOpen={() => setSupplyWatchesVisible(true)}
      />
      <SupplyWatchesSheet
        visible={supplyWatchesVisible}
        storeCode={selectedStoreCode ?? null}
        onDismiss={() => setSupplyWatchesVisible(false)}
        onOrder={handleSupplyOrder}
        onChanged={() => void supplyWatchSummary.refetch()}
        onError={setNoticeMessage}
      />
      <FlatList
        style={styles.content}
        data={displayProducts}
        key={`product-grid-${productColumns}`}
        keyExtractor={(item) => item.productCode}
        numColumns={productColumns}
        columnWrapperStyle={styles.columnWrapper}
        contentContainerStyle={productListContentStyle}
        keyboardShouldPersistTaps="handled"
        ListEmptyComponent={
          supplyLookup.items.length ? (
            <View style={styles.supplyStatusList} testID="home-supply-status-list">
              <Text variant="titleSmall">{t("supplyNotice:pausedHeading")}</Text>
              <Text variant="bodySmall" style={styles.supplyStatusHint}>{t("supplyNotice:pausedHint")}</Text>
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
          displayProducts.length ? (
            <View style={styles.paginationRow}>
              <Button
                mode="outlined"
                disabled={pageNumber <= 1}
                onPress={() => setPageNumber((value) => value - 1)}
              >
                {t("pagination.previous")}
              </Button>
              <Text variant="bodyMedium">
                {t("pagination.page", { page: pageNumber })}
              </Text>
              <Button
                mode="outlined"
                disabled={!canGoNextPage}
                onPress={() => setPageNumber((value) => value + 1)}
              >
                {t("pagination.next")}
              </Button>
            </View>
          ) : null
        }
        renderItem={({ item }) => (
          <ProductCard
            product={item}
            dynamicDataMap={displayDynamicDataMap}
            disabled={!selectedStoreCode}
            isUpdatingCart={activeCartMutationProductCode === item.productCode}
            onAddToCart={handleAddToCart}
            onDecreaseCartQuantity={handleDecreaseCartQuantity}
            onEditCartQuantity={handleEditCartQuantity}
            onIncreaseCartQuantity={handleIncreaseCartQuantity}
          />
        )}
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

      <Portal>
        <Modal
          visible={Boolean(quantityEditorProduct)}
          onDismiss={handleDismissQuantityEditor}
          contentContainerStyle={styles.quantityEditorModal}
        >
          <Text variant="titleMedium">{t("quantityEditor.title")}</Text>
          <Text
            variant="bodySmall"
            numberOfLines={2}
            style={styles.secondaryText}
          >
            {quantityEditorProduct
              ? t("quantityEditor.product", {
                  name:
                    quantityEditorProduct.productName ||
                    quantityEditorProduct.productCode,
                })
              : ""}
          </Text>
          {/* 编辑弹窗只收集覆盖数量；确认时统一走后端订货数量 mutation。 */}
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
            disabled={quantityEditorBusy}
            error={Boolean(quantityEditorError)}
            onFocus={pauseHiddenScannerFocus}
            onBlur={() => {
              if (!quantityEditorProduct) {
                resumeHiddenScannerFocusSoon();
              }
            }}
          />
          {quantityEditorError ? (
            <Text variant="bodySmall" style={styles.quantityEditorError}>
              {quantityEditorError}
            </Text>
          ) : null}
          <View style={styles.quantityEditorActions}>
            <Button
              onPress={handleDismissQuantityEditor}
              disabled={quantityEditorBusy}
            >
              {t("common:actions.cancel")}
            </Button>
            <Button
              mode="contained"
              onPress={() => void handleConfirmQuantityEdit()}
              loading={quantityEditorBusy}
              disabled={quantityEditorBusy}
            >
              {t("common:actions.confirm")}
            </Button>
          </View>
        </Modal>
      </Portal>

      <Portal>
        <Modal
          visible={storePickerVisible}
          onDismiss={() => setStorePickerVisible(false)}
          contentContainerStyle={styles.filtersModal}
        >
          <ScrollView contentContainerStyle={styles.filtersModalContent}>
            <View style={styles.filtersModalHeader}>
              <View style={styles.filtersModalTitleWrap}>
                <Text variant="titleMedium">{t("filters.store")}</Text>
                <Text variant="bodySmall" style={styles.secondaryText}>
                  {t("filters.currentStore", {
                    store: selectedStore?.storeName || t("common:na"),
                  })}
                </Text>
              </View>
              <Button mode="text" onPress={() => setStorePickerVisible(false)}>
                {t("common:actions.close")}
              </Button>
            </View>
            <View style={styles.filtersSection}>
              <View style={styles.filtersSectionHeader}>
                <Text variant="labelLarge">{t("filters.store")}</Text>
                <Button compact onPress={() => void refetchStores()}>
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
                <ScrollView
                  style={styles.storeListScroll}
                  contentContainerStyle={styles.storeListContent}
                >
                  {stores.map((item) => (
                    <Button
                      key={item.storeCode}
                      mode={
                        selectedStoreCode === item.storeCode
                          ? "contained"
                          : "outlined"
                      }
                      style={styles.storeButton}
                      onPress={() => {
                        void selectStore(item);
                        setStorePickerVisible(false);
                      }}
                    >
                      {item.storeName}
                    </Button>
                  ))}
                </ScrollView>
              ) : (
                <Text variant="bodyMedium">{t("filters.noStores")}</Text>
              )}
            </View>
          </ScrollView>
        </Modal>
      </Portal>

      <Portal>
        <Modal
          visible={filtersVisible}
          onDismiss={() => setFiltersVisible(false)}
          contentContainerStyle={styles.filtersModal}
        >
          <View style={styles.filtersModalContent}>
            <View style={styles.filtersModalHeader}>
              <View style={styles.filtersModalTitleWrap}>
                <Text variant="titleMedium">{t("filterTitle")}</Text>
                <Text variant="bodySmall" style={styles.secondaryText}>
                  {t("filters.currentCategory", {
                    category: selectedCategoryName,
                  })}
                </Text>
              </View>
              <Button mode="text" onPress={() => setFiltersVisible(false)}>
                {t("common:actions.close")}
              </Button>
            </View>
            <View style={styles.filtersSection}>
              <View style={styles.filtersSectionHeader}>
                <Text variant="labelLarge">{t("filters.category")}</Text>
                <Button
                  compact
                  onPress={() => handleSelectCategoryFilter(undefined)}
                >
                  {t("filters.allCategories")}
                </Button>
              </View>
              <View
                style={[
                  styles.categoryTreeWrap,
                  { maxHeight: Math.max(260, windowHeight - 260) },
                ]}
              >
                <Button
                  compact
                  mode={!selectedCategoryGUID ? "contained-tonal" : "text"}
                  onPress={() => handleSelectCategoryFilter(undefined)}
                  contentStyle={styles.resetFilterButtonContent}
                >
                  {t("filters.all")}
                </Button>
                {categoriesQuery.isLoading ? (
                  <Text variant="bodyMedium">{t("common:loading")}</Text>
                ) : (
                  <FlatList
                    data={filtersVisible ? visibleCategoryRows : []}
                    keyExtractor={(item) => item.node.categoryGUID}
                    renderItem={renderCategoryRow}
                    contentContainerStyle={styles.categoryTreeListContent}
                    keyboardShouldPersistTaps="handled"
                    initialNumToRender={18}
                    maxToRenderPerBatch={18}
                    windowSize={5}
                  />
                )}
              </View>
            </View>
          </View>
        </Modal>
      </Portal>

      <Portal>
        <Modal
          visible={gradeFilterVisible}
          onDismiss={() => setGradeFilterVisible(false)}
          contentContainerStyle={styles.filtersModal}
        >
          <ScrollView contentContainerStyle={styles.filtersModalContent}>
            <View style={styles.filtersModalHeader}>
              <View style={styles.filtersModalTitleWrap}>
                <Text variant="titleMedium">{t("gradeFilterTitle")}</Text>
                <Text variant="bodySmall" style={styles.secondaryText}>
                  {t("filters.currentGrade", { grade: selectedGradeLabel })}
                </Text>
              </View>
              <Button mode="text" onPress={() => setGradeFilterVisible(false)}>
                {t("common:actions.close")}
              </Button>
            </View>
            <View style={styles.filtersSection}>
              <View style={styles.filtersSectionHeader}>
                <Text variant="labelLarge">{t("filters.grade")}</Text>
                <Button compact onPress={() => setSelectedGrade(undefined)}>
                  {t("filters.allGrades")}
                </Button>
              </View>
              <View style={styles.gradeOptionsWrap}>
                <Chip
                  compact
                  mode={!selectedGrade ? "flat" : "outlined"}
                  selected={!selectedGrade}
                  onPress={() => {
                    setSelectedGrade(undefined);
                    setGradeFilterVisible(false);
                  }}
                  style={[
                    styles.filterChip,
                    !selectedGrade ? styles.filterChipActive : null,
                  ]}
                  textStyle={[
                    styles.filterChipText,
                    !selectedGrade ? styles.filterChipTextActive : null,
                  ]}
                >
                  {t("filters.all")}
                </Chip>
                {gradeOptions.map((option) => {
                  const grade = normalizeGradeValue(option.value);
                  const isSelected = selectedGrade === grade;

                  return (
                    <Chip
                      key={option.value}
                      compact
                      mode={isSelected ? "flat" : "outlined"}
                      selected={isSelected}
                      onPress={() => {
                        setSelectedGrade(isSelected ? undefined : grade);
                        setGradeFilterVisible(false);
                      }}
                      style={[
                        styles.filterChip,
                        isSelected ? styles.filterChipActive : null,
                      ]}
                      textStyle={[
                        styles.filterChipText,
                        isSelected ? styles.filterChipTextActive : null,
                      ]}
                    >
                      {t("common:grade", { grade: option.label })}
                    </Chip>
                  );
                })}
              </View>
              {productGradesQuery.isError ? (
                <Text variant="bodySmall" style={styles.secondaryText}>
                  {t("filters.gradesLoadFailed")}
                </Text>
              ) : null}
              {!gradeOptions.length ? (
                <Text variant="bodySmall" style={styles.secondaryText}>
                  {productGradesQuery.isLoading
                    ? t("common:loading")
                    : t("filters.noGrades")}
                </Text>
              ) : null}
            </View>
          </ScrollView>
        </Modal>
      </Portal>

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
        ["not_found", "blocked", "error"].includes(
          scanResult.feedback.status,
        ) ? (
          <View style={styles.cameraFeedbackBar}>
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
    backgroundColor: "#F4F6F8",
  },
  header: {
    paddingHorizontal: 16,
    paddingTop: 2,
    paddingBottom: 8,
    gap: 10,
  },
  headerCompact: {
    gap: 8,
  },
  headerTopRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    gap: 12,
  },
  headerTopRowCompact: {
    gap: 8,
  },
  headerTitleWrap: {
    flex: 1,
    gap: 2,
  },
  headerTitle: {
    color: "#0F172A",
    fontWeight: "700",
    fontSize: 18,
  },
  headerSubtitle: {
    color: "#5B6474",
  },
  headerNotice: {
    minHeight: 40,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: "#B7D6F6",
    backgroundColor: "#EAF4FF",
    paddingLeft: 12,
    paddingRight: 4,
    paddingVertical: 6,
  },
  headerNoticeText: {
    flex: 1,
    color: "#174A7C",
    fontWeight: "600",
    lineHeight: 18,
  },
  headerNoticeClose: {
    width: 28,
    height: 28,
    margin: 0,
  },
  preorderPromptModal: {
    marginHorizontal: 24,
    borderRadius: 12,
    backgroundColor: "#FFFFFF",
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
  filterToggleButton: {
    margin: 0,
  },
  cartBox: {
    minWidth: 82,
    paddingHorizontal: 8,
    paddingVertical: 4,
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#E4E7EC",
    alignItems: "center",
    justifyContent: "center",
  },
  cartButton: {
    margin: 0,
  },
  badge: {
    position: "absolute",
    top: 3,
    right: 8,
  },
  cartText: {
    color: "#2C3134",
  },
  categoryCardContent: {
    gap: 10,
  },
  categoryCardContentCompact: {
    gap: 6,
    paddingVertical: 10,
  },
  categoryCardHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    gap: 12,
  },
  categoryCardTitleWrap: {
    flex: 1,
    gap: 2,
  },
  categoryCardActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
  },
  categoryTriggerButton: {
    flex: 1,
  },
  categoryTriggerButtonContent: {
    minHeight: 38,
    justifyContent: "flex-start",
  },
  categoryTriggerButtonLabel: {
    marginVertical: 0,
  },
  categoryMenuButton: {
    margin: 0,
  },
  categoryTreeWrap: {
    gap: 6,
  },
  categoryTreeListContent: {
    gap: 6,
  },
  categoryTreeNode: {
    gap: 6,
  },
  categoryTreeRow: {
    minHeight: 40,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderRadius: 10,
    backgroundColor: "#f7f7f7",
    paddingLeft: 4,
    paddingRight: 2,
  },
  categoryTreeRowSelected: {
    backgroundColor: "#eef4ff",
  },
  categoryTreeButton: {
    flex: 1,
    alignItems: "flex-start",
    justifyContent: "center",
  },
  categoryTreeButtonContent: {
    justifyContent: "flex-start",
    minHeight: 40,
  },
  categoryTreeButtonLabel: {
    marginVertical: 0,
  },
  categoryTreeToggle: {
    margin: 0,
  },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  searchRowCompact: {
    gap: 6,
  },
  cameraQueryButton: {
    margin: 0,
  },
  scanModeSelector: {
    marginTop: 2,
  },
  inlineCameraPanel: {
    borderWidth: 1,
    borderColor: "#DFE3E7",
    borderRadius: 12,
    overflow: "hidden",
    backgroundColor: "#FFFFFF",
  },
  inlineCameraView: {
    width: "100%",
    height: 220,
  },
  inlineCameraViewCompact: {
    height: 160,
  },
  autoAddToggleButton: {
    margin: 0,
  },
  scanFocusButton: {
    margin: 0,
  },
  searchToggleButton: {
    margin: 0,
  },
  searchClearFilterButton: {
    margin: 0,
  },
  searchInputWrap: {
    flex: 1,
    minWidth: 0,
  },
  searchInput: {
    width: "100%",
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#DFE3E7",
    elevation: 0,
  },
  searchInputText: {
    minHeight: 0,
    fontSize: 14,
  },
  utilityRow: {
    gap: 8,
    paddingRight: 4,
  },
  utilityChip: {
    backgroundColor: "#FFFFFF",
    borderColor: "#DFE3E7",
  },
  utilityChipActive: {
    backgroundColor: "#E8F3EC",
    borderColor: "#9AF1C7",
  },
  utilityChipText: {
    color: "#45474C",
    fontSize: 12,
  },
  utilityChipTextActive: {
    color: "#0B704F",
    fontWeight: "700",
  },
  filterChip: {
    backgroundColor: "#FFFFFF",
    borderColor: "#C5C6CD",
  },
  filterChipActive: {
    backgroundColor: "#111C2E",
    borderColor: "#111C2E",
  },
  filterChipText: {
    fontSize: 12,
    color: "#45474C",
  },
  filterChipTextActive: {
    color: "#FFFFFF",
    fontWeight: "700",
  },
  gradeOptionsWrap: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  resetFilterButtonContent: {
    justifyContent: "flex-start",
  },
  scanHintBanner: {
    borderRadius: 8,
    backgroundColor: "#EEF4FF",
    paddingHorizontal: 12,
    paddingVertical: 6,
  },
  scanHintText: {
    color: "#1677FF",
    fontWeight: "600",
  },
  content: {
    flex: 1,
  },
  listContent: {
    paddingHorizontal: 4,
    paddingBottom: 16,
    paddingTop: 6,
    flexGrow: 1,
  },
  listContentCompact: {
    paddingBottom: 0,
  },
  supplyStatusList: {
    paddingHorizontal: 12,
    paddingVertical: 16,
    gap: 10,
  },
  supplyStatusHint: {
    color: "#475467",
    marginBottom: 4,
  },
  homeEmptyState: {
    flexGrow: 1,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 16,
    paddingTop: 28,
  },
  columnWrapper: {
    justifyContent: "space-between",
    gap: 0,
  },
  paginationRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    marginHorizontal: 8,
    marginTop: 12,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 12,
    backgroundColor: "#FFFFFF",
  },
  filtersModal: {
    margin: 16,
    borderRadius: 16,
    backgroundColor: "#fff",
    padding: 16,
  },
  filtersModalContent: {
    gap: 16,
  },
  quantityEditorModal: {
    margin: 16,
    borderRadius: 16,
    backgroundColor: "#fff",
    padding: 16,
    gap: 12,
  },
  quantityEditorError: {
    color: "#BA1A1A",
  },
  quantityEditorActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    alignItems: "center",
    gap: 8,
  },
  filtersModalHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    gap: 12,
  },
  filtersModalTitleWrap: {
    flex: 1,
    gap: 4,
  },
  filtersSection: {
    gap: 10,
  },
  filtersSectionHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    gap: 12,
  },
  cameraModal: {
    margin: 16,
    borderRadius: 16,
    backgroundColor: "#fff",
    padding: 16,
    gap: 12,
  },
  cameraModalHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    gap: 12,
  },
  cameraModalTitleWrap: {
    flex: 1,
    gap: 4,
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
  cameraHitBar: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderRadius: 8,
    backgroundColor: "#EAF2FF",
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
  storeButton: {
    marginBottom: 8,
  },
  storeListScroll: {
    maxHeight: 280,
  },
  storeListContent: {
    paddingBottom: 4,
  },
  storeErrorWrap: {
    gap: 12,
  },
  hiddenInput: {
    position: "absolute",
    width: 1,
    height: 1,
    opacity: 0,
  },
  secondaryText: {
    color: "#666",
  },
});
