/**
 * 促销只读详情（设计稿 ③）：多店促销一律只读（锁标），可「复制到本店」；
 * 没有 Promotions.Edit 权限时，本店独享促销也走这里只读查看。
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, IconButton, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { copyPromotionToStore } from "./api";
import {
  canCopyPromotionToStore,
  formatMoney,
  formatPromotionDate,
  getBundleSavings,
  getPromotionStatus,
  isLongTermPromotion,
} from "./promotion-logic";
import { fetchPromotionProductInfo, type PromotionProductInfo } from "./promotion-product-lookup";
import { buildCopyName, runWithConcurrency } from "./promotion-screen-model";
import { Badge, RuleBlock, SectionCard, StatusBadge, TABULAR, WarningBox } from "./promotion-ui";
import type { PromotionDetail } from "./types";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

const PREVIEW_COUNT = 3;
const PAGE_COUNT = 20;

export interface PromotionDetailViewProps {
  t: TranslateFn;
  storeCode: string;
  storeName: string;
  detail: PromotionDetail;
  canEdit: boolean;
  onBack: () => void;
  /** 复制成功后进入新促销的编辑页。 */
  onCopied: (copied: PromotionDetail | null) => void;
  onMessage: (message: string) => void;
  getErrorMessage: (error: unknown, fallbackKey: string) => string;
}

export function PromotionDetailView({
  t,
  storeCode,
  storeName,
  detail,
  canEdit,
  onBack,
  onCopied,
  onMessage,
  getErrorMessage,
}: PromotionDetailViewProps) {
  const [visibleCount, setVisibleCount] = useState(PREVIEW_COUNT);
  const [infos, setInfos] = useState<Record<string, PromotionProductInfo | null>>({});
  const requested = useRef(new Set<string>());
  const [copyVisible, setCopyVisible] = useState(false);
  const [copyName, setCopyName] = useState("");
  const [copying, setCopying] = useState(false);
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const status = getPromotionStatus(detail);
  const isMultiStore = detail.scopeType === "MultiStore";
  const canCopy = canEdit && canCopyPromotionToStore(detail);
  const products = detail.products.slice(0, visibleCount);
  const productTotal = Math.max(detail.productsCount, detail.products.length);

  // 只为已显示的商品按本店查名称与零售价。
  useEffect(() => {
    const codes = detail.products
      .slice(0, visibleCount)
      .map((row) => row.productCode).filter((code) => code && !requested.current.has(code));
    if (codes.length === 0) return;
    codes.forEach((code) => requested.current.add(code));
    void runWithConcurrency(codes, 4, async (code) => {
      let info: PromotionProductInfo | null = null;
      try {
        info = await fetchPromotionProductInfo(code, storeCode);
      } catch {
        info = null;
      }
      if (mountedRef.current) setInfos((current) => ({ ...current, [code]: info }));
    });
  }, [detail.products, storeCode, visibleCount]);

  const openCopy = () => {
    setCopyName(buildCopyName(detail.name, storeName));
    setCopyVisible(true);
  };

  const handleCopy = useCallback(async () => {
    setCopying(true);
    try {
      // 后端复制默认停用、优先级 0；成功后直接进入新促销编辑页。
      const copied = await copyPromotionToStore({ sourcePromotionId: detail.id, storeCode, name: copyName.trim() || undefined });
      setCopyVisible(false);
      onMessage(t("messages.copied"));
      onCopied(copied);
    } catch (error) {
      onMessage(getErrorMessage(error, "messages.copyFailed"));
    } finally {
      if (mountedRef.current) setCopying(false);
    }
  }, [copyName, detail.id, getErrorMessage, onCopied, onMessage, storeCode, t]);

  const endText = isLongTermPromotion(detail.effectiveEnd) ? t("card.longTerm") : formatPromotionDate(detail.effectiveEnd);
  const metaLine = [
    `${formatPromotionDate(detail.effectiveStart)} — ${endText}`,
    detail.isExclusive ? t("status.exclusive") : t("status.stackable"),
    t("fields.priorityValue", { value: detail.priority }),
  ].join(" · ");

  return (
    <View style={styles.flex}>
      <View style={styles.topBar}>
        <IconButton icon="arrow-left" onPress={onBack} accessibilityLabel={t("common:actions.back")} />
        <Text style={styles.topTitle} numberOfLines={1}>
          {t("detail.title")}
        </Text>
        {isMultiStore ? <Badge label={t("scope.multiStore")} icon="lock-outline" /> : <Badge label={t("scope.storeOnly")} tone="blue" />}
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        <SectionCard>
          <View style={styles.summary}>
            <RuleBlock
              size="lg"
              quantity={detail.applyQuantity}
              price={detail.fixedPrice}
              label={t("card.ruleQuantity", { count: detail.applyQuantity })}
            />
            <View style={styles.summaryText}>
              <Text style={styles.name}>{detail.name || t("fields.unnamed")}</Text>
              <Text style={[styles.meta, TABULAR]}>{metaLine}</Text>
              <View style={styles.badges}>
                <StatusBadge status={status} label={t(`status.${status}`)} />
                <Badge
                  label={
                    isMultiStore
                      ? t("detail.productsAndStores", { products: productTotal, stores: detail.storesCount })
                      : t("card.productCount", { count: productTotal })
                  }
                />
              </View>
            </View>
          </View>
        </SectionCard>

        <SectionCard title={productTotal > visibleCount ? t("detail.productsPreview", { count: Math.min(visibleCount, productTotal) }) : t("detail.productsAll", { count: productTotal })}>
          {products.map((row) => {
            const info = infos[row.productCode];
            const loaded = row.productCode in infos;
            const savings = info ? getBundleSavings(info.retailPrice, detail.applyQuantity, detail.fixedPrice) : null;
            const meta = !loaded
              ? t("editor.productLoading")
              : info?.retailPrice == null
                ? row.productCode
                : savings
                  ? t("editor.productPrice", { retail: formatMoney(info.retailPrice), unit: formatMoney(savings.unitPrice) })
                  : t("editor.productRetail", { retail: formatMoney(info.retailPrice) });
            return (
              <View key={row.productCode} style={styles.productRow}>
                <View style={styles.productIcon}>
                  <Icon source="package-variant-closed" size={20} color={HB_COLORS.textSecondary} />
                </View>
                <View style={styles.productText}>
                  <Text style={styles.productName} numberOfLines={1}>
                    {info?.productName || row.productCode}
                  </Text>
                  <Text style={[styles.productMeta, TABULAR, savings && !savings.isDiscount && styles.dangerText]} numberOfLines={1}>
                    {meta}
                  </Text>
                </View>
                {!loaded ? <ActivityIndicator size={14} /> : null}
              </View>
            );
          })}
          {detail.products.length > visibleCount ? (
            <Button
              compact
              icon="chevron-right"
              contentStyle={styles.moreContent}
              onPress={() => setVisibleCount((count) => (count <= PREVIEW_COUNT ? PAGE_COUNT : count + PAGE_COUNT))}
              style={styles.moreButton}
            >
              {visibleCount <= PREVIEW_COUNT
                ? t("detail.viewAll", { count: productTotal })
                : t("editor.showMoreProducts", { count: detail.products.length - visibleCount })}
            </Button>
          ) : null}
        </SectionCard>

        <WarningBox tone="info">
          {isMultiStore
            ? t("detail.multiStoreNote", { count: detail.storesCount })
            : t("detail.readOnlyNote")}
        </WarningBox>
      </ScrollView>

      {canCopy ? (
        <View style={styles.footer}>
          <Button mode="contained" icon="content-copy" onPress={openCopy} style={styles.footerButton} contentStyle={styles.footerButtonContent}>
            {t("actions.copyToStore")}
          </Button>
        </View>
      ) : null}

      <BusinessSheet
        visible={copyVisible}
        title={t("copy.title")}
        onDismiss={() => (copying ? undefined : setCopyVisible(false))}
        dismissable={!copying}
        footer={
          <View style={styles.copyActions}>
            <Button mode="outlined" disabled={copying} onPress={() => setCopyVisible(false)} style={styles.copyCancel} contentStyle={styles.footerButtonContent}>
              {t("common:actions.cancel")}
            </Button>
            <Button
              mode="contained"
              loading={copying}
              disabled={copying}
              onPress={() => void handleCopy()}
              style={styles.copyConfirm}
              contentStyle={styles.footerButtonContent}
            >
              {t("copy.confirm")}
            </Button>
          </View>
        }
      >
        <Text style={styles.copyDescription}>{t("copy.description", { name: detail.name, store: storeName })}</Text>
        <Text style={styles.copyLabel}>{t("copy.nameLabel")}</Text>
        <TextInput mode="outlined" value={copyName} onChangeText={setCopyName} style={styles.copyInput} outlineStyle={styles.outline} />
        <WarningBox>{t("copy.stackWarning")}</WarningBox>
      </BusinessSheet>
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  topBar: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xxs,
    paddingRight: HB_SPACING.md,
    minHeight: 56,
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  topTitle: { flex: 1, fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xl },
  summary: { flexDirection: "row", gap: HB_SPACING.sm },
  summaryText: { flex: 1, minWidth: 0, gap: 4 },
  name: { fontSize: 18, lineHeight: 24, fontWeight: "700", color: HB_COLORS.textPrimary },
  meta: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  badges: { flexDirection: "row", flexWrap: "wrap", gap: 6, marginTop: 2 },
  productRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  productIcon: {
    width: 40,
    height: 40,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    alignItems: "center",
    justifyContent: "center",
  },
  productText: { flex: 1, minWidth: 0, gap: 2 },
  productName: { fontSize: 15, lineHeight: 20, fontWeight: "600", color: HB_COLORS.textPrimary },
  productMeta: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  dangerText: { color: HB_COLORS.danger },
  moreButton: { alignSelf: "flex-start" },
  moreContent: { flexDirection: "row-reverse" },
  footer: {
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerButton: { borderRadius: HB_RADIUS.control },
  footerButtonContent: { minHeight: 48 },
  copyActions: { flexDirection: "row", gap: HB_SPACING.sm },
  copyCancel: { flex: 1, borderRadius: HB_RADIUS.control },
  copyConfirm: { flex: 1.6, borderRadius: HB_RADIUS.control },
  copyDescription: { fontSize: 14, lineHeight: 22, color: HB_COLORS.textSecondary },
  copyLabel: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary, marginTop: HB_SPACING.xs },
  copyInput: { backgroundColor: HB_COLORS.white },
  outline: { borderRadius: HB_RADIUS.control },
});
