import { ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Divider, Icon, Text } from "react-native-paper";
import { ProductImageBox } from "@/components/seasonal-product-insights/ProductImageBox";
import { QrCodePanel } from "@/components/ui/QrCodePanel";
import { InsightSheet } from "@/components/warehouse-product-insights/InsightSheet";
import type { InvoiceAction } from "@/modules/sales-orders/invoice";
import { summarizeSalesOrderLines } from "@/modules/sales-orders/logic";
import type {
  SalesOrderDetail,
  SalesOrderListItem,
  SalesOrderMatchedProduct,
} from "@/modules/sales-orders/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { productThumbnailUri } from "@/shared/utils/product-image";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { SalesOrderStatusTag } from "./SalesOrderStatusTag";
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
  const matchedCodes = new Set(
    (summary?.matchedProducts ?? []).map((product: SalesOrderMatchedProduct) => product.productCode),
  );
  return (
    <InsightSheet
      visible={visible}
      title={t("detail.title")}
      subtitle={order ? `${order.branchName ?? order.branchCode ?? "—"} · ${formatSalesOrderTime(order.orderTime, true)}` : null}
      closeLabel={t("actions.close")}
      onClose={onClose}
      heightRatio={0.92}
    >
      <ScrollView contentContainerStyle={styles.content}>
        {order ? (
          <View style={styles.card}>
            <View style={styles.row}>
              <Text selectable style={styles.guid}>
                {order.orderGuid}
              </Text>
              <SalesOrderStatusTag status={order.status} />
            </View>
            <Text style={styles.meta}>
              {t("detail.device", { value: order.deviceCode ?? "—" })}
            </Text>
            <View style={styles.amounts}>
              <Amount label={t("detail.totalAmount")} value={order.totalAmount} />
              <Amount label={t("detail.discount")} value={order.discountAmount} negative />
              <Amount label={t("detail.actualPay")} value={order.actualAmount} strong />
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

        {loading ? (
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
    </InsightSheet>
  );
}

function Amount({
  label,
  value,
  negative = false,
  strong = false,
}: {
  label: string;
  value: number | null;
  negative?: boolean;
  strong?: boolean;
}) {
  const shown = negative && value != null && value > 0 ? -value : value;
  return (
    <View style={styles.amount}>
      <Text style={styles.amountLabel}>{label}</Text>
      <Text style={[styles.amountValue, strong ? styles.amountStrong : null]}>
        {formatSalesOrderMoney(shown)}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
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
