import { MaterialCommunityIcons } from "@expo/vector-icons";
import type { TFunction } from "i18next";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import {
  AppRegistry,
  Image,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from "react-native";

import {
  CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT,
  CustomerDisplaySnapshotSchema,
  type CustomerDisplaySnapshot,
} from "../../../contracts/external-display";

import type { ExternalDisplayNativeModule } from "./external-display-bridge";

const surfaceModuleName = "HBExternalDisplay";

type ExternalDisplaySurfaceProps = {
  surfaceId: string;
  snapshot: CustomerDisplaySnapshot | null;
};

type DisplaySummary = Readonly<{
  itemQuantity: string;
  skuCount: number;
  subtotal: CustomerDisplaySnapshot["total"];
}>;

type StatusCopy = Readonly<{
  title: string;
  subtitle: string;
}>;

type VisibleItemWindow = Readonly<{
  start: number;
  items: CustomerDisplaySnapshot["items"];
  hiddenBefore: number;
  hiddenAfter: number;
}>;

type DisplayItem = CustomerDisplaySnapshot["items"][number];

export type CustomerDisplayRect = Readonly<{
  x: number;
  y: number;
  width: number;
  height: number;
}>;

export type CustomerDisplayLayout = Readonly<{
  /** 屏幕高度 / WPF 设计画布高度 768；所有 WPF 像素值乘以它。 */
  scale: number;
  /** 等效 WPF 画布宽度（限制在 1024…1366）。 */
  canvasWidth: number;
  /** 购物车面板占内容区宽度的比例。 */
  cartShare: number;
  margin: number;
  sectionGap: number;
  panelGap: number;
  summaryHeight: number;
  contentWidth: number;
  contentHeight: number;
  cartPanel: CustomerDisplayRect;
  /** 广告面板矩形；Swift 的 advertContainer 必须与它逐值一致。 */
  advertPanel: CustomerDisplayRect;
  summaryPanel: CustomerDisplayRect;
  /** 汇总区三个小计栏内文字可用宽度（Subtotal / GST / Savings）。 */
  summaryMetricTextWidth: number;
}>;

// ---- WPF 设计画布常量（单位：WPF 像素，使用时乘 scale） ----
const WPF_CANVAS_HEIGHT = 768;
const WPF_CANVAS_MIN_WIDTH = 1024;
const WPF_CANVAS_MAX_WIDTH = 1366;
const WPF_CART_SHARE_WIDE = 0.6;
const WPF_CART_SHARE_NARROW_EXTRA = 0.08;
const WPF_MARGIN = 18;
const WPF_PANEL_GAP = 18;
const WPF_SUMMARY_HEIGHT = 152;
const WPF_SUMMARY_GAP = 20;
const WPF_SUMMARY_PADDING_X = 24;
const WPF_SUMMARY_SIDE_COLUMN = 220;
const WPF_SUMMARY_LEFT_GAP = 12;
const WPF_SUMMARY_METRIC_GAP = 12;

/** 布局兜底：窗口尺寸异常（0 / NaN）时按 WPF 默认画布计算，避免出现 NaN 样式。 */
const FALLBACK_WINDOW = { width: 1366, height: 768 } as const;

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(Math.max(value, minimum), maximum);
}

/**
 * 按 WPF 客显的 `ResolveCartColumnShare` 与设计画布推导 iPad 外屏几何。
 *
 * 与 WPF 的差异：不做宽屏两侧留白，直接铺满整个屏幕宽度。
 * Swift 层（advertContainer）使用同一套公式；改动这里必须同步 Swift。
 */
export function resolveCustomerDisplayLayout(window: {
  width: number;
  height: number;
}): CustomerDisplayLayout {
  const valid =
    Number.isFinite(window.width) &&
    Number.isFinite(window.height) &&
    window.width > 0 &&
    window.height > 0;
  const width = valid ? window.width : FALLBACK_WINDOW.width;
  const height = valid ? window.height : FALLBACK_WINDOW.height;

  const scale = height / WPF_CANVAS_HEIGHT;
  const canvasWidth = clamp(
    (WPF_CANVAS_HEIGHT * width) / height,
    WPF_CANVAS_MIN_WIDTH,
    WPF_CANVAS_MAX_WIDTH,
  );
  // 宽屏 60%；越接近 4:3 越向购物车倾斜，最多 68%。
  const cartShare =
    WPF_CART_SHARE_WIDE +
    WPF_CART_SHARE_NARROW_EXTRA *
      ((WPF_CANVAS_MAX_WIDTH - canvasWidth) /
        (WPF_CANVAS_MAX_WIDTH - WPF_CANVAS_MIN_WIDTH));

  const margin = WPF_MARGIN * scale;
  const panelGap = WPF_PANEL_GAP * scale;
  const sectionGap = WPF_SUMMARY_GAP * scale;
  const summaryHeight = WPF_SUMMARY_HEIGHT * scale;
  const contentWidth = width - 2 * margin;
  const contentHeight = height - 2 * margin - summaryHeight - sectionGap;
  const cartWidth = contentWidth * cartShare;
  // 广告面板宽 = contentW*(1-share) - 18s；与购物车之间留 panelGap。
  const advertWidth = contentWidth * (1 - cartShare) - panelGap;

  // 汇总区：边框 1 + 左右内边距 24s；右侧两列各 220s，余下给左列。
  const summaryInnerWidth =
    contentWidth - 2 - 2 * WPF_SUMMARY_PADDING_X * scale;
  const summaryLeftWidth =
    summaryInnerWidth - 2 * WPF_SUMMARY_SIDE_COLUMN * scale;
  const summaryMetricTextWidth = Math.max(
    0,
    (summaryLeftWidth - WPF_SUMMARY_LEFT_GAP * scale) / 3 -
      WPF_SUMMARY_METRIC_GAP * scale,
  );

  return {
    scale,
    canvasWidth,
    cartShare,
    margin,
    sectionGap,
    panelGap,
    summaryHeight,
    contentWidth,
    contentHeight,
    cartPanel: { x: margin, y: margin, width: cartWidth, height: contentHeight },
    advertPanel: {
      x: margin + cartWidth + panelGap,
      y: margin,
      width: advertWidth,
      height: contentHeight,
    },
    summaryPanel: {
      x: margin,
      y: margin + contentHeight + sectionGap,
      width: contentWidth,
      height: summaryHeight,
    },
    summaryMetricTextWidth,
  };
}

/** 估算一段金额文字的宽度系数（相对字号）：数字与 $ 较宽，小数点/逗号较窄。 */
function estimateGlyphWidth(character: string): number {
  if (character === "." || character === ",") return 0.3;
  if (character === "-" || character === " ") return 0.4;
  return 0.6;
}

const ESTIMATED_LINE_HEIGHT_FACTOR = 1.2;

/**
 * 对应 WPF `Viewbox StretchDirection="DownOnly"`：文字超出最大宽/高时等比缩小，永不放大。
 * 这里用字符宽度估算，渲染时还会叠加 adjustsFontSizeToFit 兜底。
 */
export function fitCustomerDisplayFontSize(input: {
  text: string;
  fontSize: number;
  maxWidth: number;
  maxHeight: number;
}): number {
  const { text, fontSize, maxWidth, maxHeight } = input;
  if (text.length === 0 || fontSize <= 0) return fontSize;
  let widthFactor = 0;
  for (const character of text) {
    widthFactor += estimateGlyphWidth(character);
  }
  const estimatedWidth = widthFactor * fontSize;
  const estimatedHeight = fontSize * ESTIMATED_LINE_HEIGHT_FACTOR;
  const ratio = Math.min(
    1,
    estimatedWidth > 0 ? maxWidth / estimatedWidth : 1,
    estimatedHeight > 0 ? maxHeight / estimatedHeight : 1,
  );
  return fontSize * Math.max(ratio, 0);
}

let registeredNativeModule: ExternalDisplayNativeModule | null = null;

function parseSnapshot(value: unknown): CustomerDisplaySnapshot | null {
  const result = CustomerDisplaySnapshotSchema.safeParse(value);
  return result.success ? result.data : null;
}

function formatMoney(money: CustomerDisplaySnapshot["total"]): string {
  const absoluteCents = Math.abs(money.cents);
  const sign = money.cents < 0 ? "-" : "";
  return `${sign}$${Math.floor(absoluteCents / 100)}.${String(
    absoluteCents % 100,
  ).padStart(2, "0")}`;
}

/** snapshot 保存的是折扣绝对金额；客显明确呈现为减项（金额前缀 "-"）。 */
function formatSavings(money: CustomerDisplaySnapshot["discount"]): string {
  return `-${formatMoney({ ...money, cents: Math.abs(money.cents) })}`;
}

function summarizeSnapshot(
  snapshot: CustomerDisplaySnapshot | null,
): DisplaySummary {
  if (snapshot === null) {
    return {
      itemQuantity: "0",
      skuCount: 0,
      subtotal: { currency: "AUD", cents: 0 },
    };
  }
  if (snapshot.summary !== undefined) {
    return snapshot.summary;
  }

  // 旧版原生快照没有 summary；只使用既有白名单字段恢复等价显示。
  return {
    itemQuantity: sumFixedQuantities(
      snapshot.items.map((item) => item.quantity),
    ),
    skuCount: snapshot.items.length,
    subtotal: {
      currency: "AUD",
      cents: snapshot.total.cents + snapshot.discount.cents,
    },
  };
}

function sumFixedQuantities(quantities: readonly string[]): string {
  let totalThousandths = 0n;

  for (const quantity of quantities) {
    const match = /^(-?)(\d+)(?:\.(\d{1,3}))?$/.exec(quantity);
    if (match === null) continue;
    const whole = BigInt(match[2]!);
    const fraction = BigInt((match[3] ?? "").padEnd(3, "0") || "0");
    const value = whole * 1_000n + fraction;
    totalThousandths += match[1] === "-" ? -value : value;
  }

  const sign = totalThousandths < 0n ? "-" : "";
  const absolute = totalThousandths < 0n ? -totalThousandths : totalThousandths;
  const fraction = String(absolute % 1_000n)
    .padStart(3, "0")
    .replace(/0+$/, "");
  return `${sign}${absolute / 1_000n}${
    fraction.length > 0 ? `.${fraction}` : ""
  }`;
}

function getStatusCopy(
  snapshot: CustomerDisplaySnapshot | null,
  translate: TFunction,
): StatusCopy {
  // idle / cart / payment / 无快照统一对齐 WPF 的 "Ready for Payment"。
  const ready: StatusCopy = {
    title: translate("customerDisplay.status.ready.title"),
    subtitle: translate("customerDisplay.status.ready.subtitle"),
  };
  if (snapshot === null) {
    return ready;
  }

  switch (snapshot.mode) {
    case "idle":
    case "cart":
    case "payment":
      return ready;
    case "change":
      return {
        title: translate("customerDisplay.status.change.title"),
        subtitle: formatMoney(snapshot.change),
      };
    case "success": {
      const hasChange = snapshot.change.cents !== 0;
      return {
        title: translate("customerDisplay.status.success.title"),
        subtitle: hasChange
          ? translate("customerDisplay.status.success.change", {
              amount: formatMoney(snapshot.change),
            })
          : translate("customerDisplay.status.success.thankYou"),
      };
    }
  }
}

function visibleItemWindow(
  snapshot: CustomerDisplaySnapshot | null,
): VisibleItemWindow {
  if (snapshot === null || snapshot.items.length === 0) {
    return { start: 0, items: [], hiddenBefore: 0, hiddenAfter: 0 };
  }
  const maximumStart = Math.max(
    0,
    snapshot.items.length - CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT,
  );
  const start = Math.max(
    0,
    Math.min(snapshot.visibleItemStart ?? maximumStart, maximumStart),
  );
  const items = snapshot.items.slice(
    start,
    start + CUSTOMER_DISPLAY_VISIBLE_ITEM_LIMIT,
  );
  return {
    start,
    items,
    hiddenBefore: start,
    hiddenAfter: Math.max(0, snapshot.items.length - start - items.length),
  };
}

function hiddenItemsText(
  window: VisibleItemWindow,
  translate: TFunction,
): string | null {
  if (window.hiddenBefore > 0 && window.hiddenAfter > 0) {
    return translate("customerDisplay.hidden.both", {
      before: window.hiddenBefore,
      after: window.hiddenAfter,
    });
  }
  if (window.hiddenBefore > 0) {
    return translate("customerDisplay.hidden.before", {
      count: window.hiddenBefore,
    });
  }
  if (window.hiddenAfter > 0) {
    return translate("customerDisplay.hidden.after", {
      count: window.hiddenAfter,
    });
  }
  return null;
}

export function ExternalDisplaySurface({
  surfaceId,
  snapshot: initialSnapshot,
}: ExternalDisplaySurfaceProps) {
  // 客显面向顾客固定使用英文，不跟随收银主界面的语言切换。
  const { t } = useTranslation(undefined, { lng: "en" });
  const windowSize = useWindowDimensions();
  const [snapshot, setSnapshot] = useState(() =>
    parseSnapshot(initialSnapshot),
  );

  useEffect(() => {
    const nativeModule = registeredNativeModule;
    if (nativeModule === null) {
      return;
    }

    const subscription = nativeModule.addListener(
      "onSnapshotChanged",
      (nextSnapshot) => {
        const parsed = parseSnapshot(nextSnapshot);
        if (parsed !== null) {
          setSnapshot((currentSnapshot) =>
            currentSnapshot === null ||
            parsed.revision > currentSnapshot.revision
              ? parsed
              : currentSnapshot,
          );
        }
      },
    );
    void nativeModule.markReactSurfaceRendered(surfaceId).catch(() => {
      // Swift 会保持 UIKit 占位并发送 failed，不影响主收银界面。
    });

    return () => subscription.remove();
  }, [surfaceId]);

  const layout = useMemo(
    () =>
      resolveCustomerDisplayLayout({
        width: windowSize.width,
        height: windowSize.height,
      }),
    [windowSize.width, windowSize.height],
  );
  const styles = useMemo(() => createStyles(layout), [layout]);
  const itemWindow = useMemo(() => visibleItemWindow(snapshot), [snapshot]);
  const summary = useMemo(() => summarizeSnapshot(snapshot), [snapshot]);
  const status = getStatusCopy(snapshot, t);
  const overflowText = hiddenItemsText(itemWindow, t);
  const showsFullScreenAdvert =
    snapshot !== null &&
    snapshot.mode === "idle" &&
    snapshot.items.length === 0 &&
    snapshot.advert !== null;

  if (showsFullScreenAdvert) {
    return (
      <View
        accessible={false}
        pointerEvents="none"
        style={staticStyles.surface}
        testID="external-display-surface"
      >
        <View
          style={staticStyles.fullScreenNativeAdvertWindow}
          testID="external-display-advert-window"
        />
      </View>
    );
  }

  const scale = layout.scale;
  const subtotalText = formatMoney(summary.subtotal);
  const gstText = snapshot === null ? "$0.00" : formatMoney(snapshot.gst);
  const savingsText =
    snapshot !== null && snapshot.discount.cents !== 0
      ? formatSavings(snapshot.discount)
      : null;
  const totalText = snapshot === null ? "$0.00" : formatMoney(snapshot.total);
  const metricFontSize = (text: string) =>
    fitCustomerDisplayFontSize({
      text,
      fontSize: 28 * scale,
      maxWidth: layout.summaryMetricTextWidth,
      maxHeight: 38 * scale,
    });
  const totalFontSize = fitCustomerDisplayFontSize({
    text: totalText,
    fontSize: 62 * scale,
    maxWidth: 200 * scale,
    maxHeight: 68 * scale,
  });

  return (
    <View
      accessible={false}
      pointerEvents="none"
      style={staticStyles.surface}
      testID="external-display-surface"
    >
      <View
        style={styles.cartPanel}
        testID="external-display-transaction-panel"
      >
        <View style={styles.tableHeader} testID="external-display-table-header">
          <Text numberOfLines={1} style={[styles.headerText, styles.productColumn]}>
            {t("customerDisplay.column.itemDescription")}
          </Text>
          <Text
            numberOfLines={1}
            style={[styles.headerText, styles.quantityColumn, styles.centerText]}
          >
            {t("customerDisplay.column.quantity")}
          </Text>
          <Text
            numberOfLines={1}
            style={[styles.headerText, styles.priceColumn, styles.rightText]}
          >
            {t("customerDisplay.column.price")}
          </Text>
          <Text
            numberOfLines={1}
            style={[styles.headerText, styles.totalColumn, styles.rightText]}
          >
            {t("customerDisplay.column.total")}
          </Text>
        </View>

        <View style={styles.items}>
          {itemWindow.items.map((item, index) => {
            const absoluteIndex = itemWindow.start + index;
            return (
              <ItemRow
                // 奇数序行（从 0 计）用表面色，对应 WPF AlternatingRowBackground。
                alternate={absoluteIndex % 2 === 1}
                item={item}
                itemNumberLabel={t("customerDisplay.itemNumber")}
                key={`${absoluteIndex}-${item.name}`}
                styles={styles}
              />
            );
          })}
        </View>

        {overflowText === null ? null : (
          <Text
            numberOfLines={1}
            style={styles.hiddenItems}
            testID="external-display-hidden-items"
          >
            {overflowText}
          </Text>
        )}
      </View>

      {/* 该透明窗口由 UIKit 本地媒体层承载，不在 React 上叠加广告文案；矩形与 Swift advertContainer 同公式。 */}
      <View
        style={styles.nativeAdvertWindow}
        testID="external-display-advert-window"
      />

      <View style={styles.summaryPanel} testID="external-display-summary-panel">
        <View
          style={styles.summaryMetrics}
          testID="external-display-summary-metrics"
        >
          <View style={styles.summaryCounts}>
            <Text style={styles.summaryCountText}>
              {t("customerDisplay.summary.itemQuantity")}
            </Text>
            <Text style={[styles.summaryCountText, styles.summaryCountValue]}>
              {summary.itemQuantity}
            </Text>
            <Text style={[styles.summaryCountText, styles.summaryCountSecond]}>
              {t("customerDisplay.summary.skuCount")}
            </Text>
            <Text style={[styles.summaryCountText, styles.summaryCountValue]}>
              {summary.skuCount}
            </Text>
          </View>
          <View style={styles.metricRow}>
            <Metric
              fontSize={metricFontSize(subtotalText)}
              label={t("customerDisplay.summary.subtotal")}
              styles={styles}
              value={subtotalText}
            />
            <Metric
              fontSize={metricFontSize(gstText)}
              label={t("customerDisplay.gst")}
              styles={styles}
              value={gstText}
            />
            {savingsText === null ? (
              <View style={styles.metric} />
            ) : (
              <Metric
                fontSize={metricFontSize(savingsText)}
                label={t("customerDisplay.savings")}
                styles={styles}
                testID="external-display-savings"
                value={savingsText}
              />
            )}
          </View>
        </View>

        <View style={styles.amountDue} testID="external-display-amount-due">
          <Text numberOfLines={1} style={styles.amountDueLabel}>
            {t("customerDisplay.summary.totalToPay")}
          </Text>
          <Text
            adjustsFontSizeToFit
            minimumFontScale={0.5}
            numberOfLines={1}
            style={[styles.amountDueValue, { fontSize: totalFontSize }]}
          >
            {totalText}
          </Text>
        </View>

        <View style={styles.statusRegion} testID="external-display-status-region">
          <View style={styles.statusCard} testID="external-display-status-card">
            <Text
              adjustsFontSizeToFit
              minimumFontScale={0.6}
              numberOfLines={1}
              style={styles.statusTitle}
            >
              {status.title}
            </Text>
            <Text numberOfLines={2} style={styles.statusSubtitle}>
              {status.subtitle}
            </Text>
          </View>
        </View>
      </View>
    </View>
  );
}

type SurfaceStyles = ReturnType<typeof createStyles>;

function Metric({
  label,
  value,
  fontSize,
  styles,
  testID,
}: {
  label: string;
  value: string;
  fontSize: number;
  styles: SurfaceStyles;
  testID?: string;
}) {
  return (
    <View style={styles.metric} testID={testID}>
      <Text numberOfLines={1} style={styles.metricLabel}>
        {label}
      </Text>
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.5}
        numberOfLines={1}
        style={[styles.metricValue, { fontSize }]}
      >
        {value}
      </Text>
    </View>
  );
}

function ItemRow({
  item,
  alternate,
  itemNumberLabel,
  styles,
}: {
  item: DisplayItem;
  alternate: boolean;
  itemNumberLabel: string;
  styles: SurfaceStyles;
}) {
  // grossAmount 与 discountRate 由契约保证同时出现；以 grossAmount 判定该行有折扣。
  const hasDiscount = item.grossAmount !== undefined;
  const hasItemNumber = item.itemNumber !== undefined;
  const hasLookupCode = item.lookupCode !== undefined;

  return (
    <View
      style={[styles.itemRow, alternate ? styles.itemRowAlternate : null]}
      testID="external-display-item-row"
    >
      <View style={styles.productColumn}>
        <View style={styles.productCell}>
          <ProductThumbnail imageUri={item.imageUri} styles={styles} />
          <View style={styles.productText}>
            <Text numberOfLines={1} style={styles.itemName}>
              {item.name}
            </Text>
            {hasItemNumber || hasLookupCode ? (
              <View style={styles.codeLine}>
                {hasItemNumber ? (
                  <Text
                    numberOfLines={1}
                    style={styles.codeText}
                    testID="external-display-item-number"
                  >
                    {`${itemNumberLabel} ${item.itemNumber}`}
                  </Text>
                ) : null}
                {hasItemNumber && hasLookupCode ? (
                  <View style={styles.codeSeparator} />
                ) : null}
                {hasLookupCode ? (
                  <Text
                    numberOfLines={1}
                    style={[styles.codeText, styles.lookupCode]}
                    testID="external-display-item-lookup-code"
                  >
                    {item.lookupCode}
                  </Text>
                ) : null}
              </View>
            ) : null}
          </View>
        </View>
      </View>

      <View style={styles.quantityColumn}>
        <View style={styles.quantityPill}>
          <Text style={styles.quantityText}>{item.quantity}</Text>
        </View>
      </View>

      <View style={styles.priceColumn}>
        <Text style={styles.unitPrice}>
          {item.unitPrice === undefined ? "—" : formatMoney(item.unitPrice)}
        </Text>
        {hasDiscount && item.discountRate !== undefined ? (
          <Text
            style={styles.discountRate}
            testID="external-display-item-discount-rate"
          >
            {`-${item.discountRate}%`}
          </Text>
        ) : null}
      </View>

      <View style={styles.totalColumn}>
        {item.grossAmount !== undefined ? (
          <Text
            style={styles.grossAmount}
            testID="external-display-item-gross-amount"
          >
            {formatMoney(item.grossAmount)}
          </Text>
        ) : null}
        <Text
          style={[styles.actualAmount, hasDiscount ? styles.actualDiscounted : null]}
          testID="external-display-item-amount"
        >
          {formatMoney(item.amount)}
        </Text>
      </View>
    </View>
  );
}

/**
 * 商品缩略图：购物袋图标作底，有本地图时叠在上面（与 WPF 一致）。
 * 图片加载失败后移除图片层，只剩图标；客显层不联网，imageUri 只会是本地 file URI。
 */
function ProductThumbnail({
  imageUri,
  styles,
}: {
  imageUri: string | undefined;
  styles: SurfaceStyles;
}) {
  const [failedUri, setFailedUri] = useState<string | null>(null);
  const showsImage = imageUri !== undefined && failedUri !== imageUri;

  return (
    <View style={styles.thumbBox} testID="external-display-item-thumb">
      <View style={styles.thumbIcon} testID="external-display-item-thumb-icon">
        <MaterialCommunityIcons
          color={colors.accent}
          name="shopping"
          size={styles.thumbIconSize}
        />
      </View>
      {showsImage ? (
        <Image
          onError={() => setFailedUri(imageUri)}
          resizeMode="contain"
          source={{ uri: imageUri }}
          style={styles.thumbImage}
          testID="external-display-item-thumb-image"
        />
      ) : null}
    </View>
  );
}

export function registerExternalDisplayReactSurface(
  nativeModule: ExternalDisplayNativeModule | null,
) {
  registeredNativeModule = nativeModule;

  if (!AppRegistry.getAppKeys().includes(surfaceModuleName)) {
    AppRegistry.registerComponent(
      surfaceModuleName,
      () => ExternalDisplaySurface,
    );
  }

  if (nativeModule !== null) {
    void nativeModule.markReactSurfaceReady().catch(() => {
      // 无 Development Build 或原生 factory 未就绪时保持 disconnected/failed。
    });
  }
}

// WPF PosTheme.xaml 的 PosCustomerDisplay* 颜色。
const colors = {
  background: "#09111F",
  surface: "#101B2D",
  text: "#FFFFFF",
  muted: "rgba(255,255,255,0.64)",
  headerText: "rgba(255,255,255,0.90)",
  accent: "#69E3C2",
  amount: "#FFC73D",
  divider: "rgba(255,255,255,0.12)",
  accentSurface: "rgba(105,227,194,0.12)",
} as const;

const staticStyles = StyleSheet.create({
  surface: {
    flex: 1,
    backgroundColor: "transparent",
  },
  fullScreenNativeAdvertWindow: {
    flex: 1,
    backgroundColor: "transparent",
  },
});

/** 按尺度生成样式：所有 WPF 像素值 × scale。面板用绝对定位，保证与 Swift 矩形公式逐值一致。 */
function createStyles(layout: CustomerDisplayLayout) {
  const s = layout.scale;
  const border = Math.max(1, s);
  const columnWidths = {
    quantity: 96 * s,
    price: 116 * s,
    total: 126 * s,
  };

  const styles = StyleSheet.create({
    cartPanel: {
      position: "absolute",
      left: layout.cartPanel.x,
      top: layout.cartPanel.y,
      width: layout.cartPanel.width,
      height: layout.cartPanel.height,
      overflow: "hidden",
      padding: 20 * s,
      backgroundColor: colors.background,
      borderWidth: border,
      borderColor: colors.divider,
      borderRadius: 12 * s,
    },
    nativeAdvertWindow: {
      position: "absolute",
      left: layout.advertPanel.x,
      top: layout.advertPanel.y,
      width: layout.advertPanel.width,
      height: layout.advertPanel.height,
      overflow: "hidden",
      backgroundColor: "transparent",
      borderWidth: border,
      borderColor: colors.divider,
      borderRadius: 18 * s,
    },
    tableHeader: {
      flexDirection: "row",
      alignItems: "center",
      padding: 12 * s,
      paddingHorizontal: 0,
      backgroundColor: colors.surface,
      borderBottomWidth: border,
      borderBottomColor: colors.divider,
    },
    headerText: {
      paddingHorizontal: 12 * s,
      color: colors.headerText,
      fontSize: 17 * s,
      fontWeight: "700",
    },
    centerText: { textAlign: "center" },
    rightText: { textAlign: "right" },
    // 四列共用于表头文字与行内容器：商品列弹性，其余固定宽度。
    productColumn: { flex: 1, minWidth: 0 },
    quantityColumn: {
      width: columnWidths.quantity,
      alignItems: "center",
      justifyContent: "center",
    },
    priceColumn: {
      width: columnWidths.price,
      justifyContent: "center",
      alignItems: "flex-end",
      paddingRight: 12 * s,
    },
    totalColumn: {
      width: columnWidths.total,
      justifyContent: "center",
      alignItems: "flex-end",
      paddingRight: 12 * s,
    },
    items: {
      minHeight: 0,
    },
    itemRow: {
      height: 72 * s,
      flexGrow: 0,
      flexShrink: 0,
      flexDirection: "row",
      alignItems: "stretch",
      backgroundColor: colors.background,
      borderBottomWidth: border,
      borderBottomColor: colors.divider,
    },
    itemRowAlternate: {
      backgroundColor: colors.surface,
    },
    productCell: {
      flex: 1,
      flexDirection: "row",
      alignItems: "center",
      minWidth: 0,
      marginLeft: 12 * s,
      marginRight: 8 * s,
    },
    productText: {
      flex: 1,
      minWidth: 0,
      justifyContent: "center",
    },
    itemName: {
      color: colors.text,
      fontSize: 19 * s,
      fontWeight: "600",
    },
    codeLine: {
      marginTop: 4 * s,
      flexDirection: "row",
      alignItems: "center",
    },
    codeText: {
      flexShrink: 1,
      maxWidth: 185 * s,
      color: colors.muted,
      fontSize: 12 * s,
    },
    lookupCode: { maxWidth: undefined, flex: 1 },
    codeSeparator: {
      width: border,
      height: 12 * s,
      marginHorizontal: 10 * s,
      backgroundColor: colors.divider,
    },
    thumbBox: {
      width: 52 * s,
      height: 52 * s,
      marginRight: 12 * s,
      overflow: "hidden",
      alignItems: "center",
      justifyContent: "center",
      backgroundColor: colors.accentSurface,
      borderWidth: border,
      borderColor: colors.divider,
      borderRadius: 6 * s,
    },
    thumbIcon: {
      ...StyleSheet.absoluteFillObject,
      alignItems: "center",
      justifyContent: "center",
    },
    thumbImage: {
      position: "absolute",
      left: 2 * s,
      top: 2 * s,
      right: 2 * s,
      bottom: 2 * s,
      borderRadius: 5 * s,
    },
    quantityPill: {
      paddingHorizontal: 10 * s,
      paddingVertical: 4 * s,
      backgroundColor: colors.accentSurface,
      borderWidth: border,
      borderColor: colors.accent,
      borderRadius: 12 * s,
    },
    quantityText: {
      color: colors.accent,
      fontSize: 16 * s,
      fontWeight: "700",
      fontVariant: ["tabular-nums"],
    },
    unitPrice: {
      color: colors.text,
      fontSize: 17 * s,
      fontVariant: ["tabular-nums"],
      textAlign: "right",
    },
    discountRate: {
      marginTop: 2 * s,
      color: colors.accent,
      fontSize: 12 * s,
      fontWeight: "700",
      fontVariant: ["tabular-nums"],
      textAlign: "right",
    },
    grossAmount: {
      marginBottom: 1 * s,
      color: colors.muted,
      fontSize: 13 * s,
      fontVariant: ["tabular-nums"],
      textAlign: "right",
      textDecorationLine: "line-through",
    },
    actualAmount: {
      color: colors.text,
      fontSize: 19 * s,
      fontWeight: "700",
      fontVariant: ["tabular-nums"],
      textAlign: "right",
    },
    actualDiscounted: { color: colors.accent },
    hiddenItems: {
      position: "absolute",
      right: 20 * s,
      bottom: 8 * s,
      color: colors.muted,
      fontSize: 12 * s,
      textAlign: "right",
    },
    summaryPanel: {
      position: "absolute",
      left: layout.summaryPanel.x,
      top: layout.summaryPanel.y,
      width: layout.summaryPanel.width,
      height: layout.summaryPanel.height,
      flexDirection: "row",
      paddingHorizontal: 24 * s,
      paddingVertical: 16 * s,
      backgroundColor: colors.surface,
      borderWidth: border,
      borderColor: colors.divider,
      borderRadius: 12 * s,
    },
    summaryMetrics: {
      flex: 1,
      minWidth: 0,
      justifyContent: "center",
      marginRight: 12 * s,
    },
    summaryCounts: {
      flexDirection: "row",
      alignItems: "baseline",
      marginBottom: 8 * s,
    },
    summaryCountText: {
      color: colors.muted,
      fontSize: 15 * s,
    },
    summaryCountValue: { marginLeft: 8 * s, fontVariant: ["tabular-nums"] },
    summaryCountSecond: { marginLeft: 32 * s },
    metricRow: {
      flexDirection: "row",
      alignItems: "flex-start",
    },
    metric: {
      flex: 1,
      minWidth: 0,
      marginRight: 12 * s,
    },
    metricLabel: {
      color: colors.muted,
      fontSize: 14 * s,
      fontWeight: "700",
    },
    metricValue: {
      maxHeight: 38 * s,
      color: colors.text,
      fontSize: 28 * s,
      fontWeight: "900",
      fontVariant: ["tabular-nums"],
    },
    amountDue: {
      width: 220 * s,
      justifyContent: "center",
      alignItems: "flex-end",
      paddingRight: 20 * s,
    },
    amountDueLabel: {
      color: colors.text,
      fontSize: 16 * s,
      fontWeight: "900",
      textAlign: "right",
    },
    amountDueValue: {
      maxWidth: 200 * s,
      maxHeight: 68 * s,
      color: colors.amount,
      fontWeight: "900",
      fontVariant: ["tabular-nums"],
      textAlign: "right",
    },
    statusRegion: {
      width: 220 * s,
      justifyContent: "center",
    },
    statusCard: {
      padding: 16 * s,
      backgroundColor: colors.accentSurface,
      borderWidth: border,
      borderColor: colors.accent,
      borderRadius: 12 * s,
    },
    statusTitle: {
      color: colors.accent,
      fontSize: 18 * s,
      fontWeight: "900",
    },
    statusSubtitle: {
      marginTop: 4 * s,
      color: colors.text,
      fontSize: 15 * s,
    },
  });

  return { ...styles, thumbIconSize: 22 * s };
}
