import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { AppState, Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { CameraView } from "expo-camera";
import { useLocalSearchParams, useRouter } from "expo-router";
import {
  useFocusEffect,
  useIsFocused,
  useNavigation,
  usePreventRemove,
  type NavigationAction,
} from "@react-navigation/native";
import {
  ActivityIndicator,
  Button,
  Card,
  Modal,
  Portal,
  Snackbar,
  Text,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";
import { CreateSupplierSheet } from "@/components/product-maintenance/CreateSupplierSheet";
import { CreateBarcodeScanner } from "@/components/product-maintenance/CreateBarcodeScanner";
import { CreateProductDialog } from "@/components/product-maintenance/CreateProductDialog";
import { CodeAddSheet } from "@/components/product-maintenance/CodeAddSheet";
import { LookupResultSheet } from "@/components/product-maintenance/LookupResultSheet";
import { LabelPrintCard } from "@/components/product-maintenance/LabelPrintCard";
import { PrintSettingsModal } from "@/components/product-maintenance/PrintSettingsModal";
import {
  PrinterConnectionChip,
  PrinterConnectionSummary,
} from "@/components/product-maintenance/PrinterConnectionStatus";
import { MultiCodeCompactList } from "@/components/product-maintenance/MultiCodeCompactList";
import { NumericInputModal } from "@/components/product-maintenance/NumericInputModal";
import { OfflineCatalogStatusRow } from "@/components/product-maintenance/OfflineCatalogStatusRow";
import { OfflineModeBanner } from "@/components/product-maintenance/OfflineModeBanner";
import { ProductHeroCard } from "@/components/product-maintenance/ProductHeroCard";
import { ProductPromotionCard } from "@/components/product-maintenance/ProductPromotionCard";
import { SearchPanel } from "@/components/product-maintenance/SearchPanel";
import { SetCodeCompactSection } from "@/components/product-maintenance/SetCodeCompactSection";
import { StickyActionBar } from "@/components/product-maintenance/StickyActionBar";
import { StoreClearancePriceCard } from "@/components/product-maintenance/StoreClearancePriceCard";
import { PosterEntryRow } from "@/components/product-maintenance/PosterEntryRow";
import { PosterQueueBar } from "@/components/promo-posters/PosterQueueBar";
import {
  StorePriceStrategyCard,
  StoreSwitchButton,
} from "@/components/product-maintenance/StorePriceStrategyCard";
import { SyncToOtherStoresSection } from "@/components/product-maintenance/SyncToOtherStoresSheet";
import { CameraScanSheet } from "@/components/ui/CameraScanSheet";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import {
  getSavedPrinter,
  printBigDiscountLabel,
  printClearanceLabel,
  printDiscountLabel,
  printProductLabel,
} from "@/modules/printer/api";
import { usePrinterStore } from "@/modules/printer/state";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  createProductWithPrices,
  createSetCode,
  evaluateAutoPricing,
  fetchActiveLocalSuppliers,
  generateLocalProductBarcode,
  getProductHqSyncOperation,
  getProductCodes,
  getProductFastDetail,
  ensureStorePrice,
  lookupProducts,
  scanProductLabel,
  retryProductHqSyncOperation,
  updateMultiCode,
  updateSetCode,
  updateProductType,
  updateStorePrice,
  upsertClearancePrice,
} from "@/modules/product-maintenance/api";
import { resolveExternalQueryStore } from "@/modules/product-maintenance/external-query-store";
import { validateCreateProductForm } from "@/modules/product-maintenance/create-product-validation";
import {
  hasStoredDeviceSession,
  isOfflineProductQueryEligible,
} from "@/modules/product-maintenance/offline-eligibility";
import {
  INITIAL_PRODUCT_QUERY_CONNECTIVITY,
  reduceProductQueryConnectivity,
  shouldAutoRelookupAfterRecovery,
} from "@/modules/product-maintenance/offline-mode";
import { useOfflineReconnectProbe } from "@/modules/product-maintenance/use-offline-reconnect-probe";
import { useOfflineCatalogStore } from "@/modules/product-maintenance/offline-catalog/offline-catalog-store";
import { useNetworkRecovery } from "@/shared/network";
import { isNetworkUnavailableError } from "@/shared/network/network-error";
import {
  calcGrossMarginPercent,
  getDirtyCodeIds,
  getMarginTrend,
  resolveCodeSections,
  resolveOriginalDisplay,
} from "@/modules/product-maintenance/product-query-presentation";
import type {
  EvaluateAutoPricingResult,
  LocalSupplierOption,
  MultiCodeEditableItem,
  ProductDetail,
  ProductHqSyncOperation,
  ProductLookupItem,
  ScanLabelPrintTarget,
  ScanLabelResult,
} from "@/modules/product-maintenance/types";
import { getVerifiedScanPrintTarget } from "@/modules/product-maintenance/scan-label";
import {
  createProductDetailRequestCoordinator,
  createProductHqSyncMutationCoordinator,
  getHqSyncDisplayState,
  getHqSyncPollDelayMs,
  getHqSyncStatusFailurePollDelayMs,
  isHqSyncInFlight,
  isProductMaintenanceStoreScopeCurrent,
  isRetryableHqSyncStatusHttpError,
  type ProductHqSyncMutationToken,
} from "@/modules/product-maintenance/hq-sync";
import {
  isProductQueryInteractionBlocked,
} from "@/modules/product-maintenance/warehouse-price-sync";
import {
  useCameraScan,
  type CameraScanMode,
} from "@/modules/scanner/use-camera-scan";
import {
  createCameraSheetSession,
  isCameraSheetSessionActive,
  reduceCameraSheetSession,
} from "@/modules/scanner/camera-sheet-session";
import { isAxiosError } from "axios";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import {
  playBarcodeCapturedSound,
  playScanFeedbackSound,
  preloadScanFeedbackSounds,
} from "@/modules/scanner/scan-sound";
import type { ScanSource } from "@/modules/scanner/types";
import { getManageableStoresForSession, isStoreManageable } from "@/modules/shop/store-scope";
import { useStores } from "@/modules/shop/use-stores";
import type { Store } from "@/modules/shop/types";
import { PERMISSIONS } from "@/shared/utils/access";
import { useAuthStore } from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import {
  buildLocalSupplierInvoicesRestoreHref,
  decodeLocalSupplierInvoicesReturnParams,
} from "@/modules/local-supplier-invoices/navigation";
import {
  resolveInvoiceEditorExitAction,
  resolveProductEditorStoreScope,
} from "@/modules/product-maintenance/invoice-editor-exit";
import { createActivePromotionRequestCoordinator } from "@/modules/promotions/active-promotion-request";
import { fetchValidPromotionsByProduct } from "@/modules/promotions/api";
import type { PromotionListItem } from "@/modules/promotions/types";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { IOS_REVIEW_SAMPLE_BARCODE } from "@/modules/ios-review/helpers";
import { resolveScanPosterAvailability } from "@/modules/promo-posters/logic";
import type { PromoPosterKind } from "@/modules/promo-posters/types";

type LookupTrigger = "manual" | "scan" | "refresh" | "deep-link";

function cloneDetail(detail: ProductDetail | null): ProductDetail | null {
  return detail ? JSON.parse(JSON.stringify(detail)) : null;
}

function formatCurrency(value?: number | null) {
  return value == null ? "" : value.toFixed(2);
}

function formatFixedDecimal(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "" : value.toFixed(2);
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function calcGpPercent(
  sellPrice?: number | null,
  purchasePrice?: number | null,
): string {
  if (sellPrice == null || !Number.isFinite(sellPrice) || sellPrice <= 0)
    return "";
  if (
    purchasePrice == null ||
    !Number.isFinite(purchasePrice) ||
    purchasePrice < 0
  )
    return "";
  const gp = ((sellPrice - purchasePrice) / sellPrice) * 100;
  if (!Number.isFinite(gp)) return "";
  return gp.toFixed(0) + "%";
}

function parseDecimalInput(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }

  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

function normalizeDiscountRateValue(value?: number | null) {
  if (value == null || !Number.isFinite(value) || value < 0) {
    return null;
  }

  if (value <= 1) {
    return value;
  }

  if (value <= 100) {
    return value / 100;
  }

  return null;
}

function getDiscountedRetailPrice(
  retailPrice?: number | null,
  discountRate?: number | null,
) {
  if (
    retailPrice == null ||
    !Number.isFinite(retailPrice) ||
    retailPrice <= 0 ||
    discountRate == null ||
    !Number.isFinite(discountRate)
  ) {
    return null;
  }

  return retailPrice * (1 - clamp(discountRate, 0, 1));
}

function getDiscountRateFromDiscountedRetail(
  retailPrice?: number | null,
  discountedRetail?: number | null,
) {
  if (
    retailPrice == null ||
    !Number.isFinite(retailPrice) ||
    retailPrice <= 0 ||
    discountedRetail == null ||
    !Number.isFinite(discountedRetail)
  ) {
    return null;
  }

  return clamp(1 - discountedRetail / retailPrice, 0, 1);
}

function formatPercentValue(value?: number | null) {
  if (value == null || !Number.isFinite(value)) {
    return "";
  }

  const percent = value * 100;
  return Number.isInteger(percent)
    ? String(percent)
    : percent.toFixed(2).replace(/\.?0+$/, "");
}

function firstParam(value: string | string[] | undefined) {
  const raw = Array.isArray(value) ? value[0] : value;
  const trimmed = raw?.trim();
  return trimmed || undefined;
}

function toFixedDecimalInput(value: string) {
  const numeric = parseDecimalInput(value);
  return {
    numeric,
    display: numeric == null ? "" : numeric.toFixed(2),
  };
}

function isStorePriceDirty(
  current: ProductDetail | null,
  initial: ProductDetail | null,
) {
  const left = current?.storePrice;
  const right = initial?.storePrice;
  return JSON.stringify(left ?? null) !== JSON.stringify(right ?? null);
}

function replaceStorePriceDetail(
  detail: ProductDetail,
  storePrice: NonNullable<ProductDetail["storePrice"]>,
) {
  return {
    ...detail,
    storePrice,
  };
}

type PrintAction =
  | "product"
  | "discount"
  | "clearance"
  | "bigDiscount"
  | `set:${string}`
  | `multi:${string}`;

type QueryFeedback =
  | { type: "idle" }
  | { type: "empty"; query: string }
  | { type: "error"; query?: string; message: string };

type AutoPricingFlowStatus =
  | "no_action"
  | "saved_without_prompt"
  | "prompt_confirmed"
  | "prompt_cancelled"
  | "failed";

interface LookupFlowResult {
  keepCameraOpen: boolean;
  labelPrinted: boolean;
  autoPricingStatus: AutoPricingFlowStatus;
  /** 后续 Paper 弹层仍在等待用户输入时，不能抢先恢复 Native 相机 sheet。 */
  foregroundPending?: boolean;
}

interface AutoPricingDialogState {
  detail: ProductDetail;
  evaluation: EvaluateAutoPricingResult;
  scanSource: ScanSource | null;
}

interface AutoPricingDialogResolution {
  status: "confirmed" | "cancelled" | "failed";
  keepCameraOpen: boolean;
  labelPrinted: boolean;
  updatedDetail?: ProductDetail | null;
}

interface DetailPostLoadOptions {
  scanSource?: ScanSource | null;
  scanKeyword?: string;
  autoPrintEnabled?: boolean;
  printTarget?: ScanLabelPrintTarget | null;
}

interface NumericInputModalState {
  key: string;
  title: string;
  value: string;
  allowDecimal: boolean;
  confirmLabel?: string;
}

interface BarcodeEditModalState {
  key: string;
  title: string;
  value: string;
  targetId: string;
  codeType: "set" | "multi";
}

interface CodeAddModalState {
  codeType: "set" | "multi";
  value: string;
  retailPrice: string;
}

interface CreateProductDraft {
  localSupplierCode: string;
  itemNumber: string;
  barcode: string;
  productName: string;
  purchasePrice: string;
  retailPrice: string;
  isSpecialProduct: boolean;
  isAutoPricing: boolean;
}

function getMultiCodeItemId(item: MultiCodeEditableItem): string {
  return item.setCodeId || item.uuid;
}

const PRODUCT_TYPE_OPTIONS = [0, 1, 2] as const;
/** 离线态下本店根本没有快照：与「读快照失败」区分开，两者的提示文案不同。 */
class OfflineCatalogUnavailableError extends Error {
  public constructor() {
    super("Offline catalog snapshot is unavailable for this store.");
    this.name = "OfflineCatalogUnavailableError";
  }
}

const CODE_PAGE_SIZE = 50;
const EMPTY_CREATE_PRODUCT_DRAFT: CreateProductDraft = {
  localSupplierCode: "",
  itemNumber: "",
  barcode: "",
  productName: "",
  purchasePrice: "",
  retailPrice: "",
  isSpecialProduct: false,
  isAutoPricing: false,
};

const DEFAULT_LOOKUP_FLOW_RESULT: LookupFlowResult = {
  keepCameraOpen: false,
  labelPrinted: false,
  autoPricingStatus: "no_action",
};

function ProductQueryContent() {
  const isFocused = useIsFocused();
  const navigation = useNavigation();
  const { t, language } = useAppTranslation(["productQuery", "common"]);
  const router = useRouter();
  const queryParams = useLocalSearchParams<{
    productCode?: string | string[];
    keyword?: string | string[];
    storeCode?: string | string[];
    source?: string | string[];
    returnInvoiceGuid?: string | string[];
    returnDetailGuid?: string | string[];
    returnDetailsPage?: string | string[];
    returnDetailsPageSize?: string | string[];
    returnDetailPriceChangeFilter?: string | string[];
    returnDetailSearch?: string | string[];
    returnListPage?: string | string[];
    returnListPageSize?: string | string[];
    returnFilterStoreCode?: string | string[];
    returnFilterSupplierCode?: string | string[];
    returnFilterInvoiceNo?: string | string[];
    returnFilterInboundStatus?: string | string[];
    returnFilterOrderDateFrom?: string | string[];
    returnFilterOrderDateTo?: string | string[];
    returnSortColId?: string | string[];
    returnSortDirection?: string | string[];
  }>();
  const invoiceReturnState = useMemo(
    () => decodeLocalSupplierInvoicesReturnParams({
      source: queryParams.source,
      returnInvoiceGuid: queryParams.returnInvoiceGuid,
      returnDetailGuid: queryParams.returnDetailGuid,
      returnDetailsPage: queryParams.returnDetailsPage,
      returnDetailsPageSize: queryParams.returnDetailsPageSize,
      returnDetailPriceChangeFilter: queryParams.returnDetailPriceChangeFilter,
      returnDetailSearch: queryParams.returnDetailSearch,
      returnListPage: queryParams.returnListPage,
      returnListPageSize: queryParams.returnListPageSize,
      returnFilterStoreCode: queryParams.returnFilterStoreCode,
      returnFilterSupplierCode: queryParams.returnFilterSupplierCode,
      returnFilterInvoiceNo: queryParams.returnFilterInvoiceNo,
      returnFilterInboundStatus: queryParams.returnFilterInboundStatus,
      returnFilterOrderDateFrom: queryParams.returnFilterOrderDateFrom,
      returnFilterOrderDateTo: queryParams.returnFilterOrderDateTo,
      returnSortColId: queryParams.returnSortColId,
      returnSortDirection: queryParams.returnSortDirection,
    }),
    [
      queryParams.returnDetailsPage,
      queryParams.returnDetailsPageSize,
      queryParams.returnDetailGuid,
      queryParams.returnDetailPriceChangeFilter,
      queryParams.returnDetailSearch,
      queryParams.returnFilterInvoiceNo,
      queryParams.returnFilterInboundStatus,
      queryParams.returnFilterOrderDateFrom,
      queryParams.returnFilterOrderDateTo,
      queryParams.returnFilterStoreCode,
      queryParams.returnFilterSupplierCode,
      queryParams.returnInvoiceGuid,
      queryParams.returnListPage,
      queryParams.returnListPageSize,
      queryParams.returnSortColId,
      queryParams.returnSortDirection,
      queryParams.source,
    ],
  );
  const {
    stores,
    selectedStore: globalSelectedStore,
    selectedStoreCode: globalSelectedStoreCode,
    selectStore,
    isDeviceMode,
    isLoading: storesLoading,
    isHydratingSelection,
  } = useStores();
  const editorStoreScope = resolveProductEditorStoreScope({
    invoiceStoreCode: firstParam(queryParams.storeCode),
    selectedStoreCode: globalSelectedStoreCode,
    hasInvoiceReturnContext: Boolean(invoiceReturnState),
  });
  const selectedStoreCode = editorStoreScope.storeCode;
  const selectedStoreCodeRef = useRef(selectedStoreCode);
  selectedStoreCodeRef.current = selectedStoreCode;
  const selectedStore = editorStoreScope.locked
    ? stores.find((store) => store.storeCode === selectedStoreCode)
    : globalSelectedStore;
  const access = useAuthStore((state) => state.access);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const deviceSession = useDeviceStore((state) => state.session);
  // 离线功能只对设备注册绑定会话开放；普通账号登录与审核态保持既有在线行为。
  const offlineEligible = isOfflineProductQueryEligible({
    sessionKind,
    hasStoredDeviceSession: hasStoredDeviceSession(deviceSession),
  });
  const { state: recoveryState, triggerRecovery } = useNetworkRecovery();
  const [connectivity, dispatchConnectivity] = useReducer(
    reduceProductQueryConnectivity,
    INITIAL_PRODUCT_QUERY_CONNECTIVITY,
  );
  const offlineMode = offlineEligible && connectivity.offline;
  const offlineModeRef = useRef(offlineMode);
  offlineModeRef.current = offlineMode;
  const offlineEligibleRef = useRef(offlineEligible);
  offlineEligibleRef.current = offlineEligible;
  const offlineCatalogActiveMeta = useOfflineCatalogStore((state) =>
    selectedStoreCode ? (state.activeMeta[selectedStoreCode] ?? null) : null,
  );
  const offlineCatalogRefresh = useOfflineCatalogStore((state) => state.refresh);
  const [appActive, setAppActive] = useState(
    AppState.currentState === "active" || AppState.currentState === "unknown",
  );
  // 同步其它分店：仅登录账号（非设备模式）+ 权限码 + 当前分店可管理（isPrimary 或管理员）时开放，后端同样校验。
  const canSyncToOtherStores =
    !isDeviceMode &&
    isAuthenticated &&
    access.hasPermission(PERMISSIONS.StoreProducts.SyncToOtherStores) &&
    isStoreManageable(
      selectedStoreCode,
      getManageableStoresForSession({ stores, isDeviceMode, isAdmin: access.isAdmin }),
    );
  const printerAutoReconnectPaused = usePrinterStore(
    (state) => state.autoReconnectPaused,
  );
  const savedPrinter = usePrinterStore((state) => state.savedPrinter);
  const printerConnectionStatus = usePrinterStore((state) => state.status);
  const printerLastError = usePrinterStore((state) => state.lastError);
  const [keyword, setKeyword] = useState("");
  const [lookupItems, setLookupItems] = useState<ProductLookupItem[]>([]);
  const [selectedLookupProductCode, setSelectedLookupProductCode] =
    useState<string>();
  const [detail, setDetail] = useState<ProductDetail | null>(null);
  const [initialDetail, setInitialDetail] = useState<ProductDetail | null>(
    null,
  );
  const [activePromotions, setActivePromotions] = useState<PromotionListItem[]>(
    [],
  );
  const [lastHitLabel, setLastHitLabel] = useState<string>();
  const [lookupVisible, setLookupVisible] = useState(false);
  const [lookupSelectionSource, setLookupSelectionSource] =
    useState<ScanSource | null>(null);
  const [cameraScanMode, setCameraScanMode] =
    useState<CameraScanMode>("single");
  const [cameraSession, setCameraSession] = useState(() =>
    createCameraSheetSession("single"),
  );
  const cameraSheetSessionRef = useRef(cameraSession);
  const cameraScanModeRef = useRef(cameraScanMode);
  const isFocusedRef = useRef(isFocused);
  const cameraForegroundGenerationRef = useRef<number | null>(null);
  const [lastCameraBarcode, setLastCameraBarcode] = useState<string | null>(
    null,
  );
  const cameraVisible = cameraSession.visible;
  const [queryFeedback, setQueryFeedback] = useState<QueryFeedback>({
    type: "idle",
  });
  isFocusedRef.current = isFocused;
  cameraScanModeRef.current = cameraScanMode;
  const [loading, setLoading] = useState(false);
  const [pendingScanPresentation, setPendingScanPresentation] = useState<{
    keyword: string; productName: string;
  } | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [savingItemId, setSavingItemId] = useState<string | null>(null);
  const [snackbarMessage, setSnackbarMessage] = useState("");
  const [hqSyncOperation, setHqSyncOperation] =
    useState<ProductHqSyncOperation | null>(null);
  const [hqSyncRetrying, setHqSyncRetrying] = useState(false);
  const [printingAction, setPrintingAction] = useState<PrintAction | null>(
    null,
  );
  const [continuousPrintEnabled, setContinuousPrintEnabled] = useState(false);
  const [autoPrintOnLookupConfirm, setAutoPrintOnLookupConfirm] =
    useState(false);
  const [autoPricingDialog, setAutoPricingDialog] =
    useState<AutoPricingDialogState | null>(null);
  const [autoPricingDialogSaving, setAutoPricingDialogSaving] = useState(false);
  const [createProductVisible, setCreateProductVisible] = useState(false);
  const [createSupplierPickerVisible, setCreateSupplierPickerVisible] =
    useState(false);
  const [createProductSaving, setCreateProductSaving] = useState(false);
  const [createBarcodeGenerating, setCreateBarcodeGenerating] = useState(false);
  const [createBarcodeScannerVisible, setCreateBarcodeScannerVisible] =
    useState(false);
  const createProductBusy = createProductSaving || createBarcodeGenerating;
  const [createSuppliers, setCreateSuppliers] = useState<LocalSupplierOption[]>(
    [],
  );
  const [createSuppliersLoading, setCreateSuppliersLoading] = useState(false);
  const [createProductDraft, setCreateProductDraft] =
    useState<CreateProductDraft>(EMPTY_CREATE_PRODUCT_DRAFT);
  const [productTypeDialogVisible, setProductTypeDialogVisible] =
    useState(false);
  const [productTypeSaving, setProductTypeSaving] = useState(false);
  const [storePurchaseInput, setStorePurchaseInput] = useState("");
  const [storeRetailInput, setStoreRetailInput] = useState("");
  const [clearancePriceInput, setClearancePriceInput] = useState("");
  const [savingClearance, setSavingClearance] = useState(false);
  const [codesLoading, setCodesLoading] = useState(false);
  const [codesLoadingMore, setCodesLoadingMore] = useState(false);
  const [codePage, setCodePage] = useState(1);
  const [codesHasMore, setCodesHasMore] = useState(false);
  const [barcodeEditModal, setBarcodeEditModal] =
    useState<BarcodeEditModalState | null>(null);
  const [codeAddModal, setCodeAddModal] = useState<CodeAddModalState | null>(
    null,
  );
  const [smallLabel, setSmallLabel] = useState(false);
  const [printQuantity, setPrintQuantity] = useState(1);
  const [quantitySingleUse, setQuantitySingleUse] = useState(true);
  const [printSettingsVisible, setPrintSettingsVisible] = useState(false);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [discardReturnVisible, setDiscardReturnVisible] = useState(false);
  const [allowInvoiceExit, setAllowInvoiceExit] = useState(false);
  const pendingInvoiceExitRef = useRef<
    { kind: "invoice" } | { kind: "navigation"; action: NavigationAction } | null
  >(null);
  const invoiceExitSavingRef = useRef(false);
  const [editorTab, setEditorTab] = useState<"price" | "codes">("price");
  const [savingAndPrinting, setSavingAndPrinting] = useState(false);
  // 新增编码面板里的相机扫码：打开时临时隐藏面板，避免两个原生 Modal 叠加。
  const [codeAddScannerVisible, setCodeAddScannerVisible] = useState(false);
  const getErrorMessage = useCallback(
    (error: unknown, fallbackKey: string) =>
      resolveLocalizedErrorMessage(error, {
        language,
        t,
        fallbackKey,
      }),
    [language, t],
  );
  const autoPricingDialogResolverRef = useRef<
    ((result: AutoPricingDialogResolution) => void) | null
  >(null);
  const numericInputConfirmRef = useRef<((value: string) => void) | null>(null);
  const lookupSelectionOpenRef = useRef(false);
  const lookupRequestInFlightRef = useRef(false);
  const scanLabelUnsupportedUntilRef = useRef(0);
  const storeSelectionInFlightRef = useRef(false);
  const resumeHiddenScannerFocusTimerRef = useRef<ReturnType<
    typeof setTimeout
  > | null>(null);
  const searchInputFocusedRef = useRef(false);
  const processLoadedDetailRef = useRef<
    | ((
        targetDetail: ProductDetail,
        options: DetailPostLoadOptions,
      ) => Promise<LookupFlowResult>)
    | null
  >(null);
  const saveClearanceRef = useRef<() => Promise<void>>(async () => {});
  const saveMultiCodeRef = useRef<
    (itemId: string, retailPriceOverride?: number | null) => Promise<void>
  >(async () => {});
  const hqSyncOperationIdRef = useRef<string | null>(null);
  const loadedDetailStoreCodeRef = useRef<string | null>(null);
  const activeDetailProductCodeRef = useRef<string | null>(null);
  const hqSyncMutationCoordinatorRef = useRef<ReturnType<
    typeof createProductHqSyncMutationCoordinator
  > | null>(null);
  const detailRequestCoordinatorRef = useRef<ReturnType<
    typeof createProductDetailRequestCoordinator
  > | null>(null);
  if (!hqSyncMutationCoordinatorRef.current) {
    hqSyncMutationCoordinatorRef.current =
      createProductHqSyncMutationCoordinator();
  }
  if (!detailRequestCoordinatorRef.current) {
    detailRequestCoordinatorRef.current =
      createProductDetailRequestCoordinator();
  }
  const handledExternalQueryRef = useRef<string | null>(null);
  const [numericInputModal, setNumericInputModal] =
    useState<NumericInputModalState | null>(null);
  const activePromotionCoordinatorRef = useRef<ReturnType<
    typeof createActivePromotionRequestCoordinator
  > | null>(null);
  if (!activePromotionCoordinatorRef.current) {
    activePromotionCoordinatorRef.current =
      createActivePromotionRequestCoordinator({
        fetchPromotions: fetchValidPromotionsByProduct,
        applyPromotions: setActivePromotions,
        onFailure: (error) => {
          console.warn("[product-query] active promotions load failed", {
            message: isAxiosError(error) ? error.message : String(error),
            status: isAxiosError(error) ? error.response?.status : undefined,
          });
        },
      });
  }
  const rememberHqSyncOperation = useCallback(
    (operation: ProductHqSyncOperation) => {
      hqSyncOperationIdRef.current = operation.operationId;
      setHqSyncOperation(operation);
    },
    [],
  );
  const activateHqSyncScope = useCallback(
    (productCode?: string | null, storeCode?: string | null) => {
      detailRequestCoordinatorRef.current?.activate({ productCode, storeCode });
      if (
        !hqSyncMutationCoordinatorRef.current?.activate({
          productCode,
          storeCode,
        })
      ) {
        return;
      }

      // 商品或分店切换后，旧 mutation 与轮询结果都不能重新占用当前页面提示。
      hqSyncOperationIdRef.current = null;
      setHqSyncOperation(null);
    },
    [],
  );
  const beginHqSyncMutation = useCallback(
    (productCode?: string | null, storeCode?: string | null) => {
      activateHqSyncScope(productCode, storeCode);
      return hqSyncMutationCoordinatorRef.current!.begin();
    },
    [activateHqSyncScope],
  );
  const presentHqSyncOperation = useCallback(
    (
      mutation: ProductHqSyncMutationToken,
      operation: ProductHqSyncOperation | null | undefined,
      fallbackMessage?: string,
    ) => {
      const resultScope = operation
        ? {
            productCode: operation.productCode,
            storeCode: operation.storeCode ?? mutation.scope.storeCode,
          }
        : undefined;
      if (!operation) {
        // 没有 outbox operation 时只展示本地保存反馈，绝不能推进成功序号；
        // 否则后发的普通响应会吞掉先发真实 HQ 同步任务。
        if (!hqSyncMutationCoordinatorRef.current?.isCurrent(mutation)) {
          return false;
        }
        if (fallbackMessage) {
          setSnackbarMessage(fallbackMessage);
        }
        return false;
      }

      if (
        !hqSyncMutationCoordinatorRef.current?.succeed(mutation, resultScope)
      ) {
        return false;
      }

      rememberHqSyncOperation(operation);
      if (operation.status === "succeeded") {
        setSnackbarMessage(t("messages.hqSyncSucceeded"));
      } else if (operation.status === "blocked") {
        setSnackbarMessage(t("messages.hqSyncBlocked"));
      } else if (operation.status === "retrying") {
        setSnackbarMessage(t("messages.hqSyncRetrying"));
      } else if (operation.status === "superseded") {
        setSnackbarMessage(t("messages.hqSyncSuperseded"));
      } else {
        setSnackbarMessage(t("messages.hqSyncPending"));
      }
      return true;
    },
    [rememberHqSyncOperation, t],
  );

  useEffect(() => {
    const initialOperation = hqSyncOperation;
    if (!isFocused || !isHqSyncInFlight(initialOperation)) {
      return;
    }

    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let consecutiveStatusFailures = 0;

    const poll = (
      current: ProductHqSyncOperation,
      delayMs = getHqSyncPollDelayMs(current),
    ) => {
      timer = setTimeout(async () => {
        try {
          const latest = await getProductHqSyncOperation(current.operationId);
          if (disposed || hqSyncOperationIdRef.current !== latest.operationId) {
            return;
          }

          consecutiveStatusFailures = 0;
          setHqSyncOperation(latest);
          if (latest.status === "succeeded") {
            setSnackbarMessage(t("messages.hqSyncSucceeded"));
            return;
          }
          if (latest.status === "blocked") {
            setSnackbarMessage(t("messages.hqSyncBlocked"));
            return;
          }
          if (latest.status === "superseded") {
            setSnackbarMessage(t("messages.hqSyncSuperseded"));
            return;
          }
          if (latest.status === "retrying" && current.status !== "retrying") {
            setSnackbarMessage(t("messages.hqSyncRetrying"));
          }
          poll(latest);
        } catch (error) {
          if (
            disposed ||
            hqSyncOperationIdRef.current !== current.operationId
          ) {
            return;
          }

          const status = isAxiosError(error)
            ? error.response?.status
            : undefined;
          if (isAxiosError(error) && isRetryableHqSyncStatusHttpError(status)) {
            // 网络、限流或服务端短暂失败不改变已保存事实，继续观察同一持久任务。
            consecutiveStatusFailures += 1;
            poll(
              current,
              getHqSyncStatusFailurePollDelayMs(consecutiveStatusFailures),
            );
            return;
          }

          // 403/404 或无效响应不会自行恢复，停止热循环并明确保留“本地已保存”的事实。
          setHqSyncOperation({
            ...current,
            status: "blocked",
            retryable: false,
            errorCode: "HQ_SYNC_STATUS_UNAVAILABLE",
            message: t("messages.hqSyncStatusUnavailable"),
          });
          setSnackbarMessage(t("messages.hqSyncStatusUnavailable"));
        }
      }, delayMs);
    };

    poll(initialOperation);
    return () => {
      disposed = true;
      if (timer) {
        clearTimeout(timer);
      }
    };
  }, [hqSyncOperation, isFocused, t]);

  const handleRetryHqSync = useCallback(async () => {
    if (
      !hqSyncOperation ||
      hqSyncOperation.status !== "blocked" ||
      hqSyncRetrying
    ) {
      return;
    }

    const operationId = hqSyncOperation.operationId;
    const mutation = beginHqSyncMutation(
      hqSyncOperation.productCode,
      hqSyncOperation.storeCode ?? selectedStoreCode,
    );
    setHqSyncRetrying(true);
    try {
      const retried = await retryProductHqSyncOperation(operationId);
      if (hqSyncOperationIdRef.current === operationId) {
        presentHqSyncOperation(mutation, retried);
      }
    } catch (error) {
      hqSyncMutationCoordinatorRef.current?.fail(mutation);
      if (hqSyncOperationIdRef.current === operationId) {
        setSnackbarMessage(
          getErrorMessage(error, "messages.hqSyncRetryFailed"),
        );
      }
    } finally {
      setHqSyncRetrying(false);
    }
  }, [
    beginHqSyncMutation,
    getErrorMessage,
    hqSyncOperation,
    hqSyncRetrying,
    presentHqSyncOperation,
    selectedStoreCode,
  ]);
  const invalidateActivePromotions = useCallback(() => {
    activePromotionCoordinatorRef.current?.invalidate();
  }, []);
  const loadActivePromotions = useCallback(
    (productCode: string, storeCode: string) => {
      void activePromotionCoordinatorRef.current?.load(productCode, storeCode);
    },
    [],
  );
  const isProductQueryBusy = useCallback(
    () =>
      isProductQueryInteractionBlocked({
        loading,
        lookupVisible,
        lookupSelectionOpen: lookupSelectionOpenRef.current,
        autoPricingVisible: Boolean(autoPricingDialog),
        autoPricingSaving: autoPricingDialogSaving,
        warehouseLocked: false,
        requestInFlight: lookupRequestInFlightRef.current,
        storeSelectionInFlight: storeSelectionInFlightRef.current,
      }) ||
      saving ||
      Boolean(savingItemId) ||
      savingClearance ||
      productTypeSaving ||
      createProductBusy ||
      hqSyncRetrying,
    [
      autoPricingDialog,
      autoPricingDialogSaving,
      loading,
      lookupVisible,
      createProductBusy,
      hqSyncRetrying,
      productTypeSaving,
      saving,
      savingClearance,
      savingItemId,
    ],
  );
  useEffect(() => {
    setStorePurchaseInput(
      formatFixedDecimal(detail?.storePrice?.purchasePrice),
    );
    setStoreRetailInput(formatFixedDecimal(detail?.storePrice?.retailPrice));
  }, [
    detail?.storePrice?.purchasePrice,
    detail?.storePrice?.retailPrice,
    detail?.storePrice?.uuid,
  ]);

  useEffect(() => {
    setEditorTab("price");
  }, [detail?.productCode]);

  useEffect(() => {
    setClearancePriceInput(
      formatFixedDecimal(detail?.clearancePrice?.clearancePrice),
    );
  }, [detail?.clearancePrice?.clearancePrice, detail?.clearancePrice?.uuid]);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);

  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      setAppActive(next === "active");
    });
    return () => subscription.remove();
  }, []);

  useEffect(() => {
    // 离线资格会话进页或切店时后台读取本店快照摘要（懒打开 SQLite）。
    if (!offlineEligible || !selectedStoreCode) {
      return;
    }
    void useOfflineCatalogStore.getState().loadActiveMeta(selectedStoreCode);
  }, [offlineEligible, selectedStoreCode]);

  useEffect(() => {
    // 全局恢复控制器的探测结果也喂给连通性状态机（双保险）；早于离线时刻的结果会被忽略。
    if (!recoveryState.lastCheckedAtIso) {
      return;
    }
    const checkedAtMs = Date.parse(recoveryState.lastCheckedAtIso);
    if (!Number.isFinite(checkedAtMs)) {
      return;
    }
    dispatchConnectivity({
      type: "backend_check",
      reachable: recoveryState.isBackendReachable,
      checkedAtMs,
    });
  }, [recoveryState.isBackendReachable, recoveryState.lastCheckedAtIso]);

  const { probeNow: probeReconnectNow } = useOfflineReconnectProbe({
    enabled: offlineMode && isFocused && appActive,
    onReachable: (checkedAtMs) => {
      dispatchConnectivity({ type: "backend_check", reachable: true, checkedAtMs });
      void triggerRecovery();
    },
  });

  const enterOfflineMode = useCallback(
    (keyword?: string | null) => {
      if (!offlineEligibleRef.current) {
        return;
      }
      dispatchConnectivity({ type: "network_failure", atMs: Date.now(), keyword });
      void triggerRecovery();
    },
    [triggerRecovery],
  );

  const notifyOfflineEditing = useCallback(() => {
    setSnackbarMessage(t("offline.editingUnavailable"));
  }, [t]);

  // 扫码忙碌状态变化时保留状态行回调，避免重复格式化未变的目录时间和件数。
  const handleCancelOfflineCatalog = useCallback(() => {
    useOfflineCatalogStore.getState().cancelRefresh();
  }, []);

  const handleRefreshOfflineCatalog = useCallback(() => {
    if (!selectedStoreCode || offlineModeRef.current) {
      return;
    }
    void useOfflineCatalogStore
      .getState()
      .refreshCatalog(selectedStoreCode)
      .then((result) => {
        if (!result) {
          return;
        }
        setSnackbarMessage(
          result.mode === "noChange"
            ? t("offline.refreshNoChange")
            : t("offline.refreshCompleted", { count: result.metadata.itemCount }),
        );
      });
  }, [selectedStoreCode, t]);

  const playQueryFeedback = useCallback(
    (
      status:
        "found" | "multiple" | "not_found" | "error" | "price_update_required",
    ) => {
      playScanFeedbackSound(status);
    },
    [],
  );

  const loadProductCodes = useCallback(
    async (
      sourceDetail: ProductDetail,
      nextPage = 1,
      append = false,
      storeCodeOverride?: string,
    ): Promise<ProductDetail | undefined> => {
      const storeCode = storeCodeOverride ?? selectedStoreCode;
      if (!storeCode) {
        return;
      }
      const request = detailRequestCoordinatorRef.current!.begin({
        productCode: sourceDetail.productCode,
        storeCode,
      });

      const hasSetCodes = sourceDetail.setCodeCount > 0;
      const hasMultiCodes = sourceDetail.multiCodeCount > 0;
      if (
        !hasSetCodes &&
        !hasMultiCodes &&
        sourceDetail.productType !== 1 &&
        sourceDetail.productType !== 2
      ) {
        return;
      }

      if (append) {
        setCodesLoadingMore(true);
      } else {
        setCodesLoading(true);
        setCodePage(1);
        setCodesHasMore(false);
      }

      try {
        if (
          sourceDetail.productType === 1 ||
          (sourceDetail.productType !== 2 && hasSetCodes && !hasMultiCodes)
        ) {
          const page = await getProductCodes(
            sourceDetail.productCode,
            storeCode,
            1,
            nextPage,
            CODE_PAGE_SIZE,
          );
          if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
            return;
          }
          const applyPage = (current: ProductDetail | null) =>
            current?.productCode === sourceDetail.productCode
              ? {
                  ...current,
                  setCodes: append
                    ? [...current.setCodes, ...page.items]
                    : page.items,
                  setCodeCount: page.totalCount,
                  codesIncluded: true,
                }
              : current;
          setDetail(applyPage);
          setInitialDetail((current) => cloneDetail(applyPage(current)));
          setCodePage(page.page);
          setCodesHasMore(page.hasMore);
          if (!append) {
            return {
              ...sourceDetail,
              setCodes: page.items,
              setCodeCount: page.totalCount,
              codesIncluded: true,
            };
          }
          return;
        }

        const page = await getProductCodes(
          sourceDetail.productCode,
          storeCode,
          2,
          nextPage,
          CODE_PAGE_SIZE,
        );
        if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
          return;
        }
        const applyPage = (current: ProductDetail | null) =>
          current?.productCode === sourceDetail.productCode
            ? {
                ...current,
                multiCodes: append
                  ? [...current.multiCodes, ...page.items]
                  : page.items,
                multiCodeCount: page.totalCount,
                codesIncluded: true,
              }
            : current;
        setDetail(applyPage);
        setInitialDetail((current) => cloneDetail(applyPage(current)));
        setCodePage(page.page);
        setCodesHasMore(page.hasMore);
        if (!append) {
          return {
            ...sourceDetail,
            multiCodes: page.items,
            multiCodeCount: page.totalCount,
            codesIncluded: true,
          };
        }
      } catch (error) {
        if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
          return;
        }
        if (offlineEligibleRef.current && isNetworkUnavailableError(error)) {
          // 商品详情已经拿到、只有分页码表这一步断网：直接降级到本地快照补齐套码/多码，
          // 而不是丢给用户一条红色错误——否则详情会停在缺码表的半成品状态。
          enterOfflineMode();
          const offlineDetail = await useOfflineCatalogStore
            .getState()
            .getDetail(storeCode, sourceDetail.productCode);
          if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
            return;
          }
          if (offlineDetail) {
            // 只补码表。商品级字段与分店价必须保留刚拿到的实时值，否则屏幕显示实时价、
            // 自动打印却用快照价，两个数据源会被缝在同一个 detail 对象上。
            const merged: ProductDetail = {
              ...sourceDetail,
              setCodes: offlineDetail.setCodes,
              setCodeCount: offlineDetail.setCodeCount,
              multiCodes: offlineDetail.multiCodes,
              multiCodeCount: offlineDetail.multiCodeCount,
              codesIncluded: true,
            };
            // 成功分支会 setDetail，降级分支同样要写屏，否则列表停在「(0/N)」空表
            // 且没有「加载更多」，用户无从恢复。
            const applyOffline = (current: ProductDetail | null) =>
              current?.productCode === sourceDetail.productCode ? merged : current;
            setDetail(applyOffline);
            setInitialDetail((current) => cloneDetail(applyOffline(current)));
            setCodePage(1);
            setCodesHasMore(false);
            return merged;
          }
        }
        const fallback = t("messages.codesLoadFailed");
        let detail = "";
        if (isAxiosError(error) && !error.response) {
          detail =
            error.code === "ECONNABORTED"
              ? t("messages.lookupTimeout")
              : t("messages.lookupNetworkError");
        } else {
          detail = getErrorMessage(error, "messages.codesLoadFailed");
        }
        setSnackbarMessage(detail ? `${fallback}: ${detail}` : fallback);
        playQueryFeedback("error");
      } finally {
        if (detailRequestCoordinatorRef.current?.isCurrent(request)) {
          setCodesLoading(false);
          setCodesLoadingMore(false);
        }
      }
    },
    [enterOfflineMode, playQueryFeedback, selectedStoreCode, t],
  );

  const loadDetail = useCallback(
    async (productCode: string, storeCodeOverride?: string) => {
      const targetStoreCode = storeCodeOverride ?? selectedStoreCode;
      if (!targetStoreCode) {
        setSnackbarMessage(t("messages.selectStoreFirst"));
        return null;
      }

      activateHqSyncScope(productCode, targetStoreCode);
      invalidateActivePromotions();
      // 请求尚未返回时也记录其范围；其他 tab 换店可立即使慢响应失效。
      activeDetailProductCodeRef.current = productCode;
      loadedDetailStoreCodeRef.current = targetStoreCode;
      const request = detailRequestCoordinatorRef.current!.begin({
        productCode,
        storeCode: targetStoreCode,
      });
      console.log("[product-query] load detail", {
        selectedStoreCode: targetStoreCode,
        offline: offlineModeRef.current,
      });
      if (offlineModeRef.current) {
        // 离线态：详情来自本地快照，已含全部套码/多码，不再请求分页码表与促销。
        const offlineDetail = await useOfflineCatalogStore
          .getState()
          .getDetail(targetStoreCode, productCode);
        if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
          return null;
        }
        if (!offlineDetail) {
          setSnackbarMessage(t("offline.notInCatalog"));
          return null;
        }
        setActivePromotions([]);
        setDetail(offlineDetail);
        setInitialDetail(cloneDetail(offlineDetail));
        setSelectedLookupProductCode(productCode);
        setLastHitLabel(
          `${offlineDetail.itemNumber || offlineDetail.productCode} / ${offlineDetail.barcode || "--"}`,
        );
        setQueryFeedback({ type: "idle" });
        setCodePage(1);
        setCodesHasMore(false);
        return offlineDetail;
      }
      const payload = await getProductFastDetail(productCode, targetStoreCode);
      if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
        return null;
      }
      dispatchConnectivity({ type: "request_succeeded" });
      let detailWithStorePrice = payload;
      if (
        !payload.storePrice &&
        detailRequestCoordinatorRef.current.isCurrent(request)
      ) {
        const mutation = beginHqSyncMutation(productCode, targetStoreCode);
        try {
          const storePrice = await ensureStorePrice(productCode, targetStoreCode);
          if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
            return null;
          }
          presentHqSyncOperation(mutation, storePrice.hqSync);
          detailWithStorePrice = { ...payload, storePrice };
        } catch (error) {
          hqSyncMutationCoordinatorRef.current?.fail(mutation);
          if (!detailRequestCoordinatorRef.current?.isCurrent(request)) {
            return null;
          }
          // 只读用户仍可查询商品；其他补建失败交由既有查询错误流程处理。
          if (!(isAxiosError(error) && error.response?.status === 403)) {
            throw error;
          }
        }
      }
      loadActivePromotions(payload.productCode, targetStoreCode);
      setDetail(detailWithStorePrice);
      setInitialDetail(cloneDetail(detailWithStorePrice));
      setSelectedLookupProductCode(productCode);
      setLastHitLabel(
        `${payload.itemNumber || payload.productCode} / ${payload.barcode || "--"}`,
      );
      setQueryFeedback({ type: "idle" });
      setCodePage(1);
      setCodesHasMore(false);
      const detailWithCodes = await loadProductCodes(
        detailWithStorePrice,
        1,
        false,
        targetStoreCode,
      );
      if (
        !detailRequestCoordinatorRef.current?.isScopeActive({
          productCode,
          storeCode: targetStoreCode,
        })
      ) {
        return null;
      }
      return detailWithCodes ?? detailWithStorePrice;
    },
    [
      activateHqSyncScope,
      beginHqSyncMutation,
      invalidateActivePromotions,
      loadActivePromotions,
      loadProductCodes,
      presentHqSyncOperation,
      selectedStoreCode,
      t,
    ],
  );

  const discardStaleDetailForStoreChange = useCallback(
    (productCode?: string) => {
      loadedDetailStoreCodeRef.current = null;
      activeDetailProductCodeRef.current = null;
      activateHqSyncScope(productCode, selectedStoreCode);
      invalidateActivePromotions();
      setDetail(null);
      setInitialDetail(null);
      setSelectedLookupProductCode(undefined);
      setCodePage(1);
      setCodesHasMore(false);
    },
    [activateHqSyncScope, invalidateActivePromotions, selectedStoreCode],
  );

  useEffect(() => {
    const activeProductCode =
      detail?.productCode ?? activeDetailProductCodeRef.current;
    if (!activeProductCode || storeSelectionInFlightRef.current) {
      return;
    }

    if (
      isProductMaintenanceStoreScopeCurrent(
        loadedDetailStoreCodeRef.current,
        selectedStoreCode,
      )
    ) {
      return;
    }

    // 其他 tab 修改全局分店后，本页不能继续保留旧店的可编辑 UUID。
    discardStaleDetailForStoreChange(activeProductCode);
  }, [
    detail?.productCode,
    discardStaleDetailForStoreChange,
    selectedStoreCode,
  ]);

  const ensureCurrentDetailStoreScope = useCallback(
    (sourceDetail: ProductDetail, recordStoreCode?: string | null) => {
      const targetStoreCode =
        recordStoreCode ??
        sourceDetail.storePrice?.storeCode ??
        loadedDetailStoreCodeRef.current;
      if (
        isProductMaintenanceStoreScopeCurrent(
          targetStoreCode,
          selectedStoreCode,
        )
      ) {
        return true;
      }

      discardStaleDetailForStoreChange(sourceDetail.productCode);
      if (selectedStoreCode) {
        void loadDetail(sourceDetail.productCode, selectedStoreCode);
      }
      return false;
    },
    [discardStaleDetailForStoreChange, loadDetail, selectedStoreCode],
  );

  const canSelectStore =
    !editorStoreScope.locked &&
    !isDeviceMode &&
    stores.length > 0 &&
    !isProductQueryBusy();

  const handleSelectStore = useCallback(
    async (store: Store | null) => {
      if (!store || editorStoreScope.locked || isProductQueryBusy()) {
        return;
      }

      storeSelectionInFlightRef.current = true;
      setLoading(true);
      setStorePickerVisible(false);
      try {
        activateHqSyncScope(detail?.productCode, store.storeCode);
        invalidateActivePromotions();
        await selectStore(store);

        if (detail?.productCode) {
          const nextDetail = await loadDetail(
            detail.productCode,
            store.storeCode,
          );
          if (nextDetail) {
            await processLoadedDetailRef.current?.(nextDetail, {
            });
          }
        }
      } catch (error) {
        invalidateActivePromotions();
        if (detail?.productCode) {
          setDetail(null);
          setInitialDetail(null);
          setSelectedLookupProductCode(undefined);
          setCodePage(1);
          setCodesHasMore(false);
        }
        setSnackbarMessage(getErrorMessage(error, "messages.refreshFailed"));
        playQueryFeedback("error");
      } finally {
        storeSelectionInFlightRef.current = false;
        setLoading(false);
      }
    },
    [
      activateHqSyncScope,
      detail?.productCode,
      editorStoreScope.locked,
      getErrorMessage,
      invalidateActivePromotions,
      isProductQueryBusy,
      loadDetail,
      playQueryFeedback,
      selectStore,
    ],
  );

  const selectedCreateSupplier = useMemo(
    () =>
      createSuppliers.find(
        (supplier) =>
          supplier.supplierCode === createProductDraft.localSupplierCode,
      ) ?? null,
    [createProductDraft.localSupplierCode, createSuppliers],
  );

  const loadCreateSuppliers = useCallback(async () => {
    setCreateSuppliersLoading(true);
    try {
      const suppliers = await fetchActiveLocalSuppliers();
      setCreateSuppliers(suppliers);
      setCreateProductDraft((current) =>
        current.localSupplierCode || !suppliers[0]
          ? current
          : { ...current, localSupplierCode: suppliers[0].supplierCode },
      );
    } catch (error) {
      setSnackbarMessage(
        getErrorMessage(error, "createProduct.messages.suppliersLoadFailed"),
      );
    } finally {
      setCreateSuppliersLoading(false);
    }
  }, [getErrorMessage]);

  const openCreateProductModal = useCallback(() => {
    setCreateProductDraft({
      ...EMPTY_CREATE_PRODUCT_DRAFT,
      localSupplierCode: createSuppliers[0]?.supplierCode ?? "",
    });
    setCreateProductVisible(true);
    if (!createSuppliers.length) {
      void loadCreateSuppliers();
    }
  }, [createSuppliers, loadCreateSuppliers]);

  const updateCreateProductDraft = useCallback(
    (patch: Partial<CreateProductDraft>) => {
      setCreateProductDraft((current) => ({ ...current, ...patch }));
    },
    [],
  );

  const closeCreateProductModal = useCallback(() => {
    setCreateSupplierPickerVisible(false);
    setCreateBarcodeScannerVisible(false);
    setCreateProductVisible(false);
  }, []);

  const handleSelectCreateSupplier = useCallback(
    (supplier: LocalSupplierOption) => {
      updateCreateProductDraft({ localSupplierCode: supplier.supplierCode });
      setCreateSupplierPickerVisible(false);
    },
    [updateCreateProductDraft],
  );

  const handleGenerateCreateBarcode = useCallback(async () => {
    if (createProductBusy) return;
    const supplierCode = createProductDraft.localSupplierCode.trim();
    if (!supplierCode || supplierCode === "200") {
      setSnackbarMessage(t(supplierCode === "200"
        ? "createProduct.messages.supplierRestricted"
        : "createProduct.selectSupplier"));
      return;
    }
    setCreateBarcodeGenerating(true);
    try {
      // 服务端永久预留独立编号段，客户端不使用随机数拼接条码。
      const barcode = await generateLocalProductBarcode(supplierCode);
      updateCreateProductDraft({ barcode });
    } catch (error) {
      setSnackbarMessage(getErrorMessage(error, "createProduct.messages.barcodeGenerateFailed"));
    } finally {
      setCreateBarcodeGenerating(false);
    }
  }, [createProductBusy, createProductDraft.localSupplierCode, getErrorMessage, t, updateCreateProductDraft]);

  const handleCreateProductSubmit = useCallback(async () => {
    const validation = validateCreateProductForm(createProductDraft);
    if (!validation.ok) {
      setSnackbarMessage(t(`createProduct.messages.${validation.reason}`));
      return;
    }

    setCreateProductSaving(true);
    let createdProductCode = "";
    const mutation = beginHqSyncMutation(
      detail?.productCode,
      selectedStoreCode,
    );
    try {
      const result = await createProductWithPrices(validation.payload);
      const nextKeyword =
        result.productCode ||
        validation.payload.itemNumber ||
        validation.payload.barcode;
      createdProductCode = result.productCode;
      setCreateSupplierPickerVisible(false);
      setCreateProductVisible(false);
      setCreateProductDraft(EMPTY_CREATE_PRODUCT_DRAFT);
      setKeyword(nextKeyword);
      presentHqSyncOperation(
        mutation,
        result.hqSync,
        t("createProduct.messages.created"),
      );
    } catch (error) {
      hqSyncMutationCoordinatorRef.current?.fail(mutation);
      setSnackbarMessage(
        getErrorMessage(error, "createProduct.messages.createFailed"),
      );
      setCreateProductSaving(false);
      return;
    }

    if (createdProductCode && selectedStoreCode) {
      // 创建 B 成功后不能继续把 A 留在可编辑详情中；即使 B 的回读暂时失败，
      // HQ operation 仍独立保留，用户也不会误把后续操作提交给旧商品。
      loadedDetailStoreCodeRef.current = null;
      activeDetailProductCodeRef.current = null;
      setDetail(null);
      setInitialDetail(null);
      setSelectedLookupProductCode(undefined);
      setCodePage(1);
      setCodesHasMore(false);
      try {
        await loadDetail(createdProductCode);
      } catch (error) {
        setSnackbarMessage(
          getErrorMessage(
            error,
            "createProduct.messages.refreshFailedAfterCreate",
          ),
        );
      }
    }

    setCreateProductSaving(false);
  }, [
    beginHqSyncMutation,
    createProductDraft,
    detail?.productCode,
    getErrorMessage,
    loadDetail,
    presentHqSyncOperation,
    selectedStoreCode,
    t,
  ]);

  const persistStorePrice = useCallback(
    async (
      sourceDetail: ProductDetail,
      patch: Partial<NonNullable<ProductDetail["storePrice"]>>,
    ) => {
      if (!sourceDetail.storePrice) {
        return null;
      }
      if (
        !ensureCurrentDetailStoreScope(
          sourceDetail,
          sourceDetail.storePrice.storeCode,
        )
      ) {
        return null;
      }

      const nextStorePrice = {
        ...sourceDetail.storePrice,
        ...patch,
      };
      const scope = {
        productCode: sourceDetail.productCode,
        storeCode: selectedStoreCode,
      };

      let savedStorePrice: NonNullable<ProductDetail["storePrice"]>;
      const mutation = beginHqSyncMutation(
        sourceDetail.productCode,
        selectedStoreCode,
      );
      try {
        savedStorePrice = await updateStorePrice(sourceDetail.storePrice.uuid, {
          purchasePrice: nextStorePrice.purchasePrice ?? null,
          retailPrice: nextStorePrice.retailPrice ?? null,
          discountRate: normalizeDiscountRateValue(nextStorePrice.discountRate),
          isAutoPricing: nextStorePrice.isAutoPricing,
          isSpecialProduct: nextStorePrice.isSpecialProduct,
          isActive: nextStorePrice.isActive,
        });
      } catch (error) {
        hqSyncMutationCoordinatorRef.current?.fail(mutation);
        setSnackbarMessage(getErrorMessage(error, "messages.saveFailed"));
        playQueryFeedback("error");
        return null;
      }

      presentHqSyncOperation(
        mutation,
        savedStorePrice.hqSync,
        t("messages.saved"),
      );
      // 保存已提交后仍可能切换商品/分店；旧响应只能保留其 HQ 状态，不能回写详情。
      if (!detailRequestCoordinatorRef.current?.isScopeActive(scope)) {
        return null;
      }
      const nextDetail = replaceStorePriceDetail(sourceDetail, savedStorePrice);
      if (selectedStoreCode) {
        const refreshRequest =
          detailRequestCoordinatorRef.current!.begin(scope);
        try {
          const refreshed = await getProductFastDetail(
            sourceDetail.productCode,
            selectedStoreCode,
          );
          if (!detailRequestCoordinatorRef.current?.isCurrent(refreshRequest)) {
            return null;
          }
          loadedDetailStoreCodeRef.current = selectedStoreCode;
          setDetail(refreshed);
          setInitialDetail(cloneDetail(refreshed));
          void loadProductCodes(refreshed, 1, false);
          return refreshed;
        } catch (error) {
          console.warn(
            "[product-query] refresh after store price save failed",
            {
              message: isAxiosError(error) ? error.message : String(error),
            },
          );
        }
      }

      if (!detailRequestCoordinatorRef.current?.isScopeActive(scope)) {
        return null;
      }
      setDetail(nextDetail);
      setInitialDetail(cloneDetail(nextDetail));
      return nextDetail;
    },
    [
      beginHqSyncMutation,
      ensureCurrentDetailStoreScope,
      getErrorMessage,
      loadProductCodes,
      playQueryFeedback,
      presentHqSyncOperation,
      selectedStoreCode,
      t,
    ],
  );

  const finishAutoPricingDialog = useCallback(
    (result: AutoPricingDialogResolution) => {
      setAutoPricingDialog(null);
      setAutoPricingDialogSaving(false);
      const resolve = autoPricingDialogResolverRef.current;
      autoPricingDialogResolverRef.current = null;
      resolve?.(result);
    },
    [],
  );

  const openNumericInputModal = useCallback(
    (
      config: NumericInputModalState & {
        onConfirmValue: (value: string) => void;
      },
    ) => {
      numericInputConfirmRef.current = config.onConfirmValue;
      setNumericInputModal({
        key: config.key,
        title: config.title,
        value: config.value,
        allowDecimal: config.allowDecimal,
        confirmLabel: config.confirmLabel,
      });
    },
    [],
  );

  const dismissNumericInputModal = useCallback(() => {
    numericInputConfirmRef.current = null;
    setNumericInputModal(null);
  }, []);

  const handleConfirmNumericInputModal = useCallback(() => {
    if (!numericInputModal) {
      return;
    }

    numericInputConfirmRef.current?.(numericInputModal.value);
    dismissNumericInputModal();
  }, [dismissNumericInputModal, numericInputModal]);

  const openAutoPricingDialog = useCallback(
    (state: AutoPricingDialogState) =>
      new Promise<AutoPricingDialogResolution>((resolve) => {
        autoPricingDialogResolverRef.current = resolve;
        setAutoPricingDialog(state);
      }),
    [],
  );

  const sendProductLabel = useCallback(
    async (
      targetDetail: ProductDetail,
      options?: {
        barcode?: string | null;
        retailPrice?: number | null;
        discountRate?: number | null;
        action?: PrintAction;
        printType?: string | null;
      },
    ) => {
      const savedPrinter = await getSavedPrinter();
      if (!savedPrinter?.address) {
        setSnackbarMessage(t("messages.printerRequired"));
        playQueryFeedback("error");
        return false;
      }

      if (printerAutoReconnectPaused) {
        setSnackbarMessage(t("messages.printerPaused"));
        playQueryFeedback("error");
        return false;
      }

      const action = options?.action ?? "product";
      setPrintingAction(action);
      const printStartedAt = Date.now();
      try {
        console.log("[product-query] sendProductLabel", {
          action,
          printType: options?.printType,
        });
        await printProductLabel(
          targetDetail,
          {
            barcode: options?.barcode,
            retailPrice: options?.retailPrice,
            discountRate: options?.discountRate,
          },
          options?.printType,
        );
        const qty = printQuantity;
        for (let i = 1; i < qty; i++) {
          await printProductLabel(
            targetDetail,
            {
              barcode: options?.barcode,
              retailPrice: options?.retailPrice,
              discountRate: options?.discountRate,
            },
            options?.printType,
          );
        }
        console.log("[product-query] sendProductLabel success", {
          action,
          quantity: qty,
          elapsedMs: Date.now() - printStartedAt,
        });
        setSnackbarMessage(t("messages.printSuccess"));
        if (quantitySingleUse && qty > 1) {
          setPrintQuantity(1);
        }
        return true;
      } catch (error) {
        console.error("[product-query] sendProductLabel failed", {
          action,
          printType: options?.printType,
          error: error instanceof Error ? error.message : String(error),
          stack: error instanceof Error ? error.stack : undefined,
        });
        setSnackbarMessage(getErrorMessage(error, "messages.printFailed"));
        playQueryFeedback("error");
        return false;
      } finally {
        setPrintingAction(null);
      }
    },
    [
      getErrorMessage,
      playQueryFeedback,
      printQuantity,
      printerAutoReconnectPaused,
      quantitySingleUse,
      t,
    ],
  );

  const smartAutoPrint = useCallback(
    async (
      scanKeyword: string,
      targetDetail: ProductDetail,
      printTarget?: ScanLabelPrintTarget | null,
    ) => {
      const kw = scanKeyword.trim();

      if (printTarget && printTarget.kind !== "clearance") {
        return sendProductLabel(targetDetail, {
          barcode: printTarget.barcode,
          retailPrice: printTarget.retailPrice,
          discountRate: printTarget.discountRate ?? 0,
          action: printTarget.kind === "product"
            ? "product"
            : `${printTarget.kind}:${printTarget.codeId}` as PrintAction,
          // 主条码（product）与套码、多码一样遵循「小标签」开关，否则扫主条码总是打普通标签。
          printType: smallLabel ? "small" : null,
        });
      }

      const setMatch = printTarget ? undefined : targetDetail.setCodes.find(
        (item) => item.setBarcode?.trim() === kw,
      );
      if (setMatch?.setBarcode?.trim()) {
        return sendProductLabel(targetDetail, {
          barcode: setMatch.setBarcode.trim(),
          retailPrice: setMatch.setRetailPrice,
          action: `set:${setMatch.setCodeId}`,
          printType: smallLabel ? "small" : null,
        });
      }

      const multiMatch = printTarget ? undefined : targetDetail.multiCodes.find(
        (item) => item.barcode?.trim() === kw,
      );
      if (multiMatch?.barcode?.trim()) {
        return sendProductLabel(targetDetail, {
          barcode: multiMatch.barcode.trim(),
          retailPrice:
            multiMatch.retailPrice ??
            targetDetail.storePrice?.retailPrice ??
            null,
          action: `multi:${getMultiCodeItemId(multiMatch)}`,
          printType: smallLabel ? "small" : null,
        });
      }

      if (targetDetail.clearancePrice?.clearanceBarcode?.trim() === kw) {
        try {
          setPrintingAction("clearance");
          for (let i = 0; i < printQuantity; i++) {
            await printClearanceLabel(targetDetail);
          }
          setSnackbarMessage(t("messages.printSuccess"));
          if (quantitySingleUse && printQuantity > 1) {
            setPrintQuantity(1);
          }
          return true;
        } catch (error) {
          setSnackbarMessage(getErrorMessage(error, "messages.printFailed"));
          return false;
        } finally {
          setPrintingAction(null);
        }
      }

      // 离线或无 printTarget 时按主条码兜底打印，同样要带上小标签开关。
      return sendProductLabel(targetDetail, { printType: smallLabel ? "small" : null });
    },
    [
      getErrorMessage,
      printQuantity,
      quantitySingleUse,
      sendProductLabel,
      smallLabel,
      t,
    ],
  );

  const maybeHandleAutoPricing = useCallback(
    async (
      targetDetail: ProductDetail,
      options?: {
        forceAutoPricing?: boolean;
        scanSource?: ScanSource | null;
      },
    ): Promise<LookupFlowResult> => {
      const storePrice = targetDetail.storePrice;
      const scanSource = options?.scanSource ?? null;
      if (!storePrice || !selectedStoreCode) {
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }

      const shouldEvaluate =
        options?.forceAutoPricing === true || storePrice.isAutoPricing;
      if (!shouldEvaluate) {
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }

      try {
        const evaluation = await evaluateAutoPricing({
          productCode: targetDetail.productCode,
          storeCode: storePrice.storeCode ?? selectedStoreCode,
          forceAutoPricing: options?.forceAutoPricing === true,
        });

        if (
          evaluation.shouldUpdate &&
          evaluation.recalculatedRetailPrice != null
        ) {
          playQueryFeedback("price_update_required");
          const dialogResult = await openAutoPricingDialog({
            detail: targetDetail,
            evaluation,
            scanSource,
          });

          return {
            keepCameraOpen: dialogResult.keepCameraOpen,
            labelPrinted: dialogResult.labelPrinted,
            autoPricingStatus:
              dialogResult.status === "confirmed"
                ? "prompt_confirmed"
                : dialogResult.status === "failed"
                  ? "failed"
                  : "prompt_cancelled",
          };
        }

        if (options?.forceAutoPricing === true) {
          const savedDetail = await persistStorePrice(targetDetail, {
            isAutoPricing: true,
          });
          return savedDetail
            ? {
                keepCameraOpen: false,
                labelPrinted: false,
                autoPricingStatus: "saved_without_prompt",
              }
            : {
                keepCameraOpen: false,
                labelPrinted: false,
                autoPricingStatus: "failed",
              };
        }

        return DEFAULT_LOOKUP_FLOW_RESULT;
      } catch (error) {
        setSnackbarMessage(
          getErrorMessage(error, "messages.autoPricingEvaluateFailed"),
        );
        playQueryFeedback("error");
        return {
          keepCameraOpen: false,
          labelPrinted: false,
          autoPricingStatus: "failed",
        };
      }
    },
    [
      getErrorMessage,
      openAutoPricingDialog,
      persistStorePrice,
      playQueryFeedback,
      selectedStoreCode,
    ],
  );

  const processLoadedDetail = useCallback(
    async (
      targetDetail: ProductDetail,
      options: DetailPostLoadOptions,
    ): Promise<LookupFlowResult> => {
      if (
        !ensureCurrentDetailStoreScope(
          targetDetail,
          targetDetail.storePrice?.storeCode,
        )
      ) {
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }
      if (offlineModeRef.current) {
        // 离线态不做自动价评估，只反馈命中并按需自动打印。
        playQueryFeedback("found");
        if (options.autoPrintEnabled) {
          const labelPrinted = await smartAutoPrint(
            options.scanKeyword ?? "",
            targetDetail,
            options.printTarget,
          );
          return { ...DEFAULT_LOOKUP_FLOW_RESULT, labelPrinted };
        }
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }
      if (targetDetail.localSupplierCode?.trim() !== "200") {
        const autoPricingResult = await maybeHandleAutoPricing(targetDetail, {
          scanSource: options.scanSource,
        });
        if (autoPricingResult.autoPricingStatus === "no_action") {
          playQueryFeedback("found");
          if (options.autoPrintEnabled && !autoPricingResult.labelPrinted) {
            const labelPrinted = await smartAutoPrint(
              options.scanKeyword ?? "",
              targetDetail,
              options.printTarget,
            );
            return { ...autoPricingResult, labelPrinted };
          }
        }
        return autoPricingResult;
      }

      // 供应商 200 跳过自动定价评估，直接使用刚从服务器取得的当前门店价。
      const currentStorePrice = targetDetail.storePrice;
      playQueryFeedback("found");
      if (!options.autoPrintEnabled) {
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }
      if (
        options.printTarget?.kind !== "clearance" && (
          !currentStorePrice?.uuid ||
          currentStorePrice.retailPrice == null ||
          !Number.isFinite(currentStorePrice.retailPrice) ||
          currentStorePrice.retailPrice < 0
        )
      ) {
        // 缺少有效门店价时，不能退回打印商品主价或旧标签。
        setSnackbarMessage(t("warehousePriceSync.currentPriceUnavailable"));
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }
      const scannedCode = options.scanKeyword?.trim();
      // 条码分页未加载到本次扫码项时，不回退打印主商品的条码和价格。
      const knownCodes = [
        targetDetail.barcode,
        targetDetail.productCode,
        targetDetail.itemNumber,
        currentStorePrice?.storeProductCode,
        targetDetail.clearancePrice?.clearanceBarcode,
        options.printTarget?.barcode,
        ...targetDetail.setCodes.map((item) => item.setBarcode),
        ...targetDetail.multiCodes.map((item) => item.barcode),
      ];
      if (!scannedCode || !knownCodes.some((code) => code?.trim() === scannedCode)) {
        setSnackbarMessage(t("messages.codesLoadFailed"));
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }
      try {
        const labelPrinted = await smartAutoPrint(scannedCode, targetDetail, options.printTarget);
        return { ...DEFAULT_LOOKUP_FLOW_RESULT, labelPrinted };
      } catch (error) {
        setSnackbarMessage(getErrorMessage(error, "messages.printFailed"));
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }
    },
    [
      ensureCurrentDetailStoreScope,
      getErrorMessage,
      maybeHandleAutoPricing,
      playQueryFeedback,
      smartAutoPrint,
      t,
    ],
  );
  processLoadedDetailRef.current = processLoadedDetail;

  /** 在线/离线两种数据源共用的候选处理：0 条提示、1 条直接加载详情、多条弹候选表。 */
  const applyLookupItems = useCallback(
    async (
      items: ProductLookupItem[],
      nextKeyword: string,
      trigger: LookupTrigger,
      scanSource: ScanSource | undefined,
      scanResult?: ScanLabelResult,
    ): Promise<LookupFlowResult> => {
        if (!selectedStoreCode) return DEFAULT_LOOKUP_FLOW_RESULT;
        console.log("[product-query] lookup success", {
          count: items.length,
          trigger,
          offline: offlineModeRef.current,
        });
        setLookupItems(items);
        if (!items.length) {
          setDetail(null);
          setInitialDetail(null);
          activateHqSyncScope();
          setSelectedLookupProductCode(undefined);
          setLookupSelectionSource(null);
          lookupSelectionOpenRef.current = false;
          setLookupVisible(false);
          setQueryFeedback({ type: "empty", query: nextKeyword });
          setSnackbarMessage(t("messages.notFound"));
          playQueryFeedback("not_found");
          return DEFAULT_LOOKUP_FLOW_RESULT;
        }

        if (items.length === 1) {
          lookupSelectionOpenRef.current = false;
          setLookupVisible(false);
          setLookupSelectionSource(null);
          const detailStartedAt = Date.now();
          const fastResult = scanResult?.detail ? scanResult : undefined;
          let nextDetail: ProductDetail | null;
          let presentFastDetail: (() => void) | undefined;
          let isFastDetailCurrent: (() => boolean) | undefined;
          if (fastResult?.detail) {
            if (fastResult.detail.productCode !== items[0].productCode) {
              throw new Error("INVALID_SCAN_LABEL_RESPONSE");
            }
            // 单次在线响应已带当前门店详情，先完成打印；码表分页只负责后续编辑展示。
            nextDetail = fastResult.detail;
            activateHqSyncScope(nextDetail.productCode, selectedStoreCode);
            invalidateActivePromotions();
            activeDetailProductCodeRef.current = nextDetail.productCode;
            loadedDetailStoreCodeRef.current = selectedStoreCode;
            const fastDetailRequest = detailRequestCoordinatorRef.current!.begin({
              productCode: nextDetail.productCode,
              storeCode: selectedStoreCode,
            });
            const scannedDetail = nextDetail;
            const isCurrent = () => selectedStoreCodeRef.current === selectedStoreCode &&
              Boolean(detailRequestCoordinatorRef.current?.isCurrent(fastDetailRequest));
            isFastDetailCurrent = isCurrent;
            presentFastDetail = () => {
              // 打印等待期间切店或换商品后，不把旧响应重新写回编辑区。
              if (!isCurrent()) return;
              loadActivePromotions(scannedDetail.productCode, selectedStoreCode);
              setDetail(scannedDetail);
              setInitialDetail(cloneDetail(scannedDetail));
              setSelectedLookupProductCode(scannedDetail.productCode);
              setLastHitLabel(
                `${scannedDetail.itemNumber || scannedDetail.productCode} / ${scannedDetail.barcode || "--"}`,
              );
              setQueryFeedback({ type: "idle" });
              setCodePage(1);
              setCodesHasMore(false);
            };
          } else {
            nextDetail = await loadDetail(items[0].productCode);
          }
          if (trigger === "scan") {
            console.log("[product-query] scan detail ready", { elapsedMs: Date.now() - detailStartedAt });
          }
          if (nextDetail) {
            const printTarget = fastResult
              ? getVerifiedScanPrintTarget(fastResult, nextKeyword, selectedStoreCode)
              : null;
            if (fastResult && continuousPrintEnabled && !printTarget) {
              setSnackbarMessage(t(nextDetail.storePrice
                ? "messages.codesLoadFailed"
                : "warehousePriceSync.currentPriceUnavailable"));
            }
            const autoPrintEnabled = trigger === "scan" && continuousPrintEnabled && (!fastResult || !!printTarget);
            // 无定价确认的在线扫码先完成打印，再提交详情，避免详情渲染阻塞原生 Promise 回调。
            // 需要自动定价的商品仍先展示详情；打印失败也在 finally 展示本次商品。
            const presentAfterPrint = Boolean(
              presentFastDetail && autoPrintEnabled && printTarget &&
              (nextDetail.localSupplierCode?.trim() === "200" || !nextDetail.storePrice?.isAutoPricing),
            );
            if (presentAfterPrint) {
              setPendingScanPresentation({ keyword: nextKeyword, productName: nextDetail.productName || nextDetail.productCode });
            } else {
              presentFastDetail?.();
            }
            let flow: LookupFlowResult;
            try {
              flow = await processLoadedDetail(nextDetail, {
                scanSource,
                scanKeyword: nextKeyword,
                autoPrintEnabled,
                printTarget,
              });
            } finally {
              if (presentAfterPrint) {
                presentFastDetail?.();
                setPendingScanPresentation(null);
              }
            }
            // 过期打印结果不能启动旧店码表请求，否则 begin 会使新店的详情请求失效。
            if (fastResult && isFastDetailCurrent?.() && flow.autoPricingStatus === "no_action") {
              // 套码和多码的编辑页仍分页加载，但不占用扫码到打印的关键路径。
              void loadProductCodes(nextDetail, 1, false, selectedStoreCode);
            }
            return flow;
          }
          return DEFAULT_LOOKUP_FLOW_RESULT;
        }

        setSelectedLookupProductCode(items[0].productCode);
        setAutoPrintOnLookupConfirm(
          trigger === "scan" && continuousPrintEnabled,
        );
        setLookupSelectionSource(scanSource ?? null);
        lookupSelectionOpenRef.current = true;
        setLookupVisible(true);
        playQueryFeedback("multiple");
        return { ...DEFAULT_LOOKUP_FLOW_RESULT, foregroundPending: true };
    },
    [
      activateHqSyncScope,
      continuousPrintEnabled,
      invalidateActivePromotions,
      loadActivePromotions,
      loadDetail,
      loadProductCodes,
      playQueryFeedback,
      processLoadedDetail,
      selectedStoreCode,
      t,
    ],
  );

  const handleLookup = useCallback(
    async (
      sourceKeyword?: string,
      trigger: LookupTrigger = "manual",
      scanSource?: ScanSource,
    ): Promise<LookupFlowResult> => {
      if (isProductQueryBusy()) {
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }

      const nextKeyword = (sourceKeyword ?? keyword).trim();
      if (!nextKeyword) {
        setSnackbarMessage(t("messages.keywordRequired"));
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }

      if (!selectedStoreCode) {
        setSnackbarMessage(t("messages.storeUnavailable"));
        return DEFAULT_LOOKUP_FLOW_RESULT;
      }

      lookupRequestInFlightRef.current = true;
      invalidateActivePromotions();
      const lookupStartedAt = Date.now();

      console.log("[product-query] lookup start", {
        selectedStoreCode,
        trigger,
        offline: offlineModeRef.current,
      });
      setLoading(true);
      setAutoPrintOnLookupConfirm(false);
      setQueryFeedback({ type: "idle" });
      try {
        if (offlineModeRef.current) {
          // 离线态：直接查本地快照，命中后走与在线相同的候选/详情流程。
          void probeReconnectNow();
          // 先确认本店确实有快照：没有快照时的空结果不是「查无此货」，
          // 否则员工会听到未找到提示音、以为商品不存在。
          const activeMeta = await useOfflineCatalogStore
            .getState()
            .loadActiveMeta(selectedStoreCode);
          if (!activeMeta) {
            throw new OfflineCatalogUnavailableError();
          }
          if (trigger === "scan") {
            console.log("[product-query] scan snapshot check", { elapsedMs: Date.now() - lookupStartedAt });
          }
          const offlineItems = await useOfflineCatalogStore
            .getState()
            .lookup(selectedStoreCode, nextKeyword);
          if (trigger === "scan") {
            console.log("[product-query] scan candidates ready", { elapsedMs: Date.now() - lookupStartedAt, offline: true });
          }
          dispatchConnectivity({ type: "offline_lookup", keyword: nextKeyword });
          return await applyLookupItems(offlineItems, nextKeyword, trigger, scanSource);
        }
        const lookupRequest = { keyword: nextKeyword, storeCode: selectedStoreCode };
        let scanResult: ScanLabelResult | undefined;
        let items: ProductLookupItem[];
        if (
          trigger === "scan" &&
          !isIosReviewSessionActive() &&
          Date.now() >= scanLabelUnsupportedUntilRef.current
        ) {
          try {
            scanResult = await scanProductLabel(lookupRequest);
            items = scanResult.candidates;
          } catch (error) {
            // 客户端可先于后端升级；旧服务没有新路由时仍保留原扫码查询。
            if (!isAxiosError(error) || ![404, 405].includes(error.response?.status ?? 0)) {
              throw error;
            }
            scanLabelUnsupportedUntilRef.current = Date.now() + 60_000;
            console.log("[product-query] scan-label unavailable, using existing lookup");
            items = await lookupProducts(lookupRequest);
          }
        } else {
          items = await lookupProducts(lookupRequest);
        }
        if (trigger === "scan") {
          console.log("[product-query] scan candidates ready", { elapsedMs: Date.now() - lookupStartedAt, offline: false });
        }
        dispatchConnectivity({ type: "request_succeeded" });
        return await applyLookupItems(items, nextKeyword, trigger, scanSource, scanResult);
      } catch (error) {
        if (
          !offlineModeRef.current &&
          offlineEligibleRef.current &&
          isNetworkUnavailableError(error)
        ) {
          // 服务器不可达：进入离线模式；本店有快照时用同一关键字立即改走离线查询。
          enterOfflineMode(nextKeyword);
          const activeMeta = await useOfflineCatalogStore
            .getState()
            .loadActiveMeta(selectedStoreCode);
          if (activeMeta) {
            try {
              const offlineItems = await useOfflineCatalogStore
                .getState()
                .lookup(selectedStoreCode, nextKeyword);
              return await applyLookupItems(offlineItems, nextKeyword, trigger, scanSource);
            } catch (offlineError) {
              console.warn("[product-query] offline lookup failed", {
                message:
                  offlineError instanceof Error
                    ? offlineError.message
                    : String(offlineError),
              });
            }
          }
        }
        let message: string;
        if (isAxiosError(error)) {
          if (!error.response) {
            message =
              error.code === "ECONNABORTED"
                ? t("messages.lookupTimeout")
                : t("messages.lookupNetworkError");
          } else {
            const status = error.response.status;
            message =
              status >= 500
                ? t("messages.lookupServerError", { status })
                : t("messages.lookupFailed");
          }
        } else {
          message = getErrorMessage(error, "messages.lookupFailed");
        }
        if (error instanceof OfflineCatalogUnavailableError) {
          // 只有「本店确实没有快照」才改写成这条；读快照失败、快照损坏等
          // 必须保留真实原因，否则日志和提示都会把人引向错误方向。
          message = t("offline.catalogMissing");
        }
        console.error("[product-query] lookup failed", {
          selectedStoreCode,
          trigger,
          message,
        });
        setDetail(null);
        setInitialDetail(null);
        activateHqSyncScope();
        invalidateActivePromotions();
        lookupSelectionOpenRef.current = false;
        setLookupVisible(false);
        setQueryFeedback({ type: "error", query: nextKeyword, message });
        setSnackbarMessage(message);
        playQueryFeedback("error");
        return DEFAULT_LOOKUP_FLOW_RESULT;
      } finally {
        if (trigger === "scan") {
          console.log("[product-query] scan flow complete", { elapsedMs: Date.now() - lookupStartedAt, offline: offlineModeRef.current });
        }
        lookupRequestInFlightRef.current = false;
        setLoading(false);
      }
    },
    [
      activateHqSyncScope,
      applyLookupItems,
      enterOfflineMode,
      getErrorMessage,
      invalidateActivePromotions,
      isProductQueryBusy,
      keyword,
      playQueryFeedback,
      probeReconnectNow,
      selectedStoreCode,
      t,
    ],
  );

  const wasOfflineRef = useRef(false);
  const lastAutoRelookupAtRef = useRef<number | null>(null);
  useEffect(() => {
    // 检测到在线：提示、触发全局补传，并把离线期间的最后一次查询自动重跑为实时结果。
    const wasOffline = wasOfflineRef.current;
    wasOfflineRef.current = offlineMode;
    if (!wasOffline || offlineMode) {
      return;
    }
    setSnackbarMessage(t("offline.backOnline"));
    void triggerRecovery();
    const pendingKeyword = connectivity.pendingKeyword;
    dispatchConnectivity({ type: "reset" });
    // 自动重跑设最小间隔：探测与业务请求若看到不同的后端，会「判定恢复→重跑失败
    // →再判定恢复」地空转，这里兜底掐断；用户手动查询不受影响。
    const recoveredAtMs = Date.now();
    const canAutoRelookup = shouldAutoRelookupAfterRecovery({
      lastAutoRelookupAtMs: lastAutoRelookupAtRef.current,
      nowMs: recoveredAtMs,
    });
    if (pendingKeyword && selectedStoreCode && canAutoRelookup) {
      lastAutoRelookupAtRef.current = recoveredAtMs;
      setKeyword(pendingKeyword);
      void handleLookup(pendingKeyword, "refresh");
    }
    if (selectedStoreCode) {
      void useOfflineCatalogStore.getState().loadActiveMeta(selectedStoreCode);
    }
  }, [connectivity.pendingKeyword, handleLookup, offlineMode, selectedStoreCode, t, triggerRecovery]);

  useEffect(() => {
    const productCodeParam = firstParam(queryParams.productCode);
    const keywordParam = firstParam(queryParams.keyword);
    const storeCodeParam = firstParam(queryParams.storeCode);
    const sourceParam = firstParam(queryParams.source);
    const nextKeyword = keywordParam || productCodeParam;

    if (!nextKeyword && !storeCodeParam) {
      return;
    }

    const requestKey = `${productCodeParam ?? ""}|${keywordParam ?? ""}|${storeCodeParam ?? ""}|${sourceParam ?? ""}`;
    if (handledExternalQueryRef.current === requestKey || storesLoading) {
      return;
    }

    let cancelled = false;

    async function applyExternalQuery() {
      if (isProductQueryBusy()) {
        return;
      }

      const storeResolution = resolveExternalQueryStore({
        targetStoreCode: storeCodeParam,
        selectedStoreCode,
        stores,
        storesLoading: storesLoading || isHydratingSelection,
      });

      if (storeResolution.type === "wait") {
        return;
      }

      if (storeResolution.type === "select-store") {
        await handleSelectStore(storeResolution.store);
        return;
      }

      if (storeResolution.type === "store-not-found") {
        handledExternalQueryRef.current = requestKey;
        setSnackbarMessage(
          t("messages.targetStoreUnavailable", {
            storeCode: storeResolution.storeCode,
          }),
        );
        playQueryFeedback("error");
        return;
      }

      handledExternalQueryRef.current = requestKey;
      if (!nextKeyword || cancelled) {
        return;
      }

      setKeyword(nextKeyword);
      try {
        if (productCodeParam) {
          lookupRequestInFlightRef.current = true;
          setLoading(true);
          const nextDetail = await loadDetail(productCodeParam);
          if (nextDetail) {
            await processLoadedDetail(nextDetail, {
            });
          }
          return;
        }
        await handleLookup(nextKeyword, "deep-link");
      } catch (error) {
        setSnackbarMessage(getErrorMessage(error, "messages.lookupFailed"));
        playQueryFeedback("error");
      } finally {
        if (productCodeParam) {
          lookupRequestInFlightRef.current = false;
          setLoading(false);
        }
      }
    }

    void applyExternalQuery();

    return () => {
      cancelled = true;
    };
  }, [
    handleLookup,
    handleSelectStore,
    isHydratingSelection,
    isProductQueryBusy,
    loadDetail,
    playQueryFeedback,
    processLoadedDetail,
    queryParams.keyword,
    queryParams.productCode,
    queryParams.source,
    queryParams.storeCode,
    selectedStoreCode,
    stores,
    storesLoading,
    t,
  ]);

  const scannerInputBlocked = isProductQueryBusy();
  const isProductQueryBusyRef = useRef(isProductQueryBusy);
  isProductQueryBusyRef.current = isProductQueryBusy;
  const handleOpenProductInsights = useCallback(() => {
    if (!detail?.productCode || !selectedStoreCode || isProductQueryBusy()) {
      return;
    }

    // push 保留当前详情和未保存编辑；返回时继续停留在同一商品与门店上下文。
    router.push({
      pathname: "/(shell)/product-insights",
      params: {
        productCode: detail.productCode,
        storeCode: selectedStoreCode,
      },
    } as unknown as Parameters<typeof router.push>[0]);
  }, [detail?.productCode, isProductQueryBusy, router, selectedStoreCode]);
  const handleOpenProductTypeDialog = useCallback(() => {
    setProductTypeDialogVisible(true);
  }, []);
  const handleOfflineInsightsUnavailable = useCallback(() => {
    setSnackbarMessage(t("offline.insightsUnavailable"));
  }, [t]);
  const handleOpenPromoPoster = useCallback(
    (kind: PromoPosterKind) => {
      if (!detail?.productCode || !selectedStoreCode || isProductQueryBusy()) {
        return;
      }

      // 海报编辑页以后端已保存的价格为准；push 保留扫码页当前商品，加入待打印后返回继续扫码。
      router.push({
        pathname: "/(shell)/promo-poster-editor",
        params: {
          productCode: detail.productCode,
          storeCode: selectedStoreCode,
          kind,
        },
      } as unknown as Parameters<typeof router.push>[0]);
    },
    [detail?.productCode, isProductQueryBusy, router, selectedStoreCode],
  );
  const handleOpenPromoPosterQueue = useCallback(() => {
    if (isProductQueryBusy()) {
      return;
    }
    router.push("/(shell)/promo-poster-queue" as unknown as Parameters<typeof router.push>[0]);
  }, [isProductQueryBusy, router]);
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
  const cameraScanDisabled =
    !isFocused || scannerInputBlocked || !cameraSession.visible;
  const cameraScan = useCameraScan({
    disabled: cameraScanDisabled,
    ignoreWhileProcessing: cameraScanMode === "continuous",
    resetKey: [
      isFocused ? "focused" : "blurred",
      cameraSession.generation,
      cameraScanMode,
      selectedStoreCode ?? "",
      lookupVisible ? "lookup" : "idle",
      autoPricingDialog ? "pricing" : "idle",
    ].join(":"),
    suppressRepeatsUntilChange: cameraScanMode === "continuous",
    onBarcode: async (barcode) => {
      const session = cameraSheetSessionRef.current;
      if (
        !isFocusedRef.current ||
        isProductQueryBusy() ||
        !isCameraSheetSessionActive(session, session.generation)
      ) {
        return;
      }
      const generation = session.generation;
      setKeyword(barcode);
      // NativeModal 会遮住后续候选、自动价和详情结果，命中时先卸载相机。
      cameraForegroundGenerationRef.current = generation;
      const captured = updateCameraSheetSession(
        { type: "capture", generation },
        cameraScanModeRef.current,
      );
      if (captured === session) {
        return;
      }
      // 相机识别到条码先给短提示；查询完成后再播放命中/无结果等结果音。
      playBarcodeCapturedSound();
      const flowResult = await handleLookup(barcode, "scan", "camera");
      if (
        !flowResult.foregroundPending &&
        cameraSheetSessionRef.current.generation === generation
      ) {
        setLastCameraBarcode(barcode);
        updateCameraSheetSession(
          {
            type: "foreground-complete",
            focused: isFocusedRef.current,
            generation,
          },
          cameraScanModeRef.current,
        );
      }
    },
  });
  const lastHidScanRef = useRef<{ barcode: string; time: number } | null>(null);
  const handleHidScan = useCallback(
    async (barcode: string) => {
      if (!isFocused || isProductQueryBusy()) {
        return;
      }
      const now = Date.now();
      const lastScan = lastHidScanRef.current;
      // 可见框失焦时，同一扫码可能交给两个输入框；避免重复发起查询。
      if (lastScan?.barcode === barcode && now - lastScan.time < 100) {
        return;
      }
      lastHidScanRef.current = { barcode, time: now };
      setKeyword(barcode);
      await handleLookup(barcode, "scan", "hid");
    },
    [handleLookup, isFocused, isProductQueryBusy],
  );
  const hidScanner = useHidBarcodeScanner({
    enabled: isFocused && !scannerInputBlocked,
    onScan: handleHidScan,
  });
  useEffect(() => {
    if (!isFocused) {
      cameraForegroundGenerationRef.current = null;
      updateCameraSheetSession({ type: "blur" }, cameraScanModeRef.current);
    }
  }, [isFocused, updateCameraSheetSession]);

  const pauseHiddenScannerFocus = useCallback(() => {
    searchInputFocusedRef.current = true;
    if (resumeHiddenScannerFocusTimerRef.current) {
      clearTimeout(resumeHiddenScannerFocusTimerRef.current);
      resumeHiddenScannerFocusTimerRef.current = null;
    }
    hidScanner.pauseHiddenInputFocus();
  }, [hidScanner.pauseHiddenInputFocus]);
  const resumeHiddenScannerFocusLater = useCallback(() => {
    searchInputFocusedRef.current = false;
    if (resumeHiddenScannerFocusTimerRef.current) {
      clearTimeout(resumeHiddenScannerFocusTimerRef.current);
    }
    // 延迟恢复隐藏扫码输入框，避免搜索框失焦时两个输入框立刻互相抢焦点。
    resumeHiddenScannerFocusTimerRef.current = setTimeout(() => {
      resumeHiddenScannerFocusTimerRef.current = null;
      // 若搜索框已再次聚焦，保留用户当前输入焦点，不恢复隐藏扫码输入框。
      if (!searchInputFocusedRef.current) {
        hidScanner.resumeHiddenInputFocus();
      }
    }, 250);
  }, [hidScanner.resumeHiddenInputFocus]);

  useEffect(
    () => () => {
      if (resumeHiddenScannerFocusTimerRef.current) {
        clearTimeout(resumeHiddenScannerFocusTimerRef.current);
        resumeHiddenScannerFocusTimerRef.current = null;
      }
    },
    [],
  );

  const shouldRestoreCameraScan = useCallback(
    (source?: ScanSource | null) =>
      source === "camera" && cameraScanMode === "continuous",
    [cameraScanMode],
  );
  const restoreScanAbility = useCallback(
    (source?: ScanSource | null) => {
      const restoreCamera = shouldRestoreCameraScan(source);
      const generation = cameraForegroundGenerationRef.current;
      setTimeout(
        () => {
          hidScanner.focusHiddenInput?.();
          if (restoreCamera && generation !== null) {
            updateCameraSheetSession(
              {
                type: "foreground-complete",
                focused: isFocusedRef.current,
                generation,
              },
              cameraScanModeRef.current,
            );
          }
        },
        restoreCamera ? 160 : 60,
      );
    },
    [
      hidScanner.focusHiddenInput,
      shouldRestoreCameraScan,
      updateCameraSheetSession,
    ],
  );
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

  useFocusEffect(
    useCallback(() => {
      if (!isProductQueryBusy() && hidScanner.focusHiddenInput) {
        hidScanner.focusHiddenInput();
      }
    }, [hidScanner.focusHiddenInput, isProductQueryBusy]),
  );

  const dirtyCount = useMemo(
    () => (isStorePriceDirty(detail, initialDetail) ? 1 : 0),
    [detail, initialDetail],
  );

  const handleRefresh = useCallback(async () => {
    if (isProductQueryBusy()) {
      return;
    }

    if (offlineModeRef.current) {
      // 离线态刷新先即时探测一次，恢复在线时由连通性状态机自动重跑查询。
      void probeReconnectNow();
      void triggerRecovery();
    }

    if (!detail?.productCode) {
      if (keyword.trim()) {
        setRefreshing(true);
        try {
          await handleLookup(keyword, "refresh");
        } finally {
          setRefreshing(false);
        }
      }
      return;
    }

    setRefreshing(true);
    lookupRequestInFlightRef.current = true;
    try {
      const nextDetail = await loadDetail(detail.productCode);
      if (nextDetail) {
        await processLoadedDetail(nextDetail, {});
      }
    } catch (error) {
      setSnackbarMessage(getErrorMessage(error, "messages.refreshFailed"));
    } finally {
      lookupRequestInFlightRef.current = false;
      setRefreshing(false);
    }
  }, [
    detail?.productCode,
    handleLookup,
    isProductQueryBusy,
    keyword,
    loadDetail,
    probeReconnectNow,
    processLoadedDetail,
    triggerRecovery,
  ]);

  const handleClear = useCallback(() => {
    const wasLookupSelectionOpen = lookupSelectionOpenRef.current;
    lookupSelectionOpenRef.current = false;
    if (!wasLookupSelectionOpen && isProductQueryBusy()) {
      return;
    }
    setKeyword("");
    setLookupItems([]);
    setSelectedLookupProductCode(undefined);
    loadedDetailStoreCodeRef.current = null;
    activeDetailProductCodeRef.current = null;
    setDetail(null);
    setInitialDetail(null);
    activateHqSyncScope();
    invalidateActivePromotions();
    setLookupVisible(false);
    setLookupSelectionSource(null);
    setAutoPrintOnLookupConfirm(false);
    setQueryFeedback({ type: "idle" });
  }, [activateHqSyncScope, invalidateActivePromotions, isProductQueryBusy]);

  const handleConfirmLookup = useCallback(async () => {
    if (
      !selectedLookupProductCode ||
      !lookupSelectionOpenRef.current ||
      lookupRequestInFlightRef.current ||
      storeSelectionInFlightRef.current ||
      autoPricingDialogSaving
    ) {
      return;
    }

    const selectionSource = lookupSelectionSource;
    let flowResult = DEFAULT_LOOKUP_FLOW_RESULT;
    lookupSelectionOpenRef.current = false;
    lookupRequestInFlightRef.current = true;
    setLoading(true);
    setLookupVisible(false);
    try {
      const nextDetail = await loadDetail(selectedLookupProductCode);
      if (nextDetail) {
        flowResult = await processLoadedDetail(nextDetail, {
          scanSource: selectionSource,
          scanKeyword: keyword,
          autoPrintEnabled: autoPrintOnLookupConfirm,
        });
      }
    } catch (error) {
      const message = getErrorMessage(error, "messages.lookupFailed");
      setDetail(null);
      setInitialDetail(null);
      activateHqSyncScope();
      invalidateActivePromotions();
      setQueryFeedback({ type: "error", query: keyword.trim(), message });
      setSnackbarMessage(message);
      playQueryFeedback("error");
    } finally {
      lookupSelectionOpenRef.current = false;
      lookupRequestInFlightRef.current = false;
      setLoading(false);
      setAutoPrintOnLookupConfirm(false);
      setLookupSelectionSource(null);
      // 候选确认后的详情和自动价弹层已处理完成，才允许连续相机恢复。
      if (!flowResult.foregroundPending) {
        restoreScanAbility(selectionSource);
      }
    }
  }, [
    activateHqSyncScope,
    autoPrintOnLookupConfirm,
    autoPricingDialogSaving,
    invalidateActivePromotions,
    keyword,
    loadDetail,
    lookupSelectionSource,
    playQueryFeedback,
    processLoadedDetail,
    restoreScanAbility,
    selectedLookupProductCode,
    t,
  ]);

  const handleChangeStorePrice = useCallback(
    (patch: Partial<NonNullable<ProductDetail["storePrice"]>>) => {
      setDetail((current) =>
        current?.storePrice
          ? {
              ...current,
              storePrice: {
                ...current.storePrice,
                ...patch,
              },
            }
          : current,
      );
    },
    [],
  );

  const refreshAfterCommittedProductMutation = useCallback(
    async (productCode: string) => {
      try {
        await loadDetail(productCode);
      } catch (error) {
        // mutation 已提交；详情刷新失败不能把成功保存误报为失败。
        console.warn(
          "[product-query] refresh after committed product mutation failed",
          {
            message: isAxiosError(error) ? error.message : String(error),
          },
        );
      }
    },
    [loadDetail],
  );

  const handleUpdateProductType = useCallback(
    async (productType: number) => {
      if (!detail?.productCode || !selectedStoreCode) {
        return;
      }

      if (detail.productType === productType) {
        setProductTypeDialogVisible(false);
        return;
      }
      if (!ensureCurrentDetailStoreScope(detail)) {
        return;
      }

      setProductTypeSaving(true);
      const scope = {
        productCode: detail.productCode,
        storeCode: selectedStoreCode,
      };
      const mutation = beginHqSyncMutation(scope.productCode, scope.storeCode);
      try {
        const result = await updateProductType(detail.productCode, {
          productType,
          storeCode: selectedStoreCode,
        });

        if (!detailRequestCoordinatorRef.current?.isScopeActive(scope)) {
          return;
        }
        setDetail((current) =>
          current
            ? {
                ...current,
                productType: result.productType,
                productTypeLabel: result.productTypeLabel,
              }
            : current,
        );
        setInitialDetail((current) =>
          current
            ? {
                ...current,
                productType: result.productType,
                productTypeLabel: result.productTypeLabel,
              }
            : current,
        );
        presentHqSyncOperation(
          mutation,
          result.hqSync,
          t("messages.productTypeUpdated"),
        );
        setProductTypeDialogVisible(false);
      } catch (error) {
        hqSyncMutationCoordinatorRef.current?.fail(mutation);
        setSnackbarMessage(
          getErrorMessage(error, "messages.productTypeUpdateFailed"),
        );
      } finally {
        setProductTypeSaving(false);
      }
    },
    [
      beginHqSyncMutation,
      detail,
      ensureCurrentDetailStoreScope,
      getErrorMessage,
      presentHqSyncOperation,
      selectedStoreCode,
      t,
    ],
  );

  const handleToggleAutoPricing = useCallback(
    async (value: boolean) => {
      if (!detail?.storePrice) {
        return;
      }

      if (!value) {
        handleChangeStorePrice({ isAutoPricing: false });
        return;
      }

      await maybeHandleAutoPricing(detail, { forceAutoPricing: true });
    },
    [detail, handleChangeStorePrice, maybeHandleAutoPricing],
  );

  const handleChangeStorePurchasePrice = useCallback(
    (value: string) => {
      const next = toFixedDecimalInput(value);
      setStorePurchaseInput(next.display);
      handleChangeStorePrice({ purchasePrice: next.numeric });
    },
    [handleChangeStorePrice],
  );

  const handleChangeStoreRetailPrice = useCallback((value: string) => {
    const next = toFixedDecimalInput(value);
    setStoreRetailInput(next.display);
    setDetail((current) => {
      if (!current?.storePrice) {
        return current;
      }

      const discountRate = normalizeDiscountRateValue(
        current.storePrice.discountRate,
      );
      return {
        ...current,
        storePrice: {
          ...current.storePrice,
          retailPrice: next.numeric,
          discountRate,
        },
      };
    });
  }, []);

  const handleChangeStoreDiscountPercent = useCallback(
    (value: string) => {
      const percentValue = parseDecimalInput(value);
      const discountRate =
        percentValue == null
          ? null
          : normalizeDiscountRateValue(clamp(percentValue, 0, 100));
      handleChangeStorePrice({ discountRate });
    },
    [handleChangeStorePrice],
  );

  const handleChangeStoreDiscountedRetailPrice = useCallback(
    (value: string) => {
      const discountedRetail = parseDecimalInput(value);
      setDetail((current) => {
        if (!current?.storePrice) {
          return current;
        }

        const retailPrice = current.storePrice.retailPrice ?? null;
        const boundedDiscountedRetail =
          discountedRetail == null || retailPrice == null || retailPrice <= 0
            ? discountedRetail
            : clamp(discountedRetail, 0, retailPrice);
        const discountRate = getDiscountRateFromDiscountedRetail(
          retailPrice,
          boundedDiscountedRetail,
        );

        return {
          ...current,
          storePrice: {
            ...current.storePrice,
            discountRate,
          },
        };
      });
    },
    [],
  );

  const openStorePurchasePriceEditor = useCallback(() => {
    openNumericInputModal({
      key: "store-purchase",
      title: t("storePrice.purchase"),
      value: storePurchaseInput,
      allowDecimal: true,
      onConfirmValue: handleChangeStorePurchasePrice,
    });
  }, [
    handleChangeStorePurchasePrice,
    openNumericInputModal,
    storePurchaseInput,
    t,
  ]);

  const openStoreRetailPriceEditor = useCallback(() => {
    openNumericInputModal({
      key: "store-retail",
      title: t("storePrice.retail"),
      value: storeRetailInput,
      allowDecimal: true,
      onConfirmValue: handleChangeStoreRetailPrice,
    });
  }, [
    handleChangeStoreRetailPrice,
    openNumericInputModal,
    storeRetailInput,
    t,
  ]);

  const openStoreDiscountPercentEditor = useCallback(() => {
    const currentDiscountRate = normalizeDiscountRateValue(
      detail?.storePrice?.discountRate,
    );
    openNumericInputModal({
      key: "store-discount-percent",
      title: t("storePrice.discountPercent"),
      value: formatPercentValue(currentDiscountRate),
      allowDecimal: true,
      onConfirmValue: handleChangeStoreDiscountPercent,
    });
  }, [
    detail?.storePrice?.discountRate,
    handleChangeStoreDiscountPercent,
    openNumericInputModal,
    t,
  ]);

  const openStoreDiscountedRetailEditor = useCallback(() => {
    const currentDiscountRate = normalizeDiscountRateValue(
      detail?.storePrice?.discountRate,
    );
    const currentDiscountedRetail = getDiscountedRetailPrice(
      detail?.storePrice?.retailPrice,
      currentDiscountRate,
    );
    openNumericInputModal({
      key: "store-discounted-retail",
      title: t("storePrice.discountedRetail"),
      value: formatCurrency(currentDiscountedRetail),
      allowDecimal: true,
      onConfirmValue: handleChangeStoreDiscountedRetailPrice,
    });
  }, [
    detail?.storePrice?.discountRate,
    detail?.storePrice?.retailPrice,
    handleChangeStoreDiscountedRetailPrice,
    openNumericInputModal,
    t,
  ]);

  const handleSaveSetCode = useCallback(
    async (setCodeId: string, retailPriceOverride?: number | null) => {
      if (!detail?.productCode || !selectedStoreCode) {
        return;
      }
      if (!ensureCurrentDetailStoreScope(detail)) {
        return;
      }

      const target = detail.setCodes.find(
        (item) => item.setCodeId === setCodeId,
      );
      if (!target || !target.setBarcode?.trim()) {
        setSnackbarMessage(t("messages.setCodeBarcodeRequired"));
        return;
      }

      const retailPrice =
        retailPriceOverride === undefined
          ? target.setRetailPrice
          : retailPriceOverride;
      if (retailPrice == null || !Number.isFinite(retailPrice)) {
        setSnackbarMessage(t("messages.setCodeRetailRequired"));
        return;
      }

      setSavingItemId(setCodeId);
      const mutation = beginHqSyncMutation(
        detail.productCode,
        selectedStoreCode,
      );
      try {
        const saved = await updateSetCode(setCodeId, {
          storeCode: selectedStoreCode,
          barcode: target.setBarcode.trim(),
          retailPrice,
          isActive: target.isActive,
        });
        presentHqSyncOperation(
          mutation,
          saved.hqSync,
          t("messages.setCodeSaved"),
        );
        await refreshAfterCommittedProductMutation(detail.productCode);
      } catch (error) {
        hqSyncMutationCoordinatorRef.current?.fail(mutation);
        setSnackbarMessage(
          getErrorMessage(error, "messages.setCodeSaveFailed"),
        );
      } finally {
        setSavingItemId(null);
      }
    },
    [
      beginHqSyncMutation,
      detail,
      ensureCurrentDetailStoreScope,
      getErrorMessage,
      presentHqSyncOperation,
      refreshAfterCommittedProductMutation,
      selectedStoreCode,
      t,
    ],
  );

  const openEditSetCodeBarcode = useCallback(
    (setCodeId: string) => {
      const target = detail?.setCodes.find(
        (item) => item.setCodeId === setCodeId,
      );
      if (!target) {
        return;
      }
      setBarcodeEditModal({
        key: `set-barcode-${setCodeId}`,
        title: t("setCode.editBarcodeTitle"),
        value: target.setBarcode ?? "",
        targetId: setCodeId,
        codeType: "set",
      });
    },
    [detail?.setCodes, t],
  );

  const openEditSetCodeRetailPrice = useCallback(
    (setCodeId: string) => {
      const target = detail?.setCodes.find(
        (item) => item.setCodeId === setCodeId,
      );
      if (!target) {
        return;
      }
      openNumericInputModal({
        key: `set-retail-${setCodeId}`,
        title: t("setCode.editRetailTitle"),
        value: formatFixedDecimal(target.setRetailPrice),
        allowDecimal: true,
        onConfirmValue: (value) => {
          const retailPrice = value.trim() === "" ? null : Number(value);
          void handleSaveSetCode(setCodeId, retailPrice);
        },
      });
    },
    [detail?.setCodes, handleSaveSetCode, openNumericInputModal, t],
  );

  const openAddSetCode = useCallback(() => {
    setCodeAddModal({ codeType: "set", value: "", retailPrice: "" });
  }, []);

  const openEditMultiCodeBarcode = useCallback(
    (itemId: string) => {
      const target = detail?.multiCodes.find(
        (item) => getMultiCodeItemId(item) === itemId,
      );
      if (!target) {
        return;
      }
      setBarcodeEditModal({
        key: `multi-barcode-${itemId}`,
        title: t("multiCode.editBarcodeTitle"),
        value: target.barcode ?? "",
        targetId: itemId,
        codeType: "multi",
      });
    },
    [detail?.multiCodes, t],
  );

  const openEditMultiCodeRetailPrice = useCallback(
    (itemId: string) => {
      const target = detail?.multiCodes.find(
        (item) => getMultiCodeItemId(item) === itemId,
      );
      if (!target) {
        return;
      }
      openNumericInputModal({
        key: `multi-retail-${itemId}`,
        title: t("multiCode.editRetailTitle"),
        value: formatFixedDecimal(target.retailPrice),
        allowDecimal: true,
        onConfirmValue: (value) => {
          const retailPrice = value.trim() === "" ? null : Number(value);
          void saveMultiCodeRef.current(itemId, retailPrice);
        },
      });
    },
    [detail?.multiCodes, openNumericInputModal, t],
  );

  const openAddMultiCode = useCallback(() => {
    setCodeAddModal({ codeType: "multi", value: "", retailPrice: "" });
  }, []);

  const handleConfirmBarcodeEdit = useCallback(async () => {
    if (!barcodeEditModal || !detail?.productCode || !selectedStoreCode) {
      setBarcodeEditModal(null);
      return;
    }

    const { targetId, value, codeType } = barcodeEditModal;
    const trimmed = value.trim();
    if (!trimmed) {
      setSnackbarMessage(t("messages.setCodeBarcodeRequired"));
      setBarcodeEditModal(null);
      return;
    }

    const setTarget =
      codeType === "set"
        ? detail.setCodes.find((item) => item.setCodeId === targetId)
        : null;
    const multiTarget =
      codeType === "multi"
        ? detail.multiCodes.find(
            (item) => getMultiCodeItemId(item) === targetId,
          )
        : null;
    const setBackedRetailPrice =
      codeType === "set" ? setTarget?.setRetailPrice : multiTarget?.retailPrice;
    if (
      (codeType === "set" || Boolean(multiTarget?.setCodeId)) &&
      (setBackedRetailPrice == null || !Number.isFinite(setBackedRetailPrice))
    ) {
      setSnackbarMessage(t("messages.setCodeRetailRequired"));
      return;
    }
    if (codeType === "multi" && !multiTarget) {
      setSnackbarMessage(t("messages.multiCodeSaveFailed"));
      return;
    }
    if (!ensureCurrentDetailStoreScope(detail, multiTarget?.storeCode)) {
      return;
    }

    setSavingItemId(targetId);
    setBarcodeEditModal(null);
    const mutation = beginHqSyncMutation(detail.productCode, selectedStoreCode);
    try {
      const saved =
        codeType === "multi" && multiTarget && !multiTarget.setCodeId
          ? await updateMultiCode(multiTarget.uuid, {
              barcode: trimmed,
              purchasePrice: multiTarget.purchasePrice ?? null,
              retailPrice: multiTarget.retailPrice ?? null,
              isAutoPricing: multiTarget.isAutoPricing,
              isSpecialProduct: multiTarget.isSpecialProduct,
              isActive: multiTarget.isActive,
            })
          : await updateSetCode(targetId, {
              storeCode: selectedStoreCode,
              barcode: trimmed,
              retailPrice: setBackedRetailPrice!,
              isActive:
                codeType === "multi" ? (multiTarget?.isActive ?? true) : true,
            });
      presentHqSyncOperation(
        mutation,
        saved.hqSync,
        codeType === "set"
          ? t("messages.setCodeSaved")
          : t("messages.multiCodeSaved"),
      );
      await refreshAfterCommittedProductMutation(detail.productCode);
    } catch (error) {
      hqSyncMutationCoordinatorRef.current?.fail(mutation);
      setSnackbarMessage(
        getErrorMessage(
          error,
          codeType === "multi"
            ? "messages.multiCodeSaveFailed"
            : "messages.setCodeSaveFailed",
        ),
      );
    } finally {
      setSavingItemId(null);
    }
  }, [
    barcodeEditModal,
    beginHqSyncMutation,
    detail,
    ensureCurrentDetailStoreScope,
    getErrorMessage,
    presentHqSyncOperation,
    refreshAfterCommittedProductMutation,
    selectedStoreCode,
    t,
  ]);

  const handleConfirmCodeAdd = useCallback(async () => {
    if (!codeAddModal || !detail?.productCode || !selectedStoreCode) {
      setCodeAddModal(null);
      return;
    }
    if (!ensureCurrentDetailStoreScope(detail)) {
      setCodeAddModal(null);
      return;
    }

    const { codeType, value, retailPrice: retailPriceInput } = codeAddModal;
    const trimmed = value.trim();
    if (!trimmed) {
      setSnackbarMessage(
        codeType === "set"
          ? t("messages.setCodeBarcodeRequired")
          : t("messages.multiCodeBarcodeRequired"),
      );
      setCodeAddModal(null);
      return;
    }
    const retailPrice =
      codeType === "set" ? parseDecimalInput(retailPriceInput) : null;
    if (codeType === "set" && (retailPrice == null || retailPrice <= 0)) {
      setSnackbarMessage(t("messages.setCodeRetailRequired"));
      return;
    }

    setSavingItemId(codeType === "set" ? "new-set" : "new-multi");
    setCodeAddModal(null);
    const mutation = beginHqSyncMutation(detail.productCode, selectedStoreCode);
    try {
      const created = await createSetCode({
        productCode: detail.productCode,
        storeCode: selectedStoreCode,
        productType: codeType === "set" ? 1 : 2,
        barcode: trimmed,
        retailPrice,
        isActive: true,
      });
      presentHqSyncOperation(
        mutation,
        created.hqSync,
        codeType === "set"
          ? t("messages.setCodeCreated")
          : t("messages.multiCodeCreated"),
      );
      await refreshAfterCommittedProductMutation(detail.productCode);
    } catch (error) {
      hqSyncMutationCoordinatorRef.current?.fail(mutation);
      setSnackbarMessage(getErrorMessage(error, "messages.setCodeSaveFailed"));
    } finally {
      setSavingItemId(null);
    }
  }, [
    beginHqSyncMutation,
    codeAddModal,
    detail,
    ensureCurrentDetailStoreScope,
    getErrorMessage,
    presentHqSyncOperation,
    refreshAfterCommittedProductMutation,
    selectedStoreCode,
    t,
  ]);

  const openClearancePriceEditor = useCallback(() => {
    openNumericInputModal({
      key: "clearance-price",
      title: t("clearancePrice.price"),
      value: clearancePriceInput,
      allowDecimal: true,
      onConfirmValue: (value: string) => {
        setClearancePriceInput(value);
        setTimeout(() => void saveClearanceRef.current(), 0);
      },
    });
  }, [clearancePriceInput, openNumericInputModal, t]);

  const handleSaveMultiCode = useCallback(
    async (itemId: string, retailPriceOverride?: number | null) => {
      if (!detail?.productCode || !selectedStoreCode) {
        return;
      }

      const target = detail.multiCodes.find(
        (item) => getMultiCodeItemId(item) === itemId,
      );
      if (!target || !target.barcode?.trim()) {
        setSnackbarMessage(t("messages.multiCodeBarcodeRequired"));
        return;
      }
      if (!ensureCurrentDetailStoreScope(detail, target.storeCode)) {
        return;
      }

      const retailPrice =
        retailPriceOverride === undefined
          ? target.retailPrice
          : retailPriceOverride;
      setSavingItemId(itemId);
      const mutation = beginHqSyncMutation(
        detail.productCode,
        selectedStoreCode,
      );
      try {
        const saved = target.setCodeId
          ? await updateSetCode(target.setCodeId, {
              storeCode: selectedStoreCode,
              barcode: target.barcode.trim(),
              retailPrice: retailPrice ?? null,
              isActive: target.isActive,
            })
          : await updateMultiCode(target.uuid, {
              barcode: target.barcode.trim(),
              purchasePrice: target.purchasePrice ?? null,
              retailPrice: retailPrice ?? null,
              isAutoPricing: target.isAutoPricing,
              isSpecialProduct: target.isSpecialProduct,
              isActive: target.isActive,
            });
        presentHqSyncOperation(
          mutation,
          saved.hqSync,
          t("messages.multiCodeSaved"),
        );
        await refreshAfterCommittedProductMutation(detail.productCode);
      } catch (error) {
        hqSyncMutationCoordinatorRef.current?.fail(mutation);
        setSnackbarMessage(
          getErrorMessage(error, "messages.multiCodeSaveFailed"),
        );
      } finally {
        setSavingItemId(null);
      }
    },
    [
      beginHqSyncMutation,
      detail,
      ensureCurrentDetailStoreScope,
      getErrorMessage,
      presentHqSyncOperation,
      refreshAfterCommittedProductMutation,
      selectedStoreCode,
      t,
    ],
  );

  saveMultiCodeRef.current = handleSaveMultiCode;

  const handleLoadMoreCodes = useCallback(() => {
    if (!detail || codesLoading || codesLoadingMore || !codesHasMore) {
      return;
    }

    void loadProductCodes(detail, codePage + 1, true);
  }, [
    codePage,
    codesHasMore,
    codesLoading,
    codesLoadingMore,
    detail,
    loadProductCodes,
  ]);

  const handleSaveClearancePrice = useCallback(async () => {
    if (!detail?.productCode || !selectedStoreCode) {
      return;
    }
    if (
      !ensureCurrentDetailStoreScope(detail, detail.clearancePrice?.storeCode)
    ) {
      return;
    }

    const clearancePrice = parseDecimalInput(clearancePriceInput);
    if (clearancePriceInput.trim() && clearancePrice == null) {
      setSnackbarMessage(t("messages.clearancePriceRequired"));
      return;
    }

    setSavingClearance(true);
    const mutation = beginHqSyncMutation(detail.productCode, selectedStoreCode);
    try {
      const saved = await upsertClearancePrice(detail.productCode, {
        storeCode: selectedStoreCode,
        clearancePrice,
      });
      presentHqSyncOperation(
        mutation,
        saved.hqSync,
        t("messages.clearanceSaved"),
      );
      await refreshAfterCommittedProductMutation(detail.productCode);
    } catch (error) {
      hqSyncMutationCoordinatorRef.current?.fail(mutation);
      setSnackbarMessage(
        getErrorMessage(error, "messages.clearanceSaveFailed"),
      );
    } finally {
      setSavingClearance(false);
    }
  }, [
    beginHqSyncMutation,
    clearancePriceInput,
    detail,
    ensureCurrentDetailStoreScope,
    getErrorMessage,
    presentHqSyncOperation,
    refreshAfterCommittedProductMutation,
    selectedStoreCode,
    t,
  ]);

  saveClearanceRef.current = handleSaveClearancePrice;

  /**
   * 保存分店价格草稿：无改动时直接返回当前详情；保存成功返回服务端回读后的详情，失败返回 null。
   * 「保存」与「保存并打印」共用同一保存流程。
   */
  const saveStorePriceDraft = useCallback(async (): Promise<ProductDetail | null> => {
    if (!detail?.storePrice || !isStorePriceDirty(detail, initialDetail)) {
      return detail;
    }

    setSaving(true);
    try {
      return await persistStorePrice(detail, {
        purchasePrice: detail.storePrice.purchasePrice ?? null,
        retailPrice: detail.storePrice.retailPrice ?? null,
        discountRate: normalizeDiscountRateValue(
          detail.storePrice.discountRate,
        ),
        isAutoPricing: detail.storePrice.isAutoPricing,
        isSpecialProduct: detail.storePrice.isSpecialProduct,
        isActive: detail.storePrice.isActive,
      });
    } catch (error) {
      setSnackbarMessage(getErrorMessage(error, "messages.saveFailed"));
      return null;
    } finally {
      setSaving(false);
    }
  }, [detail, getErrorMessage, initialDetail, persistStorePrice]);

  const handleSaveAll = useCallback(async (): Promise<boolean> => {
    if (!detail?.storePrice || !isStorePriceDirty(detail, initialDetail)) {
      return true;
    }
    return Boolean(await saveStorePriceDraft());
  }, [detail, initialDetail, saveStorePriceDraft]);

  const handleReset = useCallback(() => {
    setDetail(cloneDetail(initialDetail));
  }, [initialDetail]);

  const handlePrintSetCodeProduct = useCallback(
    async (setCodeId: string) => {
      if (!detail) {
        return;
      }

      const target = detail.setCodes.find(
        (item) => item.setCodeId === setCodeId,
      );
      if (!target?.setBarcode?.trim()) {
        setSnackbarMessage(t("messages.setCodeBarcodeRequired"));
        return;
      }

      if (
        target.setRetailPrice == null ||
        !Number.isFinite(target.setRetailPrice)
      ) {
        setSnackbarMessage(t("messages.setCodeRetailRequired"));
        return;
      }

      await sendProductLabel(detail, {
        barcode: target.setBarcode.trim(),
        retailPrice: target.setRetailPrice,
        action: `set:${setCodeId}`,
        printType: smallLabel ? "small" : null,
      });
    },
    [detail, sendProductLabel, smallLabel, t],
  );

  const handlePrintMultiCodeProduct = useCallback(
    async (itemId: string) => {
      if (!detail) {
        return;
      }

      const target = detail.multiCodes.find(
        (item) => getMultiCodeItemId(item) === itemId,
      );
      if (!target?.barcode?.trim()) {
        setSnackbarMessage(t("messages.multiCodeBarcodeRequired"));
        return;
      }

      await sendProductLabel(detail, {
        barcode: target.barcode.trim(),
        retailPrice:
          target.retailPrice ?? detail.storePrice?.retailPrice ?? null,
        action: `multi:${itemId}`,
        printType: smallLabel ? "small" : null,
      });
    },
    [detail, sendProductLabel, smallLabel, t],
  );

  const handlePrint = useCallback(
    async (
      kind: "product" | "discount" | "clearance" | "bigDiscount",
      // 保存并打印需要用刚保存回读的详情，不能读闭包里尚未刷新的 detail。
      targetDetail?: ProductDetail | null,
    ) => {
      const source = targetDetail ?? detail;
      if (!source) {
        return;
      }

      if (
        (kind === "discount" || kind === "bigDiscount") &&
        !(source.storePrice?.discountRate && source.storePrice.discountRate > 0)
      ) {
        setSnackbarMessage(t("messages.discountPrintUnavailable"));
        return;
      }

      if (kind === "clearance" && !source.clearancePrice) {
        setSnackbarMessage(t("messages.clearancePrintUnavailable"));
        return;
      }

      setPrintingAction(kind);
      try {
        const printType = smallLabel ? "small" : null;
        if (kind === "product") {
          await sendProductLabel(source, { action: "product", printType });
          return;
        } else if (kind === "discount") {
          await printDiscountLabel(source, printType);
          for (let i = 1; i < printQuantity; i++) {
            await printDiscountLabel(source, printType);
          }
        } else if (kind === "bigDiscount") {
          await printBigDiscountLabel(source);
          for (let i = 1; i < printQuantity; i++) {
            await printBigDiscountLabel(source);
          }
        } else {
          await printClearanceLabel(source);
          for (let i = 1; i < printQuantity; i++) {
            await printClearanceLabel(source);
          }
        }
        setSnackbarMessage(t("messages.printSuccess"));
        if (quantitySingleUse && printQuantity > 1) {
          setPrintQuantity(1);
        }
      } catch (error) {
        setSnackbarMessage(getErrorMessage(error, "messages.printFailed"));
      } finally {
        setPrintingAction(null);
      }
    },
    [
      detail,
      getErrorMessage,
      printQuantity,
      quantitySingleUse,
      sendProductLabel,
      smallLabel,
      t,
    ],
  );
  const handlePrintRef = useRef(handlePrint);
  handlePrintRef.current = handlePrint;
  const handleProductPrintPress = useCallback(() => {
    void handlePrintRef.current("product");
  }, []);
  const handleDiscountPrintPress = useCallback(() => {
    void handlePrintRef.current("discount");
  }, []);
  const handleBigDiscountPrintPress = useCallback(() => {
    void handlePrintRef.current("bigDiscount");
  }, []);
  const handleOpenPrinterSettings = useCallback(() => {
    // 从打印设置弹窗进入打印机设置页：先收起弹窗，返回本页时不会再看到它悬在上面。
    setPrintSettingsVisible(false);
    router.navigate("/(shell)/settings");
  }, [router]);
  const handleOpenPrintSettings = useCallback(() => {
    setPrintSettingsVisible(true);
  }, []);

  const handleSaveAndPrint = useCallback(async () => {
    if (savingAndPrinting || saving || printingAction || !detail?.storePrice) {
      return;
    }
    setSavingAndPrinting(true);
    try {
      // 保存失败时 persistStorePrice 已提示错误，不再打印。
      const savedDetail = await saveStorePriceDraft();
      if (!savedDetail?.storePrice) {
        return;
      }
      // 按保存后的门店价格选择标签：有折扣打折扣标签，否则打普通标签；打印设置与打印机校验沿用 handlePrint。
      const discountRate = normalizeDiscountRateValue(
        savedDetail.storePrice.discountRate,
      );
      await handlePrint(
        discountRate && discountRate > 0 ? "discount" : "product",
        savedDetail,
      );
    } finally {
      setSavingAndPrinting(false);
    }
  }, [
    detail?.storePrice,
    handlePrint,
    printingAction,
    saveStorePriceDraft,
    saving,
    savingAndPrinting,
  ]);

  const isInvoiceEditorSessionActive = useCallback(() => {
    const auth = useAuthStore.getState();
    const device = useDeviceStore.getState().session;
    // 纯设备登录没有账号 token；仅当前设备会话可作为退出恢复的有效上下文。
    return auth.isAuthenticated || Boolean(auth.sessionKind === "device" &&
      device?.hardwareId && device.authCode && device.storeCode);
  }, []);

  const isInvoiceExitBusy = useCallback(
    () => invoiceExitSavingRef.current || saving || Boolean(savingItemId) ||
      savingClearance || productTypeSaving || createProductBusy || hqSyncRetrying ||
      autoPricingDialogSaving || Boolean(printingAction),
    [autoPricingDialogSaving, createProductBusy, hqSyncRetrying, printingAction,
      productTypeSaving, saving, savingClearance, savingItemId],
  );

  const performReturnToInvoices = useCallback(() => {
    if (!invoiceReturnState || allowInvoiceExit) return;
    pendingInvoiceExitRef.current = { kind: "invoice" };
    setDiscardReturnVisible(false);
    setAllowInvoiceExit(true);
  }, [allowInvoiceExit, invoiceReturnState]);

  const requestInvoiceExit = useCallback((exit: NonNullable<typeof pendingInvoiceExitRef.current>) => {
    if (!invoiceReturnState || allowInvoiceExit) return;
    if (isInvoiceExitBusy()) {
      setSnackbarMessage(t("messages.finishCurrentAction"));
      return;
    }
    pendingInvoiceExitRef.current = exit;
    if (isStorePriceDirty(detail, initialDetail)) {
      setDiscardReturnVisible(true);
    } else {
      setAllowInvoiceExit(true);
    }
  }, [allowInvoiceExit, detail, initialDetail, invoiceReturnState, isInvoiceExitBusy, t]);

  const handleReturnToInvoices = useCallback(() => {
    requestInvoiceExit({ kind: "invoice" });
  }, [requestInvoiceExit]);

  const handleCancelInvoiceExit = useCallback(() => {
    pendingInvoiceExitRef.current = null;
    setDiscardReturnVisible(false);
  }, []);

  const handleDiscardInvoiceExit = useCallback(() => {
    if (!pendingInvoiceExitRef.current || isInvoiceExitBusy()) return;
    setDiscardReturnVisible(false);
    setAllowInvoiceExit(true);
  }, [isInvoiceExitBusy]);

  usePreventRemove(Boolean(invoiceReturnState) && (isAuthenticated || isDeviceMode) && !allowInvoiceExit, ({ data }) => {
    // 会话可能刚刚失效：放行原始登录重定向，不能将它改写成发票返回。
    if (!isInvoiceEditorSessionActive()) {
      pendingInvoiceExitRef.current = { kind: "navigation", action: data.action };
      setAllowInvoiceExit(true);
      return;
    }
    const singlePop = data.action.type === "POP" &&
      (!data.action.payload || !("count" in data.action.payload) || data.action.payload.count === 1);
    // Android 返回键和 iOS 单页返回手势都恢复携带的发票上下文。
    requestInvoiceExit(data.action.type === "GO_BACK" || singlePop
      ? { kind: "invoice" }
      : { kind: "navigation", action: data.action });
  });

  useEffect(() => {
    if (!allowInvoiceExit) return;
    const exit = pendingInvoiceExitRef.current;
    pendingInvoiceExitRef.current = null;
    // 等本次渲染先解除 usePreventRemove，再恢复明细或重放原始导航，避免重复拦截。
    if (exit?.kind === "navigation") {
      navigation.dispatch(exit.action);
    } else if (exit?.kind === "invoice" && invoiceReturnState && isInvoiceEditorSessionActive()) {
      router.replace(buildLocalSupplierInvoicesRestoreHref(invoiceReturnState) as unknown as Parameters<typeof router.replace>[0]);
    }
  }, [allowInvoiceExit, invoiceReturnState, isInvoiceEditorSessionActive, navigation, router]);

  const handleSaveAndReturnToInvoices = useCallback(async () => {
    if (allowInvoiceExit || isInvoiceExitBusy()) return;
    const action = resolveInvoiceEditorExitAction({
      hasInvoiceReturnContext: Boolean(invoiceReturnState),
      hasUnsavedStorePrice: isStorePriceDirty(detail, initialDetail),
      intent: "save-and-return",
    });
    // ref 在发起保存前同步锁定，覆盖 React 尚未刷新 saving 时的重复点击和系统返回。
    invoiceExitSavingRef.current = true;
    try {
      if (action === "save-and-return" && !(await handleSaveAll())) return;
      if (action === "return" || action === "save-and-return") performReturnToInvoices();
    } finally {
      invoiceExitSavingRef.current = false;
    }
  }, [allowInvoiceExit, detail, handleSaveAll, initialDetail, invoiceReturnState, isInvoiceExitBusy, performReturnToInvoices]);

  const storePrice = detail?.storePrice;
  const clearancePrice = detail?.clearancePrice;
  const hqSyncDisplay = hqSyncOperation
    ? getHqSyncDisplayState(hqSyncOperation)
    : null;
  const normalizedStoreDiscountRate = normalizeDiscountRateValue(
    storePrice?.discountRate,
  );
  const hasActiveDiscount = Boolean(
    normalizedStoreDiscountRate && normalizedStoreDiscountRate > 0,
  );
  // 海报入口：审核演示会话离线不可用；无商品或无门店时不显示（启用规则与「折扣」标签按钮一致）。
  const showPosterEntry =
    !isIosReviewSessionActive() && Boolean(detail?.productCode && selectedStoreCode);
  const posterAvailability = resolveScanPosterAvailability({
    discountRate: normalizedStoreDiscountRate,
    activePromotionCount: activePromotions.length,
    clearancePrice: clearancePrice?.clearancePrice,
  });
  const discountedRetailPrice = getDiscountedRetailPrice(
    storePrice?.retailPrice,
    normalizedStoreDiscountRate,
  );
  const retailGp = calcGpPercent(
    storePrice?.retailPrice,
    storePrice?.purchasePrice,
  );
  const discountedRetailGp = calcGpPercent(
    discountedRetailPrice,
    storePrice?.purchasePrice,
  );
  const codeSections = resolveCodeSections(detail);
  const hasCodeSection = codeSections.hasCodeSection;
  const dirtyCodeIds = useMemo(
    () => getDirtyCodeIds(detail, initialDetail),
    [detail, initialDetail],
  );
  // 「原值」与毛利变化都以现有 baseline（initialDetail）为准，且仅在同一商品、价格有改动时显示。
  const baselineStorePrice =
    dirtyCount > 0 && initialDetail?.productCode === detail?.productCode
      ? initialDetail?.storePrice
      : null;
  const baselineDiscountRate = normalizeDiscountRateValue(
    baselineStorePrice?.discountRate,
  );
  const baselineDiscountedRetailPrice = getDiscountedRetailPrice(
    baselineStorePrice?.retailPrice,
    baselineDiscountRate,
  );
  const storePriceOriginals = useMemo(
    () => baselineStorePrice
      ? {
        purchasePrice: resolveOriginalDisplay(
          formatFixedDecimal(storePrice?.purchasePrice),
          formatFixedDecimal(baselineStorePrice.purchasePrice),
        ),
        retailPrice: resolveOriginalDisplay(
          formatFixedDecimal(storePrice?.retailPrice),
          formatFixedDecimal(baselineStorePrice.retailPrice),
        ),
        discountPercent: resolveOriginalDisplay(
          formatPercentValue(normalizedStoreDiscountRate),
          formatPercentValue(baselineDiscountRate),
        ),
        discountedRetailPrice: resolveOriginalDisplay(
          formatCurrency(discountedRetailPrice),
          formatCurrency(baselineDiscountedRetailPrice),
        ),
        }
      : undefined,
    [
      baselineDiscountRate,
      baselineDiscountedRetailPrice,
      baselineStorePrice,
      discountedRetailPrice,
      normalizedStoreDiscountRate,
      storePrice?.purchasePrice,
      storePrice?.retailPrice,
    ],
  );
  const storePriceSyncFooter = useMemo(
    () => canSyncToOtherStores && selectedStoreCode ? (
      <SyncToOtherStoresSection
        variant="inline"
        productCode={detail?.productCode ?? ""}
        storeCode={selectedStoreCode}
        storeName={storePrice?.storeName ?? selectedStore?.storeName}
        hasUnsavedChanges={isStorePriceDirty(detail, initialDetail)}
        disabled={saving}
        onSaveBeforeSync={handleSaveAll}
        onMessage={setSnackbarMessage}
      />
    ) : null,
    [
      canSyncToOtherStores,
      detail,
      handleSaveAll,
      initialDetail,
      saving,
      selectedStore?.storeName,
      selectedStoreCode,
      storePrice?.storeName,
    ],
  );
  const handleStorePriceAutoPricingToggle = useCallback(
    (value: boolean) => {
      if (offlineMode) {
        notifyOfflineEditing();
      } else {
        void handleToggleAutoPricing(value);
      }
    },
    [handleToggleAutoPricing, notifyOfflineEditing, offlineMode],
  );
  const handleStorePriceSpecialToggle = useCallback(
    (value: boolean) => {
      if (offlineMode) {
        notifyOfflineEditing();
      } else {
        handleChangeStorePrice({ isSpecialProduct: value });
      }
    },
    [handleChangeStorePrice, notifyOfflineEditing, offlineMode],
  );
  const retailGpTrend = baselineStorePrice
    ? getMarginTrend(
        calcGrossMarginPercent(storePrice?.retailPrice, storePrice?.purchasePrice),
        calcGrossMarginPercent(
          baselineStorePrice.retailPrice,
          baselineStorePrice.purchasePrice,
        ),
      )
    : null;
  const discountedRetailGpTrend = baselineStorePrice
    ? getMarginTrend(
        calcGrossMarginPercent(discountedRetailPrice, storePrice?.purchasePrice),
        calcGrossMarginPercent(
          baselineDiscountedRetailPrice,
          baselineStorePrice.purchasePrice,
        ),
      )
    : null;
  const storeDisplayName = selectedStore?.storeName || selectedStoreCode;
  const handleOpenStorePicker = useCallback(() => {
    if (!isProductQueryBusyRef.current()) {
      setStorePickerVisible(true);
    }
  }, []);
  const renderCameraScanner = () => {
    if (!isFocused) {
      return null;
    }

    return (
      <>
        {cameraScan.permission?.granted ? (
          <View style={styles.cameraFrame}>
            <CameraView style={styles.cameraView} {...cameraScan.cameraProps} />
          </View>
        ) : (
          <Card style={styles.permissionCard}>
            <Card.Content style={styles.permissionCardContent}>
              <Text variant="titleMedium">
                {t("camera.needPermissionTitle")}
              </Text>
              <Text variant="bodySmall" style={styles.cameraTip}>
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
        <Text variant="bodySmall" style={styles.cameraTip}>
          {t("messages.cameraTip")}
        </Text>
      </>
    );
  };

  return (
    <SafeAreaView
      style={[
        styles.safeArea,
        hasActiveDiscount ? styles.discountedSafeArea : null,
      ]}
      edges={["top", "left", "right"]}
    >
      {offlineMode ? (
        <OfflineModeBanner
          activeMeta={offlineCatalogActiveMeta}
          onRetry={() => {
            void probeReconnectNow();
            void triggerRecovery();
          }}
        />
      ) : null}

      <SearchPanel
        value={keyword}
        loading={loading || storesLoading || scannerInputBlocked}
        lastHitLabel={detail ? undefined : lastHitLabel}
        refreshing={refreshing}
        onChangeText={setKeyword}
        onScannerInput={handleHidScan}
        onFocus={pauseHiddenScannerFocus}
        onBlur={resumeHiddenScannerFocusLater}
        onScanPress={() => {
          if (isProductQueryBusy()) {
            return;
          }
          cameraForegroundGenerationRef.current = null;
          updateCameraSheetSession({ type: "open" }, cameraScanModeRef.current);
        }}
        onRefreshPress={() => void handleRefresh()}
        onOpenPrintSettings={() => setPrintSettingsVisible(true)}
        onCreateProduct={
          access.canCreateStoreProducts && detail && !offlineMode
            ? openCreateProductModal
            : undefined
        }
        createProductDisabled={createProductBusy}
        onSubmit={() => void handleLookup()}
        onClear={handleClear}
      />
      {offlineEligible && !offlineMode ? (
        <OfflineCatalogStatusRow
          storeCode={selectedStoreCode ?? null}
          activeMeta={offlineCatalogActiveMeta}
          refresh={offlineCatalogRefresh}
          online={!offlineMode}
          onRefresh={handleRefreshOfflineCatalog}
          onCancel={handleCancelOfflineCatalog}
        />
      ) : null}
      {isIosReviewSessionActive() ? (
        <Button
          compact
          mode="text"
          icon="barcode-scan"
          accessibilityLabel="Use sample barcode 9330000000017 / 使用示例条码 9330000000017"
          disabled={scannerInputBlocked}
          style={styles.sampleBarcodeButton}
          onPress={() => {
            // 一键填入稳定样例并沿用现有查询反馈流，审核员无需外部条码。
            setKeyword(IOS_REVIEW_SAMPLE_BARCODE);
            void handleLookup(IOS_REVIEW_SAMPLE_BARCODE, "manual");
          }}
        >
          {language === "zh"
            ? `使用示例条码 ${IOS_REVIEW_SAMPLE_BARCODE}`
            : `Use sample barcode ${IOS_REVIEW_SAMPLE_BARCODE}`}
        </Button>
      ) : null}

      <View style={styles.queryBody}>
      <ScrollView
        style={[styles.scrollArea, pendingScanPresentation ? styles.hiddenPreviousDetail : null]}
        accessibilityElementsHidden={Boolean(pendingScanPresentation)}
        importantForAccessibility={pendingScanPresentation ? "no-hide-descendants" : "auto"}
        contentContainerStyle={styles.content}
        pointerEvents={scannerInputBlocked ? "none" : "auto"}
      >
        {!storePrice ? (
          // 门店名已移入价格卡页眉；未查到商品或当前分店无价格记录时在这里保留切换入口。
          <View style={styles.storeRow}>
            <StoreSwitchButton
              storeName={storeDisplayName}
              canSelectStore={canSelectStore}
              storeLocked={editorStoreScope.locked}
              onPress={handleOpenStorePicker}
            />
          </View>
        ) : null}
        {access.canCreateStoreProducts && !detail && !offlineMode ? (
          <View style={styles.createProductBar}>
            <Button
              icon="plus"
              mode="contained-tonal"
              onPress={openCreateProductModal}
              disabled={createProductBusy}
            >
              {t("createProduct.action")}
            </Button>
          </View>
        ) : null}
        {hqSyncOperation && hqSyncDisplay?.visible ? (
          <View
            accessibilityLiveRegion="polite"
            style={[
              styles.hqSyncStatus,
              hqSyncDisplay.tone === "warning"
                ? styles.hqSyncStatusWarning
                : null,
            ]}
          >
            {hqSyncOperation.status === "blocked" ? null : (
              <ActivityIndicator size={16} color="#2563EB" />
            )}
            <View style={styles.hqSyncStatusCopy}>
              <Text variant="labelMedium" style={styles.hqSyncStatusTitle}>
                {t(hqSyncDisplay.messageKey)}
              </Text>
              <Text variant="bodySmall" style={styles.hqSyncStatusMeta}>
                {hqSyncOperation.storeCode
                  ? t("messages.hqSyncProduct", {
                      productCode: hqSyncOperation.productCode,
                      storeCode: hqSyncOperation.storeCode,
                    })
                  : t("messages.hqSyncProductAllStores", {
                      productCode: hqSyncOperation.productCode,
                    })}
                {hqSyncOperation.attemptCount > 0
                  ? ` · ${t("messages.hqSyncAttempt", { count: hqSyncOperation.attemptCount })}`
                  : ""}
              </Text>
            </View>
            {hqSyncDisplay.canRetry && !offlineMode ? (
              <Button
                compact
                mode="text"
                loading={hqSyncRetrying}
                disabled={hqSyncRetrying}
                onPress={() => void handleRetryHqSync()}
              >
                {t("messages.hqSyncRetry")}
              </Button>
            ) : null}
          </View>
        ) : null}
        {invoiceReturnState ? (
          <View style={styles.returnBar}>
            <View style={styles.returnContext}>
              <Text variant="labelLarge" style={styles.returnContextTitle}>
                {t("actions.invoiceStoreLocked")}
              </Text>
              <Text variant="bodySmall" style={styles.returnContextMeta}>
                {selectedStore?.storeName || selectedStoreCode || t("common:na")}
              </Text>
            </View>
            <Button compact icon="arrow-left" mode="text" onPress={handleReturnToInvoices}>
              {t("actions.returnToInvoiceDetails")}
            </Button>
          </View>
        ) : null}
        {detail ? (
          <>
            <ProductHeroCard
              imageUrl={detail.productImage}
              productName={detail.productName}
              itemNumber={detail.itemNumber}
              supplierName={detail.localSupplierName}
              supplierCode={detail.localSupplierCode}
              barcode={detail.barcode}
              productType={detail.productType}
              grade={detail.grade}
              variant={hasCodeSection && editorTab === "codes" ? "compact" : "full"}
              mainRetailPrice={formatFixedDecimal(storePrice?.retailPrice)}
              onPressProductType={offlineMode ? undefined : handleOpenProductTypeDialog}
              onOpenInsights={
                isIosReviewSessionActive()
                  ? undefined
                  : offlineMode
                    ? handleOfflineInsightsUnavailable
                    : handleOpenProductInsights
              }
              insightsDisabled={scannerInputBlocked}
            />

            {hasCodeSection ? (
              <View accessibilityRole="tablist" style={styles.editorTabs}>
                {([
                  ["price", t("sections.priceTab"), dirtyCount > 0],
                  [
                    "codes",
                    t("sections.codesTab", { count: codeSections.count }),
                    dirtyCodeIds.size > 0,
                  ],
                ] as const).map(([tab, label, tabDirty]) => {
                  const selected = editorTab === tab;
                  return (
                    <Pressable
                      key={tab}
                      accessibilityRole="tab"
                      accessibilityState={{ selected }}
                      accessibilityHint={tabDirty ? t("sections.unsaved") : undefined}
                      onPress={() => setEditorTab(tab)}
                      style={[styles.editorTab, selected ? styles.editorTabActive : null]}
                    >
                      <Text
                        style={[styles.editorTabText, selected ? styles.editorTabTextActive : null]}
                        numberOfLines={1}
                      >
                        {label}
                      </Text>
                      {tabDirty ? <View style={styles.editorTabDot} /> : null}
                    </Pressable>
                  );
                })}
              </View>
            ) : null}

            {editorTab === "price" || !hasCodeSection ? (
              <View style={styles.firstScreenSection}>
              <ProductPromotionCard items={activePromotions} />

              {storePrice ? (
                <StorePriceStrategyCard
                  storeName={storePrice.storeName || storeDisplayName}
                  canSelectStore={canSelectStore}
                  storeLocked={editorStoreScope.locked}
                  onStorePress={handleOpenStorePicker}
                  purchasePrice={storePurchaseInput}
                  retailPrice={storeRetailInput}
                  retailGp={retailGp}
                  retailGpTrend={retailGpTrend}
                  discountPercent={formatPercentValue(
                    normalizedStoreDiscountRate,
                  )}
                  discountedRetailPrice={formatCurrency(discountedRetailPrice)}
                  discountedRetailGp={discountedRetailGp}
                  discountedRetailGpTrend={discountedRetailGpTrend}
                  originalValues={storePriceOriginals}
                  footer={storePriceSyncFooter}
                  autoPricing={storePrice.isAutoPricing}
                  isSpecialProduct={storePrice.isSpecialProduct}
                  rate={formatFixedDecimal(storePrice.rate)}
                  strategySourceLabel={storePrice.strategySourceLabel}
                  strategyRuleLabel={storePrice.strategyRuleLabel}
                  readOnly={offlineMode}
                  onEditPurchasePrice={
                    offlineMode ? notifyOfflineEditing : openStorePurchasePriceEditor
                  }
                  onEditRetailPrice={
                    offlineMode ? notifyOfflineEditing : openStoreRetailPriceEditor
                  }
                  onEditDiscountPercent={
                    offlineMode ? notifyOfflineEditing : openStoreDiscountPercentEditor
                  }
                  onEditDiscountedRetailPrice={
                    offlineMode ? notifyOfflineEditing : openStoreDiscountedRetailEditor
                  }
                  onToggleAutoPricing={handleStorePriceAutoPricingToggle}
                  onToggleSpecial={handleStorePriceSpecialToggle}
                />
              ) : (
                <View style={styles.emptyBlock}>
                  <Text variant="bodyMedium">
                    {t("messages.emptyStorePrice")}
                  </Text>
                </View>
              )}

              <Card style={styles.printExtrasCard} mode="contained">
                <StoreClearancePriceCard
                  clearanceBarcode={clearancePrice?.clearanceBarcode}
                  clearancePrice={clearancePriceInput}
                  isPrintingClearance={printingAction === "clearance"}
                  readOnly={offlineMode}
                  onEditClearancePrice={
                    offlineMode ? notifyOfflineEditing : openClearancePriceEditor
                  }
                  onPrintClearance={
                    printingAction && printingAction !== "clearance"
                      ? undefined
                      : () => void handlePrint("clearance")
                  }
                />
                {showPosterEntry ? (
                  <View style={styles.posterFooterRow}>
                    <PosterEntryRow
                      availability={posterAvailability}
                      disabled={scannerInputBlocked || offlineMode}
                      onOpen={handleOpenPromoPoster}
                    />
                  </View>
                ) : null}
              </Card>
              </View>
            ) : null}

            {hasCodeSection && editorTab === "codes" ? (
              <View style={styles.secondarySection}>
                {codeSections.showSet ? (
                  <SetCodeCompactSection
                    items={detail.setCodes}
                    savingItemId={savingItemId}
                    dirtyItemIds={dirtyCodeIds}
                    printingItemId={
                      printingAction?.startsWith("set:")
                        ? printingAction.slice(4)
                        : null
                    }
                    totalCount={detail.setCodeCount}
                    loading={codesLoading}
                    loadingMore={codesLoadingMore}
                    hasMore={codesHasMore}
                    readOnly={offlineMode}
                    onEditItemBarcode={
                      offlineMode ? notifyOfflineEditing : openEditSetCodeBarcode
                    }
                    onEditItemRetailPrice={
                      offlineMode ? notifyOfflineEditing : openEditSetCodeRetailPrice
                    }
                    onSaveItem={(setCodeId) =>
                      offlineMode
                        ? notifyOfflineEditing()
                        : void handleSaveSetCode(setCodeId)
                    }
                    onPrintItem={(setCodeId) =>
                      void handlePrintSetCodeProduct(setCodeId)
                    }
                    onAddItem={offlineMode ? notifyOfflineEditing : openAddSetCode}
                    onLoadMore={handleLoadMoreCodes}
                  />
                ) : null}
                {codeSections.showMulti ? (
                  <MultiCodeCompactList
                    items={detail.multiCodes}
                    savingItemId={savingItemId}
                    dirtyItemIds={dirtyCodeIds}
                    printingItemId={
                      printingAction?.startsWith("multi:")
                        ? printingAction.slice(6)
                        : null
                    }
                    totalCount={detail.multiCodeCount}
                    loading={codesLoading}
                    loadingMore={codesLoadingMore}
                    hasMore={codesHasMore}
                    readOnly={offlineMode}
                    onEditItemBarcode={
                      offlineMode ? notifyOfflineEditing : openEditMultiCodeBarcode
                    }
                    onEditItemRetailPrice={
                      offlineMode ? notifyOfflineEditing : openEditMultiCodeRetailPrice
                    }
                    onSaveItem={(setCodeId) =>
                      offlineMode
                        ? notifyOfflineEditing()
                        : void handleSaveMultiCode(setCodeId)
                    }
                    onPrintItem={(setCodeId) =>
                      void handlePrintMultiCodeProduct(setCodeId)
                    }
                    onAddItem={offlineMode ? notifyOfflineEditing : openAddMultiCode}
                    onLoadMore={handleLoadMoreCodes}
                  />
                ) : null}
              </View>
            ) : null}
          </>
        ) : queryFeedback.type === "empty" ? (
          <View style={styles.emptyBlock}>
            <Text variant="titleSmall" style={styles.emptyTitle}>
              {t("messages.noResultTitle")}
            </Text>
            <Text variant="bodyMedium" style={styles.emptyText}>
              {t("messages.noResultDescription", {
                value: queryFeedback.query,
              })}
            </Text>
          </View>
        ) : queryFeedback.type === "error" ? (
          <View style={styles.emptyBlock}>
            <Text variant="titleSmall" style={styles.emptyTitle}>
              {t("messages.lookupErrorTitle")}
            </Text>
            <Text variant="bodyMedium" style={styles.emptyText}>
              {queryFeedback.message || t("messages.lookupErrorDescription")}
            </Text>
          </View>
        ) : (
          <View style={styles.emptyBlock}>
            <Text variant="bodyMedium">{t("messages.emptyPrompt")}</Text>
          </View>
        )}
      </ScrollView>
      {pendingScanPresentation ? (
        <View style={styles.scanPrintProgress} accessibilityLiveRegion="polite">
          <ActivityIndicator size="small" />
          <Text variant="titleMedium">{t("print.inProgress")}</Text>
          <Text variant="bodyMedium" numberOfLines={2}>{pendingScanPresentation.productName}</Text>
          <Text variant="bodyMedium">{pendingScanPresentation.keyword}</Text>
        </View>
      ) : null}
      </View>

      {/* 固定区只放标签操作，清货价与海报仍可滚动，避免占满 Zebra 小屏；
          打印机连接状态以 chip 并入打印行，不再单独占一行高度。 */}
      {detail && (editorTab === "price" || !hasCodeSection) ? (
        <View
          style={styles.printDock}
          accessibilityElementsHidden={Boolean(pendingScanPresentation)}
          importantForAccessibility={pendingScanPresentation ? "no-hide-descendants" : "auto"}
          pointerEvents={scannerInputBlocked ? "none" : "auto"}
        >
          <LabelPrintCard
            leading={
              <PrinterConnectionChip
                savedPrinter={savedPrinter}
                status={printerConnectionStatus}
                lastError={printerLastError}
                onPress={pendingScanPresentation ? undefined : handleOpenPrintSettings}
              />
            }
            isPrintingProduct={printingAction === "product"}
            isPrintingDiscount={printingAction === "discount"}
            isPrintingBigDiscount={printingAction === "bigDiscount"}
            canPrintDiscount={Boolean(
              normalizedStoreDiscountRate &&
              normalizedStoreDiscountRate > 0,
            )}
            canPrintBigDiscount={Boolean(
              normalizedStoreDiscountRate &&
              normalizedStoreDiscountRate > 0,
            )}
            onPrintProduct={
              pendingScanPresentation || (printingAction && printingAction !== "product")
                ? undefined
                : handleProductPrintPress
            }
            onPrintDiscount={
              pendingScanPresentation || (printingAction && printingAction !== "discount")
                ? undefined
                : handleDiscountPrintPress
            }
            onPrintBigDiscount={
              pendingScanPresentation || (printingAction && printingAction !== "bigDiscount")
                ? undefined
                : handleBigDiscountPrintPress
            }
          />
        </View>
      ) : null}

      {/* 待打印海报浮条：有未保存修改时让位给保存操作条，避免底部叠两层操作。 */}
      {!isIosReviewSessionActive() && !(dirtyCount > 0 && !scannerInputBlocked) ? (
        <PosterQueueBar onOpen={handleOpenPromoPosterQueue} />
      ) : null}

      <StickyActionBar
        visible={dirtyCount > 0 && !scannerInputBlocked && !offlineMode}
        dirtyCount={dirtyCount}
        saving={saving}
        savingAndPrinting={savingAndPrinting}
        onReset={handleReset}
        onSaveAll={() => void handleSaveAll()}
        onSaveAndReturn={
          invoiceReturnState
            ? () => void handleSaveAndReturnToInvoices()
            : undefined
        }
        onSaveAndPrint={
          !invoiceReturnState && storePrice
            ? () => void handleSaveAndPrint()
            : undefined
        }
      />

      <LookupResultSheet
        visible={isFocused && lookupVisible}
        queryText={keyword}
        items={lookupItems}
        selectedValue={selectedLookupProductCode}
        onSelect={setSelectedLookupProductCode}
        onClose={() => {
          const selectionSource = lookupSelectionSource;
          lookupSelectionOpenRef.current = false;
          setLookupVisible(false);
          restoreScanAbility(selectionSource);
        }}
        onConfirm={() => void handleConfirmLookup()}
      />

      <Portal>
        {isFocused ? (
          <>
            <CreateProductDialog
              visible={createProductVisible && !createBarcodeScannerVisible}
              values={createProductDraft}
              supplierLabel={
                selectedCreateSupplier
                  ? `${selectedCreateSupplier.supplierCode} · ${selectedCreateSupplier.supplierName || selectedCreateSupplier.supplierCode}`
                  : null
              }
              suppliersLoading={createSuppliersLoading}
              hasSuppliers={createSuppliers.length > 0}
              saving={createProductSaving}
              generating={createBarcodeGenerating}
              onChange={updateCreateProductDraft}
              onSelectSupplier={() => setCreateSupplierPickerVisible(true)}
              onReloadSuppliers={() => void loadCreateSuppliers()}
              onGenerate={() => void handleGenerateCreateBarcode()}
              onScan={() => setCreateBarcodeScannerVisible(true)}
              onDismiss={closeCreateProductModal}
              onSubmit={() => void handleCreateProductSubmit()}
            />
            <Modal
              visible={discardReturnVisible}
              onDismiss={handleCancelInvoiceExit}
              contentContainerStyle={styles.discardReturnModal}
            >
              <Text variant="titleMedium" style={styles.discardReturnTitle}>
                {t("actions.unsavedTitle")}
              </Text>
              <Text variant="bodyMedium" style={styles.discardReturnDescription}>
                {t("actions.unsavedDescription")}
              </Text>
              <View style={styles.discardReturnActions}>
                <Button onPress={handleCancelInvoiceExit}>
                  {t("actions.continueEditing")}
                </Button>
                <Button
                  mode="contained"
                  onPress={handleDiscardInvoiceExit}
                >
                  {t("actions.discardAndReturn")}
                </Button>
              </View>
            </Modal>

            {createSupplierPickerVisible ? (
              <CreateSupplierSheet
                suppliers={createSuppliers}
                selectedCode={createProductDraft.localSupplierCode}
                loading={createSuppliersLoading}
                disabled={createProductBusy}
                onSelect={handleSelectCreateSupplier}
                onDismiss={() => setCreateSupplierPickerVisible(false)}
                onReload={() => void loadCreateSuppliers()}
              />
            ) : null}
            {createBarcodeScannerVisible ? (
              <CreateBarcodeScanner
                onDismiss={() => setCreateBarcodeScannerVisible(false)}
                onScan={(barcode) => {
                  updateCreateProductDraft({ barcode });
                  setCreateBarcodeScannerVisible(false);
                }}
              />
            ) : null}

            <Modal
              visible={Boolean(autoPricingDialog)}
              onDismiss={
                autoPricingDialogSaving
                  ? undefined
                  : () => {
                      if (!autoPricingDialog) {
                        return;
                      }

                      const keepCameraOpen = shouldRestoreCameraScan(
                        autoPricingDialog.scanSource,
                      );
                      restoreScanAbility(autoPricingDialog.scanSource);
                      finishAutoPricingDialog({
                        status: "cancelled",
                        keepCameraOpen,
                        labelPrinted: false,
                        updatedDetail: autoPricingDialog.detail,
                      });
                    }
              }
              contentContainerStyle={styles.autoPricingModal}
            >
              {autoPricingDialog ? (
                <View style={styles.autoPricingContent}>
                  <Text variant="titleMedium" style={styles.autoPricingTitle}>
                    {t("autoPricingConfirm.title")}
                  </Text>
                  <Text
                    variant="bodyMedium"
                    style={styles.autoPricingDescription}
                  >
                    {t("autoPricingConfirm.description", {
                      name:
                        autoPricingDialog.detail.productName ||
                        t("hero.unnamedProduct"),
                      code:
                        autoPricingDialog.detail.itemNumber ||
                        autoPricingDialog.detail.productCode,
                    })}
                  </Text>
                  <View style={styles.autoPricingPriceBlock}>
                    <Text
                      variant="bodyMedium"
                      style={styles.autoPricingCurrentPrice}
                    >
                      {t("autoPricingConfirm.currentRetail", {
                        value:
                          autoPricingDialog.evaluation
                            .currentRetailPriceFormatted || "--",
                      })}
                    </Text>
                    <Text
                      variant="bodyMedium"
                      style={styles.autoPricingNextPrice}
                    >
                      {t("autoPricingConfirm.nextRetail", {
                        value:
                          autoPricingDialog.evaluation
                            .recalculatedRetailPriceFormatted || "--",
                      })}
                    </Text>
                  </View>
                  <Text variant="bodySmall" style={styles.autoPricingHint}>
                    {t("autoPricingConfirm.discountHint")}
                  </Text>
                  <View style={styles.autoPricingActions}>
                    <Button
                      mode="outlined"
                      onPress={() => {
                        if (!autoPricingDialog) {
                          return;
                        }

                        const keepCameraOpen = shouldRestoreCameraScan(
                          autoPricingDialog.scanSource,
                        );
                        restoreScanAbility(autoPricingDialog.scanSource);
                        finishAutoPricingDialog({
                          status: "cancelled",
                          keepCameraOpen,
                          labelPrinted: false,
                          updatedDetail: autoPricingDialog.detail,
                        });
                      }}
                      disabled={autoPricingDialogSaving}
                    >
                      {t("common:actions.cancel")}
                    </Button>
                    <Button
                      mode="contained"
                      loading={autoPricingDialogSaving}
                      disabled={autoPricingDialogSaving}
                      onPress={async () => {
                        if (!autoPricingDialog) {
                          return;
                        }

                        setAutoPricingDialogSaving(true);
                        const keepCameraOpen = shouldRestoreCameraScan(
                          autoPricingDialog.scanSource,
                        );
                        const savedDetail = await persistStorePrice(
                          autoPricingDialog.detail,
                          {
                            retailPrice:
                              autoPricingDialog.evaluation
                                .recalculatedRetailPrice ??
                              autoPricingDialog.detail.storePrice
                                ?.retailPrice ??
                              null,
                            discountRate: normalizeDiscountRateValue(
                              autoPricingDialog.evaluation.discountRate ??
                                autoPricingDialog.detail.storePrice
                                  ?.discountRate,
                            ),
                            isAutoPricing: true,
                          },
                        );

                        if (!savedDetail) {
                          restoreScanAbility(autoPricingDialog.scanSource);
                          finishAutoPricingDialog({
                            status: "failed",
                            keepCameraOpen,
                            labelPrinted: false,
                            updatedDetail: autoPricingDialog.detail,
                          });
                          return;
                        }

                        const labelPrinted =
                          await sendProductLabel(savedDetail);
                        restoreScanAbility(autoPricingDialog.scanSource);
                        finishAutoPricingDialog({
                          status: "confirmed",
                          keepCameraOpen,
                          labelPrinted,
                          updatedDetail: savedDetail,
                        });
                      }}
                    >
                      {t("autoPricingConfirm.confirm")}
                    </Button>
                  </View>
                </View>
              ) : null}
            </Modal>

            <NumericInputModal
              visible={Boolean(numericInputModal)}
              title={numericInputModal?.title ?? ""}
              value={numericInputModal?.value ?? ""}
              allowDecimal={numericInputModal?.allowDecimal ?? true}
              confirmLabel={numericInputModal?.confirmLabel}
              onChangeValue={(value) =>
                setNumericInputModal((current) =>
                  current ? { ...current, value } : current,
                )
              }
              onConfirm={handleConfirmNumericInputModal}
              onDismiss={dismissNumericInputModal}
            />

            <Modal
              visible={Boolean(barcodeEditModal)}
              onDismiss={() => setBarcodeEditModal(null)}
              contentContainerStyle={styles.textEditModal}
            >
              <View style={styles.textEditModalContent}>
                <Text variant="titleMedium" style={styles.textEditModalTitle}>
                  {barcodeEditModal?.title ?? ""}
                </Text>
                <TextInput
                  style={styles.textEditInput}
                  value={barcodeEditModal?.value ?? ""}
                  onChangeText={(value) =>
                    setBarcodeEditModal((current) =>
                      current ? { ...current, value } : current,
                    )
                  }
                  autoFocus
                  selectTextOnFocus
                />
                <View style={styles.textEditModalFooter}>
                  <Button mode="text" onPress={() => setBarcodeEditModal(null)}>
                    {t("common:actions.cancel")}
                  </Button>
                  <Button
                    mode="contained"
                    onPress={() => void handleConfirmBarcodeEdit()}
                  >
                    {t("common:actions.apply")}
                  </Button>
                </View>
              </View>
            </Modal>

            <CodeAddSheet
              visible={Boolean(codeAddModal) && !codeAddScannerVisible}
              codeType={codeAddModal?.codeType ?? "set"}
              barcode={codeAddModal?.value ?? ""}
              retailPrice={codeAddModal?.retailPrice ?? ""}
              unitRetailPrice={detail?.storePrice?.retailPrice}
              onChangeBarcode={(value) =>
                setCodeAddModal((current) =>
                  current ? { ...current, value } : current,
                )
              }
              onChangeRetailPrice={(retailPrice) =>
                setCodeAddModal((current) =>
                  current ? { ...current, retailPrice } : current,
                )
              }
              onScan={() => setCodeAddScannerVisible(true)}
              onDismiss={() => setCodeAddModal(null)}
              onSubmit={() => void handleConfirmCodeAdd()}
            />
            {codeAddModal && codeAddScannerVisible ? (
              <CreateBarcodeScanner
                onDismiss={() => setCodeAddScannerVisible(false)}
                onScan={(barcode) => {
                  // 只回填条码，不触发查询或保存；提交仍由面板按钮走原新增流程。
                  setCodeAddModal((current) =>
                    current ? { ...current, value: barcode } : current,
                  );
                  setCodeAddScannerVisible(false);
                }}
              />
            ) : null}

            <Modal
              visible={productTypeDialogVisible}
              onDismiss={
                productTypeSaving
                  ? undefined
                  : () => setProductTypeDialogVisible(false)
              }
              contentContainerStyle={styles.productTypeModal}
            >
              <View style={styles.productTypeModalContent}>
                <Text
                  variant="titleMedium"
                  style={styles.productTypeModalTitle}
                >
                  {t("hero.productTypeChooseTitle")}
                </Text>
                <View style={styles.productTypeOptions}>
                  {PRODUCT_TYPE_OPTIONS.map((type) => {
                    const selected = detail?.productType === type;
                    const label =
                      type === 0
                        ? t("hero.productType.normal")
                        : type === 1
                          ? t("hero.productType.set")
                          : t("hero.productType.multi");
                    const description =
                      type === 0
                        ? t("hero.productTypeDescription.normal")
                        : type === 1
                          ? t("hero.productTypeDescription.set")
                          : t("hero.productTypeDescription.multi");

                    return (
                      <View key={type} style={styles.productTypeOptionCard}>
                        <Button
                          mode={selected ? "contained" : "outlined"}
                          onPress={() => void handleUpdateProductType(type)}
                          disabled={productTypeSaving}
                          loading={productTypeSaving && selected}
                          style={styles.productTypeOptionButton}
                        >
                          {label}
                        </Button>
                        <Text
                          variant="bodySmall"
                          style={styles.productTypeOptionDescription}
                        >
                          {description}
                        </Text>
                      </View>
                    );
                  })}
                </View>
                <View style={styles.productTypeFooter}>
                  <Button
                    mode="text"
                    onPress={() => setProductTypeDialogVisible(false)}
                    disabled={productTypeSaving}
                  >
                    {t("common:actions.cancel")}
                  </Button>
                </View>
              </View>
            </Modal>

            <PrintSettingsModal
              visible={printSettingsVisible}
              continuousPrint={continuousPrintEnabled}
              smallLabel={smallLabel}
              printQuantity={printQuantity}
              quantitySingleUse={quantitySingleUse}
              printerSection={
                <PrinterConnectionSummary
                  savedPrinter={savedPrinter}
                  status={printerConnectionStatus}
                  lastError={printerLastError}
                  onManage={handleOpenPrinterSettings}
                />
              }
              onToggleContinuousPrint={setContinuousPrintEnabled}
              onToggleSmallLabel={setSmallLabel}
              onChangePrintQuantity={setPrintQuantity}
              onToggleQuantitySingleUse={setQuantitySingleUse}
              onDismiss={() => setPrintSettingsVisible(false)}
            />
            <StorePickerModal
              visible={storePickerVisible}
              presentation="sheet"
              stores={stores}
              selectedStoreCode={selectedStoreCode}
              title={t("common:labels.selectStore")}
              cancelLabel={t("common:actions.cancel")}
              onDismiss={() => setStorePickerVisible(false)}
              onSelectStore={handleSelectStore}
            />
          </>
        ) : null}
      </Portal>

      <CameraScanSheet
        visible={cameraVisible}
        title={t("camera.title")}
        subtitle={selectedStore?.storeName}
        mode={cameraScanMode}
        onModeChange={handleCameraScanModeChange}
        onDismiss={() => {
          cameraForegroundGenerationRef.current = null;
          updateCameraSheetSession(
            { type: "dismiss" },
            cameraScanModeRef.current,
          );
        }}
      >
        {lastCameraBarcode && queryFeedback.type === "empty" ? (
          <View style={styles.cameraFeedbackBar}>
            <Text variant="labelLarge">{t("messages.notFound")}</Text>
            <Text
              variant="bodySmall"
              style={styles.cameraTip}
              numberOfLines={1}
            >
              {lastCameraBarcode}
            </Text>
          </View>
        ) : null}
        {lastCameraBarcode && queryFeedback.type === "error" ? (
          <View style={styles.cameraFeedbackBar}>
            <Text variant="labelLarge">{queryFeedback.message}</Text>
            <Text
              variant="bodySmall"
              style={styles.cameraTip}
              numberOfLines={1}
            >
              {lastCameraBarcode}
            </Text>
          </View>
        ) : null}
        {lastCameraBarcode && detail ? (
          <View style={styles.cameraHitBar}>
            <View style={styles.cameraHitCopy}>
              <Text variant="labelLarge" numberOfLines={1}>
                {detail.productName || detail.productCode}
              </Text>
              <Text
                variant="bodySmall"
                style={styles.cameraTip}
                numberOfLines={1}
              >
                {lastCameraBarcode}
              </Text>
            </View>
            <Button
              compact
              mode="contained-tonal"
              onPress={() => {
                cameraForegroundGenerationRef.current = null;
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
        {renderCameraScanner()}
      </CameraScanSheet>

      <Snackbar
        visible={Boolean(snackbarMessage)}
        onDismiss={() => setSnackbarMessage("")}
        duration={2500}
      >
        {snackbarMessage}
      </Snackbar>

      {/* 查询期间卸载扫码输入框，避免 Android 禁用后丢失原生焦点而 JS 仍认为已聚焦。 */}
      {isFocused &&
      !scannerInputBlocked &&
      hidScanner.mode === "textInput" &&
      hidScanner.textInputProps ? (
        <TextInput style={styles.hiddenInput} {...hidScanner.textInputProps} />
      ) : null}
    </SafeAreaView>
  );
}

export default function ProductQueryScreen() {
  return <ProductQueryContent />;
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F4F6F8",
  },
  discountedSafeArea: {
    backgroundColor: "#FFE0B2",
  },
  queryBody: { flex: 1 },
  scrollArea: {
    flex: 1,
  },
  hiddenPreviousDetail: { opacity: 0 },
  scanPrintProgress: {
    ...StyleSheet.absoluteFillObject,
    alignItems: "center",
    justifyContent: "center",
    padding: 24,
    gap: 12,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  printDock: {
    flexShrink: 0,
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  printExtrasCard: {
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
    overflow: "hidden",
  },
  content: {
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 20,
    gap: 12,
  },
  posterFooterRow: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: "#E4E7EC",
  },
  cameraModeSelector: {
    marginHorizontal: 12,
    marginBottom: 6,
  },
  sampleBarcodeButton: {
    alignSelf: "flex-start",
    marginHorizontal: 12,
    marginBottom: 2,
  },
  inlineCameraPanel: {
    marginHorizontal: 12,
    marginBottom: 8,
    padding: 10,
    borderRadius: 12,
    backgroundColor: "#fff",
    gap: 8,
  },
  returnBar: {
    minHeight: 56,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    borderRadius: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#BFDBFE",
    backgroundColor: "#EFF6FF",
    paddingHorizontal: 10,
    paddingVertical: 7,
  },
  returnContext: {
    flex: 1,
    minWidth: 0,
    gap: 1,
  },
  returnContextTitle: {
    color: "#0958D9",
    fontWeight: "700",
  },
  returnContextMeta: {
    color: "#475467",
  },
  storeRow: {
    flexDirection: "row",
    alignItems: "center",
    minHeight: 36,
    paddingHorizontal: 4,
  },
  createProductBar: {
    alignItems: "flex-start",
    paddingTop: 8,
  },
  hqSyncStatus: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    borderRadius: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: "#BFDBFE",
    backgroundColor: "#EFF6FF",
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  hqSyncStatusWarning: {
    borderColor: "#FDE68A",
    backgroundColor: "#FFFBEB",
  },
  hqSyncStatusCopy: {
    flex: 1,
    gap: 1,
  },
  hqSyncStatusTitle: {
    color: "#1F2937",
    fontWeight: "700",
  },
  hqSyncStatusMeta: {
    color: "#667085",
  },
  hiddenInput: {
    position: "absolute",
    width: 1,
    height: 1,
    opacity: 0,
  },
  firstScreenSection: {
    gap: 8,
  },
  // 紧凑分段：价格 / 编码 两个页签只占 34pt，把首屏高度留给内容。
  editorTabs: {
    flexDirection: "row",
    minHeight: 34,
    borderWidth: 1,
    borderColor: "#D0D5DD",
    borderRadius: 8,
    overflow: "hidden",
    backgroundColor: "#FFFFFF",
  },
  editorTab: {
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    paddingVertical: 6,
  },
  editorTabActive: {
    backgroundColor: "#E8F1FF",
  },
  editorTabText: {
    color: "#475467",
    fontSize: 14,
    fontWeight: "600",
  },
  editorTabDot: {
    width: 7,
    height: 7,
    borderRadius: 4,
    marginLeft: 6,
    backgroundColor: "#F79009",
  },
  editorTabTextActive: {
    color: "#0958D9",
    fontWeight: "700",
  },
  secondarySection: {
    gap: 8,
    paddingTop: 4,
  },
  emptyBlock: {
    borderRadius: 12,
    backgroundColor: "#FFFFFF",
    padding: 14,
    gap: 6,
  },
  emptyTitle: {
    fontWeight: "700",
  },
  emptyText: {
    color: "#555",
  },
  discardReturnModal: {
    marginHorizontal: 18,
    borderRadius: 16,
    backgroundColor: "#FFFFFF",
    padding: 18,
    gap: 12,
  },
  discardReturnTitle: {
    color: "#111827",
    fontWeight: "700",
  },
  discardReturnDescription: {
    color: "#475467",
    lineHeight: 22,
  },
  discardReturnActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
  autoPricingModal: {
    marginHorizontal: 18,
    borderRadius: 16,
    backgroundColor: "#fff",
    padding: 16,
  },
  createHint: {
    color: "#667085",
  },
  autoPricingContent: {
    gap: 12,
  },
  autoPricingTitle: {
    fontWeight: "700",
    color: "#111827",
  },
  autoPricingDescription: {
    color: "#344054",
  },
  autoPricingPriceBlock: {
    gap: 6,
    borderRadius: 12,
    backgroundColor: "#F2F4F7",
    padding: 12,
  },
  autoPricingCurrentPrice: {
    color: "#475467",
  },
  autoPricingNextPrice: {
    color: "#1677FF",
    fontWeight: "700",
  },
  autoPricingHint: {
    color: "#667085",
  },
  autoPricingActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
  productTypeModal: {
    marginHorizontal: 18,
    borderRadius: 16,
    backgroundColor: "#fff",
    padding: 16,
  },
  textEditModal: {
    marginHorizontal: 18,
    borderRadius: 16,
    backgroundColor: "#fff",
    padding: 16,
  },
  textEditModalContent: {
    gap: 14,
  },
  textEditModalTitle: {
    fontWeight: "700",
    color: "#111827",
  },
  textEditInput: {
    borderWidth: 1,
    borderColor: "#CBD5E1",
    borderRadius: 8,
    paddingHorizontal: 12,
    paddingVertical: 8,
    fontSize: 16,
  },
  textEditModalFooter: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-end",
    gap: 8,
  },
  productTypeModalContent: {
    gap: 12,
  },
  productTypeModalTitle: {
    fontWeight: "700",
    color: "#111827",
  },
  productTypeOptions: {
    gap: 8,
  },
  productTypeOptionCard: {
    gap: 6,
  },
  productTypeOptionButton: {
    justifyContent: "center",
  },
  productTypeOptionDescription: {
    color: "#475467",
    lineHeight: 18,
    paddingHorizontal: 4,
  },
  productTypeFooter: {
    flexDirection: "row",
    justifyContent: "flex-end",
  },
  cameraModal: {
    marginHorizontal: 16,
    padding: 16,
    borderRadius: 16,
    backgroundColor: "#fff",
    gap: 12,
  },
  cameraHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  cameraFrame: {
    overflow: "hidden",
    borderRadius: 12,
    height: 320,
    backgroundColor: "#000",
  },
  permissionCard: {
    borderRadius: 12,
  },
  permissionCardContent: {
    gap: 12,
    paddingVertical: 8,
  },
  cameraView: {
    flex: 1,
  },
  cameraFeedbackBar: {
    gap: 2,
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
  cameraTip: {
    color: "#666",
  },
});
