import { memo, useCallback, useEffect, useMemo, useState } from "react";
import { Alert, Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Icon, Switch, Text } from "react-native-paper";
import { useRouter } from "expo-router";
import { ProductPromotionQuickCreateSheet } from "@/components/product-maintenance/ProductPromotionQuickCreateSheet";
import {
  buildProductPromotionCards,
  createQuickPromotionDraft,
  draftFromPromotionDetail,
  isPromotionExclusiveConflictError,
  type ProductPromotionAccessMode,
  type ProductPromotionCardModel,
  type QuickPromotionDraft,
} from "@/modules/product-maintenance/product-promotion-section";
import {
  copyPromotionToStore,
  endPromotionNow,
  fetchPromotionDetail,
  setPromotionEnabled,
} from "@/modules/promotions/api";
import { formatMoney, formatPromotionDate } from "@/modules/promotions/promotion-logic";
import type { PromotionListItem } from "@/modules/promotions/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

// 沿用旧促销摘要条的浅橙色系。
const PROMO = {
  background: "#FFF7E6",
  line: "#FFD591",
  itemLine: "#FFE7BA",
  text: "#AD4E00",
  addBackground: "#FFFDF8",
} as const;

interface ProductPromotionSectionProps {
  items: PromotionListItem[];
  accessMode: ProductPromotionAccessMode;
  productCode: string;
  productName: string;
  /** 本店零售价，用于「单件 · 省多少」与新建时的折扣校验。 */
  retailPrice: number | null;
  storeCode: string;
  storeName: string;
  /** 任何操作成功后由页面重新加载本商品促销。 */
  onChanged: () => void;
  onMessage: (message: string) => void;
  /** 弹层开关通知页面，打开期间页面暂停扫码查询。 */
  onSheetVisibleChange?: (visible: boolean) => void;
}

type SheetState = { mode: "create" | "edit"; draft: QuickPromotionDraft } | null;

/**
 * 商品查询页「本商品促销」区块（设计稿第 1 / 3 屏）。
 * - 只有 Promotions.View：只读，不出现任何按钮；
 * - 有 Promotions.Edit：本店独享可开关 / 编辑 / 提前结束，多店只读可复制到本店，可为本商品新建。
 */
export const ProductPromotionSection = memo(function ProductPromotionSection({
  items,
  accessMode,
  productCode,
  productName,
  retailPrice,
  storeCode,
  storeName,
  onChanged,
  onMessage,
  onSheetVisibleChange,
}: ProductPromotionSectionProps) {
  const { t, language } = useAppTranslation(["productQuery", "common"]);
  const router = useRouter();
  const [busyId, setBusyId] = useState<string | null>(null);
  const [sheet, setSheet] = useState<SheetState>(null);

  const cards = useMemo(
    () => buildProductPromotionCards(items, retailPrice, accessMode),
    [items, retailPrice, accessMode],
  );
  const editable = accessMode === "editable";
  // 失去编辑权限（如转为离线）时弹层随之关闭，不能让页面一直处于「弹层打开、暂停扫码」状态。
  const sheetVisible = Boolean(sheet) && editable;

  useEffect(() => {
    onSheetVisibleChange?.(sheetVisible);
  }, [onSheetVisibleChange, sheetVisible]);
  // 区块被卸载（如清空商品）时也要通知页面恢复扫码。
  useEffect(() => () => onSheetVisibleChange?.(false), [onSheetVisibleChange]);

  // 换商品 / 换分店时关闭弹层，避免把草稿保存到新上下文。
  useEffect(() => {
    setSheet(null);
    setBusyId(null);
  }, [productCode, storeCode]);

  const resolveError = useCallback(
    (error: unknown) =>
      isPromotionExclusiveConflictError(error)
        ? t("promotion.messages.exclusiveConflict")
        : resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "promotion.messages.actionFailed" }),
    [language, t],
  );

  const runAction = useCallback(
    async (id: string, action: () => Promise<unknown>, successKey: string) => {
      if (busyId) return;
      setBusyId(id);
      try {
        const result = await action();
        if (result === false) throw new Error(t("promotion.messages.actionFailed"));
        onMessage(t(successKey));
        onChanged();
      } catch (error) {
        onMessage(resolveError(error));
      } finally {
        setBusyId(null);
      }
    },
    [busyId, onChanged, onMessage, resolveError, t],
  );

  const handleToggle = (card: ProductPromotionCardModel, enable: boolean) => {
    void runAction(
      card.item.id,
      () => setPromotionEnabled(card.item.id, storeCode, enable),
      enable ? "promotion.messages.enabled" : "promotion.messages.disabled",
    );
  };

  const handleEndNow = (card: ProductPromotionCardModel) => {
    const name = card.item.name || t("promotion.unnamed");
    Alert.alert(t("promotion.confirm.endTitle"), t("promotion.confirm.endMessage", { name }), [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("promotion.confirm.endConfirm"),
        style: "destructive",
        onPress: () => {
          void runAction(card.item.id, () => endPromotionNow(card.item.id, storeCode), "promotion.messages.ended");
        },
      },
    ]);
  };

  const handleCopy = (card: ProductPromotionCardModel) => {
    const name = card.item.name || t("promotion.unnamed");
    Alert.alert(t("promotion.confirm.copyTitle"), t("promotion.confirm.copyMessage", { name }), [
      { text: t("common:actions.cancel"), style: "cancel" },
      {
        text: t("promotion.confirm.copyConfirm"),
        onPress: () => {
          void runAction(
            card.item.id,
            () => copyPromotionToStore({ sourcePromotionId: card.item.id, storeCode }),
            "promotion.messages.copied",
          );
        },
      },
    ]);
  };

  const anchorProduct = useMemo(
    () => ({ productCode, productName, retailPrice, unitWeight: 1 }),
    [productCode, productName, retailPrice],
  );

  const handleEdit = async (card: ProductPromotionCardModel) => {
    if (busyId) return;
    setBusyId(card.item.id);
    try {
      // 列表项可能不带完整商品清单，编辑前取详情，保存时整体回写不丢商品。
      const detail = await fetchPromotionDetail(card.item.id, storeCode);
      if (!detail) throw new Error(t("promotion.messages.loadDetailFailed"));
      setSheet({ mode: "edit", draft: draftFromPromotionDetail(detail, anchorProduct) });
    } catch (error) {
      onMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "promotion.messages.loadDetailFailed" }));
    } finally {
      setBusyId(null);
    }
  };

  const handleCreate = () => {
    setSheet({ mode: "create", draft: createQuickPromotionDraft(anchorProduct) });
  };

  const handleSaved = (mode: "create" | "edit") => {
    setSheet(null);
    onMessage(t(mode === "create" ? "promotion.messages.created" : "promotion.messages.updated"));
    onChanged();
  };

  if (accessMode === "hidden" || (!editable && cards.length === 0)) {
    return null;
  }

  return (
    <View style={styles.section}>
      <View style={styles.header}>
        <Text style={styles.headerTitle}>{t("promotion.title")}</Text>
        {cards.length > 0 ? (
          <View style={styles.countBadge}>
            <Text style={styles.countText}>{cards.length}</Text>
          </View>
        ) : null}
        <View style={styles.headerSpacer} />
        <Pressable
          accessibilityRole="link"
          onPress={() => router.push("/(shell)/promotions")}
          hitSlop={8}
          style={({ pressed }) => [styles.viewAll, pressed ? styles.pressed : null]}
        >
          <Text style={styles.viewAllText}>{t("promotion.viewAll")}</Text>
          <Icon source="chevron-right" size={16} color={PROMO.text} />
        </Pressable>
      </View>

      {cards.map((card) => (
        <PromotionItemCard
          key={card.item.id}
          card={card}
          busy={busyId === card.item.id}
          disabled={Boolean(busyId)}
          onToggle={(enable) => handleToggle(card, enable)}
          onEdit={() => void handleEdit(card)}
          onEndNow={() => handleEndNow(card)}
          onCopy={() => handleCopy(card)}
        />
      ))}

      {editable ? (
        <Pressable
          accessibilityRole="button"
          onPress={handleCreate}
          disabled={Boolean(busyId)}
          style={({ pressed }) => [styles.addButton, pressed ? styles.pressed : null]}
        >
          <Icon source="plus" size={18} color={PROMO.text} />
          <Text style={styles.addText}>{t("promotion.actions.create")}</Text>
        </Pressable>
      ) : (
        <View style={styles.readonlyHint}>
          <Icon source="eye-outline" size={14} color={PROMO.text} />
          <Text style={styles.readonlyHintText}>{t("promotion.readonlyHint")}</Text>
        </View>
      )}

      {editable ? (
        <ProductPromotionQuickCreateSheet
          visible={sheetVisible}
          mode={sheet?.mode ?? "create"}
          initialDraft={sheet?.draft ?? null}
          anchorProductCode={productCode}
          anchorProductName={productName}
          anchorRetailPrice={retailPrice}
          storeCode={storeCode}
          storeName={storeName}
          existingPromotions={items}
          onDismiss={() => setSheet(null)}
          onSaved={handleSaved}
        />
      ) : null}
    </View>
  );
});

interface PromotionItemCardProps {
  card: ProductPromotionCardModel;
  busy: boolean;
  disabled: boolean;
  onToggle: (enable: boolean) => void;
  onEdit: () => void;
  onEndNow: () => void;
  onCopy: () => void;
}

function PromotionItemCard({ card, busy, disabled, onToggle, onEdit, onEndNow, onCopy }: PromotionItemCardProps) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const { item, status, scope, savings } = card;
  const name = item.name || t("promotion.unnamed");

  const pricingParts = [
    savings ? t("promotion.unitPrice", { price: formatMoney(savings.unitPrice) }) : null,
    savings ? (savings.isDiscount ? t("promotion.saving", { amount: formatMoney(savings.savingPerUnit) }) : t("promotion.noSaving")) : null,
  ].filter(Boolean);

  const dateText = card.longTerm
    ? t("promotion.longTerm")
    : t("promotion.dateRange", {
        start: formatPromotionDate(item.effectiveStart),
        end: formatPromotionDate(item.effectiveEnd),
      });
  const dayText =
    card.dayCount == null
      ? null
      : status === "upcoming"
        ? t("promotion.startsIn", { count: card.dayCount })
        : t("promotion.daysLeft", { count: card.dayCount });

  return (
    <View style={[styles.item, status === "disabled" ? styles.itemDisabled : null]}>
      <View style={styles.itemTop}>
        <StatusTag status={status} label={t(`promotion.status.${status}`)} />
        <View style={[styles.tag, scope === "store" ? styles.tagStore : styles.tagMulti]}>
          <Text style={[styles.tagText, scope === "store" ? styles.tagStoreText : styles.tagMultiText]}>
            {scope === "store"
              ? t("promotion.scope.store")
              : t("promotion.scope.multi", { count: item.storesCount })}
          </Text>
        </View>
        <Text style={styles.itemName} numberOfLines={1}>{name}</Text>
        {card.canToggle ? (
          busy ? (
            <ActivityIndicator size="small" />
          ) : (
            <Switch
              value={item.isEnabled}
              onValueChange={onToggle}
              disabled={disabled}
              accessibilityLabel={t("promotion.toggleLabel", { name })}
            />
          )
        ) : scope === "multi" ? (
          <Icon source="lock-outline" size={16} color={HB_COLORS.textSecondary} />
        ) : null}
      </View>

      <View style={styles.ruleRow}>
        <Text style={styles.ruleText}>
          {t("promotion.rule", { count: item.applyQuantity, price: formatMoney(item.fixedPrice) })}
        </Text>
        {pricingParts.length > 0 ? (
          <Text style={[styles.ruleSub, savings && !savings.isDiscount ? styles.ruleSubWarn : null]} numberOfLines={1}>
            {pricingParts.join(" · ")}
          </Text>
        ) : null}
      </View>

      <View style={styles.metaRow}>
        <Icon source="calendar-blank-outline" size={13} color={HB_COLORS.textSecondary} />
        <Text style={styles.metaText}>{dayText ? `${dateText}（${dayText}）` : dateText}</Text>
        <Text style={styles.metaText}>{t("promotion.products", { count: item.productsCount })}</Text>
        <Text style={styles.metaText}>{t(item.isExclusive ? "promotion.exclusive" : "promotion.stackable")}</Text>
      </View>

      {card.canEdit || card.canEndNow ? (
        <View style={styles.actions}>
          {card.canEdit ? (
            <MiniButton icon="pencil-outline" label={t("promotion.actions.edit")} tone="tonal" disabled={disabled} onPress={onEdit} />
          ) : null}
          {card.canEndNow ? (
            <MiniButton icon="stop-circle-outline" label={t("promotion.actions.endNow")} tone="ghost" disabled={disabled} onPress={onEndNow} />
          ) : null}
          {busy && !card.canToggle ? <ActivityIndicator size="small" /> : null}
        </View>
      ) : null}

      {scope === "multi" && card.canCopy ? (
        <>
          <View style={styles.lockLine}>
            <Icon source="information-outline" size={14} color={HB_COLORS.textSecondary} />
            <Text style={styles.lockText}>{t("promotion.lockedHint")}</Text>
          </View>
          <View style={styles.actions}>
            <MiniButton icon="content-copy" label={t("promotion.actions.copy")} tone="ghost" disabled={disabled} onPress={onCopy} />
            {busy ? <ActivityIndicator size="small" /> : null}
          </View>
        </>
      ) : null}
    </View>
  );
}

function StatusTag({ status, label }: { status: ProductPromotionCardModel["status"]; label: string }) {
  const tone =
    status === "active"
      ? { bg: "#ECFDF3", fg: HB_COLORS.success }
      : status === "upcoming"
        ? { bg: "#FFF1E6", fg: PROMO.text }
        : { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary };
  return (
    <View style={[styles.tag, { backgroundColor: tone.bg }]}>
      <Text style={[styles.tagText, { color: tone.fg }]}>{label}</Text>
    </View>
  );
}

function MiniButton({
  icon,
  label,
  tone,
  disabled,
  onPress,
}: {
  icon: string;
  label: string;
  tone: "tonal" | "ghost";
  disabled?: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.mini,
        tone === "tonal" ? styles.miniTonal : styles.miniGhost,
        disabled ? styles.miniDisabled : null,
        pressed ? styles.pressed : null,
      ]}
    >
      <Icon source={icon} size={14} color={tone === "tonal" ? HB_COLORS.action : HB_COLORS.textPrimary} />
      <Text style={[styles.miniText, tone === "tonal" ? styles.miniTonalText : null]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  section: {
    gap: HB_SPACING.xs,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: PROMO.line,
    backgroundColor: PROMO.background,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
  },
  header: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 28 },
  headerTitle: { fontSize: 14, fontWeight: "800", color: PROMO.text },
  countBadge: {
    minWidth: 20,
    height: 20,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: PROMO.line,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 5,
  },
  countText: { fontSize: 11, fontWeight: "800", color: PROMO.text, fontVariant: ["tabular-nums"] },
  headerSpacer: { flex: 1 },
  viewAll: { flexDirection: "row", alignItems: "center", minHeight: 28 },
  viewAllText: { fontSize: 12, fontWeight: "700", color: PROMO.text },
  item: {
    gap: 6,
    backgroundColor: HB_COLORS.white,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: PROMO.itemLine,
    padding: 10,
  },
  itemDisabled: { opacity: 0.78 },
  itemTop: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 32 },
  tag: { borderRadius: 4, paddingHorizontal: 6, paddingVertical: 2, flexShrink: 0 },
  tagText: { fontSize: 11, fontWeight: "800" },
  tagStore: { backgroundColor: "#EFF8FF" },
  tagStoreText: { color: HB_COLORS.action },
  tagMulti: { backgroundColor: HB_COLORS.surfaceMuted },
  tagMultiText: { color: HB_COLORS.textSecondary },
  itemName: { flex: 1, minWidth: 0, fontSize: 13.5, fontWeight: "800", color: HB_COLORS.textPrimary },
  ruleRow: { flexDirection: "row", alignItems: "baseline", flexWrap: "wrap", columnGap: HB_SPACING.xs },
  ruleText: { fontSize: 19, fontWeight: "900", color: PROMO.text, fontVariant: ["tabular-nums"] },
  ruleSub: { flexShrink: 1, fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  ruleSubWarn: { color: HB_COLORS.warning },
  metaRow: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", columnGap: HB_SPACING.xs, rowGap: 2 },
  metaText: { fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  actions: { flexDirection: "row", alignItems: "center", gap: 6, flexWrap: "wrap" },
  mini: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    minHeight: 32,
    paddingHorizontal: 10,
    borderRadius: HB_RADIUS.control,
  },
  miniTonal: { backgroundColor: "#EFF8FF" },
  miniGhost: { borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white },
  miniDisabled: { opacity: 0.5 },
  miniText: { fontSize: 12.5, fontWeight: "800", color: HB_COLORS.textPrimary },
  miniTonalText: { color: HB_COLORS.action },
  lockLine: {
    flexDirection: "row",
    gap: 6,
    alignItems: "flex-start",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: HB_RADIUS.control,
    paddingHorizontal: 10,
    paddingVertical: HB_SPACING.xs,
  },
  lockText: { flex: 1, fontSize: 12, lineHeight: 17, color: HB_COLORS.textSecondary },
  addButton: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
    minHeight: 40,
    borderWidth: 1.5,
    borderStyle: "dashed",
    borderColor: PROMO.text,
    borderRadius: 10,
    backgroundColor: PROMO.addBackground,
  },
  addText: { fontSize: 13.5, fontWeight: "800", color: PROMO.text },
  readonlyHint: { flexDirection: "row", alignItems: "center", gap: 6 },
  readonlyHintText: { fontSize: 12, color: PROMO.text },
  pressed: { opacity: 0.72 },
});
