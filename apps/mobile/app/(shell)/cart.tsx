import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  Alert,
  Animated,
  FlatList,
  PanResponder,
  Pressable,
  StyleSheet,
  TextInput as NativeTextInput,
  useWindowDimensions,
  View,
} from "react-native";
import {
  Button,
  Menu,
  Searchbar,
  Text,
  TextInput as PaperTextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useFocusEffect, useIsFocused } from "@react-navigation/native";
import { type Href, useRouter } from "expo-router";
import { useIsMutating, useQueryClient } from "@tanstack/react-query";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { EmptyState } from "@/components/ui/EmptyState";
import { LoadingOverlay } from "@/components/ui/LoadingOverlay";
import { ScanResultPicker } from "@/components/ui/ScanResultPicker";
import { OrderBarPrimaryButton, OrderBottomBar } from "@/components/order/OrderBottomBar";
import { OrderStepper } from "@/components/order/OrderStepper";
import { GradeTag, OrderStatusTag, OrderThumbnail } from "@/components/order/OrderTags";
import { QuantityPresetRow } from "@/components/order/QuantityPresetRow";
import {
  mapScanFeedbackToNotice,
  ORDER_NOTICE_DURATION_MS,
  type OrderNotice,
  type OrderNoticeTone,
} from "@/components/order/order-notice";
import {
  formatOrderMoney,
  ORDER_COLORS,
  resolveOrderStep,
  resolveTotalPages,
} from "@/components/order/order-ui";
import { ORDER_MONO_FONT } from "@/components/order/order-fonts";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { submitStoreOrder } from "@/modules/orders/store-order-api";
import { reconcileSubmittedCartCache } from "@/modules/shop/cart-cache";
import { useClearCart } from "@/modules/shop/use-clear-cart";
import { useCartPage } from "@/modules/shop/use-cart-page";
import { useRemoveCartLine } from "@/modules/shop/use-remove-cart-line";
import { useStores } from "@/modules/shop/use-stores";
import { useUpdateCartQuantity } from "@/modules/shop/use-update-cart-quantity";
import {
  canDismissCartQuantityEditor,
  canSubmitCartQuantityEdit,
  parseCartQuantityInput,
  resolveCurrentCartQuantityItem,
  shouldSubmitCartQuantityUpdate,
} from "@/modules/shop/cart-quantity-input";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { useScanResult } from "@/modules/scanner/use-scan-result";
import type { StoreOrderCartItem } from "@/modules/shop/types";
import { isPreorderRequiredError } from "@/modules/preorder/api";
import { canBypassPreorderGate } from "@/modules/preorder/gate";
import { PreorderGateBanner } from "@/modules/preorder/preorder-gate-banner";
import { usePreorderGate } from "@/modules/preorder/use-preorder-gate";
import { useAuthStore } from "@/store/auth-store";

const PAGE_SIZE_OPTIONS = [10, 20, 50, 100];
const SWIPE_ACTION_WIDTH = 92;
// 与 Web 端购物车备注长度保持一致。
const ORDER_REMARKS_MAX_LENGTH = 500;

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

interface CartListItemCardProps {
  clearCartPending: boolean;
  compact: boolean;
  isDeleting: boolean;
  isPriority: boolean;
  isUpdating: boolean;
  item: StoreOrderCartItem;
  openSwipeDetailGUID: string | null;
  t: (key: string, options?: Record<string, unknown>) => string;
  onDelete: (item: StoreOrderCartItem) => Promise<void>;
  onEditQuantity: (item: StoreOrderCartItem) => void;
  onOpenSwipe: (detailGUID: string | null) => void;
  onUpdateQuantity: (item: StoreOrderCartItem, nextQuantity: number) => Promise<void>;
}

function CartListItemCard({
  clearCartPending,
  compact,
  isDeleting,
  isPriority,
  isUpdating,
  item,
  openSwipeDetailGUID,
  t,
  onDelete,
  onEditQuantity,
  onOpenSwipe,
  onUpdateQuantity,
}: CartListItemCardProps) {
  const translateX = useRef(new Animated.Value(0)).current;
  const offsetRef = useRef(0);
  const step = resolveOrderStep(item.minOrderQuantity);
  const isBusy = isUpdating || isDeleting || clearCartPending;
  const importPrice = Number(item.importPrice ?? 0);
  const importAmount = Number(item.importAmount ?? importPrice * item.quantity);
  const hasZeroImportPrice = importPrice <= 0;
  // 加购后被仓库暂停供货：服务端会拦截加量和提交，只放行减量与移除，这里提前标出来。
  const isPaused = item.isActive === false;
  const skuValue = item.itemNumber || item.productCode || "--";

  const animateTo = useCallback(
    (toValue: number) => {
      Animated.spring(translateX, {
        toValue,
        useNativeDriver: true,
        bounciness: 0,
      }).start(() => {
        offsetRef.current = toValue;
      });
    },
    [translateX]
  );

  const openSwipe = useCallback(() => {
    onOpenSwipe(item.detailGUID);
    animateTo(-SWIPE_ACTION_WIDTH);
  }, [animateTo, item.detailGUID, onOpenSwipe]);

  const closeSwipe = useCallback(
    (shouldResetOpenKey: boolean) => {
      if (shouldResetOpenKey && openSwipeDetailGUID === item.detailGUID) {
        onOpenSwipe(null);
      }
      animateTo(0);
    },
    [animateTo, item.detailGUID, onOpenSwipe, openSwipeDetailGUID]
  );

  useEffect(() => {
    if (openSwipeDetailGUID !== item.detailGUID && offsetRef.current !== 0) {
      animateTo(0);
    }
  }, [animateTo, item.detailGUID, openSwipeDetailGUID]);

  const panResponder = useMemo(
    () =>
      PanResponder.create({
        onMoveShouldSetPanResponder: (_, gestureState) =>
          Math.abs(gestureState.dx) > Math.abs(gestureState.dy) && Math.abs(gestureState.dx) > 8,
        onPanResponderGrant: () => {
          if (openSwipeDetailGUID && openSwipeDetailGUID !== item.detailGUID) {
            onOpenSwipe(null);
          }
        },
        onPanResponderMove: (_, gestureState) => {
          const nextValue = clamp(offsetRef.current + gestureState.dx, -SWIPE_ACTION_WIDTH, 0);
          translateX.setValue(nextValue);
        },
        onPanResponderRelease: (_, gestureState) => {
          const nextValue = clamp(offsetRef.current + gestureState.dx, -SWIPE_ACTION_WIDTH, 0);
          if (nextValue <= -SWIPE_ACTION_WIDTH / 2) {
            openSwipe();
            return;
          }

          closeSwipe(true);
        },
        onPanResponderTerminate: () => {
          if (offsetRef.current <= -SWIPE_ACTION_WIDTH / 2) {
            openSwipe();
            return;
          }

          closeSwipe(true);
        },
      }),
    [closeSwipe, item.detailGUID, onOpenSwipe, openSwipe, openSwipeDetailGUID, translateX]
  );

  return (
    <View style={styles.swipeRow}>
      <View style={styles.deleteActionWrap}>
        <Pressable
          accessibilityRole="button"
          disabled={isBusy}
          onPress={() => {
            closeSwipe(true);
            void onDelete(item);
          }}
          style={({ pressed }) => [
            styles.deleteAction,
            pressed && !isBusy ? styles.deleteActionPressed : null,
            isBusy ? styles.deleteActionDisabled : null,
          ]}
        >
          <MaterialCommunityIcons name="trash-can-outline" size={20} color={HB_COLORS.white} />
          <Text style={styles.deleteActionText}>
            {isDeleting ? t("item.deleting") : t("item.delete")}
          </Text>
        </Pressable>
      </View>

      <Animated.View
        style={[
          styles.itemRow,
          isPriority ? styles.itemRowPriority : null,
          isPaused ? styles.itemRowPaused : null,
          { transform: [{ translateX }] },
        ]}
        {...panResponder.panHandlers}
      >
        <OrderThumbnail uri={item.productImage} size={compact ? 48 : 56} muted={isPaused} />
        <View style={styles.itemBody}>
          <Text numberOfLines={2} style={[styles.itemTitle, isPaused ? styles.itemTitleMuted : null]}>
            {item.productName || item.productCode}
          </Text>
          <View style={styles.itemMetaRow}>
            {isPriority ? <OrderStatusTag label={t("common:orderRow.justScanned")} tone="solidAction" /> : null}
            {isPaused ? <OrderStatusTag label={t("supplyNotice:cartPausedTag")} tone="solidDark" /> : null}
            <GradeTag grade={item.grade} />
            <Text numberOfLines={1} style={styles.itemNumberText}>
              {skuValue}
            </Text>
            {step > 1 ? (
              <Text numberOfLines={1} style={styles.itemMetaMuted}>
                · {t("common:orderRow.minOrder", { quantity: step })}
              </Text>
            ) : null}
            {hasZeroImportPrice ? <OrderStatusTag label={t("common:orderRow.zeroImportPrice")} tone="danger" /> : null}
          </View>
          <View style={styles.itemBottomRow}>
            <View style={styles.itemPriceColumn}>
              <Text numberOfLines={1} style={[styles.itemUnitPriceText, hasZeroImportPrice ? styles.zeroImportText : null]}>
                {t("common:orderRow.unitPrice", { amount: formatOrderMoney(importPrice) })}
              </Text>
              <Text numberOfLines={1} style={styles.itemSubtotalLine}>
                <Text style={styles.itemSubtotalLabel}>{t("common:orderRow.subtotal")} </Text>
                <Text
                  style={[
                    styles.itemSubtotalValue,
                    hasZeroImportPrice ? styles.zeroImportText : null,
                    isPaused ? styles.itemTitleMuted : null,
                  ]}
                >
                  {formatOrderMoney(importAmount)}
                </Text>
              </Text>
            </View>
            <OrderStepper
              quantity={item.quantity}
              step={step}
              compact={compact}
              busy={isBusy}
              increaseDisabled={isPaused}
              accessibilityLabel={t("common:labels.editCartQuantity", { quantity: item.quantity })}
              decreaseLabel={t("common:orderRow.decrease")}
              increaseLabel={t("common:orderRow.increase")}
              removeLabel={t("common:orderRow.remove")}
              onDecrease={() => void onUpdateQuantity(item, Math.max(0, item.quantity - step))}
              onIncrease={() => void onUpdateQuantity(item, item.quantity + step)}
              onEdit={() => onEditQuantity(item)}
            />
          </View>
        </View>
      </Animated.View>
    </View>
  );
}

export default function Cart() {
  const isFocused = useIsFocused();
  const router = useRouter();
  const viewport = useWindowDimensions();
  const { t, language } = useAppTranslation(["cart", "common", "supplyNotice"]);
  const queryClient = useQueryClient();
  const { selectedStore, selectedStoreCode } = useStores();
  const access = useAuthStore((state) => state.access);
  const preorderGate = usePreorderGate(
    selectedStoreCode,
    canBypassPreorderGate(access)
  );
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(10);
  const [keyword, setKeyword] = useState("");
  const [searchInput, setSearchInput] = useState("");
  const [moreMenuVisible, setMoreMenuVisible] = useState(false);
  const [pageSizeMenuVisible, setPageSizeMenuVisible] = useState(false);
  const [submitDialogVisible, setSubmitDialogVisible] = useState(false);
  const [quantityEditorItem, setQuantityEditorItem] = useState<StoreOrderCartItem | null>(null);
  const [quantityEditorStoreCode, setQuantityEditorStoreCode] = useState<string | null>(null);
  const [quantityDraft, setQuantityDraft] = useState("");
  const [quantityEditorError, setQuantityEditorError] = useState("");
  const quantityEditorSubmittingRef = useRef(false);
  const [orderRemarks, setOrderRemarks] = useState("");
  const [notice, setNotice] = useState<OrderNotice | null>(null);
  // 购物车的提示统一在底部结算栏闪现，不再用会盖住结算按钮的 Snackbar。
  const showNotice = useCallback((tone: OrderNoticeTone, title: string) => {
    setNotice({ tone, title });
  }, []);
  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    })
  ), [language, t]);
  const [activeCartItemCode, setActiveCartItemCode] = useState<string | null>(null);
  const [activeDeleteDetailGUID, setActiveDeleteDetailGUID] = useState<string | null>(null);
  const [openSwipeDetailGUID, setOpenSwipeDetailGUID] = useState<string | null>(null);
  const [priorityProductCode, setPriorityProductCode] = useState<string | null>(null);
  const [submitPending, setSubmitPending] = useState(false);
  const updateCartQuantity = useUpdateCartQuantity(selectedStoreCode);
  const removeCartLine = useRemoveCartLine(selectedStoreCode);
  const clearCart = useClearCart(selectedStoreCode);
  const quantityEditorBusy = Boolean(
    quantityEditorItem &&
    (updateCartQuantity.isPending || quantityEditorSubmittingRef.current)
  );

  const openPreorder = useCallback(() => {
    const firstActivation = preorderGate.activations[0];
    if (firstActivation && selectedStoreCode) {
      router.push({
        pathname: "/preorders/[activationGuid]",
        params: { activationGuid: firstActivation.activationGuid, storeCode: selectedStoreCode },
      } as unknown as Href);
      return;
    }
    router.push("/preorders" as Href);
  }, [preorderGate.activations, router, selectedStoreCode]);

  const cartQuery = useCartPage({
    page,
    pageSize,
    priorityProductCode,
    storeCode: selectedStoreCode,
  });

  const filteredItems = useMemo(() => {
    const normalizedKeyword = keyword.trim().toLowerCase();
    if (!normalizedKeyword) {
      return cartQuery.items;
    }

    return cartQuery.items.filter((item) =>
      [item.productName, item.productCode, item.itemNumber, item.barcode]
        .filter(Boolean)
        .some((value) => value!.toLowerCase().includes(normalizedKeyword))
    );
  }, [cartQuery.items, keyword]);

  const canGoNextPage = page * pageSize < cartQuery.total;

  const scanResult = useScanResult({
    onAddedToCart: async (product) => {
      setPriorityProductCode(product.productCode);
    },
    mode: "add-to-cart",
    storeCode: selectedStoreCode,
  });
  const globalCartMutationPending =
    useIsMutating({ mutationKey: ["cartMutation", selectedStoreCode ?? null] }) > 0;
  // 购物车写操作 pending 时禁止提交，避免提交到乐观缓存尚未确认的数量。
  const cartMutationPending =
    submitPending ||
    globalCartMutationPending ||
    scanResult.isBusy ||
    updateCartQuantity.isPending ||
    removeCartLine.isPending ||
    clearCart.isPending;
  const cartMutationPendingRef = useRef(cartMutationPending);
  cartMutationPendingRef.current = cartMutationPending;

  const hidScanner = useHidBarcodeScanner({
    enabled: isFocused,
    onScan: async (barcode) => {
      await scanResult.handleBarcode(barcode, "hid");
    },
  });

  useFocusEffect(
    useCallback(() => {
      if (hidScanner.focusHiddenInput) {
        hidScanner.focusHiddenInput();
      }
    }, [hidScanner.focusHiddenInput])
  );

  useEffect(() => {
    if (!(submitDialogVisible || Boolean(quantityEditorItem))) {
      return;
    }

    // 备注或数量弹窗需要软键盘输入，期间暂停隐藏扫码输入，避免它抢走焦点。
    hidScanner.pauseHiddenInputFocus();
    return () => {
      hidScanner.resumeHiddenInputFocus();
    };
  }, [
    hidScanner.pauseHiddenInputFocus,
    hidScanner.resumeHiddenInputFocus,
    quantityEditorItem,
    submitDialogVisible,
  ]);

  useEffect(() => {
    setPage(1);
  }, [pageSize, selectedStoreCode]);

  useEffect(() => {
    setPriorityProductCode(null);
    setOpenSwipeDetailGUID(null);
    setSubmitDialogVisible(false);
    setQuantityEditorItem(null);
    setQuantityEditorStoreCode(null);
    setQuantityDraft("");
    setQuantityEditorError("");
    setOrderRemarks("");
  }, [selectedStoreCode]);

  useEffect(() => {
    const maxPage = Math.max(1, Math.ceil(cartQuery.total / pageSize));
    if (page > maxPage) {
      setPage(maxPage);
    }
  }, [cartQuery.total, page, pageSize]);

  useEffect(() => {
    const nextNotice = mapScanFeedbackToNotice(scanResult.feedback, t);
    if (nextNotice) {
      setNotice(nextNotice);
    }
  }, [scanResult.feedback, t]);

  useEffect(() => {
    if (!notice) {
      return;
    }

    const dismissTimer = setTimeout(() => {
      setNotice(null);
    }, ORDER_NOTICE_DURATION_MS);

    return () => {
      clearTimeout(dismissTimer);
    };
  }, [notice]);

  async function handleUpdateQuantity(item: StoreOrderCartItem, nextQuantity: number) {
    setActiveCartItemCode(item.productCode);

    try {
      await updateCartQuantity.mutateAsync({
        nextQuantity,
        product: item,
      });
    } catch (error) {
      if (isPreorderRequiredError(error)) {
        void preorderGate.refresh();
        openPreorder();
      } else {
        showNotice("error", getErrorMessage(error, "messages.updateQtyFailed"));
      }
    } finally {
      setActiveCartItemCode(null);
    }
  }

  function handleEditQuantity(item: StoreOrderCartItem) {
    if (cartMutationPendingRef.current || quantityEditorSubmittingRef.current) {
      showNotice("info", t("common:loading"));
      return;
    }

    setOpenSwipeDetailGUID(null);
    setQuantityEditorItem(item);
    setQuantityEditorStoreCode(selectedStoreCode ?? null);
    setQuantityDraft(String(item.quantity));
    setQuantityEditorError("");
  }

  function resetQuantityEditor() {
    setQuantityEditorItem(null);
    setQuantityEditorStoreCode(null);
    setQuantityDraft("");
    setQuantityEditorError("");
  }

  function handleDismissQuantityEditor() {
    if (!canDismissCartQuantityEditor({
      isPending: updateCartQuantity.isPending,
      isSubmitting: quantityEditorSubmittingRef.current,
    })) {
      return;
    }

    resetQuantityEditor();
  }

  async function handleConfirmQuantityEdit() {
    if (
      !quantityEditorItem ||
      quantityEditorBusy ||
      cartMutationPendingRef.current || quantityEditorSubmittingRef.current
    ) {
      return;
    }

    if (
      !canSubmitCartQuantityEdit({
        currentStoreCode: selectedStoreCode,
        editorStoreCode: quantityEditorStoreCode,
        isPending: updateCartQuantity.isPending,
      })
    ) {
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

    const editorItem = quantityEditorItem;
    const currentItem = resolveCurrentCartQuantityItem(cartQuery.items, editorItem);
    if (!currentItem) {
      resetQuantityEditor();
      showNotice("warning", t("quantityEditor.itemUnavailable"));
      return;
    }

    if (!shouldSubmitCartQuantityUpdate(currentItem.quantity, nextQuantity)) {
      resetQuantityEditor();
      return;
    }

    // ref 在 React 重渲染前同步置位，避免快速双击确认产生两个绝对数量写入。
    quantityEditorSubmittingRef.current = true;
    setQuantityEditorError("");
    setActiveCartItemCode(currentItem.productCode);
    try {
      await updateCartQuantity.mutateAsync({
        nextQuantity,
        product: currentItem,
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
        showNotice("error", message);
      }
    } finally {
      quantityEditorSubmittingRef.current = false;
      setActiveCartItemCode(null);
    }
  }

  async function handleDeleteCartItem(item: StoreOrderCartItem) {
    setActiveDeleteDetailGUID(item.detailGUID);

    try {
      await removeCartLine.mutateAsync({
        detailGUID: item.detailGUID,
      });
      if (openSwipeDetailGUID === item.detailGUID) {
        setOpenSwipeDetailGUID(null);
      }
      if (priorityProductCode === item.productCode) {
        setPriorityProductCode(null);
      }
    } catch (error) {
      showNotice("error", getErrorMessage(error, "messages.deleteFailed"));
    } finally {
      setActiveDeleteDetailGUID(null);
    }
  }

  async function handleClearCart() {
    if (cartMutationPendingRef.current) {
      showNotice("info", t("common:loading"));
      return;
    }

    try {
      await clearCart.mutateAsync();
      setOpenSwipeDetailGUID(null);
      setPriorityProductCode(null);
      setPage(1);
      showNotice("success", t("messages.clearSuccess"));
    } catch (error) {
      showNotice("error", getErrorMessage(error, "messages.clearFailed"));
    }
  }

  function confirmClearCart() {
    Alert.alert(t("confirm.clearTitle"), t("confirm.clearMessage"), [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("confirm.clearAction"),
        style: "destructive",
        onPress: () => {
          void handleClearCart();
        },
      },
    ]);
  }

  async function handleSubmitCart() {
    if (cartMutationPendingRef.current) {
      showNotice("info", t("common:loading"));
      return;
    }

    if (preorderGate.normalOrderBlocked) {
      setSubmitDialogVisible(false);
      openPreorder();
      return;
    }

    if (!selectedStoreCode) {
      showNotice("warning", t("messages.needStore"));
      return;
    }

    if (!cartQuery.total) {
      showNotice("warning", t("messages.emptyCart"));
      return;
    }

    // 备注是选填项；空白备注不传给后端，避免保存无意义空字符串。
    const trimmedRemarks = orderRemarks.trim();

    setSubmitPending(true);
    try {
      await submitStoreOrder(selectedStoreCode, trimmedRemarks || undefined);
      reconcileSubmittedCartCache(queryClient, selectedStoreCode);
      setPriorityProductCode(null);
      setOpenSwipeDetailGUID(null);
      setSubmitDialogVisible(false);
      setOrderRemarks("");
      setPage(1);
      showNotice("success", t("messages.submitSuccess"));
      router.push("/(shell)/orders");
    } catch (error) {
      if (isPreorderRequiredError(error)) {
        setSubmitDialogVisible(false);
        void preorderGate.refresh();
        openPreorder();
      } else {
        showNotice("error", getErrorMessage(error, "messages.submitFailed"));
      }
    } finally {
      setSubmitPending(false);
    }
  }

  function confirmSubmitCart() {
    if (cartMutationPendingRef.current) {
      showNotice("info", t("common:loading"));
      return;
    }

    if (preorderGate.normalOrderBlocked) {
      openPreorder();
      return;
    }

    if (!selectedStoreCode) {
      showNotice("warning", t("messages.needStore"));
      return;
    }

    if (!cartQuery.total) {
      showNotice("warning", t("messages.emptyCart"));
      return;
    }

    setOrderRemarks("");
    setSubmitDialogVisible(true);
  }

  function renderCartItem({ item }: { item: StoreOrderCartItem }) {
    return (
      <CartListItemCard
        clearCartPending={clearCart.isPending}
        compact={viewport.width <= 390}
        isDeleting={activeDeleteDetailGUID === item.detailGUID}
        isPriority={Boolean(priorityProductCode) && priorityProductCode === item.productCode}
        isUpdating={activeCartItemCode === item.productCode}
        item={item}
        t={t}
        onDelete={handleDeleteCartItem}
        onEditQuantity={handleEditQuantity}
        onOpenSwipe={setOpenSwipeDetailGUID}
        onUpdateQuantity={handleUpdateQuantity}
        openSwipeDetailGUID={openSwipeDetailGUID}
      />
    );
  }

  const totalPages = resolveTotalPages(cartQuery.total, pageSize);
  const storeName = selectedStore?.storeName || t("common:labels.selectStore");
  // 购物车为空时结算栏隐藏；但扫码提示仍需要一个固定位置显示。
  const showCheckoutBar = Boolean(cartQuery.total) || Boolean(notice);
  // 当前页有暂停供货商品时在结算栏提前提示；服务端仍是提交时的最终拦截方。
  const hasPausedLines = cartQuery.items.some((item) => item.isActive === false);
  const submitDisabled =
    !selectedStoreCode || !cartQuery.total || (cartMutationPending && !submitPending);

  function handleBack() {
    if (router.canGoBack()) {
      router.back();
      return;
    }

    router.push("/(shell)/home");
  }

  function handleSearchChange(value: string) {
    setSearchInput(value);
    if (!value.trim()) {
      setKeyword("");
    }
  }

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container}>
      <View style={styles.header}>
        <View style={styles.headerRow}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("header.back")}
            onPress={handleBack}
            style={({ pressed }) => [styles.headerIconButton, pressed ? styles.pressed : null]}
          >
            <MaterialCommunityIcons name="chevron-left" size={26} color={HB_COLORS.textPrimary} />
          </Pressable>
          <View style={styles.headerTitleWrap}>
            <Text style={styles.headerTitle}>{t("title")}</Text>
            <View style={styles.headerStoreRow}>
              <MaterialCommunityIcons name="storefront-outline" size={13} color={HB_COLORS.textSecondary} />
              <Text numberOfLines={1} style={styles.headerStore}>
                {storeName}
              </Text>
            </View>
          </View>
          <Menu
            visible={moreMenuVisible}
            onDismiss={() => setMoreMenuVisible(false)}
            anchorPosition="bottom"
            contentStyle={styles.menuContent}
            anchor={
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t("header.more")}
                onPress={() => setMoreMenuVisible(true)}
                style={({ pressed }) => [styles.headerIconButton, pressed ? styles.pressed : null]}
              >
                <MaterialCommunityIcons name="dots-horizontal" size={24} color={HB_COLORS.textPrimary} />
              </Pressable>
            }
          >
            <Menu.Item
              leadingIcon="storefront-outline"
              title={t("filters.goHome")}
              onPress={() => {
                setMoreMenuVisible(false);
                router.push("/(shell)/home");
              }}
            />
            <Menu.Item
              leadingIcon="delete-sweep-outline"
              title={t("header.clearCart")}
              titleStyle={styles.menuDangerText}
              disabled={!selectedStoreCode || !cartQuery.total || cartMutationPending}
              onPress={() => {
                setMoreMenuVisible(false);
                confirmClearCart();
              }}
            />
          </Menu>
        </View>
        <Searchbar
          placeholder={t("filters.searchPlaceholder")}
          value={searchInput}
          onChangeText={handleSearchChange}
          onSubmitEditing={() => setKeyword(searchInput.trim())}
          onIconPress={() => setKeyword(searchInput.trim())}
          elevation={0}
          style={styles.searchbar}
          inputStyle={styles.searchInputText}
        />
      </View>

      <PreorderGateBanner gate={preorderGate} onOpen={openPreorder} />

      <FlatList
        data={filteredItems}
        keyExtractor={(item) => item.detailGUID || item.productCode}
        renderItem={renderCartItem}
        contentContainerStyle={styles.listContent}
        keyboardShouldPersistTaps="handled"
        ListHeaderComponent={
          cartQuery.total ? (
            <View>
              <View style={styles.listMeta}>
                <Text style={styles.listMetaText}>
                  {t("listMeta.summary", { count: cartQuery.total, page, pages: totalPages })}
                </Text>
                <Menu
                  visible={pageSizeMenuVisible}
                  onDismiss={() => setPageSizeMenuVisible(false)}
                  anchorPosition="bottom"
                  contentStyle={styles.menuContent}
                  anchor={
                    <Pressable
                      accessibilityRole="button"
                      accessibilityLabel={t("listMeta.pageSize", { size: pageSize })}
                      onPress={() => setPageSizeMenuVisible(true)}
                      style={({ pressed }) => [styles.pageSizeButton, pressed ? styles.pressed : null]}
                    >
                      <Text style={styles.pageSizeText}>{t("listMeta.pageSize", { size: pageSize })}</Text>
                      <MaterialCommunityIcons name="chevron-down" size={16} color={HB_COLORS.textSecondary} />
                    </Pressable>
                  }
                >
                  {PAGE_SIZE_OPTIONS.map((option) => (
                    <Menu.Item
                      key={option}
                      title={t("listMeta.pageSizeOption", { size: option })}
                      trailingIcon={pageSize === option ? "check" : undefined}
                      onPress={() => {
                        setPageSize(option);
                        setPageSizeMenuVisible(false);
                      }}
                    />
                  ))}
                </Menu>
              </View>
              {keyword ? (
                <Text style={styles.searchScope}>{t("listMeta.searchScope", { keyword })}</Text>
              ) : null}
            </View>
          ) : null
        }
        ListEmptyComponent={
          <View style={styles.emptyWrap}>
            <EmptyState
              title={selectedStoreCode ? t("empty.cartEmptyTitle") : t("empty.selectStoreTitle")}
              description={
                selectedStoreCode ? t("empty.cartEmptyDescription") : t("empty.selectStoreDescription")
              }
              actionLabel={t("empty.goHome")}
              onAction={() => router.push("/(shell)/home")}
            />
          </View>
        }
        ListFooterComponent={
          cartQuery.total ? (
            <View style={styles.paginationRow}>
              <Button
                mode="outlined"
                icon="chevron-left"
                disabled={page <= 1}
                onPress={() => setPage((value) => value - 1)}
                style={styles.paginationButton}
                contentStyle={styles.paginationButtonContent}
              >
                {t("pagination.previous")}
              </Button>
              <Text style={styles.paginationText}>
                {t("pagination.pageOf", { page, pages: totalPages })}
              </Text>
              <Button
                mode="outlined"
                icon="chevron-right"
                disabled={!canGoNextPage}
                onPress={() => setPage((value) => value + 1)}
                style={styles.paginationButton}
                contentStyle={[styles.paginationButtonContent, styles.paginationNextContent]}
              >
                {t("pagination.next")}
              </Button>
            </View>
          ) : null
        }
      />

      {showCheckoutBar ? (
        <OrderBottomBar
          notice={notice}
          emphasizeTitle
          title={formatOrderMoney(cartQuery.stats.totalImportAmount)}
          subtitle={
            preorderGate.normalOrderBlocked
              ? t("checkout.preorderBlocked")
              : cartMutationPending && !submitPending
                ? t("checkout.syncing")
                : hasPausedLines
                  ? t("checkout.pausedLines")
                : t("checkout.summaryLine", {
                    sku: cartQuery.stats.skuCount,
                    quantity: cartQuery.stats.totalQuantity,
                  })
          }
          action={
            <OrderBarPrimaryButton
              large
              label={
                preorderGate.normalOrderBlocked
                  ? t("checkout.preorderFirst")
                  : submitPending
                    ? t("checkout.submitting")
                    : t("checkout.submit")
              }
              icon={preorderGate.normalOrderBlocked ? undefined : "arrow-right"}
              tone={preorderGate.normalOrderBlocked ? "warning" : "action"}
              loading={submitPending}
              disabled={submitDisabled}
              onPress={confirmSubmitCart}
            />
          }
        />
      ) : null}

      {cartQuery.isLoading ? <LoadingOverlay /> : null}

      <BusinessSheet
        visible={Boolean(quantityEditorItem)}
        title={t("quantityEditor.title")}
        onDismiss={handleDismissQuantityEditor}
        dismissable={!quantityEditorBusy}
        footer={
          <View style={styles.sheetActions}>
            <Button
              mode="outlined"
              disabled={quantityEditorBusy}
              onPress={handleDismissQuantityEditor}
              style={styles.sheetActionButton}
              contentStyle={styles.sheetActionContent}
            >
              {t("common:actions.cancel")}
            </Button>
            <Button
              mode="contained"
              loading={quantityEditorBusy}
              disabled={quantityEditorBusy}
              onPress={() => void handleConfirmQuantityEdit()}
              style={[styles.sheetActionButton, styles.sheetActionPrimary]}
              contentStyle={styles.sheetActionContent}
            >
              {t("common:actions.confirm")}
            </Button>
          </View>
        }
      >
        {quantityEditorItem ? (
          <>
            <View style={styles.editorProduct}>
              <OrderThumbnail uri={quantityEditorItem.productImage} size={44} />
              <View style={styles.editorProductCopy}>
                <Text numberOfLines={2} style={styles.editorProductName}>
                  {quantityEditorItem.productName || quantityEditorItem.productCode}
                </Text>
                <View style={styles.editorProductMeta}>
                  <GradeTag grade={quantityEditorItem.grade} />
                  <Text numberOfLines={1} style={styles.editorItemNumber}>
                    {quantityEditorItem.itemNumber || quantityEditorItem.productCode}
                  </Text>
                  <Text numberOfLines={1} style={styles.editorMetaMuted}>
                    · {t("common:orderRow.minOrder", { quantity: resolveOrderStep(quantityEditorItem.minOrderQuantity) })}
                  </Text>
                </View>
              </View>
            </View>
            {/* 只收集覆盖数量；确认后继续走统一乐观更新与失败回滚链路。 */}
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
              style={styles.editorInput}
              contentStyle={styles.editorInputContent}
            />
            <Text style={styles.editorHelper}>
              {t("quantityEditor.helper", { current: quantityEditorItem.quantity })}
            </Text>
            {quantityEditorError ? (
              <Text
                variant="bodySmall"
                accessibilityLiveRegion="polite"
                accessibilityRole="alert"
                style={styles.quantityEditorError}
              >
                {quantityEditorError}
              </Text>
            ) : null}
            <QuantityPresetRow
              step={resolveOrderStep(quantityEditorItem.minOrderQuantity)}
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
        visible={submitDialogVisible}
        title={t("confirm.submitTitle")}
        subtitle={t("confirm.submitMessage")}
        dismissable={!submitPending}
        onDismiss={() => {
          if (!submitPending) {
            setSubmitDialogVisible(false);
          }
        }}
        footer={
          <View style={styles.sheetActions}>
            <Button
              mode="outlined"
              disabled={submitPending}
              onPress={() => setSubmitDialogVisible(false)}
              style={styles.sheetActionButton}
              contentStyle={styles.sheetActionContent}
            >
              {t("common:actions.cancel")}
            </Button>
            <Button
              mode="contained"
              icon="arrow-right"
              loading={submitPending}
              disabled={
                !selectedStoreCode ||
                !cartQuery.total ||
                cartMutationPending
              }
              onPress={() => {
                void handleSubmitCart();
              }}
              style={[styles.sheetActionButton, styles.sheetActionPrimary]}
              contentStyle={[styles.sheetActionContent, styles.submitButtonContent]}
            >
              {t("confirm.submitAction")}
            </Button>
          </View>
        }
      >
        {/* 提交前把门店和金额放在最显眼处核对，避免给错门店下单。 */}
        <View style={styles.submitStore}>
          <View style={styles.submitStoreIcon}>
            <MaterialCommunityIcons name="storefront-outline" size={18} color={HB_COLORS.action} />
          </View>
          <View style={styles.submitStoreCopy}>
            <Text style={styles.submitStoreLabel}>{t("submitSheet.store")}</Text>
            <Text numberOfLines={1} style={styles.submitStoreName}>
              {storeName}
            </Text>
          </View>
        </View>
        <View style={styles.submitSummary}>
          <Text style={styles.submitTotalLabel}>{t("submitSheet.totalLabel")}</Text>
          <Text style={styles.submitTotalValue}>{formatOrderMoney(cartQuery.stats.totalImportAmount)}</Text>
          <View style={styles.submitGrid}>
            <View style={styles.submitGridCell}>
              <Text style={styles.submitGridLabel}>{t("submitSheet.sku")}</Text>
              <Text style={styles.submitGridValue}>{cartQuery.stats.skuCount}</Text>
            </View>
            <View style={styles.submitGridCell}>
              <Text style={styles.submitGridLabel}>{t("submitSheet.quantity")}</Text>
              <Text style={styles.submitGridValue}>{cartQuery.stats.totalQuantity}</Text>
            </View>
            <View style={styles.submitGridCell}>
              <Text style={styles.submitGridLabel}>{t("submitSheet.salesAmount")}</Text>
              <Text style={styles.submitGridValue}>
                {formatOrderMoney(Number(cartQuery.cart?.totalAmount ?? 0))}
              </Text>
            </View>
            <View style={styles.submitGridCell}>
              <Text style={styles.submitGridLabel}>{t("submitSheet.volume")}</Text>
              <Text style={styles.submitGridValue}>
                {Number(cartQuery.cart?.totalVolume ?? 0).toFixed(2)}
              </Text>
            </View>
          </View>
        </View>
        <PaperTextInput
          mode="outlined"
          label={t("remarks.label")}
          placeholder={t("remarks.placeholder")}
          value={orderRemarks}
          onChangeText={(value) => setOrderRemarks(value.slice(0, ORDER_REMARKS_MAX_LENGTH))}
          multiline
          numberOfLines={3}
          maxLength={ORDER_REMARKS_MAX_LENGTH}
          disabled={submitPending}
          style={styles.submitRemarksInput}
        />
        <Text style={styles.submitRemarksCounter}>
          {t("remarks.counter", {
            count: orderRemarks.length,
            max: ORDER_REMARKS_MAX_LENGTH,
          })}
        </Text>
      </BusinessSheet>

      <ScanResultPicker
        visible={Boolean(scanResult.selectionState)}
        barcode={scanResult.selectionState?.barcode}
        items={scanResult.selectionState?.items ?? []}
        selectLabel={t("common:actions.select")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => {
          scanResult.clearSelection();
        }}
        onSelect={async (product) => {
          await scanResult.confirmSelection(product);
        }}
      />

      {hidScanner.mode === "textInput" && hidScanner.textInputProps ? (
        <NativeTextInput style={styles.hiddenInput} {...hidScanner.textInputProps} />
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
  headerRow: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    marginHorizontal: -8,
  },
  headerIconButton: {
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
  },
  headerTitleWrap: {
    flex: 1,
    minWidth: 0,
  },
  headerTitle: {
    color: HB_COLORS.textPrimary,
    fontSize: 17,
    lineHeight: 22,
    fontWeight: "700",
  },
  headerStoreRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
  },
  headerStore: {
    flexShrink: 1,
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
  },
  menuContent: {
    backgroundColor: HB_COLORS.white,
  },
  menuDangerText: {
    color: HB_COLORS.danger,
    fontWeight: "600",
  },
  searchbar: {
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
  listContent: {
    flexGrow: 1,
    paddingBottom: 12,
  },
  listMeta: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingLeft: 12,
    paddingRight: 4,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outline,
  },
  listMetaText: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
    fontVariant: ["tabular-nums"],
  },
  pageSizeButton: {
    minHeight: 40,
    flexDirection: "row",
    alignItems: "center",
    gap: 2,
    paddingHorizontal: 8,
  },
  pageSizeText: {
    color: "#344054",
    fontSize: 12,
  },
  searchScope: {
    paddingHorizontal: 12,
    paddingVertical: 6,
    color: ORDER_COLORS.subtleText,
    fontSize: 12,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  emptyWrap: {
    flex: 1,
    justifyContent: "center",
    paddingHorizontal: 16,
    paddingVertical: 24,
  },
  swipeRow: {
    position: "relative",
    backgroundColor: "#D92D20",
  },
  deleteActionWrap: {
    position: "absolute",
    top: 0,
    right: 0,
    bottom: 0,
    width: SWIPE_ACTION_WIDTH,
  },
  deleteAction: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
  },
  deleteActionPressed: {
    backgroundColor: "#B42318",
  },
  deleteActionDisabled: {
    opacity: 0.6,
  },
  deleteActionText: {
    color: HB_COLORS.white,
    fontSize: 14,
    fontWeight: "700",
  },
  itemRow: {
    flexDirection: "row",
    gap: 12,
    paddingHorizontal: 12,
    paddingVertical: 10,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outline,
  },
  itemRowPriority: {
    backgroundColor: ORDER_COLORS.inCartRow,
  },
  itemRowPaused: {
    backgroundColor: ORDER_COLORS.mutedRow,
  },
  itemBody: {
    flex: 1,
    minWidth: 0,
    gap: 6,
  },
  itemTitle: {
    color: HB_COLORS.textPrimary,
    fontSize: 14,
    lineHeight: 18,
    fontWeight: "600",
  },
  itemTitleMuted: {
    color: HB_COLORS.textSecondary,
  },
  itemMetaRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 6,
  },
  itemNumberText: {
    flexShrink: 1,
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
    fontFamily: ORDER_MONO_FONT,
  },
  itemMetaMuted: {
    color: ORDER_COLORS.subtleText,
    fontSize: 12,
    lineHeight: 16,
  },
  itemBottomRow: {
    flexDirection: "row",
    alignItems: "flex-end",
    gap: 8,
  },
  itemPriceColumn: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  itemUnitPriceText: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
    fontVariant: ["tabular-nums"],
  },
  itemSubtotalLine: {
    fontVariant: ["tabular-nums"],
  },
  itemSubtotalLabel: {
    color: ORDER_COLORS.subtleText,
    fontSize: 11,
  },
  itemSubtotalValue: {
    color: HB_COLORS.textPrimary,
    fontSize: 15,
    fontWeight: "700",
  },
  zeroImportText: {
    color: ORDER_COLORS.danger,
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
  submitButtonContent: {
    flexDirection: "row-reverse",
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
  },
  submitStore: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    padding: 12,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: ORDER_COLORS.stepperBorder,
    backgroundColor: ORDER_COLORS.tonalBackground,
  },
  submitStoreIcon: {
    width: 32,
    height: 32,
    borderRadius: 8,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: HB_COLORS.white,
  },
  submitStoreCopy: {
    flex: 1,
    minWidth: 0,
  },
  submitStoreLabel: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
  },
  submitStoreName: {
    color: ORDER_COLORS.tonalText,
    fontSize: 16,
    lineHeight: 22,
    fontWeight: "700",
  },
  submitSummary: {
    gap: 4,
    padding: 14,
    borderRadius: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outline,
    backgroundColor: ORDER_COLORS.mutedRow,
  },
  submitTotalLabel: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    lineHeight: 16,
  },
  submitTotalValue: {
    color: HB_COLORS.textPrimary,
    fontSize: 28,
    lineHeight: 34,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  submitGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    marginTop: 8,
    paddingTop: 10,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outline,
    rowGap: 8,
  },
  submitGridCell: {
    width: "50%",
    flexDirection: "row",
    justifyContent: "space-between",
    paddingRight: 12,
  },
  submitGridLabel: {
    color: HB_COLORS.textSecondary,
    fontSize: 13,
  },
  submitGridValue: {
    color: HB_COLORS.textPrimary,
    fontSize: 13,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  submitRemarksInput: {
    minHeight: 88,
    backgroundColor: HB_COLORS.white,
  },
  submitRemarksCounter: {
    alignSelf: "flex-end",
    color: ORDER_COLORS.subtleText,
    fontSize: 12,
  },
  hiddenInput: {
    position: "absolute",
    width: 1,
    height: 1,
    opacity: 0,
  },
});
