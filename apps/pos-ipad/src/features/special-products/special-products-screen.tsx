import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  FlatList,
  Image,
  Modal,
  PanResponder,
  StyleSheet,
  Text,
  View,
} from "react-native";

import {
  resolveSpecialProductsLocale,
  specialProductsStatusCopyKey,
  specialProductsText,
  type SpecialProductsCopyKey,
} from "./special-products-copy";
import {
  type SpecialProductsPresenter,
  type SpecialProductsStatusCode,
  type SpecialProductsState,
} from "./special-products-presenter";

import type { SpecialProductItem } from "@/core/contracts";
import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPanResponderView } from "@/ui/controls/pos-pan-responder-view";
import { PosPill } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "@/ui/theme";

export const SPECIAL_PRODUCTS_MIN_TOUCH_TARGET = 44;
export const SPECIAL_PRODUCTS_GRID_COLUMNS = 5;
const SPECIAL_PRODUCTS_GRID_GAP = 10;

/**
 * 由拖拽位移计算目标网格索引（行列整数格取整，越界收敛到有效范围）。
 * 纯函数便于单元测试。
 */
export function specialProductsDragTargetIndex(
  fromIndex: number,
  dx: number,
  dy: number,
  columnCount: number,
  cellWidth: number,
  cellHeight: number,
  itemCount: number,
): number {
  if (
    columnCount <= 0 ||
    cellWidth <= 0 ||
    cellHeight <= 0 ||
    itemCount <= 0
  ) {
    return fromIndex;
  }
  const fromRow = Math.floor(fromIndex / columnCount);
  const fromCol = fromIndex % columnCount;
  const col = clampInt(
    fromCol + Math.round(dx / cellWidth),
    0,
    columnCount - 1,
  );
  const maxRow = Math.max(0, Math.ceil(itemCount / columnCount) - 1);
  const row = clampInt(fromRow + Math.round(dy / cellHeight), 0, maxRow);
  return Math.min(row * columnCount + col, itemCount - 1);
}

function clampInt(value: number, lower: number, upper: number): number {
  return Math.min(Math.max(value, lower), upper);
}

/** 拖拽中实时计算目标插槽索引；网格尺寸未知时返回 null（不高亮）。 */
function dragTargetIndex(
  state: SpecialProductsState,
  drag: Readonly<{
    code: string;
    dx: number;
    dy: number;
    fromIndex: number;
  }>,
  width: number,
  height: number,
): number | null {
  if (width <= 0 || height <= 0) return null;
  return specialProductsDragTargetIndex(
    drag.fromIndex,
    drag.dx,
    drag.dy,
    SPECIAL_PRODUCTS_GRID_COLUMNS,
    width,
    height,
    state.items.length,
  );
}

type SpecialProductsScreenProps = Readonly<{
  onBack?(): void;
  presenter: SpecialProductsScreenPresenter;
}>;

type SpecialProductsTranslate = (
  key: SpecialProductsCopyKey,
  values?: Readonly<Record<string, string | number>>,
) => string;

export type SpecialProductsScreenPresenter = Pick<
  SpecialProductsPresenter,
  | "addToCart"
  | "download"
  | "getState"
  | "load"
  | "mark"
  | "moveTo"
  | "reorder"
  | "searchCandidates"
  | "setSearchQuery"
  | "subscribe"
> & Readonly<{ getState(): SpecialProductsState }>;

/**
 * 特殊商品页：整张卡片就是加购按钮，本地列表离线也能浏览和加购。
 * 管理操作（下载、添加、排序、取消标记）只在有 Manage 权限时出现，且需要在线；
 * 其中排序与取消标记收在「整理」模式里，日常状态下卡片上没有小按钮，避免误触。
 */
export function SpecialProductsScreen({
  onBack,
  presenter,
}: SpecialProductsScreenProps) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const [addModalVisible, setAddModalVisible] = useState(false);
  // 整理模式是页面内的本地状态，默认关闭；离开页面即恢复日常状态。
  const [organizing, setOrganizing] = useState(false);
  const [drag, setDrag] = useState<Readonly<{
    code: string;
    dx: number;
    dy: number;
    fromIndex: number;
  }> | null>(null);
  const [cellWidth, setCellWidth] = useState(0);
  const [cellHeight, setCellHeight] = useState(0);
  const locale = resolveSpecialProductsLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t: SpecialProductsTranslate = (key, values) =>
    specialProductsText(locale, key, values);
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );

  useEffect(() => {
    void presenter.load();
  }, [presenter]);

  // 管理写操作的统一锁定条件：离线或正在处理上一个写操作。
  const managementLocked = !state.online || state.busy;
  // 整理模式只对有管理权限的收银员生效；权限消失时自动失效，
  // 卡片不会停在「点了不加购」的状态。
  const organizeActive = organizing && state.access.canManage;
  const loading = state.kind === "loading";

  return (
    <PosScreenFrame
      active="none"
      header={
        <PosPageHeader
          actions={
            <>
              <PosButton
                disabled={state.busy || loading}
                icon="refresh"
                label={t("action.refreshLocal")}
                onPress={() => void presenter.load()}
                size="medium"
                testID="special-products-refresh-local"
                tone="quiet"
              />
              {state.access.canManage ? (
                <>
                  <PosButton
                    disabled={managementLocked}
                    icon="cloud-download-outline"
                    label={
                      state.busy ? t("action.working") : t("action.download")
                    }
                    onPress={() => void presenter.download()}
                    size="medium"
                    testID="special-products-download"
                    tone="quiet"
                  />
                  <PosButton
                    disabled={managementLocked}
                    icon="plus"
                    label={t("management.title")}
                    onPress={() => {
                      // 打开弹窗前清空上次搜索，避免旧候选误导
                      presenter.setSearchQuery("");
                      setAddModalVisible(true);
                    }}
                    size="medium"
                    testID="special-products-add-product"
                    tone="quiet"
                  />
                  <PosButton
                    // 进入整理与其他管理操作同条件（在线且空闲）；
                    // 退出整理任何时候都允许，掉线后也能回到可加购的日常状态。
                    disabled={!organizeActive && managementLocked}
                    icon={organizeActive ? "check" : "sort-variant"}
                    label={
                      organizeActive ? t("organize.done") : t("organize.start")
                    }
                    onPress={() => setOrganizing(!organizeActive)}
                    size="medium"
                    testID="special-products-organize"
                    tone={organizeActive ? "primary" : "secondary"}
                  />
                </>
              ) : null}
            </>
          }
          backLabel={t("action.back")}
          backTestID="special-products-back"
          onBack={onBack}
          subtitle={t("header.subtitle")}
          title={t("header.title")}
        />
      }
      testID="special-products-screen"
    >
      <View style={styles.page}>
        {!state.online ? (
          <NoticeBanner
            icon="wifi-off"
            liveRegion
            testID="special-products-offline-note"
            text={t("offlineNote")}
            tone="info"
          />
        ) : null}

        {state.statusCode ? (
          <StatusBanner statusCode={state.statusCode} t={t} />
        ) : null}

        {organizeActive ? (
          // 整理状态：黄色提示条取代小标题行，明确告诉收银员此时点卡片不会加购。
          <NoticeBanner
            icon="sort-variant"
            testID="special-products-organize-banner"
            text={t("organize.banner")}
            tone="warning"
            trailing={
              loading ? (
                <ActivityIndicator
                  color={posColors.accent}
                  testID="special-products-loading"
                />
              ) : null
            }
          />
        ) : (
          <View style={styles.sectionHeader}>
            <Text style={styles.sectionTitle}>{t("catalog.title")}</Text>
            <PosPill
              label={t("catalog.itemCount", { count: state.items.length })}
              style={styles.sectionPill}
            />
            {loading ? (
              <ActivityIndicator
                color={posColors.accent}
                testID="special-products-loading"
              />
            ) : null}
            {state.access.canAddToCart && state.items.length > 0 ? (
              <Text numberOfLines={1} style={styles.sectionHint}>
                {t("catalog.tapHint")}
              </Text>
            ) : null}
          </View>
        )}

        {state.kind === "unauthorized" ? (
          <EmptyState
            icon="lock-outline"
            message={t("catalog.unauthorized")}
            testID="special-products-unauthorized"
          />
        ) : null}
        {state.kind === "failed" && state.items.length === 0 ? (
          <EmptyState
            icon="alert-circle-outline"
            message={t("catalog.failed")}
            testID="special-products-failed"
          />
        ) : null}
        {state.kind === "ready" && state.items.length === 0 ? (
          <EmptyState
            icon="star-outline"
            message={t("catalog.empty")}
            testID="special-products-empty"
          />
        ) : null}
        {state.items.length > 0 ? (
          <FlatList
            columnWrapperStyle={styles.productCardRow}
            contentContainerStyle={styles.productList}
            data={state.items}
            keyExtractor={(item) => item.productCode}
            numColumns={SPECIAL_PRODUCTS_GRID_COLUMNS}
            renderItem={({ item, index }) => (
              <SpecialProductCard
                canAddToCart={state.access.canAddToCart}
                // 长按拖动排序只在整理状态下启用，日常状态长按不会抬起卡片。
                canDrag={organizeActive && !managementLocked}
                cellHeight={cellHeight}
                disabled={state.busy}
                drag={drag}
                dragTargetIndex={
                  drag
                    ? dragTargetIndex(state, drag, cellWidth, cellHeight)
                    : null
                }
                index={index}
                item={item}
                itemCount={state.items.length}
                online={state.online}
                onAddToCart={() => void presenter.addToCart(item.productCode)}
                onDragEnd={(code, fromIndex, dx, dy) => {
                  const targetIndex = specialProductsDragTargetIndex(
                    fromIndex,
                    dx,
                    dy,
                    SPECIAL_PRODUCTS_GRID_COLUMNS,
                    cellWidth,
                    cellHeight,
                    state.items.length,
                  );
                  setDrag(null);
                  if (targetIndex !== fromIndex) {
                    void presenter.moveTo(code, targetIndex);
                  }
                }}
                onDragMove={(code, fromIndex, dx, dy) =>
                  setDrag({ code, fromIndex, dx, dy })
                }
                onDragStart={(code, fromIndex) =>
                  setDrag({ code, fromIndex, dx: 0, dy: 0 })
                }
                onMeasureCell={(width, height) => {
                  // 从卡片实测尺寸推导网格单元格（宽=列宽，高=卡片高+行距）
                  if (width > 0) setCellWidth(width);
                  if (height > 0) setCellHeight(height + SPECIAL_PRODUCTS_GRID_GAP);
                }}
                onMoveDown={() => void presenter.reorder(item.productCode, 1)}
                onMoveUp={() => void presenter.reorder(item.productCode, -1)}
                onRemove={() => void presenter.mark(item.productCode, false)}
                organizing={organizeActive}
                t={t}
              />
            )}
            style={styles.productListFrame}
            testID="special-products-list"
          />
        ) : null}
      </View>

      {state.access.canManage ? (
        <SpecialProductsAddModal
          busy={state.busy}
          candidates={state.candidates}
          onAdd={(productCode) => {
            // 关闭弹窗后由页面状态横幅呈现 mark 结果
            setAddModalVisible(false);
            void presenter.mark(productCode, true);
          }}
          onClose={() => setAddModalVisible(false)}
          onQueryChange={(query) => presenter.setSearchQuery(query)}
          onSearch={() => void presenter.searchCandidates()}
          query={state.searchQuery}
          searching={state.searching}
          t={t}
          visible={addModalVisible}
        />
      ) : null}
    </PosScreenFrame>
  );
}

export function SpecialProductsUnavailableScreen({
  onBack,
}: Readonly<{ onBack(): void }>) {
  usePosThemeVersion();
  const { i18n } = useTranslation();
  const locale = resolveSpecialProductsLocale(
    i18n.resolvedLanguage ?? i18n.language,
  );
  const t: SpecialProductsTranslate = (key, values) =>
    specialProductsText(locale, key, values);
  return (
    <PosScreenFrame
      active="none"
      header={
        <PosPageHeader
          backLabel={t("unavailable.back")}
          backTestID="special-products-unavailable-back"
          onBack={onBack}
          title={t("unavailable.eyebrow")}
        />
      }
      testID="special-products-runtime-unavailable"
    >
      <View style={styles.unavailable}>
        <View style={styles.unavailableCard}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.mutedInk}
            name="alert-circle-outline"
            size={56}
          />
          <Text style={styles.unavailableTitle}>{t("unavailable.title")}</Text>
          <Text style={styles.unavailableHint}>{t("unavailable.hint")}</Text>
        </View>
      </View>
    </PosScreenFrame>
  );
}

function SpecialProductsAddModal({
  busy,
  candidates,
  onAdd,
  onClose,
  onQueryChange,
  onSearch,
  query,
  searching,
  t,
  visible,
}: Readonly<{
  busy: boolean;
  candidates: readonly SpecialProductItem[];
  onAdd(productCode: string): void;
  onClose(): void;
  onQueryChange(query: string): void;
  onSearch(): void;
  query: string;
  searching: boolean;
  t: SpecialProductsTranslate;
  visible: boolean;
}>) {
  usePosThemeVersion();
  return (
    <Modal
      animationType="fade"
      onRequestClose={onClose}
      presentationStyle="overFullScreen"
      statusBarTranslucent
      supportedOrientations={["landscape-left", "landscape-right"]}
      transparent
      visible={visible}
    >
      <View
        accessibilityViewIsModal
        style={styles.modalOverlay}
        testID="special-products-add-modal"
      >
        <PosPressable
          accessible={false}
          onPress={onClose}
          sound="navigate"
          style={styles.backdropDismissArea}
          testID="special-products-add-modal-backdrop"
        />
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.modalPanelContent}
          keyboardShouldPersistTaps="handled"
          style={styles.modalPanel}
          testID="special-products-add-modal-scroll"
        >
          <View style={styles.modalHeader}>
            <View style={styles.modalTitleGroup}>
              <Text style={styles.modalTitle}>{t("management.title")}</Text>
              <Text style={styles.managementHint}>{t("management.hint")}</Text>
            </View>
            <PosButton
              icon="close"
              label={t("action.close")}
              onPress={onClose}
              size="medium"
              sound="navigate"
              testID="special-products-add-modal-close"
              tone="quiet"
            />
          </View>

          <View style={styles.searchRow}>
            <PosKeyboardAwareTextInput
              accessibilityLabel={t("management.searchLabel")}
              autoCapitalize="none"
              autoCorrect={false}
              editable={!busy}
              onChangeText={onQueryChange}
              onSubmitEditing={onSearch}
              placeholder={t("management.searchPlaceholder")}
              placeholderTextColor={posColors.placeholder}
              selectionColor={posColors.accent}
              style={styles.searchInput}
              testID="special-products-search-input"
              value={query}
            />
            <PosButton
              disabled={busy || searching}
              icon="magnify"
              label={
                searching ? t("management.searching") : t("management.search")
              }
              onPress={onSearch}
              size="medium"
              testID="special-products-search"
            />
          </View>

          {candidates.length === 0 ? (
            <Text style={styles.candidateEmpty}>{t("management.empty")}</Text>
          ) : (
            <View style={styles.candidateList}>
              {/* 候选上限 50 条，直接 map 渲染普通 View，避免 FlatList
                  嵌套在 ScrollView 内触发虚拟化警告与全量渲染 */}
              {candidates.map((item) => (
                // 整行可点击 = 添加为特殊商品，触屏友好
                <PosPressable
                  accessibilityRole="button"
                  accessibilityState={{ disabled: busy }}
                  disabled={busy}
                  key={item.productCode}
                  onPress={() => onAdd(item.productCode)}
                  style={({ pressed }) => [
                    styles.candidateRow,
                    pressed && !busy && styles.candidatePressed,
                  ]}
                  testID={`special-products-candidate-${item.productCode}`}
                >
                  {/* 候选商品同样展示缩略图，与目录卡片一致 */}
                  <ProductCardImage
                    imageUri={item.productImage}
                    placeholder={item.displayName.slice(0, 1).toUpperCase()}
                    size={56}
                    testID={`special-products-candidate-image-${item.productCode}`}
                  />
                  <View style={styles.candidateIdentity}>
                    <Text numberOfLines={2} style={styles.candidateName}>
                      {item.displayName}
                    </Text>
                    <Text numberOfLines={1} style={styles.candidateCode}>
                      {item.lookupCode || item.productCode}
                    </Text>
                  </View>
                  <View style={styles.candidateAdd}>
                    <MaterialCommunityIcons
                      accessible={false}
                      color={posColors.blue}
                      name="plus-circle-outline"
                      size={24}
                    />
                    <Text style={styles.candidateAddText}>
                      {t("management.mark")}
                    </Text>
                  </View>
                </PosPressable>
              ))}
            </View>
          )}
        </PosKeyboardAwareScrollView>
      </View>
    </Modal>
  );
}

function SpecialProductCard({
  canAddToCart,
  canDrag,
  cellHeight,
  disabled,
  drag,
  dragTargetIndex,
  index,
  item,
  itemCount,
  online,
  onAddToCart,
  onDragEnd,
  onDragMove,
  onDragStart,
  onMeasureCell,
  onMoveDown,
  onMoveUp,
  onRemove,
  organizing,
  t,
}: Readonly<{
  canAddToCart: boolean;
  canDrag: boolean;
  cellHeight: number;
  disabled: boolean;
  drag: Readonly<{
    code: string;
    dx: number;
    dy: number;
    fromIndex: number;
  }> | null;
  dragTargetIndex: number | null;
  index: number;
  item: SpecialProductItem;
  itemCount: number;
  online: boolean;
  onAddToCart(): void;
  onDragEnd(
    productCode: string,
    fromIndex: number,
    dx: number,
    dy: number,
  ): void;
  onDragMove(
    productCode: string,
    fromIndex: number,
    dx: number,
    dy: number,
  ): void;
  onDragStart(productCode: string, fromIndex: number): void;
  onMeasureCell(width: number, height: number): void;
  onMoveDown(): void;
  onMoveUp(): void;
  onRemove(): void;
  /** 整理状态：显示 ↑ / ↓ / 取消标记，点卡片不加购，只保留长按拖动。 */
  organizing: boolean;
  t: SpecialProductsTranslate;
}>) {
  usePosThemeVersion();
  const managementDisabled = disabled || !online;
  const addable = canAddToCart && !disabled;
  const dragActive = drag?.code === item.productCode;
  // 日常状态下卡片是加购按钮；整理状态下它只承接长按拖动，
  // 不能拖（离线 / 处理中）时整张卡片不可点。
  const pressDisabled = (organizing ? !canDrag : !addable) || dragActive;
  const longPressActive = useRef(false);
  const panResponder = useMemo(
    () =>
      PanResponder.create({
        // 点击/长按由内层 Pressable 处理；仅在长按后开始移动时接管手势
        onStartShouldSetPanResponder: () => false,
        onMoveShouldSetPanResponder: () => longPressActive.current,
        onPanResponderMove: (_event, gesture) => {
          if (!longPressActive.current) return;
          onDragMove(item.productCode, index, gesture.dx, gesture.dy);
        },
        onPanResponderRelease: (_event, gesture) => {
          if (!longPressActive.current) return;
          longPressActive.current = false;
          onDragEnd(item.productCode, index, gesture.dx, gesture.dy);
        },
        onPanResponderTerminate: () => {
          if (!longPressActive.current) return;
          longPressActive.current = false;
          onDragEnd(item.productCode, index, 0, 0);
        },
      }),
    [index, item.productCode, onDragEnd, onDragMove],
  );
  const panHandlers = panResponder.panHandlers;
  return (
    <PosPanResponderView
      onLayout={(event) => {
        const { height, width } = event.nativeEvent.layout;
        onMeasureCell(width, height);
      }}
      panHandlers={panHandlers}
      style={[
        styles.productCard,
        dragTargetIndex !== null &&
          dragTargetIndex === index &&
          dragTargetIndex !== drag?.fromIndex &&
          styles.productCardDragTarget,
      ]}
      testID={`special-product-card-${item.productCode}-shell`}
    >
      {/* 整卡可点击 = 加购，触屏操作无需精确瞄准小按钮（与 WPF 卡片一致）；
          整理状态下不加购，长按（canDrag 时）抬起卡片进入拖拽排序 */}
      <PosPressable
        accessibilityRole="button"
        accessibilityState={{ disabled: pressDisabled }}
        disabled={pressDisabled}
        onLongPress={
          canDrag
            ? () => {
                longPressActive.current = true;
                onDragStart(item.productCode, index);
              }
            : undefined
        }
        // 仅可拖拽时注册排序反馈，避免普通卡片的长按抑制 tap 音。
        longPressSound={canDrag ? "navigate" : false}
        onPress={organizing ? undefined : onAddToCart}
        // 整理状态下点卡片什么都不发生，所以也不出声，免得听起来像加购成功。
        sound={organizing ? false : "tap"}
        style={({ pressed }) => [
          styles.productCardMain,
          pressed && !pressDisabled && styles.productCardPressed,
          dragActive && styles.productCardDragging,
          dragActive &&
            cellHeight > 0 && {
              transform: [
                { translateX: drag.dx ?? 0 },
                { translateY: drag.dy ?? 0 },
              ],
            },
        ]}
        testID={`special-product-card-${item.productCode}`}
      >
        <ProductCardImage
          imageUri={item.productImage}
          placeholder={item.displayName.slice(0, 1).toUpperCase()}
          testID={`special-product-card-image-${item.productCode}`}
        />
        <View style={styles.productCardBody}>
          <Text numberOfLines={2} style={styles.productCardName}>
            {item.displayName}
          </Text>
          <Text numberOfLines={1} style={styles.productCardMeta}>
            {t("row.itemNumber", {
              itemNumber: item.itemNumber?.trim() || "—",
            })}
          </Text>
          <Text numberOfLines={1} style={styles.productCardMeta}>
            {item.lookupCode || item.productCode}
          </Text>
          <Text numberOfLines={1} style={styles.productCardPrice}>
            {formatAud(item.retailPriceCents)}
          </Text>
        </View>
      </PosPressable>
      {organizing ? (
        <View style={styles.productCardActions}>
          <PosButton
            disabled={managementDisabled || index === 0}
            label="↑"
            labelStyle={styles.manageArrowLabel}
            onPress={onMoveUp}
            size="small"
            style={styles.manageArrow}
            testID={`special-products-move-up-${item.productCode}`}
          />
          <PosButton
            disabled={managementDisabled || index === itemCount - 1}
            label="↓"
            labelStyle={styles.manageArrowLabel}
            onPress={onMoveDown}
            size="small"
            style={styles.manageArrow}
            testID={`special-products-move-down-${item.productCode}`}
          />
          <PosButton
            disabled={managementDisabled}
            label={t("row.remove")}
            labelStyle={styles.manageRemoveLabel}
            onPress={onRemove}
            size="small"
            sound="danger"
            style={styles.manageRemove}
            testID={`special-products-remove-${item.productCode}`}
            tone="danger"
          />
        </View>
      ) : null}
    </PosPanResponderView>
  );
}

function ProductCardImage({
  imageUri,
  placeholder,
  size,
  testID,
}: Readonly<{
  imageUri: string | null;
  placeholder: string;
  size?: number;
  testID: string;
}>) {
  usePosThemeVersion();
  // 渲染层协议白名单：仅允许 https/http 与 data:image，避免被篡改的
  // file:// 等非预期 uri 进入 RN Image 渲染管线（纵深防御）。
  const safeImageUri =
    typeof imageUri === "string" && /^(https?:|data:image\/)/iu.test(imageUri)
      ? imageUri
      : null;
  const [failedUri, setFailedUri] = useState<string | null>(null);
  const showImage = Boolean(safeImageUri) && failedUri !== safeImageUri;
  return (
    <View
      style={[
        styles.productCardImageFrame,
        // 提供 size 时渲染为方形缩略图（候选列表用），否则撑满卡片宽度
        size !== undefined && [
          styles.productThumbFrame,
          { height: size, width: size },
        ],
      ]}
      testID={testID}
    >
      {showImage ? (
        <Image
          accessible={false}
          onError={() => setFailedUri(safeImageUri)}
          resizeMode="cover"
          source={{ uri: safeImageUri as string }}
          style={[
            styles.productCardImage,
            size !== undefined && { height: size, width: size },
          ]}
          testID={`${testID}-content`}
        />
      ) : (
        <Text
          accessibilityElementsHidden
          importantForAccessibility="no-hide-descendants"
          style={[
            styles.productCardImagePlaceholder,
            size !== undefined && styles.productThumbPlaceholder,
          ]}
          testID={`${testID}-placeholder`}
        >
          {placeholder}
        </Text>
      )}
    </View>
  );
}

type NoticeTone = "info" | "success" | "danger" | "warning";

/** 页面内的提示条：离线、操作结果、整理模式共用同一套尺寸，只换颜色和图标。 */
function NoticeBanner({
  alert = false,
  icon,
  liveRegion = false,
  testID,
  text,
  tone,
  trailing,
}: Readonly<{
  alert?: boolean;
  icon: PosIconName;
  liveRegion?: boolean;
  testID: string;
  text: string;
  tone: NoticeTone;
  trailing?: ReactNode;
}>) {
  usePosThemeVersion();
  return (
    <View
      accessibilityLiveRegion={liveRegion ? "polite" : undefined}
      accessibilityRole={alert ? "alert" : undefined}
      style={[styles.banner, noticeFrameStyle(tone)]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={noticeInk(tone)}
        name={icon}
        size={24}
      />
      <Text style={[styles.bannerText, { color: noticeInk(tone) }]}>{text}</Text>
      {trailing}
    </View>
  );
}

function noticeFrameStyle(tone: NoticeTone) {
  switch (tone) {
    case "info":
      return styles.bannerInfo;
    case "success":
      return styles.bannerSuccess;
    case "danger":
      return styles.bannerDanger;
    case "warning":
      return styles.bannerWarning;
  }
}

// 颜色在渲染时才从当前主题取，换主题后提示条跟着变。
function noticeInk(tone: NoticeTone): string {
  switch (tone) {
    case "info":
      return posColors.blue;
    case "success":
      return posColors.green;
    case "danger":
      return posColors.red;
    case "warning":
      return posColors.yellowInk;
  }
}

function StatusBanner({
  statusCode,
  t,
}: Readonly<{
  statusCode: SpecialProductsStatusCode;
  t: SpecialProductsTranslate;
}>) {
  usePosThemeVersion();
  const failure =
    statusCode.endsWith("-failed") ||
    statusCode === "online-required" ||
    statusCode === "permission-required";
  return (
    <NoticeBanner
      alert={failure}
      icon={failure ? "alert-circle-outline" : "check-circle-outline"}
      liveRegion
      testID="special-products-status"
      text={t(specialProductsStatusCopyKey(statusCode))}
      tone={failure ? "danger" : "success"}
    />
  );
}

function EmptyState({
  icon,
  message,
  testID,
}: Readonly<{ icon: PosIconName; message: string; testID: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.emptyState} testID={testID}>
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.mutedInk}
        name={icon}
        size={56}
      />
      <Text style={styles.emptyStateText}>{message}</Text>
    </View>
  );
}

function formatAud(cents: number): string {
  return `$${(cents / 100).toFixed(2)}`;
}

const styles = createPosStyles((colors) => ({
  page: {
    flex: 1,
    gap: 10,
    minHeight: 0,
    padding: 12,
  },
  banner: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: SPECIAL_PRODUCTS_MIN_TOUCH_TARGET,
    paddingHorizontal: 14,
    paddingVertical: 9,
  },
  bannerText: {
    flex: 1,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  bannerInfo: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
  },
  bannerSuccess: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
  },
  bannerDanger: {
    backgroundColor: colors.redSoft,
    borderColor: colors.redBorder,
  },
  bannerWarning: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
  },
  sectionHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    minHeight: 30,
  },
  sectionTitle: {
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  sectionPill: {
    alignSelf: "center",
  },
  sectionHint: {
    color: colors.mutedInk,
    flex: 1,
    fontSize: 13,
    fontWeight: "600",
    textAlign: "right",
  },
  productListFrame: {
    flex: 1,
  },
  productList: {
    paddingBottom: 12,
  },
  productCardRow: {
    gap: SPECIAL_PRODUCTS_GRID_GAP,
    marginBottom: SPECIAL_PRODUCTS_GRID_GAP,
  },
  productCard: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1.5,
    // 固定百分比宽度避免末行卡片被 flex 拉伸占满整行：
    // 一行 5 张 + 4 个 10pt 间距，19.2% × 5 留出的 4% 正好放间距，放不下时再收缩。
    flexShrink: 1,
    width: "19.2%",
  },
  productCardMain: {
    borderRadius: posRadius.medium,
    flex: 1,
    minHeight: SPECIAL_PRODUCTS_MIN_TOUCH_TARGET,
  },
  productCardPressed: {
    opacity: 0.72,
  },
  productCardDragging: {
    backgroundColor: colors.surface,
    elevation: 12,
    shadowColor: colors.shadow,
    shadowOffset: { height: 6, width: 0 },
    shadowOpacity: 0.25,
    shadowRadius: 12,
    zIndex: 10,
  },
  productCardDragTarget: {
    borderColor: colors.accent,
    borderWidth: 2,
  },
  productCardImageFrame: {
    alignItems: "center",
    backgroundColor: colors.surfaceSunken,
    // 图片贴着卡片上沿，圆角跟随卡片内圈；不靠外壳裁切，拖动时卡片才不会被截掉。
    borderTopLeftRadius: posRadius.medium,
    borderTopRightRadius: posRadius.medium,
    height: 92,
    justifyContent: "center",
    overflow: "hidden",
    width: "100%",
  },
  productThumbFrame: {
    borderRadius: posRadius.small,
  },
  productCardImage: {
    height: 92,
    width: "100%",
  },
  productCardImagePlaceholder: {
    color: colors.mutedInk,
    fontSize: 34,
    fontWeight: "900",
  },
  productThumbPlaceholder: {
    fontSize: 24,
  },
  productCardBody: {
    flex: 1,
    gap: 2,
    paddingBottom: 10,
    paddingHorizontal: 10,
    paddingTop: 8,
  },
  productCardName: {
    color: colors.ink,
    fontSize: 14.5,
    fontWeight: "900",
    lineHeight: 18,
    minHeight: 36,
  },
  productCardMeta: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    lineHeight: 17,
  },
  productCardPrice: {
    color: colors.ink,
    fontSize: 21,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    marginTop: "auto",
    paddingTop: 2,
  },
  productCardActions: {
    flexDirection: "row",
    gap: 6,
    paddingBottom: 8,
    paddingHorizontal: 8,
  },
  manageArrow: {
    flex: 1,
    paddingHorizontal: 0,
  },
  manageArrowLabel: {
    fontSize: 22,
  },
  manageRemove: {
    flex: 1.3,
    paddingHorizontal: 4,
  },
  manageRemoveLabel: {
    fontSize: 13.5,
  },
  modalTitle: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
  },
  managementHint: {
    color: colors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 4,
  },
  searchRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    marginTop: 14,
  },
  searchInput: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    color: colors.ink,
    flex: 1,
    fontSize: 17,
    fontWeight: "700",
    minHeight: posTouch.medium,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  candidateEmpty: {
    color: colors.mutedInk,
    fontSize: 15,
    lineHeight: 22,
    marginTop: 22,
    textAlign: "center",
  },
  candidateList: {
    gap: 8,
    paddingTop: 14,
  },
  candidateRow: {
    alignItems: "center",
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.medium,
    borderWidth: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 78,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  candidateIdentity: {
    flex: 1,
  },
  candidateName: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "800",
    lineHeight: 21,
  },
  candidateCode: {
    color: colors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    marginTop: 3,
  },
  candidatePressed: {
    opacity: 0.7,
  },
  candidateAdd: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
  },
  candidateAddText: {
    color: colors.blue,
    fontSize: 16,
    fontWeight: "900",
  },
  modalOverlay: {
    alignItems: "center",
    backgroundColor: colors.overlay,
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  backdropDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  modalPanel: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1,
    maxHeight: 680,
    width: 620,
  },
  modalPanelContent: {
    padding: 18,
  },
  modalHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
  },
  modalTitleGroup: {
    flex: 1,
  },
  emptyState: {
    alignItems: "center",
    flex: 1,
    gap: 12,
    justifyContent: "center",
    padding: 28,
  },
  emptyStateText: {
    color: colors.mutedInk,
    fontSize: 17,
    fontWeight: "700",
    textAlign: "center",
  },
  unavailable: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  unavailableCard: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    gap: 10,
    maxWidth: 560,
    padding: 28,
  },
  unavailableTitle: {
    color: colors.ink,
    fontSize: 23,
    fontWeight: "900",
    textAlign: "center",
  },
  unavailableHint: {
    color: colors.mutedInk,
    fontSize: 16,
    lineHeight: 24,
    textAlign: "center",
  },
}));
