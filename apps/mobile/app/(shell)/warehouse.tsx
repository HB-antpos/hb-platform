import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Image, Keyboard, Platform, Pressable, ScrollView, StyleSheet, TextInput as NativeTextInput, useWindowDimensions, View } from "react-native";
import { useFocusEffect, useIsFocused } from "@react-navigation/native";
import { CameraView, useCameraPermissions } from "expo-camera";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Card, IconButton, Menu, Modal, Portal, Searchbar, SegmentedButtons, Snackbar, Switch, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { LoadingOverlay } from "@/components/ui/LoadingOverlay";
import { CameraScanSheet } from "@/components/ui/CameraScanSheet";
import { NumericInputModal } from "@/components/product-maintenance/NumericInputModal";
import {
  RetailPriceNotificationPreview,
  WarehouseSuggestedDiscountField,
} from "@/components/price-updates/WarehousePriceNotificationFields";
import { buildPriceNotificationSaveMessage } from "@/modules/price-updates/price-notification";
import { hasVisibleTabRoute } from "@/modules/navigation/default-route";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { useCameraScan, type CameraScanMode } from "@/modules/scanner/use-camera-scan";
import {
  createCameraSheetSession,
  isCameraSheetSessionActive,
  reduceCameraSheetSession,
  type CameraSheetSession,
  type CameraSheetSessionEvent,
} from "@/modules/scanner/camera-sheet-session";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { reportApplicationLog } from "@/shared/logging/log-center-runtime";
import { useAuthStore } from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import {
  bindProductToLocation,
  createLocation,
  deleteLocation,
  getLocationDetail,
  getDefaultUnusedLocations,
  getWarehouseImageUploadSignature,
  getWarehouseLocationPrintPayload,
  getWarehouseProduct,
  getWarehouseProductPrintPayload,
  lookupLocations,
  lookupWarehouseProducts,
  patchWarehouseProduct,
  patchWarehouseProductWithNotification,
  setWarehouseProductLocation,
  unbindProductFromLocation,
  updateLocation,
  uploadFileToSignedUrl,
} from "@/modules/warehouse/api";
import { buildWarehouseLocationLabelPayload } from "@/modules/warehouse/location-label-print";
import type { WarehouseLocation, WarehouseLocationDetail, WarehouseLocationMutation, WarehouseProduct } from "@/modules/warehouse/types";
import {
  buildWarehouseLocationCode as buildLocationCode,
  getAvailableWarehouseLocationSlots,
  resolveAvailableWarehouseLocationParts,
  splitWarehouseLocationCode as splitLocationCode,
  type WarehouseLocationCodePart as LocationCodePart,
  type WarehouseLocationCodeParts,
} from "@/modules/warehouse/location-code-options";
import { canMaintainWarehouseLocations } from "@/modules/warehouse/location-permissions";
import {
  canBindMoreProductsToWarehouseLocation,
  getProductLocationCandidateAction,
  getProductLocationLookupAction,
  getProductLocationScanBindDecision,
  getWarehouseProductPdaLayout,
  getWarehouseProductSummaryVisualRows,
  getWarehouseProductSections,
  isProductLocationCandidateDisabled,
  type WarehouseProductSummaryField,
} from "@/modules/warehouse/pda-layout";
import { toggleWarehouseProductGradeSelection } from "@/modules/warehouse/product-grade";
import { buildWarehouseProductPatchRequest, isWarehouseStatusOnlyPatch, type WarehouseProductPatchField } from "@/modules/warehouse/product-patch";
import { printWarehouseLocationLabel, printWarehouseProductLabel } from "@/modules/printer/api";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { reviewAwareFetch } from "@/modules/ios-review/network";

type SegmentValue = "product" | "location";
type ScannerTarget = "product" | "location" | "bindProduct" | "productLocation";
type LocationVisualState = "bound" | "empty" | "lowStock";
type ProductStockState = "inStock" | "lowStock" | "outOfStock";
type NumericProductFieldKey =
  | "purchasePrice"
  | "retailPrice"
  | "domesticPrice"
  | "stockQuantity"
  | "middlePackageQuantity"
  | "packingQuantity"
  | "volume";

interface NumericInputModalState {
  field: NumericProductFieldKey;
  title: string;
  value: string;
  allowDecimal: boolean;
}

type ProductChoiceModalState = "grade" | "warehouseStatus" | null;
type BoundLocationProduct = WarehouseLocationDetail["products"][number];
interface PendingUnbindProductState {
  locationGuid: string;
  locationCode?: string | null;
  product: BoundLocationProduct;
}

interface PendingProductLocationUnbindState {
  productCode: string;
  locationCode?: string | null;
}

interface PendingRetailPriceSyncState {
  retailPrice: string;
}

interface WarehouseCameraScanSession {
  id: number;
  sheetGeneration: number;
  mode: CameraScanMode;
  target: ScannerTarget;
}

interface CameraResultSummary {
  title: string;
  detail: string;
}

const LOCATION_LETTER_OPTIONS = Array.from({ length: 26 }, (_, index) => String.fromCharCode(65 + index));
const LOCATION_NUMBER_OPTIONS = Array.from({ length: 100 }, (_, index) => String(index).padStart(2, "0"));
const PRODUCT_GRADE_OPTIONS = ["A", "B", "C", "D"];
const PRODUCT_GRADE_CONFIG: Record<string, { color: string }> = {
  A: { color: "#722ED1" },
  B: { color: "#1890FF" },
  C: { color: "#FA8C16" },
  D: { color: "#F5222D" },
};
const LOCATION_VISUALS: Record<LocationVisualState, { stripe: string; badgeBackground: string; badgeText: string; badgeBorder: string }> = {
  bound: {
    stripe: "#10B981",
    badgeBackground: "#DCFCE7",
    badgeText: "#166534",
    badgeBorder: "#BBF7D0",
  },
  empty: {
    stripe: "#CBD5E1",
    badgeBackground: "#F1F5F9",
    badgeText: "#475569",
    badgeBorder: "#E2E8F0",
  },
  lowStock: {
    stripe: "#EF4444",
    badgeBackground: "#FEE2E2",
    badgeText: "#B91C1C",
    badgeBorder: "#FECACA",
  },
};
const PRODUCT_STOCK_COLORS: Record<ProductStockState, { background: string; text: string; border: string }> = {
  inStock: { background: "#DCFCE7", text: "#166534", border: "#BBF7D0" },
  lowStock: { background: "#FEF3C7", text: "#B45309", border: "#FDE68A" },
  outOfStock: { background: "#FEE2E2", text: "#B91C1C", border: "#FECACA" },
};

function formatNumber(value?: number | null, digits = 2) {
  if (value == null || Number.isNaN(value)) {
    return "";
  }
  return value.toFixed(digits);
}

function formatDisplayValue(value?: string | number | null) {
  if (value == null || value === "") {
    return "--";
  }
  return String(value);
}

function formatPrice(value?: number | null) {
  if (value == null || Number.isNaN(value)) {
    return "--";
  }
  return value.toFixed(2);
}

function getBindInitialQuantityValue(item?: WarehouseProduct | null) {
  if (item?.stockQuantity != null && Number.isFinite(item.stockQuantity) && item.stockQuantity >= 0) {
    return item.stockQuantity.toString();
  }
  return "0";
}

function buildLocationCodeGroupKeyword(parts: WarehouseLocationCodeParts) {
  return `${parts.letter}-${parts.section}-${parts.shelf}-`;
}

function getRawErrorMessage(error: unknown) {
  const responseData = (error as { response?: { data?: unknown } } | undefined)?.response?.data;
  if (typeof responseData === "string") {
    return responseData;
  }
  if (responseData && typeof responseData === "object" && "message" in responseData) {
    return String((responseData as { message?: unknown }).message ?? "");
  }
  return error instanceof Error ? error.message : "";
}

function reportWarehouseFailure(
  action: string,
  error: unknown,
  properties?: Record<string, unknown>
) {
  const normalizedError = error instanceof Error ? error : new Error(String(error));
  reportApplicationLog({
    level: "Error",
    message: `仓库 PDA 操作失败: ${action}`,
    sourceType: "warehouse.pda",
    exceptionType: normalizedError.name,
    exceptionMessage: normalizedError.message,
    stackTrace: normalizedError.stack,
    properties,
  });
}

function getLocationVisualState(productCount: number) {
  if (productCount <= 0) {
    return "empty";
  }
  if (productCount === 1) {
    return "lowStock";
  }
  return "bound";
}

function getProductStockState(stockQuantity?: number | null) {
  if (stockQuantity == null) {
    return "inStock";
  }
  if (stockQuantity <= 0) {
    return "outOfStock";
  }
  if (stockQuantity <= 5) {
    return "lowStock";
  }
  return "inStock";
}

function InfoTile({
  label,
  value,
  emphasize = false,
  dense = false,
  singleColumn = false,
  valueLines,
  onPress,
}: {
  label: string;
  value: string;
  emphasize?: boolean;
  dense?: boolean;
  singleColumn?: boolean;
  valueLines?: number;
  onPress?: () => void;
}) {
  const content = (
    <>
      <Text variant="labelSmall" style={styles.infoTileLabel} numberOfLines={1}>
        {label}
      </Text>
      <Text variant={emphasize ? "titleMedium" : "bodyMedium"} style={styles.infoTileValue} numberOfLines={valueLines ?? (dense ? 1 : 2)}>
        {value}
      </Text>
    </>
  );

  if (onPress) {
    return (
      <Pressable onPress={onPress} style={[styles.infoTile, styles.infoTilePressable, dense ? styles.infoTileDense : null, singleColumn ? styles.infoTileSingle : null]}>
        {content}
      </Pressable>
    );
  }

  return (
    <View style={[styles.infoTile, dense ? styles.infoTileDense : null, singleColumn ? styles.infoTileSingle : null]}>
      {content}
    </View>
  );
}

function LocationPartMenu({
  label,
  value,
  options,
  visible,
  onDismiss,
  onOpen,
  onSelect,
}: {
  label: string;
  value: string;
  options: string[];
  visible: boolean;
  onDismiss: () => void;
  onOpen: () => void;
  onSelect: (value: string) => void;
}) {
  return (
    <View style={styles.locationPart}>
      <Text variant="labelSmall" style={styles.locationPartLabel}>
        {label}
      </Text>
      <Menu
        visible={visible}
        onDismiss={onDismiss}
        anchor={
          <Button mode="outlined" compact onPress={onOpen} contentStyle={styles.locationPartButton}>
            {value}
          </Button>
        }
      >
        <ScrollView style={styles.locationPartMenu}>
          {options.map((option) => (
            <Menu.Item
              key={`${label}-${option}`}
              title={option}
              onPress={() => {
                onSelect(option);
                onDismiss();
              }}
            />
          ))}
        </ScrollView>
      </Menu>
    </View>
  );
}

export default function WarehouseScreen() {
  const isFocused = useIsFocused();
  const router = useRouter();
  const { t, language } = useAppTranslation(["warehouse", "common"]);
  const { t: tPriceUpdates } = useAppTranslation("priceUpdates");
  const { width: windowWidth } = useWindowDimensions();
  const access = useAuthStore((state) => state.access);
  const deviceSession = useDeviceStore((state) => state.session);
  const hasStoredDeviceSession = Boolean(deviceSession?.hardwareId && deviceSession?.authCode);
  const photoCameraRef = useRef<CameraView | null>(null);
  const resumeHiddenScannerFocusTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const productLocationLookupRequestRef = useRef(0);
  const productLocationLookupKeywordRef = useRef("");
  const productLocationBindRequestRef = useRef(0);
  const productLocationBindingRef = useRef(false);
  const currentProductCodeRef = useRef<string | null>(null);
  const locationCodeGroupLookupRequestRef = useRef(0);
  const defaultLocationLookupRequestRef = useRef(0);
  const cameraScanSessionIdRef = useRef(0);
  const activeCameraScanSessionRef = useRef<WarehouseCameraScanSession | null>(null);
  const cameraSheetSessionRef = useRef<CameraSheetSession>(createCameraSheetSession("single"));
  const [photoPermission, requestPhotoPermission] = useCameraPermissions();
  const [segment, setSegment] = useState<SegmentValue>("product");
  const [scannerTarget, setScannerTarget] = useState<ScannerTarget>("product");
  const [cameraScanMode, setCameraScanMode] = useState<CameraScanMode>("single");
  const [snackbar, setSnackbar] = useState("");
  const [busy, setBusy] = useState(false);

  const [productKeyword, setProductKeyword] = useState("");
  const [productMatches, setProductMatches] = useState<WarehouseProduct[]>([]);
  const [product, setProduct] = useState<WarehouseProduct | null>(null);
  const [hasProductLookup, setHasProductLookup] = useState(false);
  const [productChoiceModal, setProductChoiceModal] = useState<ProductChoiceModalState>(null);
  const [productChoiceDraft, setProductChoiceDraft] = useState({ grade: "", warehouseIsActive: true });
  const [productLocationModalVisible, setProductLocationModalVisible] = useState(false);
  const [unbindLocationConfirmVisible, setUnbindLocationConfirmVisible] = useState(false);
  const [pendingProductLocationUnbind, setPendingProductLocationUnbind] = useState<PendingProductLocationUnbindState | null>(null);
  const [pendingStorageLocationBind, setPendingStorageLocationBind] = useState<WarehouseLocation | null>(null);
  const [productLocationBindFeedback, setProductLocationBindFeedback] = useState("");
  const [productForm, setProductForm] = useState({
    purchasePrice: "",
    retailPrice: "",
    domesticPrice: "",
    stockQuantity: "",
    middlePackageQuantity: "",
    packingQuantity: "",
    volume: "",
    grade: "",
    warehouseIsActive: true,
  });
  const [numericInputModal, setNumericInputModal] = useState<NumericInputModalState | null>(null);
  const [pendingRetailPriceSync, setPendingRetailPriceSync] = useState<PendingRetailPriceSyncState | null>(null);
  const [locationLookupKeyword, setLocationLookupKeyword] = useState("");
  const [locationMatches, setLocationMatches] = useState<WarehouseLocation[]>([]);

  const [locationKeyword, setLocationKeyword] = useState("");
  const [locationResults, setLocationResults] = useState<WarehouseLocation[]>([]);
  const [selectedLocation, setSelectedLocation] = useState<WarehouseLocationDetail | null>(null);
  const [hasLocationLookup, setHasLocationLookup] = useState(false);
  const [defaultLocationLoading, setDefaultLocationLoading] = useState(false);
  const [defaultLocationLoaded, setDefaultLocationLoaded] = useState(false);
  const [defaultLocationLoadFailed, setDefaultLocationLoadFailed] = useState(false);
  const [bindModalVisible, setBindModalVisible] = useState(false);
  const [bindProductKeyword, setBindProductKeyword] = useState("");
  const [bindProductMatches, setBindProductMatches] = useState<WarehouseProduct[]>([]);
  const [selectedBindProduct, setSelectedBindProduct] = useState<WarehouseProduct | null>(null);
  const [pendingUnbindProduct, setPendingUnbindProduct] = useState<PendingUnbindProductState | null>(null);
  const [bindInitialQuantity, setBindInitialQuantity] = useState("0");
  const [hasBindProductLookup, setHasBindProductLookup] = useState(false);
  const [locationModalVisible, setLocationModalVisible] = useState(false);
  const [locationModalState, setLocationModalState] = useState<WarehouseLocationMutation>({
    locationCode: "",
    locationBarcode: "",
    locationType: 1,
    status: 1,
  });
  const [locationCodeParts, setLocationCodeParts] = useState<WarehouseLocationCodeParts>({
    letter: "A",
    section: "01",
    shelf: "01",
    slot: "01",
  });
  const [locationCodeGroupLocations, setLocationCodeGroupLocations] = useState<WarehouseLocation[]>([]);
  const [locationPartMenus, setLocationPartMenus] = useState<Record<LocationCodePart, boolean>>({
    letter: false,
    section: false,
    shelf: false,
    slot: false,
  });
  const [editingLocationGuid, setEditingLocationGuid] = useState<string | null>(null);
  const [cameraSheetSession, setCameraSheetSession] = useState<CameraSheetSession>(() => createCameraSheetSession("single"));
  const [suspendedCameraScan, setSuspendedCameraScan] = useState<WarehouseCameraScanSession | null>(null);
  const [cameraResultSummary, setCameraResultSummary] = useState<CameraResultSummary | null>(null);
  const [photoVisible, setPhotoVisible] = useState(false);
  const navigationItems = useAppNavigationStore((state) => state.items);

  const hasVisibleWarehouseTab = hasVisibleTabRoute(
    navigationItems.map((item) => item.routeName),
    "warehouse"
  );
  const notAvailableText = t("messages.notAvailable");
  const shouldForcePdaLayout = Platform.OS === "ios" && !Platform.isPad;
  const productLayoutMode = getWarehouseProductPdaLayout(windowWidth, { forcePda: shouldForcePdaLayout });
  const productSectionConfig = getWarehouseProductSections(productLayoutMode);
  const isPdaProductLayout = productLayoutMode === "pda";
  const canMaintainLocations = canMaintainWarehouseLocations(access);
  const canUseWarehouseTools =
    hasStoredDeviceSession ||
    (
      hasVisibleWarehouseTab &&
      (
        access.canManageWarehouse ||
        access.hasPermission("Warehouse.ManageProducts") ||
        canMaintainLocations
      )
    );
  // 货柜管理已拆为工作台「仓库与采购」下的独立入口，本页只承载商品和货位管理。
  const hasWarehouseAccess = canUseWarehouseTools;
  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    })
  ), [language, t]);
  const locationSlotOptions = useMemo(
    () => getAvailableWarehouseLocationSlots(
      locationCodeGroupLocations,
      locationCodeParts,
      LOCATION_NUMBER_OPTIONS,
      editingLocationGuid
    ),
    [editingLocationGuid, locationCodeGroupLocations, locationCodeParts]
  );

  const parseNullableNumber = useCallback((value: string) => {
    if (!value.trim()) {
      return null;
    }
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) {
      throw new Error(t("messages.invalidNumber"));
    }
    return parsed;
  }, [t]);

  const parseInitialQuantity = useCallback((value: string) => {
    const trimmed = value.trim();
    if (!trimmed) {
      throw new Error(t("messages.invalidQuantity"));
    }

    const parsed = Number(trimmed);
    if (!Number.isFinite(parsed) || parsed < 0) {
      throw new Error(t("messages.invalidQuantity"));
    }

    return parsed;
  }, [getErrorMessage, t]);

  const isCameraTargetContextActive = useCallback((target: ScannerTarget) => {
    if (target === "product") {
      return segment === "product" && !productLocationModalVisible && !bindModalVisible;
    }
    if (target === "location") {
      return segment === "location" && !bindModalVisible;
    }
    if (target === "productLocation") {
      return segment === "product" && productLocationModalVisible;
    }
    return bindModalVisible;
  }, [bindModalVisible, productLocationModalVisible, segment]);

  const updateCameraSheetSession = useCallback((event: CameraSheetSessionEvent, mode: CameraScanMode) => {
    const next = reduceCameraSheetSession(cameraSheetSessionRef.current, event, mode);
    cameraSheetSessionRef.current = next;
    setCameraSheetSession(next);
    return next;
  }, []);

  const invalidateCameraScanSession = useCallback(() => {
    cameraScanSessionIdRef.current += 1;
    activeCameraScanSessionRef.current = null;
    setSuspendedCameraScan(null);
    setCameraResultSummary(null);
    updateCameraSheetSession({ type: "dismiss" }, cameraScanMode);
  }, [cameraScanMode, updateCameraSheetSession]);

  const beginCameraScanSession = useCallback((target: ScannerTarget, mode: CameraScanMode) => {
    const sheetSession = updateCameraSheetSession({ type: "open" }, mode);
    const session: WarehouseCameraScanSession = {
      id: cameraScanSessionIdRef.current + 1,
      sheetGeneration: sheetSession.generation,
      target,
      mode,
    };
    cameraScanSessionIdRef.current = session.id;
    activeCameraScanSessionRef.current = session;
    setSuspendedCameraScan(null);
    setCameraResultSummary(null);
    setScannerTarget(target);
    setCameraScanMode(mode);
  }, [updateCameraSheetSession]);

  const suspendCameraForResult = useCallback((session: WarehouseCameraScanSession) => {
    updateCameraSheetSession({ type: "capture", generation: session.sheetGeneration }, session.mode);
    setSuspendedCameraScan(session);
  }, [updateCameraSheetSession]);

  const resumeSuspendedCameraScan = useCallback((candidate = suspendedCameraScan) => {
    // 新目标、主动关闭或页面失焦已递增会话号；迟到结果不能改写新会话的可见状态。
    if (!candidate || candidate.id !== cameraScanSessionIdRef.current) {
      return;
    }
    const canResume = Boolean(
      candidate.mode === "continuous"
      && cameraScanMode === "continuous"
      && candidate.target === scannerTarget
      && isFocused
      && isCameraTargetContextActive(candidate.target)
    );
    updateCameraSheetSession(
      { type: "foreground-complete", focused: canResume, generation: candidate.sheetGeneration },
      candidate.mode,
    );
    setSuspendedCameraScan(null);
  }, [cameraScanMode, isCameraTargetContextActive, isFocused, scannerTarget, suspendedCameraScan, updateCameraSheetSession]);

  const cameraScanDisabled =
    !isFocused ||
    !cameraSheetSession.visible ||
    cameraScanMode === "continuous" &&
    scannerTarget === "productLocation" &&
    Boolean(pendingStorageLocationBind);
  const cameraScan = useCameraScan({
    disabled: cameraScanDisabled,
    ignoreWhileProcessing: cameraScanMode === "continuous",
    resetKey: [
      cameraScanMode,
      cameraSheetSession.generation,
      scannerTarget,
      segment,
      pendingStorageLocationBind ? "pending-location-bind" : "ready",
    ].join(":"),
    suppressRepeatsUntilChange: cameraScanMode === "continuous",
    onBarcode: async (barcode) => {
      const session = activeCameraScanSessionRef.current;
      if (
        !isFocused ||
        !session ||
        session.id !== cameraScanSessionIdRef.current ||
        !isCameraSheetSessionActive(cameraSheetSessionRef.current, session.sheetGeneration)
      ) {
        return;
      }
      // React Native Modal 覆盖 Portal；结果出现前必须卸载相机，不能只禁用扫码回调。
      suspendCameraForResult(session);
      if (session.target === "productLocation") {
        updateProductLocationLookupKeyword(barcode);
        const requiresForeground = await handleLookupLocationsForProductScan(barcode, true);
        if (!requiresForeground) resumeSuspendedCameraScan(session);
        return;
      }

      if (session.target === "location") {
        setLocationKeyword(barcode);
        const requiresForeground = await handleLookupLocationsByKeyword(barcode, true);
        if (!requiresForeground) resumeSuspendedCameraScan(session);
        return;
      }

      if (session.target === "bindProduct") {
        setBindProductKeyword(barcode);
        const requiresForeground = await handleLookupBindProducts(barcode, true);
        if (!requiresForeground) resumeSuspendedCameraScan(session);
        return;
      }

      setProductKeyword(barcode);
      const requiresForeground = await handleLookupProduct(barcode, true);
      if (!requiresForeground) resumeSuspendedCameraScan(session);
    },
  });
  const openCameraScanner = useCallback((target: ScannerTarget) => {
    beginCameraScanSession(target, cameraScanMode);
  }, [beginCameraScanSession, cameraScanMode]);
  const handleCameraScanModeChange = useCallback((mode: CameraScanMode) => {
    // 模式切换本身也是新会话，旧异步结果不能把相机重新打开。
    beginCameraScanSession(scannerTarget, mode);
  }, [beginCameraScanSession, scannerTarget]);
  const handleSegmentChange = useCallback((value: string) => {
    const nextSegment = value as SegmentValue;
    invalidateCameraScanSession();
    setSegment(nextSegment);
    setScannerTarget(nextSegment === "location" ? "location" : "product");
  }, [invalidateCameraScanSession]);
  const restoreDefaultScannerTarget = useCallback(() => {
    // 离开局部目标会废弃该会话，迟到的扫码查询绝不能覆盖页面上的结果或重新打开相机。
    invalidateCameraScanSession();
    setScannerTarget(segment === "location" ? "location" : "product");
  }, [invalidateCameraScanSession, segment]);
  const closeProductLocationModal = useCallback(() => {
    setProductLocationModalVisible(false);
    restoreDefaultScannerTarget();
  }, [restoreDefaultScannerTarget]);
  const closeBindProductModal = useCallback(() => {
    setBindModalVisible(false);
    restoreDefaultScannerTarget();
  }, [restoreDefaultScannerTarget]);

  const syncFormFromProduct = useCallback((item: WarehouseProduct | null) => {
    setProductForm({
      purchasePrice: formatNumber(item?.purchasePrice ?? item?.importPrice),
      retailPrice: formatNumber(item?.retailPrice ?? item?.oemPrice),
      domesticPrice: formatNumber(item?.domesticPrice),
      stockQuantity: item?.stockQuantity?.toString() ?? "",
      middlePackageQuantity: item?.middlePackageQuantity?.toString() ?? "",
      packingQuantity: item?.packingQuantity?.toString() ?? "",
      volume: formatNumber(item?.volume, 3),
      grade: item?.grade?.trim().toUpperCase() ?? "",
      warehouseIsActive: item?.warehouseIsActive ?? true,
    });
  }, []);

  const updateProductLocationLookupKeyword = useCallback((value: string) => {
    productLocationLookupKeywordRef.current = value;
    productLocationLookupRequestRef.current += 1;
    setLocationLookupKeyword(value);
    setLocationMatches([]);
    setBusy(false);
  }, []);

  const openNumericInputModal = useCallback((field: NumericProductFieldKey, title: string, allowDecimal: boolean) => {
    setNumericInputModal({
      field,
      title,
      value: productForm[field],
      allowDecimal,
    });
  }, [productForm]);

  const dismissNumericInputModal = useCallback(() => {
    setNumericInputModal(null);
  }, []);

  const openProductChoiceModal = useCallback((choice: Exclude<ProductChoiceModalState, null>) => {
    setProductChoiceDraft({
      grade: productForm.grade.trim().toUpperCase(),
      warehouseIsActive: productForm.warehouseIsActive,
    });
    setProductChoiceModal(choice);
  }, [productForm.grade, productForm.warehouseIsActive]);

  const applyProduct = useCallback((item: WarehouseProduct | null) => {
    currentProductCodeRef.current = item?.productCode ?? null;
    setProduct(item);
    syncFormFromProduct(item);
  }, [syncFormFromProduct]);

  const applyLocationDetail = useCallback((detail: WarehouseLocationDetail | null) => {
    setSelectedLocation(detail);
    setPendingUnbindProduct((current) => {
      if (!current) {
        return current;
      }
      if (!detail || current.locationGuid !== detail.locationGuid) {
        return null;
      }
      return current;
    });
    if (!detail) {
      return;
    }

    setLocationResults((current) => current.map((item) => (
      item.locationGuid === detail.locationGuid
        ? {
            ...item,
            locationCode: detail.locationCode,
            locationBarcode: detail.locationBarcode,
            locationType: detail.locationType,
            status: detail.status,
            productCount: detail.products.length,
          }
        : item
    )));
  }, []);

  const loadDefaultUnusedLocations = useCallback(async (options?: { clearSelection?: boolean }) => {
    const requestId = defaultLocationLookupRequestRef.current + 1;
    defaultLocationLookupRequestRef.current = requestId;
    setDefaultLocationLoading(true);
    setDefaultLocationLoadFailed(false);
    try {
      const items = await getDefaultUnusedLocations();
      if (requestId !== defaultLocationLookupRequestRef.current) {
        return;
      }

      // 默认列表是货位页的基础视图，和搜索结果用 hasLocationLookup 区分标题与空状态。
      setHasLocationLookup(false);
      setDefaultLocationLoaded(true);
      setDefaultLocationLoadFailed(false);
      setLocationResults(items);
      if (options?.clearSelection) {
        applyLocationDetail(null);
      }
    } catch (error) {
      if (requestId === defaultLocationLookupRequestRef.current) {
        // 失败也标记为已完成，避免 useEffect 立即再次触发默认加载形成无限重试。
        setDefaultLocationLoaded(true);
        setDefaultLocationLoadFailed(true);
        setSnackbar(getErrorMessage(error, "messages.locationLookupFailed"));
      }
    } finally {
      if (requestId === defaultLocationLookupRequestRef.current) {
        setDefaultLocationLoading(false);
      }
    }
  }, [applyLocationDetail, getErrorMessage]);

  const updateLocationKeyword = useCallback((value: string) => {
    setLocationKeyword(value);
    if (value.trim()) {
      // 输入变化即让旧货位请求失效，避免搜索结果覆盖后续默认列表。
      defaultLocationLookupRequestRef.current += 1;
      return;
    }

    void loadDefaultUnusedLocations({ clearSelection: true });
  }, [loadDefaultUnusedLocations]);

  const handleLookupProduct = useCallback(async (value?: string, fromCamera = false): Promise<boolean> => {
    const keyword = (value ?? productKeyword).trim();
    if (!keyword) {
      setSnackbar(t("messages.keywordRequired"));
      return true;
    }

    setBusy(true);
    setHasProductLookup(true);
    try {
      const items = await lookupWarehouseProducts(keyword);
      setProductMatches(items);
      if (items.length === 1) {
        const detail = await getWarehouseProduct(items[0].productCode);
        applyProduct(detail);
      } else {
        applyProduct(null);
      }
      if (fromCamera) {
        const first = items[0];
        setCameraResultSummary({
          title: first?.productName || first?.productCode || t("product.noResultsTitle"),
          detail: first ? `${first.itemNumber || first.productCode} · ${first.barcode || "--"}` : t("product.noResultsDescription"),
        });
      }
      return items.length > 1;
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.lookupFailed"));
      if (fromCamera) {
        setCameraResultSummary({ title: t("messages.lookupFailed"), detail: keyword });
      }
      return false;
    } finally {
      setBusy(false);
    }
  }, [applyProduct, productKeyword, t]);

  const handleSelectProduct = useCallback(async (productCode: string) => {
    setBusy(true);
    try {
      const detail = await getWarehouseProduct(productCode);
      applyProduct(detail);
      // 候选商品已由用户明确选择，仍有效的连续扫码会话才回到取景框。
      resumeSuspendedCameraScan();
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.lookupFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyProduct, resumeSuspendedCameraScan, t]);

  const handleSaveProductPatch = useCallback(async (
    patch: Partial<typeof productForm>,
    options?: { field?: WarehouseProductPatchField; syncStoreRetailPrices?: boolean }
  ) => {
    if (!product) {
      return;
    }

    const nextForm = { ...productForm, ...patch };
    const patchField = options?.field ?? (isWarehouseStatusOnlyPatch(patch) ? "warehouseIsActive" : undefined);
    setBusy(true);
    try {
      const { product: saved, notification } = await patchWarehouseProductWithNotification(
        product.productCode,
        buildWarehouseProductPatchRequest(nextForm, parseNullableNumber, {
          field: patchField,
          syncStoreRetailPrices: options?.syncStoreRetailPrices,
        })
      );
      applyProduct(saved);
      setProductChoiceModal(null);
      // 响应带 X-Price-Notification 头时说明本次保存涉及分店价格通知，改用通知结果文案。
      setSnackbar(buildPriceNotificationSaveMessage(notification, tPriceUpdates) ?? t("messages.saved"));
    } catch (error) {
      reportWarehouseFailure("保存商品字段", error, {
        productCode: product.productCode,
        patchField: patchField ?? "multiple",
      });
      syncFormFromProduct(product);
      setSnackbar(getErrorMessage(error, "messages.saveFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyProduct, parseNullableNumber, product, productForm, syncFormFromProduct, t, tPriceUpdates]);

  const handleConfirmNumericInputModal = useCallback(() => {
    if (!numericInputModal) {
      return;
    }

    if (numericInputModal.field === "retailPrice") {
      setPendingRetailPriceSync({ retailPrice: numericInputModal.value });
      dismissNumericInputModal();
      return;
    }

    void handleSaveProductPatch(
      { [numericInputModal.field]: numericInputModal.value },
      { field: numericInputModal.field }
    );
    dismissNumericInputModal();
  }, [dismissNumericInputModal, handleSaveProductPatch, numericInputModal]);

  const closeRetailPriceSyncConfirm = useCallback(() => {
    if (busy) {
      return;
    }
    setPendingRetailPriceSync(null);
  }, [busy]);

  const handleConfirmRetailPriceSync = useCallback(async (syncStoreRetailPrices: boolean) => {
    if (!pendingRetailPriceSync) {
      return;
    }

    try {
      await handleSaveProductPatch(
        { retailPrice: pendingRetailPriceSync.retailPrice },
        { field: "retailPrice", syncStoreRetailPrices }
      );
    } finally {
      setPendingRetailPriceSync(null);
    }
  }, [handleSaveProductPatch, pendingRetailPriceSync]);

  const openProductLocationModal = useCallback(() => {
    updateProductLocationLookupKeyword("");
    setProductLocationBindFeedback("");
    if (cameraScanMode === "continuous") {
      setScannerTarget("productLocation");
    }
    setProductLocationModalVisible(true);
  }, [cameraScanMode, updateProductLocationLookupKeyword]);

  const openUnbindLocationConfirm = useCallback(() => {
    if (!product?.locationGuid && !product?.locationCode) {
      setSnackbar(t("product.noLocation"));
      return;
    }
    setPendingProductLocationUnbind({
      productCode: product.productCode,
      locationCode: product.locationCode,
    });
    setUnbindLocationConfirmVisible(true);
  }, [product, t]);

  const closeUnbindLocationConfirm = useCallback(() => {
    if (busy) {
      return;
    }
    setPendingProductLocationUnbind(null);
    setUnbindLocationConfirmVisible(false);
  }, [busy]);

  const handleLookupLocationsForProductByKeyword = useCallback(async (value?: string) => {
    const keyword = (value ?? locationLookupKeyword).trim();
    if (!keyword) {
      setSnackbar(t("messages.keywordRequired"));
      return;
    }

    const requestId = productLocationLookupRequestRef.current + 1;
    productLocationLookupRequestRef.current = requestId;
    productLocationLookupKeywordRef.current = keyword;
    setBusy(true);
    try {
      const items = await lookupLocations(keyword);
      if (requestId === productLocationLookupRequestRef.current && productLocationLookupKeywordRef.current.trim() === keyword) {
        setLocationMatches(items);
      }
    } catch (error) {
      if (requestId === productLocationLookupRequestRef.current) {
        setSnackbar(getErrorMessage(error, "messages.locationLookupFailed"));
      }
    } finally {
      if (requestId === productLocationLookupRequestRef.current) {
        setBusy(false);
      }
    }
  }, [locationLookupKeyword, t]);

  const handleLookupLocationsForProduct = useCallback(async () => {
    await handleLookupLocationsForProductByKeyword();
  }, [handleLookupLocationsForProductByKeyword]);

  const handleBindLocation = useCallback(async (location?: WarehouseLocation | null) => {
    if (!product) {
      return;
    }
    if (productLocationBindingRef.current) {
      return;
    }
    if (location && !canBindMoreProductsToWarehouseLocation(location.locationType, location.productCount)) {
      const message = t("location.pickLocationSingleProductHint");
      setProductLocationBindFeedback(message);
      setSnackbar(message);
      return;
    }

    const requestId = productLocationBindRequestRef.current + 1;
    productLocationBindRequestRef.current = requestId;
    productLocationBindingRef.current = true;
    const productCode = product.productCode;
    setProductLocationBindFeedback("");
    setBusy(true);
    try {
      const saved = await setWarehouseProductLocation(productCode, location?.locationGuid ?? null);
      const refreshed = await getWarehouseProduct(productCode).catch(() => saved);
      if (requestId !== productLocationBindRequestRef.current) {
        return;
      }
      if (currentProductCodeRef.current && currentProductCodeRef.current !== productCode) {
        const staleMessage = t("messages.locationBindFailed");
        setProductLocationBindFeedback(staleMessage);
        setSnackbar(staleMessage);
        return;
      }
      applyProduct(refreshed);
      setLocationMatches([]);
      updateProductLocationLookupKeyword("");
      setPendingStorageLocationBind(null);
      closeProductLocationModal();
      setUnbindLocationConfirmVisible(false);
      setSnackbar(t("messages.locationSaved"));
    } catch (error) {
      reportWarehouseFailure("绑定货位到商品", error, {
        productCode,
        locationGuid: location?.locationGuid ?? null,
        locationCode: location?.locationCode ?? null,
      });
      if (requestId === productLocationBindRequestRef.current) {
        const message = getErrorMessage(error, "messages.locationBindFailed");
        setProductLocationBindFeedback(message);
        setSnackbar(message);
      }
    } finally {
      if (requestId === productLocationBindRequestRef.current) {
        productLocationBindingRef.current = false;
        setBusy(false);
      }
    }
  }, [applyProduct, closeProductLocationModal, product, t, updateProductLocationLookupKeyword]);

  const handleRequestBindLocation = useCallback(async (location: WarehouseLocation) => {
    const decision = getProductLocationScanBindDecision(location.locationType, location.productCount);
    if (decision === "block") {
      const message = t("location.pickLocationOccupiedHint");
      setProductLocationBindFeedback(message);
      setSnackbar(message);
      return;
    }
    if (decision === "confirm") {
      setPendingStorageLocationBind(location);
      return;
    }

    await handleBindLocation(location);
  }, [handleBindLocation, t]);

  const handlePressProductLocationCandidate = useCallback((location: WarehouseLocation) => {
    Keyboard.dismiss();
    void handleRequestBindLocation(location);
  }, [handleRequestBindLocation]);

  const closeStorageLocationBindConfirm = useCallback(() => {
    if (busy) {
      return;
    }
    setPendingStorageLocationBind(null);
    // 取消存货位确认后仍在原商品货位弹层内，恢复前会再次核验会话、目标和前台状态。
    resumeSuspendedCameraScan();
  }, [busy, resumeSuspendedCameraScan]);

  const handleLookupLocationsForProductScan = useCallback(async (barcode: string, fromCamera = false): Promise<boolean> => {
    // 连续相机下一帧开始前清掉旧结果，避免失败时仍显示上一笔货位摘要。
    if (fromCamera) {
      setCameraResultSummary(null);
    }
    if (productLocationBindingRef.current || pendingStorageLocationBind) {
      // 确认弹窗打开后不再接收后台扫码，避免替换用户正在确认的货位。
      return true;
    }
    const keyword = barcode.trim();
    if (!keyword) {
      setSnackbar(t("messages.keywordRequired"));
      return true;
    }

    const requestId = productLocationLookupRequestRef.current + 1;
    productLocationLookupRequestRef.current = requestId;
    productLocationLookupKeywordRef.current = keyword;
    setBusy(true);
    let autoBindLocation: WarehouseLocation | null = null;
    let action: ReturnType<typeof getProductLocationLookupAction> = "showResults";
    let firstMatch: WarehouseLocation | undefined;
    try {
      const items = await lookupLocations(keyword);
      if (requestId !== productLocationLookupRequestRef.current || productLocationLookupKeywordRef.current.trim() !== keyword) {
        return true;
      }

      setLocationMatches(items);
      firstMatch = items[0];
      action = getProductLocationLookupAction({
        source: "scan",
        matchCount: items.length,
        locationType: firstMatch?.locationType,
        productCount: firstMatch?.productCount,
      });
      if (action !== "showResults" && firstMatch) {
        autoBindLocation = firstMatch;
      }
      if (fromCamera) {
        setCameraResultSummary({
          title: firstMatch?.locationCode || t("product.noLocation"),
          detail: firstMatch?.locationBarcode || keyword,
        });
      }
    } catch (error) {
      if (requestId === productLocationLookupRequestRef.current) {
        const message = getErrorMessage(error, "messages.locationLookupFailed");
        setSnackbar(message);
        if (fromCamera) {
          setCameraResultSummary({ title: message, detail: keyword });
        }
      }
    } finally {
      if (requestId === productLocationLookupRequestRef.current) {
        setBusy(false);
      }
    }
    if (autoBindLocation && requestId === productLocationLookupRequestRef.current) {
      await handleRequestBindLocation(autoBindLocation);
      // 自动绑定会离开商品货位上下文；存货位确认要给前景确认框让位。
      return action === "bind" || action === "confirm";
    }
    // 多候选必须让用户选择；空结果和配货位已占用则在相机内显示真实摘要并继续识别。
    return action === "showResults" && Boolean(firstMatch);
  }, [getErrorMessage, handleRequestBindLocation, pendingStorageLocationBind, t]);

  const handleConfirmUnbindProductLocation = useCallback(async () => {
    if (!pendingProductLocationUnbind || busy) {
      return;
    }

    setBusy(true);
    try {
      const saved = await setWarehouseProductLocation(pendingProductLocationUnbind.productCode, null);
      if (product?.productCode === pendingProductLocationUnbind.productCode) {
        applyProduct(saved);
      }
      setLocationMatches([]);
      updateProductLocationLookupKeyword("");
      setPendingProductLocationUnbind(null);
      setUnbindLocationConfirmVisible(false);
      setSnackbar(t("messages.locationSaved"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.saveFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyProduct, busy, pendingProductLocationUnbind, product?.productCode, t, updateProductLocationLookupKeyword]);

  const handlePrintProduct = useCallback(async () => {
    if (!product) {
      return;
    }
    try {
      const payload = await getWarehouseProductPrintPayload(product.productCode);
      await printWarehouseProductLabel(payload);
      setSnackbar(t("messages.printSent"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.printFailed"));
    }
  }, [product, t]);

  const handlePrintLocation = useCallback(async () => {
    if (!product) {
      return;
    }
    try {
      const payload = await getWarehouseLocationPrintPayload(product.productCode);
      await printWarehouseLocationLabel(payload);
      setSnackbar(t("messages.printSent"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.printFailed"));
    }
  }, [product, t]);

  const handlePrintSelectedLocation = useCallback(async () => {
    if (!selectedLocation) {
      return;
    }
    try {
      await printWarehouseLocationLabel(buildWarehouseLocationLabelPayload(selectedLocation));
      setSnackbar(t("messages.printSent"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.printFailed"));
    }
  }, [selectedLocation, t]);

  const handlePrintLocationListItem = useCallback(async (item: WarehouseLocation) => {
    try {
      // 未使用货位列表可直接打印标签，避免必须先进入货位详情。
      await printWarehouseLocationLabel(buildWarehouseLocationLabelPayload(item));
      setSnackbar(t("messages.printSent"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.printFailed"));
    }
  }, [t]);

  const handleCapturePhoto = useCallback(async () => {
    if (!product || !photoCameraRef.current) {
      return;
    }

    setBusy(true);
    try {
      const picture = await photoCameraRef.current.takePictureAsync({ quality: 0.7 });
      if (!picture?.uri) {
        throw new Error(t("messages.uploadFailed"));
      }
      setSnackbar(t("messages.photoCaptured"));
      let productImage = picture.uri;
      if (!isIosReviewSessionActive()) {
        const fileName = picture.uri.split("/").pop() || `${product.productCode}.jpg`;
        const fileBlob = await reviewAwareFetch(picture.uri).then((response) => response.blob());
        const signature = await getWarehouseImageUploadSignature(product.productCode, {
          fileName,
          contentType: "image/jpeg",
          fileSize: fileBlob.size,
        });
        await uploadFileToSignedUrl(picture.uri, signature);
        productImage = signature.url.split("?")[0];
      }
      // 审核模式把本地 URI 写入内存商品，跨页面立即可见且不触发外部上传。
      const saved = await patchWarehouseProduct(product.productCode, { productImage });
      applyProduct(saved);
      setPhotoVisible(false);
      setSnackbar(t("messages.saved"));
    } catch (error) {
      reportWarehouseFailure("上传商品图片", error, {
        productCode: product.productCode,
      });
      setSnackbar(getErrorMessage(error, "messages.uploadFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyProduct, product, t]);

  const handleLookupLocationsByKeyword = useCallback(async (value?: string, fromCamera = false): Promise<boolean> => {
    const keyword = (value ?? locationKeyword).trim();
    if (!keyword) {
      await loadDefaultUnusedLocations({ clearSelection: true });
      return false;
    }

    setBusy(true);
    setHasLocationLookup(true);
    const requestId = defaultLocationLookupRequestRef.current + 1;
    defaultLocationLookupRequestRef.current = requestId;
    try {
      const items = await lookupLocations(keyword);
      if (requestId !== defaultLocationLookupRequestRef.current) {
        return false;
      }
      setLocationResults(items);
      if (items.length === 1) {
        const detail = await getLocationDetail(items[0].locationGuid);
        if (requestId !== defaultLocationLookupRequestRef.current) {
          return false;
        }
        applyLocationDetail(detail);
      } else {
        applyLocationDetail(null);
      }
      if (fromCamera) {
        const first = items[0];
        setCameraResultSummary({
          title: first?.locationCode || t("location.noResultsTitle"),
          detail: first?.locationBarcode || t("location.noResultsDescription"),
        });
      }
      return items.length > 1;
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.locationLookupFailed"));
      if (fromCamera) {
        setCameraResultSummary({ title: t("messages.locationLookupFailed"), detail: keyword });
      }
      return false;
    } finally {
      setBusy(false);
    }
  }, [applyLocationDetail, loadDefaultUnusedLocations, locationKeyword, t]);

  const handleLookupLocations = useCallback(async () => {
    await handleLookupLocationsByKeyword();
  }, [handleLookupLocationsByKeyword]);

  const hidScanner = useHidBarcodeScanner({
    enabled: isFocused,
    onScan: async (barcode) => {
      if (unbindLocationConfirmVisible || pendingProductLocationUnbind) {
        return;
      }
      if (pendingStorageLocationBind) {
        // 货位绑定确认弹窗打开时，扫码枪也不能替换用户正在确认的货位。
        return;
      }

      if (bindModalVisible) {
        setBindProductKeyword(barcode);
        await handleLookupBindProducts(barcode);
        return;
      }

      if (productLocationModalVisible) {
        updateProductLocationLookupKeyword(barcode);
        await handleLookupLocationsForProductScan(barcode);
        return;
      }

      if (segment === "location") {
        setLocationKeyword(barcode);
        await handleLookupLocationsByKeyword(barcode);
        return;
      }

      setProductKeyword(barcode);
      await handleLookupProduct(barcode);
    },
  });

  useEffect(() => {
    if (!isFocused) {
      // 页面失焦后所有相机回调均视为过期；返回页面需要用户重新明确开启扫码。
      invalidateCameraScanSession();
    }
  }, [invalidateCameraScanSession, isFocused]);

  const pauseHiddenScannerFocus = useCallback(() => {
    if (resumeHiddenScannerFocusTimerRef.current) {
      clearTimeout(resumeHiddenScannerFocusTimerRef.current);
      resumeHiddenScannerFocusTimerRef.current = null;
    }
    hidScanner.pauseHiddenInputFocus();
  }, [hidScanner.pauseHiddenInputFocus]);

  const resumeHiddenScannerFocusLater = useCallback(() => {
    if (resumeHiddenScannerFocusTimerRef.current) {
      clearTimeout(resumeHiddenScannerFocusTimerRef.current);
    }

    // iOS 粘贴菜单需要短暂保留可见输入框焦点，避免隐藏扫码输入立即抢回去。
    resumeHiddenScannerFocusTimerRef.current = setTimeout(() => {
      resumeHiddenScannerFocusTimerRef.current = null;
      hidScanner.resumeHiddenInputFocus();
    }, 250);
  }, [hidScanner.resumeHiddenInputFocus]);

  useFocusEffect(
    useCallback(() => {
      hidScanner.resumeHiddenInputFocus();
    }, [hidScanner.resumeHiddenInputFocus])
  );

  useEffect(() => {
    hidScanner.resumeHiddenInputFocus();
  }, [hidScanner.resumeHiddenInputFocus, segment]);

  useEffect(() => {
    if (segment !== "location" || hasLocationLookup || locationKeyword.trim() || defaultLocationLoaded || defaultLocationLoading) {
      return;
    }

    void loadDefaultUnusedLocations();
  }, [defaultLocationLoaded, defaultLocationLoading, hasLocationLookup, loadDefaultUnusedLocations, locationKeyword, segment]);

  useEffect(() => () => {
    if (resumeHiddenScannerFocusTimerRef.current) {
      clearTimeout(resumeHiddenScannerFocusTimerRef.current);
    }
  }, []);

  const handleSelectLocation = useCallback(async (locationGuid: string) => {
    setBusy(true);
    try {
      const detail = await getLocationDetail(locationGuid);
      applyLocationDetail(detail);
      // 多货位结果被选择后才恢复连续扫码；直接命中会保持详情可见。
      resumeSuspendedCameraScan();
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.locationLookupFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyLocationDetail, resumeSuspendedCameraScan, t]);

  const resolveLocationCodeGroupParts = useCallback(async (
    parts: WarehouseLocationCodeParts,
    excludeLocationGuid?: string | null
  ) => {
    const requestId = locationCodeGroupLookupRequestRef.current + 1;
    locationCodeGroupLookupRequestRef.current = requestId;
    const items = await lookupLocations(buildLocationCodeGroupKeyword(parts));
    if (requestId !== locationCodeGroupLookupRequestRef.current) {
      return null;
    }

    setLocationCodeGroupLocations(items);
    return resolveAvailableWarehouseLocationParts(items, parts, LOCATION_NUMBER_OPTIONS, excludeLocationGuid);
  }, []);

  const applyLocationCodeParts = useCallback((parts: WarehouseLocationCodeParts) => {
    setLocationCodeParts(parts);
    setLocationModalState((modal) => ({ ...modal, locationCode: buildLocationCode(parts) }));
  }, []);

  const openCreateLocation = useCallback(async () => {
    const initialParts = splitLocationCode(null);
    let nextParts = initialParts;
    setBusy(true);
    try {
      nextParts = await resolveLocationCodeGroupParts(initialParts, null) ?? initialParts;
    } catch (error) {
      setLocationCodeGroupLocations([]);
      setSnackbar(getErrorMessage(error, "messages.locationLookupFailed"));
    } finally {
      setBusy(false);
    }

    setEditingLocationGuid(null);
    setLocationCodeParts(nextParts);
    setLocationModalState({ locationCode: buildLocationCode(nextParts), locationBarcode: "", locationType: 1, status: 1 });
    setLocationModalVisible(true);
  }, [getErrorMessage, resolveLocationCodeGroupParts]);

  const openEditLocation = useCallback(async (detail: WarehouseLocationDetail) => {
    const nextParts = splitLocationCode(detail.locationCode);
    let resolvedParts = nextParts;
    setBusy(true);
    try {
      resolvedParts = await resolveLocationCodeGroupParts(nextParts, detail.locationGuid) ?? nextParts;
    } catch (error) {
      setLocationCodeGroupLocations([]);
      setSnackbar(getErrorMessage(error, "messages.locationLookupFailed"));
    } finally {
      setBusy(false);
    }

    setEditingLocationGuid(detail.locationGuid);
    setLocationCodeParts(resolvedParts);
    setLocationModalState({
      locationCode: buildLocationCode(resolvedParts),
      locationBarcode: detail.locationBarcode ?? "",
      locationType: detail.locationType ?? 1,
      status: detail.status ?? 1,
    });
    setLocationModalVisible(true);
  }, [getErrorMessage, resolveLocationCodeGroupParts]);

  const handleSaveLocation = useCallback(async () => {
    const locationCode = buildLocationCode(locationCodeParts);
    if (!locationCode.trim()) {
      return;
    }
    if (!editingLocationGuid && !locationSlotOptions.length) {
      setSnackbar(t("location.slotGroupFull"));
      return;
    }

    setBusy(true);
    try {
      const isCreatingLocation = !editingLocationGuid;
      if (!editingLocationGuid) {
        const matchedLocations = await lookupLocations(locationCode);
        const duplicate = matchedLocations.some((item) => (item.locationCode ?? "").trim().toUpperCase() === locationCode);
        if (duplicate) {
          setSnackbar(t("location.duplicateCode", { code: locationCode }));
          return;
        }
      }

      const payload = {
        ...locationModalState,
        locationCode,
        locationBarcode: editingLocationGuid ? locationModalState.locationBarcode : null,
      };
      const detail = editingLocationGuid
        ? await updateLocation(editingLocationGuid, payload)
        : await createLocation(payload);
      applyLocationDetail(detail);
      setLocationModalVisible(false);
      setSnackbar(t("messages.saved"));
      setDefaultLocationLoaded(false);
      if (isCreatingLocation) {
        try {
          // 新建货位后默认打印标签，打印失败不回滚已创建的货位。
          await printWarehouseLocationLabel(buildWarehouseLocationLabelPayload(detail));
          setSnackbar(t("messages.printSent"));
        } catch (printError) {
          setSnackbar(getErrorMessage(printError, "messages.printFailed"));
        }
      }
      if (locationKeyword.trim()) {
        await handleLookupLocations();
      } else {
        await loadDefaultUnusedLocations();
      }
    } catch (error) {
      reportWarehouseFailure("保存货位", error, {
        locationCode,
        editingLocationGuid: editingLocationGuid ?? null,
      });
      const status = (error as { response?: { status?: number } } | undefined)?.response?.status;
      const normalizedMessage = getRawErrorMessage(error).trim().toLowerCase();
      const looksLikeDuplicate = status === 409
        || normalizedMessage.includes("duplicate")
        || normalizedMessage.includes("exists")
        || normalizedMessage.includes("unique")
        || normalizedMessage.includes("重复")
        || normalizedMessage.includes("已存在");
      setSnackbar(looksLikeDuplicate ? t("location.duplicateCode", { code: locationCode }) : getErrorMessage(error, "messages.saveFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyLocationDetail, editingLocationGuid, getErrorMessage, handleLookupLocations, loadDefaultUnusedLocations, locationCodeParts, locationKeyword, locationModalState, locationSlotOptions.length, t]);

  const handleDeleteLocation = useCallback(async () => {
    if (!selectedLocation) {
      return;
    }
    setBusy(true);
    try {
      await deleteLocation(selectedLocation.locationGuid);
      applyLocationDetail(null);
      setLocationResults((current) => current.filter((item) => item.locationGuid !== selectedLocation.locationGuid));
      setDefaultLocationLoaded(false);
      closeBindProductModal();
      setSnackbar(t("messages.saved"));
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.locationDeleteFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyLocationDetail, closeBindProductModal, selectedLocation, t]);

  const openBindProductModal = useCallback(() => {
    if (!selectedLocation) {
      return;
    }
    if (!canBindMoreProductsToWarehouseLocation(selectedLocation.locationType, selectedLocation.products.length)) {
      setSnackbar(t("location.pickLocationSingleProductHint"));
      return;
    }
    setBindProductKeyword("");
    setBindProductMatches([]);
    setSelectedBindProduct(null);
    setBindInitialQuantity("0");
    setHasBindProductLookup(false);
    if (cameraScanMode === "continuous") {
      setScannerTarget("bindProduct");
    }
    setBindModalVisible(true);
  }, [cameraScanMode, selectedLocation, t]);

  const handleLookupBindProducts = useCallback(async (value?: string, fromCamera = false): Promise<boolean> => {
    const keyword = (value ?? bindProductKeyword).trim();
    if (!keyword) {
      setSnackbar(t("messages.keywordRequired"));
      return true;
    }

    setBusy(true);
    setHasBindProductLookup(true);
    try {
      const items = await lookupWarehouseProducts(keyword);
      setBindProductMatches(items);
      if (items.length === 1) {
        setSelectedBindProduct(items[0]);
        setBindInitialQuantity(getBindInitialQuantityValue(items[0]));
      } else {
        setSelectedBindProduct(null);
        setBindInitialQuantity("0");
      }
      if (fromCamera) {
        const first = items[0];
        setCameraResultSummary({
          title: first?.productName || first?.productCode || t("location.bindModalNoResults"),
          detail: first ? `${first.itemNumber || first.productCode} · ${first.barcode || "--"}` : keyword,
        });
      }
      return items.length > 0;
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.lookupFailed"));
      if (fromCamera) {
        setCameraResultSummary({ title: t("messages.lookupFailed"), detail: keyword });
      }
      return false;
    } finally {
      setBusy(false);
    }
  }, [bindProductKeyword, t]);

  const handleBindProductToLocation = useCallback(async () => {
    const productIdentifier = selectedBindProduct?.productCode ?? bindProductKeyword.trim();
    if (!selectedLocation || !productIdentifier) {
      setSnackbar(t("location.bindModalSelectRequired"));
      return;
    }
    if (!canBindMoreProductsToWarehouseLocation(selectedLocation.locationType, selectedLocation.products.length)) {
      closeBindProductModal();
      setSnackbar(t("location.pickLocationSingleProductHint"));
      return;
    }

    let initialQuantity: number;
    try {
      initialQuantity = parseInitialQuantity(bindInitialQuantity);
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.invalidQuantity"));
      return;
    }

    setBusy(true);
    try {
      const detail = await bindProductToLocation(selectedLocation.locationGuid, {
        productIdentifier,
        initialQuantity,
      });
      applyLocationDetail(detail);
      closeBindProductModal();
      setBindProductKeyword("");
      setBindProductMatches([]);
      setSelectedBindProduct(null);
      setBindInitialQuantity("0");
      setHasBindProductLookup(false);
      setDefaultLocationLoaded(false);
      if (!hasLocationLookup && !locationKeyword.trim()) {
        await loadDefaultUnusedLocations();
      }
      setSnackbar(t("messages.locationBound"));
    } catch (error) {
      reportWarehouseFailure("绑定商品到货位", error, {
        locationGuid: selectedLocation.locationGuid,
        locationCode: selectedLocation.locationCode,
        productIdentifier,
      });
      const responseData = (error as { response?: { data?: unknown } } | undefined)?.response?.data;
      const rawMessage = typeof responseData === "string"
        ? responseData
        : responseData && typeof responseData === "object" && "message" in responseData
          ? String((responseData as { message?: unknown }).message ?? "")
          : error instanceof Error
            ? error.message
            : "";
      const normalizedMessage = rawMessage.trim().toLowerCase();
      const looksLikeQuantityContractIssue = normalizedMessage.includes("initialquantity")
        || normalizedMessage.includes("initial quantity")
        || normalizedMessage.includes("quantity")
        || normalizedMessage.includes("json")
        || normalizedMessage.includes("convert")
        || normalizedMessage.includes("deserialize")
        || normalizedMessage.includes("validation");
      setSnackbar(looksLikeQuantityContractIssue ? t("messages.quantityBackendRequired") : rawMessage || t("messages.saveFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyLocationDetail, bindInitialQuantity, bindProductKeyword, closeBindProductModal, hasLocationLookup, loadDefaultUnusedLocations, locationKeyword, parseInitialQuantity, selectedBindProduct, selectedLocation, t]);

  const openUnbindProductConfirm = useCallback((item: BoundLocationProduct) => {
    if (!selectedLocation || !item.productCode) {
      return;
    }
    setPendingUnbindProduct({
      locationGuid: selectedLocation.locationGuid,
      locationCode: selectedLocation.locationCode,
      product: item,
    });
  }, [selectedLocation]);

  const handleConfirmUnbindProduct = useCallback(async () => {
    if (!pendingUnbindProduct?.product.productCode || busy) {
      return;
    }

    setBusy(true);
    try {
      const detail = await unbindProductFromLocation(pendingUnbindProduct.locationGuid, pendingUnbindProduct.product.productCode);
      applyLocationDetail(detail);
      setPendingUnbindProduct(null);
      setDefaultLocationLoaded(false);
      if (!hasLocationLookup && !locationKeyword.trim()) {
        await loadDefaultUnusedLocations();
      }
      setSnackbar(t("messages.locationUnbound"));
    } catch (error) {
      reportWarehouseFailure("从货位解绑商品", error, {
        locationGuid: pendingUnbindProduct.locationGuid,
        locationCode: pendingUnbindProduct.locationCode ?? null,
        productCode: pendingUnbindProduct.product.productCode,
      });
      setSnackbar(getErrorMessage(error, "messages.saveFailed"));
    } finally {
      setBusy(false);
    }
  }, [applyLocationDetail, busy, hasLocationLookup, loadDefaultUnusedLocations, locationKeyword, pendingUnbindProduct, t]);

  const productTypeText = useMemo(() => {
    if (!product) {
      return "";
    }
    return product.productTypeLabel || product.productType?.toString() || "";
  }, [product]);

  const savedProductGrade = product?.grade?.trim().toUpperCase() ?? "";
  const editableProductGrade = productForm.grade.trim().toUpperCase();
  const displayProductGrade = isPdaProductLayout ? savedProductGrade : editableProductGrade;
  const displayWarehouseIsActive = isPdaProductLayout ? product?.warehouseIsActive ?? true : productForm.warehouseIsActive;
  // 商品已有货位时，“绑定货位”实际会替换旧货位，因此文案显示为“更新货位”。
  const hasProductLocation = Boolean(product?.locationGuid || product?.locationCode);
  const productGradeColor = displayProductGrade
    ? PRODUCT_GRADE_CONFIG[displayProductGrade]?.color ?? "#98A2B3"
    : "#98A2B3";
  const productStockState = getProductStockState(product?.stockQuantity);
  const selectedLocationProductCount = selectedLocation?.products.length ?? 0;
  const selectedLocationCanBindMore = selectedLocation
    ? canBindMoreProductsToWarehouseLocation(selectedLocation.locationType, selectedLocationProductCount)
    : false;
  const selectedLocationVisualState = getLocationVisualState(selectedLocationProductCount);
  const selectedLocationVisualColors = LOCATION_VISUALS[selectedLocationVisualState];
  const productStockColors = PRODUCT_STOCK_COLORS[productStockState];
  const setLocationPart = useCallback((part: LocationCodePart, value: string) => {
    setLocationCodeParts((current) => {
      const next = { ...current, [part]: value };
      setLocationModalState((modal) => ({ ...modal, locationCode: buildLocationCode(next) }));
      if (part !== "slot") {
        void resolveLocationCodeGroupParts(next, editingLocationGuid)
          .then((resolvedParts) => {
            if (resolvedParts) {
              applyLocationCodeParts(resolvedParts);
            }
          })
          .catch((error) => {
            setLocationCodeGroupLocations([]);
            setSnackbar(getErrorMessage(error, "messages.locationLookupFailed"));
          });
      }
      return next;
    });
  }, [applyLocationCodeParts, editingLocationGuid, getErrorMessage, resolveLocationCodeGroupParts]);

  const setLocationPartMenuVisible = useCallback((part: LocationCodePart, visible: boolean) => {
    setLocationPartMenus((current) => ({ ...current, [part]: visible }));
  }, []);

  const getLocationTypeLabel = useCallback((locationType?: number | null) => (
    locationType === 2 ? t("location.typeStorage") : t("location.typePick")
  ), [t]);

  const getLocationStatusLabel = useCallback((state: LocationVisualState) => {
    if (state === "empty") {
      return t("location.statusEmpty");
    }
    if (state === "lowStock") {
      return t("location.statusLowStock");
    }
    return t("location.statusBound");
  }, [t]);

  const getProductStockLabel = useCallback((state: ProductStockState) => {
    if (state === "outOfStock") {
      return t("product.stockStates.outOfStock");
    }
    if (state === "lowStock") {
      return t("product.stockStates.lowStock");
    }
    return t("product.stockStates.inStock");
  }, [t]);
  const productSummaryVisualRows = getWarehouseProductSummaryVisualRows(productLayoutMode);
  const getProductSummaryFieldConfig = useCallback((field: WarehouseProductSummaryField) => {
    switch (field) {
      case "itemNumber":
        return {
          label: t("product.fields.itemNumber"),
          value: formatDisplayValue(product?.itemNumber || product?.productCode),
          valueLines: 2,
        };
      case "barcode":
        return {
          label: t("product.fields.barcode"),
          value: formatDisplayValue(product?.barcode),
          valueLines: 2,
        };
      case "stockQuantity":
        return {
          label: t("product.fields.stockQuantity"),
          value: formatDisplayValue(product?.stockQuantity),
          emphasize: true,
          onPress: () => openNumericInputModal("stockQuantity", t("product.fields.stockQuantity"), false),
        };
      case "location":
        return {
          label: t("product.fields.location"),
          value: formatDisplayValue(product?.locationCode || t("product.noLocation")),
          emphasize: true,
          valueLines: 2,
          onPress: openProductLocationModal,
        };
      case "domesticPrice":
        return {
          label: t("product.fields.domesticPrice"),
          value: formatPrice(product?.domesticPrice),
          onPress: () => openNumericInputModal("domesticPrice", t("product.fields.domesticPrice"), true),
        };
      case "purchaseImportPrice":
        return {
          label: t("product.fields.purchaseImportPrice"),
          value: formatPrice(product?.purchasePrice ?? product?.importPrice),
          onPress: () => openNumericInputModal("purchasePrice", t("product.fields.purchaseImportPrice"), true),
        };
      case "retailOemPrice":
        return {
          label: t("product.fields.retailOemPrice"),
          value: formatPrice(product?.retailPrice ?? product?.oemPrice),
          onPress: () => openNumericInputModal("retailPrice", t("product.fields.retailOemPrice"), true),
        };
      case "volume":
        return {
          label: t("product.fields.volume"),
          value: formatDisplayValue(product?.volume),
          onPress: () => openNumericInputModal("volume", t("product.fields.volume"), true),
        };
      case "middlePackageQuantity":
        return {
          label: t("product.fields.middlePackageQuantity"),
          value: formatDisplayValue(product?.middlePackageQuantity),
          onPress: () => openNumericInputModal("middlePackageQuantity", t("product.fields.middlePackageQuantity"), false),
        };
      case "packingQuantity":
        return {
          label: t("product.fields.packingQuantity"),
          value: formatDisplayValue(product?.packingQuantity),
          onPress: () => openNumericInputModal("packingQuantity", t("product.fields.packingQuantity"), false),
        };
      case "grade":
        return {
          label: t("product.fields.grade"),
          value: displayProductGrade || "--",
          onPress: () => openProductChoiceModal("grade"),
        };
      case "warehouseStatus":
        return {
          label: t("product.fields.warehouseStatus"),
          value: displayWarehouseIsActive ? t("product.onShelf") : t("product.offShelf"),
          onPress: () => openProductChoiceModal("warehouseStatus"),
        };
    }
  }, [
    displayWarehouseIsActive,
    displayProductGrade,
    openNumericInputModal,
    openProductChoiceModal,
    openProductLocationModal,
    product,
    t,
  ]);
  const renderCameraScanner = () => (
    <>
      {cameraScan.permission?.granted ? (
        <CameraView style={styles.cameraView} {...cameraScan.cameraProps} />
      ) : (
        <View style={styles.permissionBlock}>
          <Text variant="titleMedium">{t("camera.permissionTitle")}</Text>
          <Text variant="bodySmall">{t("camera.permissionDescription")}</Text>
          <Button mode="contained" onPress={() => void cameraScan.requestPermission()}>{t("camera.grantPermission")}</Button>
        </View>
      )}
      {cameraResultSummary ? (
        <View style={styles.cameraResultSummary}>
          <View style={styles.cameraResultText}>
            <Text variant="labelLarge" numberOfLines={1}>{cameraResultSummary.title}</Text>
            <Text variant="bodySmall" style={styles.secondaryText} numberOfLines={1}>{cameraResultSummary.detail}</Text>
          </View>
          <Button compact mode="outlined" onPress={invalidateCameraScanSession}>
            {t("common:actions.viewDetail")}
          </Button>
        </View>
      ) : null}
    </>
  );
  if (!hasWarehouseAccess) {
    return (
      <SafeAreaView style={styles.safeArea}>
        <EmptyState
          title={t("messages.noAccessTitle")}
          description={t("messages.noAccessDescription")}
          primaryAction={{
            label: t("common:actions.goToSettings"),
            icon: "cog-outline",
            onPress: () => router.navigate("/(shell)/settings"),
          }}
        />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.safeArea} edges={["top", "left", "right"]}>
      <View style={[styles.header, isPdaProductLayout ? styles.headerCompact : null]}>
        <Text variant="headlineSmall">{t("title")}</Text>
      </View>

      <SegmentedButtons
        value={segment}
        onValueChange={handleSegmentChange}
        buttons={[
          { value: "product", label: t("segments.product") },
          { value: "location", label: t("segments.location") },
        ]}
        style={[styles.segmented, isPdaProductLayout ? styles.segmentedCompact : null]}
      />
      <ScrollView contentContainerStyle={[styles.content, isPdaProductLayout ? styles.contentCompact : null]}>
        {segment === "product" ? (
          <>
            <View style={styles.searchRow}>
              <Searchbar
                placeholder={t("product.searchPlaceholder")}
                value={productKeyword}
                onChangeText={setProductKeyword}
                onFocus={pauseHiddenScannerFocus}
                onBlur={resumeHiddenScannerFocusLater}
                onSubmitEditing={() => void handleLookupProduct()}
                style={styles.search}
              />
              <IconButton
                accessibilityLabel={t("common:actions.search")}
                icon="magnify"
                mode="contained"
                onPress={() => void handleLookupProduct()}
              />
              <IconButton
                icon="barcode-scan"
                mode="contained-tonal"
                onPress={() => openCameraScanner("product")}
              />
            </View>
            {!product && productMatches.length === 0 ? (
              <EmptyState
                title={hasProductLookup ? t("product.noResultsTitle") : t("product.emptyTitle")}
                description={hasProductLookup ? t("product.noResultsDescription") : t("product.emptyDescription")}
              />
            ) : null}

            {!product && productMatches.length > 1 ? (
              <View style={styles.sectionBlock}>
                <Text variant="titleSmall" style={styles.sectionTitle}>
                  {t("product.multipleTitle")}
                </Text>
                <View style={styles.compactCardList}>
                  {productMatches.map((item) => {
                    const stockState = getProductStockState(item.stockQuantity);
                    const stockColors = PRODUCT_STOCK_COLORS[stockState];
                    return (
                      <Pressable
                        key={item.productCode}
                        onPress={() => void handleSelectProduct(item.productCode)}
                        style={styles.compactProductCard}
                      >
                        {item.productImage ? (
                          <Image source={{ uri: item.productImage }} style={styles.compactProductImage} resizeMode="cover" />
                        ) : (
                          <View style={[styles.compactProductImage, styles.productImagePlaceholder]}>
                            <Text variant="labelSmall" numberOfLines={2}>
                              {item.productCode}
                            </Text>
                          </View>
                        )}
                        <View style={styles.compactProductMeta}>
                          <Text variant="titleSmall" numberOfLines={2}>
                            {item.productName || item.productCode}
                          </Text>
                          <Text variant="bodyMedium" style={styles.mutedText}>
                            {t("product.fields.itemNumber")}: {formatDisplayValue(item.itemNumber || item.productCode)}
                          </Text>
                          <Text variant="bodySmall" style={styles.mutedText} numberOfLines={1}>
                            {t("product.fields.barcode")}: {formatDisplayValue(item.barcode)}
                          </Text>
                        </View>
                        <View style={styles.compactProductAside}>
                          <View style={[styles.statusPill, { backgroundColor: stockColors.background, borderColor: stockColors.border }]}>
                            <Text variant="labelSmall" style={[styles.statusPillText, { color: stockColors.text }]}>
                              {getProductStockLabel(stockState)}
                            </Text>
                          </View>
                          <Text variant="titleSmall" style={styles.priceText}>
                            {formatPrice(item.retailPrice ?? item.oemPrice ?? item.domesticPrice)}
                          </Text>
                        </View>
                      </Pressable>
                    );
                  })}
                </View>
              </View>
            ) : null}

            {product ? (
              <>
                <Card mode="contained" style={[styles.card, isPdaProductLayout ? styles.cardCompact : null]}>
                  <Card.Content style={[styles.productHeroCard, isPdaProductLayout ? styles.productHeroCardCompact : null]}>
                    {product.productImage ? (
                      <Image
                        source={{ uri: product.productImage }}
                        style={[styles.productImage, isPdaProductLayout ? styles.productImageCompact : null]}
                        resizeMode="cover"
                      />
                    ) : (
                      <View style={[styles.productImage, isPdaProductLayout ? styles.productImageCompact : null, styles.productImagePlaceholder]}>
                        <Text variant="bodyMedium" numberOfLines={3}>
                          {product.productCode}
                        </Text>
                      </View>
                    )}
                    <View style={styles.heroMeta}>
                      <Text variant={isPdaProductLayout ? "titleLarge" : "headlineSmall"} style={styles.heroTitle} numberOfLines={2}>
                        {product.productName || product.productCode}
                      </Text>
                      <Text variant="titleMedium" style={styles.heroIdentifier}>
                        {formatDisplayValue(product.itemNumber || product.productCode)}
                      </Text>
                      <Text variant="bodyMedium" style={styles.mutedText}>
                        {t("product.fields.barcode")}: {formatDisplayValue(product.barcode)}
                      </Text>
                      <Text variant="bodyMedium" style={styles.mutedText}>
                        {t("product.fields.supplier")}: {formatDisplayValue(product.supplierName || product.localSupplierCode)}
                      </Text>
                      <View style={styles.heroBadgeRow}>
                        <View style={[styles.statusPill, { backgroundColor: displayWarehouseIsActive ? "#DCFCE7" : "#F1F5F9", borderColor: displayWarehouseIsActive ? "#BBF7D0" : "#E2E8F0" }]}>
                          <Text variant="labelSmall" style={[styles.statusPillText, { color: displayWarehouseIsActive ? "#166534" : "#475569" }]}>
                            {displayWarehouseIsActive ? t("product.onShelf") : t("product.offShelf")}
                          </Text>
                        </View>
                        <View style={[styles.statusPill, { backgroundColor: productStockColors.background, borderColor: productStockColors.border }]}>
                          <Text variant="labelSmall" style={[styles.statusPillText, { color: productStockColors.text }]}>
                            {getProductStockLabel(productStockState)}
                          </Text>
                        </View>
                        {displayProductGrade ? (
                          <View style={[styles.gradeBadge, { backgroundColor: productGradeColor }]}>
                            <Text variant="labelSmall" style={styles.gradeBadgeText}>
                              {t("product.gradeText", { grade: displayProductGrade })}
                            </Text>
                          </View>
                        ) : null}
                      </View>
                      {productTypeText ? (
                        <Text variant="bodySmall" style={styles.mutedText}>
                          {productTypeText}
                        </Text>
                      ) : null}
                    </View>
                  </Card.Content>
                  <Card.Content style={[styles.infoGridRows, isPdaProductLayout ? styles.infoGridCompact : null]}>
                    {productSummaryVisualRows.map((row, rowIndex) => (
                      <View key={`${row.join("-")}-${rowIndex}`} style={styles.infoGridRow}>
                        {row.map((field) => {
                          const config = getProductSummaryFieldConfig(field);
                          return (
                            <View key={field} style={styles.infoGridCell}>
                              <InfoTile
                                label={config.label}
                                value={config.value}
                                emphasize={config.emphasize}
                                dense={isPdaProductLayout}
                                valueLines={config.valueLines}
                                singleColumn
                                onPress={config.onPress}
                              />
                            </View>
                          );
                        })}
                      </View>
                    ))}
                    <WarehouseSuggestedDiscountField
                      productCode={product.productCode}
                      retailPrice={product.retailPrice ?? product.oemPrice ?? null}
                      editable={access.isAdmin || access.isWarehouseManager || access.isWarehouseStaff}
                      dense={isPdaProductLayout}
                      onMessage={setSnackbar}
                    />
                  </Card.Content>
                </Card>

                <Card mode="contained" style={[styles.card, isPdaProductLayout ? styles.cardCompact : null]}>
                  <Card.Title
                    title={t("product.currentLocation")}
                    subtitle={product.locationCode || t("product.noLocation")}
                    right={() => (
                      productSectionConfig.showLocationAction ? (
                        <Button compact onPress={openProductLocationModal}>
                          {t(hasProductLocation ? "product.updateLocation" : "product.bindLocation")}
                        </Button>
                      ) : null
                    )}
                  />
                  <Card.Content style={styles.cardContent}>
                    <Text variant="bodySmall" style={styles.secondaryText}>
                      {product.locationCode ? t("product.locationBoundHint") : t("product.locationLookupHint")}
                    </Text>
                    <View style={styles.locationActionRow}>
                      <Button compact mode="contained" icon="map-marker-plus-outline" onPress={openProductLocationModal}>
                        {t(hasProductLocation ? "product.updateLocation" : "product.bindLocation")}
                      </Button>
                      <Button compact mode="outlined" icon="map-marker-remove-outline" onPress={openUnbindLocationConfirm} disabled={!product.locationGuid && !product.locationCode}>
                        {t("product.clearLocation")}
                      </Button>
                    </View>
                  </Card.Content>
                </Card>

                <View style={styles.actionRow}>
                  <Button mode="outlined" icon="camera-outline" onPress={() => setPhotoVisible(true)}>
                    {t("product.takePhoto")}
                  </Button>
                  <Button mode="outlined" icon="printer-outline" onPress={() => void handlePrintProduct()}>
                    {t("product.printProduct")}
                  </Button>
                  <Button mode="outlined" icon="map-marker-outline" onPress={() => void handlePrintLocation()}>
                    {t("product.printLocation")}
                  </Button>
                </View>
              </>
            ) : null}
          </>
        ) : (
          <>
            <View style={[styles.locationSearchRow, isPdaProductLayout ? styles.locationSearchRowCompact : null]}>
              <Searchbar
                placeholder={t("location.searchPlaceholder")}
                value={locationKeyword}
                onChangeText={updateLocationKeyword}
                onFocus={pauseHiddenScannerFocus}
                onBlur={resumeHiddenScannerFocusLater}
                onSubmitEditing={() => void handleLookupLocations()}
                style={[styles.search, isPdaProductLayout ? styles.locationSearchCompact : null]}
              />
              <View style={styles.locationSearchActions}>
                <IconButton
                  accessibilityLabel={t("common:actions.search")}
                  icon="magnify"
                  mode="contained"
                  onPress={() => void handleLookupLocations()}
                />
                <IconButton
                  icon="barcode-scan"
                  mode="contained-tonal"
                  onPress={() => openCameraScanner("location")}
                />
                {canMaintainLocations ? (
                  <Button mode="contained" icon="plus" onPress={() => void openCreateLocation()}>
                    {t("location.newLocation")}
                  </Button>
                ) : null}
              </View>
            </View>
            {defaultLocationLoading && !hasLocationLookup && locationResults.length === 0 ? (
              <View style={styles.inlineLoadingState}>
                <ActivityIndicator size="small" />
                <Text variant="bodyMedium" style={styles.secondaryText}>{t("location.loadingUnused")}</Text>
              </View>
            ) : null}

            {!defaultLocationLoading && defaultLocationLoadFailed && !hasLocationLookup && locationResults.length === 0 ? (
              <View style={styles.inlineErrorState}>
                <Text variant="bodyMedium" style={styles.secondaryText}>{t("location.unusedLoadFailed")}</Text>
                <Button compact mode="outlined" icon="refresh" onPress={() => void loadDefaultUnusedLocations({ clearSelection: true })}>
                  {t("location.retry")}
                </Button>
              </View>
            ) : null}

            {!defaultLocationLoading && !defaultLocationLoadFailed && !selectedLocation && locationResults.length === 0 ? (
              <EmptyState
                title={hasLocationLookup ? t("location.noResultsTitle") : t("location.emptyTitle")}
                description={hasLocationLookup ? t("location.noResultsDescription") : t("location.emptyDescription")}
              />
            ) : null}

            {locationResults.length ? (
              <View style={styles.sectionBlock}>
                <Text variant="titleSmall" style={styles.sectionTitle}>
                  {hasLocationLookup ? t("location.searchResultsTitle") : t("location.unusedResultsTitle")}
                </Text>
                <View style={styles.binCardList}>
                  {locationResults.map((item) => {
                    const visualState = getLocationVisualState(item.productCount);
                    const visualColors = LOCATION_VISUALS[visualState];
                    return (
                      <Pressable
                        key={item.locationGuid}
                        onPress={() => void handleSelectLocation(item.locationGuid)}
                        style={styles.binCard}
                      >
                        <View style={[styles.binStripe, { backgroundColor: visualColors.stripe }]} />
                        <View style={styles.binCardBody}>
                          <View style={styles.binCardHeader}>
                            <View style={styles.binTitleBlock}>
                              <Text variant="titleLarge">{item.locationCode || item.locationGuid}</Text>
                              <Text variant="bodySmall" style={styles.mutedText}>
                                {getLocationTypeLabel(item.locationType)}
                              </Text>
                            </View>
                            <View style={[styles.statusPill, { backgroundColor: visualColors.badgeBackground, borderColor: visualColors.badgeBorder }]}>
                              <Text variant="labelSmall" style={[styles.statusPillText, { color: visualColors.badgeText }]}>
                                {getLocationStatusLabel(visualState)}
                              </Text>
                            </View>
                          </View>
                          <View style={styles.binInfoRow}>
                            <InfoTile label={t("location.fields.locationBarcode")} value={formatDisplayValue(item.locationBarcode)} />
                            <InfoTile label={t("location.productsLabel")} value={t("location.productCountValue", { count: item.productCount })} emphasize />
                          </View>
                          <View style={styles.binFooter}>
                            <Button compact icon="eye-outline" onPress={() => void handleSelectLocation(item.locationGuid)}>
                              {t("common:actions.viewDetail")}
                            </Button>
                            <Button
                              compact
                              icon="printer-outline"
                              onPress={(event) => {
                                event.stopPropagation();
                                void handlePrintLocationListItem(item);
                              }}
                            >
                              {t("product.printLocation")}
                            </Button>
                          </View>
                        </View>
                      </Pressable>
                    );
                  })}
                </View>
              </View>
            ) : null}

            {selectedLocation ? (
              <View style={styles.sectionBlock}>
                <Text variant="titleSmall" style={styles.sectionTitle}>
                  {t("location.selectedTitle")}
                </Text>
                <View style={styles.binCard}>
                  <View style={[styles.binStripe, { backgroundColor: selectedLocationVisualColors.stripe }]} />
                  <View style={styles.binCardBody}>
                    <View style={styles.binCardHeader}>
                      <View style={styles.binTitleBlock}>
                        <Text variant="headlineSmall">{selectedLocation.locationCode || selectedLocation.locationGuid}</Text>
                        <Text variant="bodySmall" style={styles.mutedText}>
                          {getLocationTypeLabel(selectedLocation.locationType)}
                        </Text>
                      </View>
                      <View style={styles.inlineActions}>
                        <View style={[styles.statusPill, { backgroundColor: selectedLocationVisualColors.badgeBackground, borderColor: selectedLocationVisualColors.badgeBorder }]}>
                          <Text variant="labelSmall" style={[styles.statusPillText, { color: selectedLocationVisualColors.badgeText }]}>
                            {getLocationStatusLabel(selectedLocationVisualState)}
                          </Text>
                        </View>
                        <IconButton icon="printer-outline" size={20} onPress={() => void handlePrintSelectedLocation()} />
                        {canMaintainLocations ? (
                          <>
                            <IconButton icon="pencil-outline" size={20} onPress={() => void openEditLocation(selectedLocation)} />
                            <IconButton icon="delete-outline" size={20} onPress={() => void handleDeleteLocation()} />
                          </>
                        ) : null}
                      </View>
                    </View>

                    <View style={styles.binInfoRow}>
                      <InfoTile label={t("location.fields.locationBarcode")} value={formatDisplayValue(selectedLocation.locationBarcode)} />
                      <InfoTile label={t("location.productsLabel")} value={t("location.productCountValue", { count: selectedLocationProductCount })} emphasize />
                    </View>

                    <View style={styles.locationProductsSection}>
                      <View style={styles.sectionHeaderRow}>
                        <Text variant="titleSmall">{t("location.productListTitle")}</Text>
                        {selectedLocationCanBindMore && selectedLocation.products.length ? (
                          <Button compact icon="link-variant" mode="contained" onPress={openBindProductModal}>
                            {t("location.bindProduct")}
                          </Button>
                        ) : null}
                      </View>
                      {!selectedLocationCanBindMore && selectedLocation.products.length ? (
                        <Text variant="bodySmall" style={styles.secondaryText}>
                          {t("location.pickLocationSingleProductHint")}
                        </Text>
                      ) : null}

                      {selectedLocation.products.length ? (
                        <View style={styles.locationProductList}>
                          {selectedLocation.products.map((item) => (
                            <View key={`${selectedLocation.locationGuid}-${item.productCode}`} style={styles.locationProductRow}>
                              {item.productImage ? (
                                <Image source={{ uri: item.productImage }} style={styles.locationProductImage} resizeMode="cover" />
                              ) : (
                                <View style={[styles.locationProductImage, styles.productImagePlaceholder]}>
                                  <Text variant="labelSmall">{item.productCode || "--"}</Text>
                                </View>
                              )}
                              <View style={styles.locationProductMeta}>
                                <Text variant="bodyMedium" style={styles.locationProductName} numberOfLines={1}>
                                  {item.productName || item.productCode || notAvailableText}
                                </Text>
                                <Text variant="bodySmall" style={styles.mutedText} numberOfLines={1}>
                                  {t("product.fields.itemNumber")}: {item.itemNumber || item.productCode || notAvailableText}
                                </Text>
                                <Text variant="bodySmall" style={styles.mutedText} numberOfLines={1}>
                                  {t("location.boundProductCode")}: {item.productCode || notAvailableText}
                                </Text>
                              </View>
                              <Button compact mode="outlined" icon="link-variant-off" onPress={() => openUnbindProductConfirm(item)} disabled={!item.productCode}>
                                {t("location.unbindProduct")}
                              </Button>
                            </View>
                          ))}
                        </View>
                      ) : (
                        <View style={styles.emptyBinState}>
                          <Text variant="bodyMedium" style={styles.secondaryText}>
                            {t("location.productListEmpty")}
                          </Text>
                          {selectedLocationCanBindMore ? (
                            <Button mode="contained" icon="link-variant" onPress={openBindProductModal}>
                              {t("location.bindProduct")}
                            </Button>
                          ) : null}
                        </View>
                      )}
                    </View>
                  </View>
                </View>
              </View>
            ) : null}
          </>
        )}
      </ScrollView>

      <Portal>
        <Modal visible={photoVisible} onDismiss={() => setPhotoVisible(false)} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium" style={styles.modalTitle}>{t("camera.photoTitle")}</Text>
          {photoPermission?.granted ? (
            <>
              <CameraView ref={photoCameraRef} style={styles.cameraView} facing="back" />
              <Button mode="contained" onPress={() => void handleCapturePhoto()} style={styles.primaryButton}>
                {t("camera.capture")}
              </Button>
            </>
          ) : (
            <View style={styles.permissionBlock}>
              <Text variant="titleMedium">{t("camera.permissionTitle")}</Text>
              <Text variant="bodySmall">{t("camera.permissionDescription")}</Text>
              <Button mode="contained" onPress={() => void requestPhotoPermission()}>{t("camera.grantPermission")}</Button>
            </View>
          )}
        </Modal>

        <Modal
          visible={productChoiceModal === "grade"}
          onDismiss={() => setProductChoiceModal(null)}
          contentContainerStyle={styles.modal}
        >
          <Text variant="titleMedium" style={styles.modalTitle}>{t("product.fields.grade")}</Text>
          <SegmentedButtons
            value={productChoiceDraft.grade}
            onValueChange={(value) =>
              setProductChoiceDraft((current) => ({
                ...current,
                grade: toggleWarehouseProductGradeSelection(current.grade, value),
              }))
            }
            buttons={PRODUCT_GRADE_OPTIONS.map((grade) => ({ value: grade, label: grade }))}
            style={styles.gradeSegmented}
          />
          <View style={styles.modalActionRow}>
            <Button onPress={() => setProductChoiceModal(null)}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" onPress={() => void handleSaveProductPatch({ grade: productChoiceDraft.grade }, { field: "grade" })}>
              {t("common:actions.save")}
            </Button>
          </View>
        </Modal>

        <Modal
          visible={productChoiceModal === "warehouseStatus"}
          onDismiss={() => setProductChoiceModal(null)}
          contentContainerStyle={styles.modal}
        >
          <Text variant="titleMedium" style={styles.modalTitle}>{t("product.fields.warehouseStatus")}</Text>
          <View style={[styles.switchRow, styles.modalSwitchRow]}>
            <Text variant="bodyMedium">{productChoiceDraft.warehouseIsActive ? t("product.onShelf") : t("product.offShelf")}</Text>
            <Switch
              value={productChoiceDraft.warehouseIsActive}
              onValueChange={(value) => setProductChoiceDraft((current) => ({ ...current, warehouseIsActive: value }))}
            />
          </View>
          <View style={styles.modalActionRow}>
            <Button onPress={() => setProductChoiceModal(null)}>{t("common:actions.cancel")}</Button>
            <Button
              mode="contained"
              onPress={() =>
                void handleSaveProductPatch(
                  { warehouseIsActive: productChoiceDraft.warehouseIsActive },
                  { field: "warehouseIsActive" }
                )
              }
            >
              {t("common:actions.save")}
            </Button>
          </View>
        </Modal>

        <Modal
          visible={Boolean(pendingRetailPriceSync)}
          onDismiss={closeRetailPriceSyncConfirm}
          contentContainerStyle={styles.modal}
        >
          <Text variant="titleMedium" style={styles.modalTitle}>{t("product.retailSyncConfirmTitle")}</Text>
          <Text variant="bodyMedium" style={styles.secondaryText}>
            {t("product.retailSyncConfirmDescription")}
          </Text>
          {product && pendingRetailPriceSync ? (
            <RetailPriceNotificationPreview
              productCode={product.productCode}
              retailPrice={pendingRetailPriceSync.retailPrice}
            />
          ) : null}
          <View style={styles.sheetFooter}>
            <Button onPress={() => void handleConfirmRetailPriceSync(false)} disabled={busy}>
              {t("product.retailSyncProductOnly")}
            </Button>
            <Button
              mode="contained"
              onPress={() => void handleConfirmRetailPriceSync(true)}
              disabled={busy}
            >
              {t("product.retailSyncStores")}
            </Button>
          </View>
        </Modal>

        <Modal
          visible={productLocationModalVisible}
          onDismiss={closeProductLocationModal}
          style={styles.bottomSheetModal}
          contentContainerStyle={styles.bottomSheetContainer}
        >
          <View style={styles.bottomSheetHandle} />
          <View style={styles.sheetHeader}>
            <View style={styles.sheetHeaderMeta}>
              <Text variant="titleLarge">{t(hasProductLocation ? "product.updateLocation" : "product.locationModalTitle")}</Text>
              <Text variant="bodySmall" style={styles.secondaryText} numberOfLines={1}>
                {t("product.currentLocation")}: {product?.locationCode || t("product.noLocation")}
              </Text>
            </View>
            <IconButton icon="close" size={20} onPress={closeProductLocationModal} />
          </View>

          <ScrollView contentContainerStyle={styles.sheetContent} keyboardShouldPersistTaps="always">
            <View style={styles.searchRow}>
              <Searchbar
                placeholder={t("location.searchPlaceholder")}
                value={locationLookupKeyword}
                onChangeText={updateProductLocationLookupKeyword}
                onFocus={pauseHiddenScannerFocus}
                onBlur={resumeHiddenScannerFocusLater}
                onSubmitEditing={() => void handleLookupLocationsForProduct()}
                style={styles.search}
              />
              <IconButton
                accessibilityLabel={t("common:actions.search")}
                icon="magnify"
                mode="contained"
                onPress={() => void handleLookupLocationsForProduct()}
              />
              <IconButton
                icon="barcode-scan"
                mode="contained-tonal"
                onPress={() => openCameraScanner("productLocation")}
              />
            </View>
            {productLocationBindFeedback ? (
              <Text variant="bodySmall" style={styles.productLocationBindFeedback}>
                {productLocationBindFeedback}
              </Text>
            ) : null}

            {locationMatches.length ? (
              <View style={styles.compactCardList}>
                {locationMatches.map((item) => {
                  const candidateAction = getProductLocationCandidateAction({
                    locationType: item.locationType,
                    productCount: item.productCount,
                  });
                  const isCandidateDisabled = isProductLocationCandidateDisabled(candidateAction, busy);
                  return (
                    <View
                      key={item.locationGuid}
                      style={[styles.locationCandidateCard, isCandidateDisabled ? styles.locationCandidateCardDisabled : null]}
                    >
                      <View style={styles.locationCandidateMeta}>
                        <Text variant="titleSmall">{item.locationCode || item.locationGuid}</Text>
                        <Text variant="bodySmall" style={styles.mutedText}>
                          {item.locationBarcode || notAvailableText}
                        </Text>
                        {candidateAction === "block" ? (
                          <Text variant="bodySmall" style={styles.secondaryText}>
                            {t("location.pickLocationOccupiedHint")}
                          </Text>
                        ) : null}
                      </View>
                      <View style={styles.locationCandidateAction}>
                        <Text variant="bodySmall" style={styles.mutedText}>
                          {t("location.productCountValue", { count: item.productCount })}
                        </Text>
                        <Button
                          compact
                          mode={candidateAction !== "block" ? "contained" : "outlined"}
                          disabled={isCandidateDisabled}
                          loading={busy && candidateAction !== "block"}
                          onPress={() => handlePressProductLocationCandidate(item)}
                        >
                          {candidateAction === "confirm" ? t("location.storageBindConfirmAction") : t("location.bindLocationAction")}
                        </Button>
                      </View>
                    </View>
                  );
                })}
              </View>
            ) : (
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("product.locationLookupHint")}
              </Text>
            )}
          </ScrollView>
        </Modal>

        <Modal visible={Boolean(pendingStorageLocationBind)} onDismiss={closeStorageLocationBindConfirm} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium" style={styles.modalTitle}>{t("location.storageBindConfirmTitle")}</Text>
          <Text variant="bodyMedium" style={styles.secondaryText}>
            {t("location.storageBindConfirmDescription", {
              location: pendingStorageLocationBind?.locationCode || pendingStorageLocationBind?.locationGuid || notAvailableText,
              count: pendingStorageLocationBind?.productCount ?? 0,
            })}
          </Text>
          <View style={styles.sheetFooter}>
            <Button onPress={closeStorageLocationBindConfirm} disabled={busy}>{t("common:actions.cancel")}</Button>
            <Button
              mode="contained"
              icon="map-marker-plus-outline"
              onPress={() => pendingStorageLocationBind && void handleBindLocation(pendingStorageLocationBind)}
              disabled={busy}
            >
              {t("location.storageBindConfirmAction")}
            </Button>
          </View>
        </Modal>

        <Modal visible={unbindLocationConfirmVisible} onDismiss={closeUnbindLocationConfirm} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium" style={styles.modalTitle}>{t("product.unbindLocationTitle")}</Text>
          <Text variant="bodyMedium" style={styles.secondaryText}>
            {t("product.unbindLocationDescription", { location: pendingProductLocationUnbind?.locationCode || t("product.noLocation") })}
          </Text>
          <View style={styles.sheetFooter}>
            <Button onPress={closeUnbindLocationConfirm} disabled={busy}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" icon="map-marker-remove-outline" onPress={() => void handleConfirmUnbindProductLocation()} disabled={busy}>
              {t("product.clearLocation")}
            </Button>
          </View>
        </Modal>

        <Modal visible={Boolean(pendingUnbindProduct)} onDismiss={() => setPendingUnbindProduct(null)} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium" style={styles.modalTitle}>{t("location.unbindConfirmTitle")}</Text>
          <Text variant="bodyMedium" style={styles.secondaryText}>
            {t("location.unbindConfirmDescription", {
              product: pendingUnbindProduct?.product.productName || pendingUnbindProduct?.product.itemNumber || pendingUnbindProduct?.product.productCode || notAvailableText,
              location: pendingUnbindProduct?.locationCode || pendingUnbindProduct?.locationGuid || notAvailableText,
            })}
          </Text>
          <View style={styles.unbindConfirmProductCard}>
            <Text variant="bodyMedium" style={styles.locationProductName} numberOfLines={2}>
              {pendingUnbindProduct?.product.productName || pendingUnbindProduct?.product.productCode || notAvailableText}
            </Text>
            <Text variant="bodySmall" style={styles.mutedText} numberOfLines={1}>
              {t("product.fields.itemNumber")}: {pendingUnbindProduct?.product.itemNumber || pendingUnbindProduct?.product.productCode || notAvailableText}
            </Text>
            <Text variant="bodySmall" style={styles.mutedText} numberOfLines={1}>
              {t("location.boundProductCode")}: {pendingUnbindProduct?.product.productCode || notAvailableText}
            </Text>
          </View>
          <View style={styles.sheetFooter}>
            <Button onPress={() => setPendingUnbindProduct(null)} disabled={busy}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" icon="link-variant-off" onPress={() => void handleConfirmUnbindProduct()} disabled={busy}>
              {t("location.unbindConfirmAction")}
            </Button>
          </View>
        </Modal>

        <Modal
          visible={bindModalVisible}
          onDismiss={closeBindProductModal}
          style={styles.bottomSheetModal}
          contentContainerStyle={styles.bottomSheetContainer}
        >
          <View style={styles.bottomSheetHandle} />
          <View style={styles.sheetHeader}>
            <View style={styles.sheetHeaderMeta}>
              <Text variant="titleLarge">{t("location.bindModalTitle")}</Text>
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("location.bindModalCurrentLocation")}: {selectedLocation?.locationCode || selectedLocation?.locationGuid || notAvailableText}
              </Text>
            </View>
            <IconButton icon="close" size={20} onPress={closeBindProductModal} />
          </View>

          <ScrollView contentContainerStyle={styles.sheetContent}>
            <View style={styles.searchRow}>
              <Searchbar
                placeholder={t("location.bindModalSearchPlaceholder")}
                value={bindProductKeyword}
                onChangeText={setBindProductKeyword}
                onFocus={pauseHiddenScannerFocus}
                onBlur={resumeHiddenScannerFocusLater}
                onSubmitEditing={() => void handleLookupBindProducts()}
                style={styles.search}
              />
              <IconButton
                accessibilityLabel={t("common:actions.search")}
                icon="magnify"
                mode="contained"
                onPress={() => void handleLookupBindProducts()}
              />
              <IconButton
                icon="barcode-scan"
                mode="contained-tonal"
                onPress={() => openCameraScanner("bindProduct")}
              />
            </View>
            <View style={styles.bindHintCard}>
              <Text variant="labelSmall" style={styles.infoTileLabel}>
                {t("location.bindModalQuantityTitle")}
              </Text>
              <TextInput
                mode="outlined"
                value={bindInitialQuantity}
                onChangeText={setBindInitialQuantity}
                keyboardType="decimal-pad"
                autoCapitalize="none"
                placeholder={t("location.bindModalQuantityPlaceholder")}
                style={styles.bindQuantityInput}
              />
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("location.bindModalQuantityHint")}
              </Text>
            </View>

            <View style={styles.sectionHeaderRow}>
              <Text variant="titleSmall">{t("location.bindModalSearchResults")}</Text>
            </View>

            {bindProductMatches.length ? (
              <View style={styles.compactCardList}>
                {bindProductMatches.map((item) => {
                  const isSelected = selectedBindProduct?.productCode === item.productCode;
                  return (
                    <Pressable
                      key={item.productCode}
                      onPress={() => {
                        setSelectedBindProduct(item);
                        setBindInitialQuantity(getBindInitialQuantityValue(item));
                      }}
                      style={[
                        styles.bindResultCard,
                        isSelected ? styles.bindResultCardSelected : null,
                      ]}
                    >
                      {item.productImage ? (
                        <Image source={{ uri: item.productImage }} style={styles.bindResultImage} resizeMode="cover" />
                      ) : (
                        <View style={[styles.bindResultImage, styles.productImagePlaceholder]}>
                          <Text variant="labelSmall">{item.productCode}</Text>
                        </View>
                      )}
                      <View style={styles.bindResultMeta}>
                        <Text variant="titleSmall" numberOfLines={1}>
                          {item.productName || item.productCode}
                        </Text>
                        <Text variant="bodySmall" style={styles.mutedText} numberOfLines={1}>
                          {t("product.fields.itemNumber")}: {formatDisplayValue(item.itemNumber || item.productCode)}
                        </Text>
                        <Text variant="bodySmall" style={styles.mutedText} numberOfLines={1}>
                          {t("product.fields.barcode")}: {formatDisplayValue(item.barcode)}
                        </Text>
                      </View>
                      {isSelected ? (
                        <View style={styles.bindResultCheck}>
                          <Text variant="labelSmall" style={styles.bindResultCheckText}>✓</Text>
                        </View>
                      ) : null}
                    </Pressable>
                  );
                })}
              </View>
            ) : hasBindProductLookup ? (
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("location.bindModalNoResults")}
              </Text>
            ) : (
              <Text variant="bodySmall" style={styles.secondaryText}>
                {t("location.bindModalSearchHint")}
              </Text>
            )}
          </ScrollView>

          <View style={styles.sheetFooter}>
            <Button onPress={closeBindProductModal}>{t("common:actions.cancel")}</Button>
            <Button mode="contained" icon="link-variant" onPress={() => void handleBindProductToLocation()}>
              {t("location.bindModalConfirm")}
            </Button>
          </View>
        </Modal>

        <Modal visible={locationModalVisible} onDismiss={() => setLocationModalVisible(false)} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium" style={styles.modalTitle}>
            {editingLocationGuid ? t("location.editLocation") : t("location.newLocation")}
          </Text>
          <View style={styles.generatedLocationCode}>
            <Text variant="labelMedium">{t("location.fields.locationCode")}</Text>
            <Text variant="titleMedium">{buildLocationCode(locationCodeParts)}</Text>
          </View>
          <View style={styles.locationPartRow}>
            <LocationPartMenu
              label={t("location.codeParts.letter")}
              value={locationCodeParts.letter}
              options={LOCATION_LETTER_OPTIONS}
              visible={locationPartMenus.letter}
              onOpen={() => setLocationPartMenuVisible("letter", true)}
              onDismiss={() => setLocationPartMenuVisible("letter", false)}
              onSelect={(value) => setLocationPart("letter", value)}
            />
            <LocationPartMenu
              label={t("location.codeParts.section")}
              value={locationCodeParts.section}
              options={LOCATION_NUMBER_OPTIONS}
              visible={locationPartMenus.section}
              onOpen={() => setLocationPartMenuVisible("section", true)}
              onDismiss={() => setLocationPartMenuVisible("section", false)}
              onSelect={(value) => setLocationPart("section", value)}
            />
            <LocationPartMenu
              label={t("location.codeParts.shelf")}
              value={locationCodeParts.shelf}
              options={LOCATION_NUMBER_OPTIONS}
              visible={locationPartMenus.shelf}
              onOpen={() => setLocationPartMenuVisible("shelf", true)}
              onDismiss={() => setLocationPartMenuVisible("shelf", false)}
              onSelect={(value) => setLocationPart("shelf", value)}
            />
            <LocationPartMenu
              label={t("location.codeParts.slot")}
              value={locationCodeParts.slot}
              options={locationSlotOptions}
              visible={locationPartMenus.slot}
              onOpen={() => setLocationPartMenuVisible("slot", true)}
              onDismiss={() => setLocationPartMenuVisible("slot", false)}
              onSelect={(value) => setLocationPart("slot", value)}
            />
          </View>
          {!editingLocationGuid && !locationSlotOptions.length ? (
            <Text variant="bodySmall" style={styles.secondaryText}>{t("location.slotGroupFull")}</Text>
          ) : null}
          {editingLocationGuid && locationModalState.locationBarcode ? (
            <View style={styles.generatedLocationCode}>
              <Text variant="labelMedium">{t("location.fields.locationBarcode")}</Text>
              <Text variant="bodyMedium">{locationModalState.locationBarcode}</Text>
            </View>
          ) : (
            <Text variant="bodySmall" style={styles.secondaryText}>{t("location.barcodeAutoHint")}</Text>
          )}
          <SegmentedButtons
            value={String(locationModalState.locationType ?? 1)}
            onValueChange={(value) => setLocationModalState((current) => ({ ...current, locationType: Number(value) }))}
            buttons={[
              { value: "1", label: t("location.typePick") },
              { value: "2", label: t("location.typeStorage") },
            ]}
          />
          <View style={styles.switchRow}>
            <Text variant="bodyMedium">{t("location.fields.status")}</Text>
            <Switch
              value={(locationModalState.status ?? 1) === 1}
              onValueChange={(value) => setLocationModalState((current) => ({ ...current, status: value ? 1 : 0 }))}
            />
          </View>
          <Button mode="contained" onPress={() => void handleSaveLocation()} disabled={!editingLocationGuid && !locationSlotOptions.length} style={styles.primaryButton}>
            {t("common:actions.save")}
          </Button>
        </Modal>

      </Portal>

      <CameraScanSheet
        visible={cameraSheetSession.visible}
        title={scannerTarget === "bindProduct" ? t("location.bindModalScanTitle") : t("camera.scanTitle")}
        mode={cameraScanMode}
        onModeChange={handleCameraScanModeChange}
        onDismiss={invalidateCameraScanSession}
      >
        {renderCameraScanner()}
      </CameraScanSheet>

      {numericInputModal ? (
        <NumericInputModal
          visible
          title={numericInputModal.title}
          value={numericInputModal.value}
          allowDecimal={numericInputModal.allowDecimal}
          onChangeValue={(value) =>
            setNumericInputModal((current) => (current ? { ...current, value } : current))
          }
          onConfirm={handleConfirmNumericInputModal}
          onDismiss={dismissNumericInputModal}
        />
      ) : null}

      {busy ? <LoadingOverlay /> : null}

      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={2500}>
        {snackbar}
      </Snackbar>

      {hidScanner.mode === "textInput" && hidScanner.textInputProps ? (
        <NativeTextInput style={styles.hiddenInput} {...hidScanner.textInputProps} />
      ) : null}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F4F6F8",
  },
  header: {
    paddingHorizontal: 16,
    paddingTop: 12,
    paddingBottom: 12,
  },
  headerCompact: {
    paddingHorizontal: 12,
    paddingTop: 4,
    paddingBottom: 8,
  },
  segmented: {
    marginHorizontal: 16,
    marginBottom: 12,
  },
  segmentedCompact: {
    marginHorizontal: 12,
    marginBottom: 8,
  },
  content: {
    padding: 16,
    paddingTop: 12,
    gap: 16,
    paddingBottom: 32,
  },
  contentCompact: {
    paddingHorizontal: 10,
    paddingTop: 8,
    gap: 8,
    paddingBottom: 40,
  },
  searchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  search: {
    flex: 1,
  },
  locationSearchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  locationSearchRowCompact: {
    flexDirection: "column",
    alignItems: "stretch",
  },
  locationSearchCompact: {
    flex: 0,
    width: "100%",
  },
  locationSearchActions: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-end",
    gap: 8,
  },
  card: {
    backgroundColor: "#fff",
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E4E7EC",
  },
  cardCompact: {
    borderRadius: 10,
  },
  cardContent: {
    gap: 10,
  },
  productHeroCard: {
    flexDirection: "row",
    gap: 14,
  },
  productHeroCardCompact: {
    gap: 10,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  productImage: {
    width: 112,
    height: 112,
    borderRadius: 8,
    backgroundColor: "#F1F5F9",
  },
  productImageCompact: {
    width: 64,
    height: 64,
    borderRadius: 8,
  },
  productImagePlaceholder: {
    alignItems: "center",
    justifyContent: "center",
    padding: 12,
    borderWidth: 1,
    borderColor: "#E2E8F0",
  },
  heroMeta: {
    flex: 1,
    gap: 6,
    minWidth: 0,
  },
  heroTitle: {
    flexShrink: 1,
  },
  heroIdentifier: {
    color: "#0F172A",
  },
  heroBadgeRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    flexWrap: "wrap",
  },
  gradeBadge: {
    borderRadius: 999,
    paddingHorizontal: 8,
    paddingVertical: 2,
  },
  gradeBadgeText: {
    color: "#fff",
    fontWeight: "700",
  },
  switchRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  infoGridRows: {
    gap: 10,
    paddingTop: 14,
  },
  infoGridCompact: {
    gap: 6,
    paddingHorizontal: 12,
    paddingTop: 8,
    paddingBottom: 10,
  },
  infoGridRow: {
    flexDirection: "row",
    gap: 6,
  },
  infoGridCell: {
    flex: 1,
    minWidth: 0,
  },
  infoTile: {
    width: "48%",
    minWidth: 150,
    backgroundColor: "#F8FAFC",
    borderRadius: 8,
    paddingHorizontal: 12,
    paddingVertical: 10,
    gap: 4,
    borderWidth: 1,
    borderColor: "#E2E8F0",
  },
  infoTilePressable: {
    borderColor: "#BFDBFE",
    backgroundColor: "#F8FBFF",
  },
  infoTileDense: {
    borderRadius: 8,
    paddingHorizontal: 10,
    paddingVertical: 7,
    gap: 2,
  },
  infoTileSingle: {
    width: "100%",
    minWidth: 0,
  },
  infoTileLabel: {
    color: "#64748B",
  },
  infoTileValue: {
    color: "#0F172A",
    fontWeight: "600",
    minWidth: 0,
  },
  fieldGrid: {
    gap: 10,
  },
  fieldRow: {
    flexDirection: "row",
    gap: 10,
  },
  fieldCell: {
    flex: 1,
    minWidth: 0,
  },
  numericField: {
    borderRadius: 12,
  },
  numericFieldContent: {
    minHeight: 54,
    justifyContent: "flex-start",
  },
  numericFieldInner: {
    width: "100%",
    alignItems: "flex-start",
    gap: 4,
  },
  numericFieldLabel: {
    color: "#64748B",
  },
  numericFieldValue: {
    color: "#0F172A",
    fontWeight: "700",
  },
  gradeCell: {
    flex: 2,
    gap: 6,
  },
  gradeFieldLabel: {
    color: "#64748B",
  },
  gradeSegmented: {
    minHeight: 36,
  },
  secondaryText: {
    color: "#64748B",
  },
  primaryButton: {
    marginTop: 8,
    borderRadius: 12,
  },
  actionRow: {
    flexDirection: "row",
    gap: 8,
    flexWrap: "wrap",
  },
  locationActionRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  inlineActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
  },
  locationProductRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    paddingVertical: 10,
    paddingHorizontal: 10,
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#E2E8F0",
  },
  locationProductImage: {
    width: 44,
    height: 44,
    borderRadius: 10,
    backgroundColor: "#F8FAFC",
  },
  locationProductMeta: {
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  locationProductName: {
    color: "#0F172A",
    fontWeight: "700",
  },
  unbindConfirmProductCard: {
    gap: 4,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 8,
    backgroundColor: "#F8FAFC",
    borderWidth: 1,
    borderColor: "#E2E8F0",
  },
  generatedLocationCode: {
    gap: 4,
    padding: 12,
    borderRadius: 12,
    backgroundColor: "#F8FAFC",
  },
  locationPartRow: {
    flexDirection: "row",
    gap: 8,
  },
  locationPart: {
    flex: 1,
    gap: 4,
  },
  locationPartLabel: {
    color: "#64748B",
  },
  locationPartButton: {
    minHeight: 36,
  },
  locationPartMenu: {
    maxHeight: 260,
  },
  modal: {
    backgroundColor: "#fff",
    margin: 16,
    borderRadius: 20,
    padding: 16,
    gap: 12,
  },
  modalTitle: {
    marginBottom: 8,
  },
  cameraView: {
    width: "100%",
    height: 320,
    borderRadius: 12,
    overflow: "hidden",
  },
  cameraResultSummary: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    padding: 10,
    backgroundColor: "#EEF4FF",
    borderTopWidth: 1,
    borderTopColor: "#B2CCFF",
  },
  cameraResultText: {
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  permissionBlock: {
    gap: 10,
    alignItems: "center",
  },
  sectionBlock: {
    gap: 10,
  },
  sectionTitle: {
    color: "#0F172A",
  },
  inlineLoadingState: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingVertical: 16,
  },
  inlineErrorState: {
    gap: 8,
    alignItems: "flex-start",
    paddingVertical: 16,
  },
  sectionHeaderRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  compactCardList: {
    gap: 10,
  },
  compactProductCard: {
    backgroundColor: "#FFFFFF",
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    padding: 12,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  compactProductImage: {
    width: 72,
    height: 72,
    borderRadius: 12,
    backgroundColor: "#F8FAFC",
  },
  compactProductMeta: {
    flex: 1,
    gap: 4,
    minWidth: 0,
  },
  compactProductAside: {
    alignItems: "flex-end",
    gap: 8,
  },
  mutedText: {
    color: "#64748B",
  },
  statusPill: {
    paddingHorizontal: 8,
    paddingVertical: 4,
    borderRadius: 999,
    borderWidth: 1,
  },
  statusPillText: {
    fontWeight: "700",
  },
  priceText: {
    color: "#0F172A",
    fontWeight: "700",
  },
  locationCandidateCard: {
    backgroundColor: "#F8FAFC",
    borderRadius: 8,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    paddingHorizontal: 12,
    paddingVertical: 10,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  locationCandidateCardDisabled: {
    opacity: 0.58,
  },
  locationCandidateMeta: {
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  locationCandidateAction: {
    alignItems: "flex-end",
    gap: 6,
    flexShrink: 0,
  },
  productLocationBindFeedback: {
    color: "#B91C1C",
    fontWeight: "600",
  },
  binCardList: {
    gap: 12,
  },
  binCard: {
    flexDirection: "row",
    backgroundColor: "#FFFFFF",
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    overflow: "hidden",
  },
  binStripe: {
    width: 4,
  },
  binCardBody: {
    flex: 1,
    paddingHorizontal: 14,
    paddingVertical: 14,
    gap: 12,
  },
  binCardHeader: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 10,
  },
  binTitleBlock: {
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  binInfoRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  binFooter: {
    flexDirection: "row",
    justifyContent: "space-between",
    flexWrap: "wrap",
    gap: 8,
  },
  locationProductsSection: {
    gap: 12,
  },
  locationProductList: {
    gap: 10,
  },
  emptyBinState: {
    gap: 12,
    alignItems: "flex-start",
    paddingVertical: 4,
  },
  bottomSheetModal: {
    justifyContent: "flex-end",
    margin: 0,
  },
  bottomSheetContainer: {
    backgroundColor: "#FFFFFF",
    borderTopLeftRadius: 18,
    borderTopRightRadius: 18,
    alignSelf: "stretch",
    height: "54%",
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 20,
    maxHeight: "92%",
  },
  bottomSheetHandle: {
    alignSelf: "center",
    width: 44,
    height: 5,
    borderRadius: 999,
    backgroundColor: "#CBD5E1",
    marginBottom: 10,
  },
  sheetHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  sheetHeaderMeta: {
    flex: 1,
    gap: 2,
  },
  sheetContent: {
    gap: 12,
    paddingBottom: 12,
  },
  bindHintCard: {
    backgroundColor: "#F8FAFC",
    borderRadius: 8,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderWidth: 1,
    borderColor: "#E2E8F0",
    gap: 4,
  },
  bindQuantityInput: {
    backgroundColor: "#FFFFFF",
  },
  modalSwitchRow: {
    minHeight: 44,
    paddingHorizontal: 10,
    paddingVertical: 8,
    borderRadius: 10,
    backgroundColor: "#F8FAFC",
  },
  modalActionRow: {
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
  bindResultCard: {
    backgroundColor: "#FFFFFF",
    borderRadius: 8,
    borderWidth: 1,
    borderColor: "#CBD5E1",
    padding: 10,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  bindResultCardSelected: {
    borderColor: "#34D399",
    backgroundColor: "#ECFDF5",
  },
  bindResultImage: {
    width: 52,
    height: 52,
    borderRadius: 10,
    backgroundColor: "#F8FAFC",
  },
  bindResultMeta: {
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  bindResultCheck: {
    minWidth: 28,
    height: 28,
    borderRadius: 14,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#34D399",
  },
  bindResultCheckText: {
    color: "#FFFFFF",
    fontWeight: "700",
  },
  sheetFooter: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-end",
    gap: 8,
    paddingTop: 12,
    borderTopWidth: 1,
    borderTopColor: "#E2E8F0",
  },
  hiddenInput: {
    position: "absolute",
    width: 1,
    height: 1,
    opacity: 0,
  },
});
