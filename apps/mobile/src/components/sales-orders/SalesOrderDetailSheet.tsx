import * as Clipboard from "expo-clipboard";
import { useEffect, useState } from "react";
import { Alert, ScrollView, Share, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Divider, Icon, SegmentedButtons, Text } from "react-native-paper";
import { ProductImageBox } from "@/components/seasonal-product-insights/ProductImageBox";
import { QrCodePanel } from "@/components/ui/QrCodePanel";
import { InsightSheet } from "@/components/warehouse-product-insights/InsightSheet";
import type { InvoiceAction } from "@/modules/sales-orders/invoice";
import { useSalesOrderReceipt } from "@/modules/sales-orders/use-sales-order-receipt";
import {
  SALES_ORDER_REFUND_TYPE,
  resolveSalesOrderRefundTone,
  summarizeSalesOrderLines,
} from "@/modules/sales-orders/logic";
import type {
  SalesOrderDetail,
  SalesOrderListItem,
  SalesOrderMatchedProduct,
} from "@/modules/sales-orders/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { productThumbnailUri } from "@/shared/utils/product-image";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ReceiptPaper } from "./ReceiptPaper";
import { ReceiptQrOverlay } from "./ReceiptQrOverlay";
import { REFUND_TONES, SalesOrderRefundTag, SalesOrderStatusTag } from "./SalesOrderStatusTag";
import { formatSalesOrderMoney, formatSalesOrderTime } from "./format";

/** 明细行缩略图边长（dp）。 */
const LINE_IMAGE_SIZE = 56;

export interface SalesOrderDetailSheetProps {
  visible: boolean;
  /** 列表项先行展示订单头，详情加载完成后再补充明细与支付。 */
  summary: SalesOrderListItem | null;
  detail: SalesOrderDetail | null;
  loading: boolean;
  error: string | null;
  invoiceBusy: InvoiceAction | null;
  invoiceError: string | null;
  onRetry: () => void;
  onInvoice: (action: InvoiceAction) => void;
  onClose: () => void;
}

export function SalesOrderDetailSheet({
  visible,
  summary,
  detail,
  loading,
  error,
  invoiceBusy,
  invoiceError,
  onRetry,
  onInvoice,
  onClose,
}: SalesOrderDetailSheetProps) {
  const { t } = useAppTranslation("salesOrders");
  const order = detail?.order ?? summary;
  // 含退款商品：整单金额不为负是部分退款（橙），为负是整单退款（红）。
  const refundTone = order ? resolveSalesOrderRefundTone(order) : null;
  const matchedCodes = new Set(
    (summary?.matchedProducts ?? []).map((product: SalesOrderMatchedProduct) => product.productCode),
  );

  // 页签：默认停在「小票」（与收银机打印一致的纸面），原商品明细保留为第二个页签。
  const [tab, setTab] = useState<"receipt" | "items">("receipt");
  const [qrOpen, setQrOpen] = useState(false);
  const [qrBusy, setQrBusy] = useState(false);
  const [qrFailed, setQrFailed] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const receipt = useSalesOrderReceipt(order?.orderGuid ?? "", visible);
  const receiptLink = receipt.view?.link ?? null;

  useEffect(() => {
    // 关闭详情面板时一并收起二维码面板，下次打开不会残留。
    if (!visible) setQrOpen(false);
  }, [visible]);

  useEffect(() => {
    if (!notice) return undefined;
    const timer = setTimeout(() => setNotice(null), 2500);
    return () => clearTimeout(timer);
  }, [notice]);

  /** 首次打开才生成链接：没人点二维码的订单不会白白产生一条公开链接。 */
  const ensureReceiptLink = async () => {
    setQrBusy(true);
    setQrFailed(false);
    try {
      return await receipt.ensureLink();
    } catch {
      setQrFailed(true);
      return null;
    } finally {
      setQrBusy(false);
    }
  };

  const openQr = () => {
    setQrOpen(true);
    if (!receiptLink) void ensureReceiptLink();
  };

  const copyLink = async () => {
    if (!receiptLink) return;
    await Clipboard.setStringAsync(receiptLink.url);
    setNotice(t("receipt.copied"));
  };

  const shareLink = async () => {
    const link = receiptLink ?? (await ensureReceiptLink());
    if (!link) return;
    try {
      // iOS 同时给 url 才会按链接预览；Android 只认 message。
      await Share.share({ message: link.url, url: link.url });
    } catch {
      setNotice(t("receipt.shareFailed"));
    }
  };

  const confirmRegenerate = () => {
    Alert.alert(t("receipt.regenerate"), t("receipt.regenerateConfirm"), [
      { text: t("actions.close"), style: "cancel" },
      {
        text: t("receipt.regenerate"),
        style: "destructive",
        onPress: () => {
          setQrBusy(true);
          receipt
            .regenerate()
            .then(() => setNotice(t("receipt.regenerated")))
            .catch(() => setQrFailed(true))
            .finally(() => setQrBusy(false));
        },
      },
    ]);
  };

  const receiptContent =
    receipt.loading && !receipt.view ? (
      <View style={styles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={styles.stateText}>{t("receipt.loading")}</Text>
      </View>
    ) : receipt.errorCode !== null ? (
      <View style={styles.state}>
        <Icon source="alert-circle-outline" size={32} color={HB_COLORS.outline} />
        <Text style={styles.stateText}>
          {receipt.errorCode === "RECEIPT_UNSUPPORTED" ? t("receipt.unsupported") : t("receipt.loadFailed")}
        </Text>
        {receipt.errorCode === "RECEIPT_UNSUPPORTED" ? null : (
          <Button mode="outlined" compact onPress={() => void receipt.reload()}>
            {t("actions.retry")}
          </Button>
        )}
      </View>
    ) : receipt.view ? (
      <View style={styles.receiptCanvas}>
        <ReceiptPaper document={receipt.view.document} />
        <View style={styles.receiptActions}>
          <Button mode="contained" icon="qrcode" style={styles.invoiceButton} onPress={openQr}>
            {t("receipt.openQr")}
          </Button>
          <Button
            mode="contained-tonal"
            icon="export-variant"
            style={styles.invoiceButton}
            disabled={qrBusy}
            onPress={() => void shareLink()}
          >
            {t("receipt.sendToCustomer")}
          </Button>
        </View>
      </View>
    ) : null;

  return (
    <InsightSheet
      visible={visible}
      title={t("detail.title")}
      subtitle={order ? `${order.branchName ?? order.branchCode ?? "—"} · ${formatSalesOrderTime(order.orderTime, true)}` : null}
      closeLabel={t("actions.close")}
      onClose={onClose}
      heightRatio={0.92}
    >
      <View style={styles.root}>
      <ScrollView contentContainerStyle={styles.content}>
        {order ? (
          <View style={styles.card}>
            <View style={styles.row}>
              <Text selectable style={styles.guid}>
                {order.orderGuid}
              </Text>
              <View style={styles.tags}>
                {refundTone && order.status !== SALES_ORDER_REFUND_TYPE ? <SalesOrderRefundTag tone={refundTone} /> : null}
                <SalesOrderStatusTag status={order.status} refundTone={refundTone} />
              </View>
            </View>
            <Text style={styles.meta}>
              {t("detail.device", { value: order.deviceCode ?? "—" })}
            </Text>
            <View style={styles.amounts}>
              <Amount label={t("detail.totalAmount")} value={order.totalAmount} />
              <Amount label={t("detail.discount")} value={order.discountAmount} negative />
              <Amount
                label={t("detail.actualPay")}
                value={order.actualAmount}
                strong
                color={refundTone ? REFUND_TONES[refundTone].color : undefined}
              />
            </View>
            <View style={styles.invoiceRow}>
              <Button
                mode="outlined"
                compact
                icon="file-eye-outline"
                loading={invoiceBusy === "preview"}
                disabled={invoiceBusy != null}
                onPress={() => onInvoice("preview")}
                style={styles.invoiceButton}
              >
                {t("detail.previewInvoice")}
              </Button>
              <Button
                mode="contained-tonal"
                compact
                icon="download-outline"
                loading={invoiceBusy === "share"}
                disabled={invoiceBusy != null}
                onPress={() => onInvoice("share")}
                style={styles.invoiceButton}
              >
                {t("detail.downloadInvoice")}
              </Button>
            </View>
            {invoiceError ? (
              <View style={styles.notice}>
                <Icon source="alert-circle-outline" size={16} color={HB_COLORS.danger} />
                <Text style={styles.noticeText}>{invoiceError}</Text>
              </View>
            ) : null}
          </View>
        ) : null}

        {/*
          订单二维码：载荷固定为完整 orderGuid，与 POS 小票上打印的机读码一致，
          收银台可直接扫它调出这笔订单（退货等）。订单头已显示 GUID 原文，这里不再重复。
          列表项先行展示订单头时即可显示，不必等明细加载完成。
        */}
        {order?.orderGuid ? (
          <QrCodePanel label={t("detail.orderQr")} value={order.orderGuid} showValue={false} />
        ) : null}

        <SegmentedButtons
          value={tab}
          onValueChange={(value) => setTab(value as "receipt" | "items")}
          density="small"
          buttons={[
            { value: "receipt", label: t("receipt.tab") },
            { value: "items", label: t("receipt.tabItems") },
          ]}
        />

        {tab === "receipt" ? receiptContent : loading ? (
          <View style={styles.state}>
            <ActivityIndicator color={HB_COLORS.brand} />
            <Text style={styles.stateText}>{t("detail.loading")}</Text>
          </View>
        ) : error ? (
          <View style={styles.state}>
            <Icon source="alert-circle-outline" size={32} color={HB_COLORS.outline} />
            <Text style={styles.stateText}>{error}</Text>
            <Button mode="outlined" compact onPress={onRetry}>
              {t("actions.retry")}
            </Button>
          </View>
        ) : detail ? (
          <>
            <View style={styles.sectionHeader}>
              <Text style={styles.sectionTitle}>{t("detail.lines")}</Text>
              <Text style={styles.sectionHint}>
                {t("card.counts", {
                  sku: summarizeSalesOrderLines(detail.lines).skuCount,
                  items: summarizeSalesOrderLines(detail.lines).itemCount,
                })}
              </Text>
            </View>
            {detail.lines.length === 0 ? (
              <Text style={styles.empty}>{t("detail.noLines")}</Text>
            ) : (
              detail.lines.map((line, index) => {
                const matched = line.productCode != null && matchedCodes.has(line.productCode);
                return (
                  <View
                    key={`${line.productCode ?? "line"}-${index}`}
                    style={[styles.line, matched ? styles.lineMatched : null]}
                  >
                    {/* 左侧缩略图（主档无图或加载失败时显示占位），右侧保持原有文字布局 */}
                    <ProductImageBox
                      uri={productThumbnailUri(line.productImage, 96)}
                      size={LINE_IMAGE_SIZE}
                      label={line.productName ?? line.itemNumber ?? line.productCode ?? ""}
                    />
                    <View style={styles.lineBody}>
                      <View style={styles.row}>
                        <Text style={styles.lineCode}>
                          {line.itemNumber ?? line.productCode ?? "—"}
                        </Text>
                        {matched ? (
                          <Text style={styles.matchedTag}>{t("detail.matched")}</Text>
                        ) : null}
                      </View>
                      <Text numberOfLines={2} style={styles.lineName}>
                        {line.productName ?? "—"}
                      </Text>
                      <View style={styles.row}>
                        <Text style={styles.meta}>
                          {line.quantity ?? 0} × {formatSalesOrderMoney(line.unitPrice)}
                          {line.discountAmount ? (
                            <Text style={styles.lineDiscount}>
                              {"  "}
                              {t("detail.lineDiscount", {
                                value: formatSalesOrderMoney(line.discountAmount),
                              })}
                            </Text>
                          ) : null}
                        </Text>
                        <Text style={styles.lineAmount}>{formatSalesOrderMoney(line.actualAmount)}</Text>
                      </View>
                    </View>
                  </View>
                );
              })
            )}

            <Text style={[styles.sectionTitle, styles.sectionSpacing]}>{t("detail.payments")}</Text>
            {detail.payments.length === 0 ? (
              <Text style={styles.empty}>{t("detail.noPayments")}</Text>
            ) : (
              <View style={styles.card}>
                {detail.payments.map((payment, index) => (
                  <View key={`${payment.paymentTime ?? "pay"}-${index}`}>
                    {index > 0 ? <Divider style={styles.divider} /> : null}
                    <View style={styles.row}>
                      <Text style={styles.paymentMethod}>
                        {payment.paymentMethodName ??
                          (payment.paymentMethod != null
                            ? t("detail.paymentMethod", { value: payment.paymentMethod })
                            : "—")}
                      </Text>
                      <Text style={styles.lineAmount}>{formatSalesOrderMoney(payment.amount)}</Text>
                    </View>
                    <Text style={styles.meta}>{formatSalesOrderTime(payment.paymentTime, true)}</Text>
                  </View>
                ))}
              </View>
            )}
          </>
        ) : null}
      </ScrollView>
      {qrOpen ? (
        <ReceiptQrOverlay
          link={receiptLink}
          busy={qrBusy}
          failed={qrFailed}
          notice={notice}
          labels={{
            title: t("receipt.qrTitle"),
            hint: t("receipt.qrHint"),
            creating: t("receipt.creating"),
            createFailed: t("receipt.createFailed"),
            retry: t("actions.retry"),
            copy: t("receipt.copyLink"),
            share: t("receipt.sendToCustomer"),
            regenerate: t("receipt.regenerate"),
            close: t("actions.close"),
            meta: receiptLink
              ? t("receipt.meta", { date: receiptLink.expiresAtUtc.slice(0, 10), count: receiptLink.openCount })
              : "",
          }}
          onClose={() => setQrOpen(false)}
          onCopy={() => void copyLink()}
          onShare={() => void shareLink()}
          onRegenerate={confirmRegenerate}
          onRetry={() => void ensureReceiptLink()}
        />
      ) : null}
      </View>
    </InsightSheet>
  );
}

function Amount({
  label,
  value,
  negative = false,
  strong = false,
  color,
}: {
  label: string;
  value: number | null;
  negative?: boolean;
  strong?: boolean;
  /** 含退款商品时整单金额按正负着色（橙 / 红）。 */
  color?: string;
}) {
  const shown = negative && value != null && value > 0 ? -value : value;
  return (
    <View style={styles.amount}>
      <Text style={styles.amountLabel}>{label}</Text>
      <Text style={[styles.amountValue, strong ? styles.amountStrong : null, color ? { color } : null]}>
        {formatSalesOrderMoney(shown)}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  receiptCanvas: {
    gap: HB_SPACING.sm,
    paddingVertical: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.xs,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: "#EEF1F5",
  },
  receiptActions: { flexDirection: "row", gap: HB_SPACING.xs },
  content: { padding: HB_SPACING.sm, gap: HB_SPACING.xs },
  card: {
    backgroundColor: HB_COLORS.surface,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
    gap: 6,
  },
  row: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 8 },
  tags: { flexDirection: "row", alignItems: "center", gap: 6 },
  guid: { flex: 1, fontSize: 12, fontWeight: "700", color: HB_COLORS.textPrimary },
  meta: { fontSize: 12, color: HB_COLORS.textSecondary },
  amounts: {
    flexDirection: "row",
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
    paddingTop: HB_SPACING.xs,
    marginTop: 4,
  },
  amount: { flex: 1, gap: 2 },
  amountLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  amountValue: { fontSize: 13, color: HB_COLORS.textPrimary },
  amountStrong: { fontSize: 16, fontWeight: "800" },
  invoiceRow: { flexDirection: "row", gap: HB_SPACING.xs, marginTop: 4 },
  invoiceButton: { flex: 1 },
  notice: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    backgroundColor: "#FCEBEB",
  },
  noticeText: { flex: 1, fontSize: 12, color: HB_COLORS.danger },
  sectionHeader: { flexDirection: "row", alignItems: "baseline", justifyContent: "space-between", paddingHorizontal: 4, paddingTop: HB_SPACING.xs },
  sectionTitle: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  sectionSpacing: { paddingHorizontal: 4, paddingTop: HB_SPACING.sm },
  sectionHint: { fontSize: 12, color: HB_COLORS.textSecondary },
  line: {
    backgroundColor: HB_COLORS.surface,
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.sm,
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.sm,
  },
  lineBody: { flex: 1, minWidth: 0, gap: 2 },
  lineMatched: { borderColor: "#FAC775", backgroundColor: "#FFFBF2" },
  lineCode: { fontSize: 12, fontWeight: "700", color: HB_COLORS.textPrimary },
  lineName: { fontSize: 13, color: HB_COLORS.textPrimary },
  lineDiscount: { color: HB_COLORS.warning },
  lineAmount: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  matchedTag: {
    fontSize: 10,
    fontWeight: "700",
    color: "#633806",
    backgroundColor: "#FAEEDA",
    paddingHorizontal: 6,
    paddingVertical: 2,
    borderRadius: 6,
    overflow: "hidden",
  },
  paymentMethod: { fontSize: 13, color: HB_COLORS.textPrimary },
  divider: { marginVertical: 6 },
  empty: { fontSize: 12, color: HB_COLORS.textSecondary, paddingHorizontal: 4 },
  state: { alignItems: "center", gap: HB_SPACING.xs, padding: HB_SPACING.lg },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center" },
});
