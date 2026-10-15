import { useEffect, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Text } from "react-native-paper";
import { NumericInputModal } from "@/components/product-maintenance/NumericInputModal";
import {
  getPriceNotificationPreview,
  lookupSuggestedDiscount,
  updateSuggestedDiscount,
} from "@/modules/price-updates/api";
import {
  buildPriceNotificationPreviewMessage,
  buildPriceNotificationSaveMessage,
} from "@/modules/price-updates/price-notification";
import { applyDiscount, formatDiscountLabel, formatMoney } from "@/modules/price-updates/presentation";
import { priceUpdateCountQueryKey } from "@/modules/price-updates/query-keys";
import {
  formatSuggestedDiscountInput,
  isSameSuggestedDiscount,
  parseSuggestedDiscountInput,
} from "@/modules/price-updates/suggested-discount";
import type { PriceNotificationPreview } from "@/modules/price-updates/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";

const PREVIEW_DEBOUNCE_MS = 500;

interface WarehouseSuggestedDiscountFieldProps {
  productCode: string;
  /** 仓库零售价，用于计算只读的建议折后价。 */
  retailPrice: number | null;
  /** 后端只允许 Admin/WarehouseManager/WarehouseStaff 修改；其它角色只读展示。 */
  editable: boolean;
  dense?: boolean;
  onMessage: (message: string) => void;
}

/**
 * 仓库维护页的「建议折扣 / 建议折后价」。自带 lookup 回填、数字键盘编辑、保存前预告与保存，
 * 页面只需要渲染它并接收提示文案。
 */
export function WarehouseSuggestedDiscountField({
  productCode,
  retailPrice,
  editable,
  dense = false,
  onMessage,
}: WarehouseSuggestedDiscountFieldProps) {
  const { t, language } = useAppTranslation(["priceUpdates", "common"]);
  const queryClient = useQueryClient();
  // 建议折扣接口只接受账号登录；设备会话下不发请求，避免无意义的 401。
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const queryKey = ["priceUpdates", "suggestedDiscount", productCode] as const;
  const lookupQuery = useQuery({
    queryKey,
    enabled: isAuthenticated && Boolean(productCode),
    queryFn: () => lookupSuggestedDiscount(productCode),
    staleTime: 0,
  });
  const [draft, setDraft] = useState<string | null>(null);
  const [preview, setPreview] = useState<PriceNotificationPreview | null>(null);
  const [saving, setSaving] = useState(false);

  const currentRate = lookupQuery.data ?? null;
  const loaded = lookupQuery.isSuccess;
  const parsedDraft = draft == null ? null : parseSuggestedDiscountInput(draft);
  const draftRate = parsedDraft?.ok ? parsedDraft.rate : undefined;
  const draftDirty = draftRate !== undefined && !isSameSuggestedDiscount(draftRate, currentRate);

  useEffect(() => {
    setDraft(null);
  }, [productCode]);

  useEffect(() => {
    setPreview(null);
    if (draft == null || !draftDirty || draftRate === undefined) {
      return;
    }
    let cancelled = false;
    // 每次按键都会触发，防抖后只为最后一次输入请求预告。
    const timer = setTimeout(() => {
      getPriceNotificationPreview({ productCode, suggestedDiscountRate: draftRate })
        .then((result) => {
          if (!cancelled) setPreview(result);
        })
        .catch(() => {
          // 预告只是提示，失败不影响保存。
        });
    }, PREVIEW_DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [draft, draftDirty, draftRate, productCode]);

  if (!isAuthenticated) {
    return null;
  }

  const handleConfirm = async () => {
    if (saving || draft == null) return;
    if (!parsedDraft?.ok) {
      onMessage(t("suggestedDiscount.invalid"));
      return;
    }
    if (!draftDirty) {
      setDraft(null);
      return;
    }
    setSaving(true);
    try {
      const result = await updateSuggestedDiscount([productCode], parsedDraft.rate, "MobileWarehouse");
      queryClient.setQueryData(queryKey, parsedDraft.rate);
      void queryClient.invalidateQueries({ queryKey: priceUpdateCountQueryKey() });
      setDraft(null);
      onMessage(buildPriceNotificationSaveMessage(result.notification, t) ?? t("suggestedDiscount.saved"));
    } catch (error) {
      onMessage(resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "suggestedDiscount.saveFailed" }));
    } finally {
      setSaving(false);
    }
  };

  const valueText = !loaded
    ? lookupQuery.isError ? "--" : "…"
    : currentRate == null
      ? t("suggestedDiscount.unset")
      : formatDiscountLabel(currentRate, t);
  const finalPriceText = loaded && currentRate != null ? formatMoney(applyDiscount(retailPrice, currentRate)) : "--";
  const helperText = !parsedDraft?.ok && draft != null
    ? t("suggestedDiscount.invalid")
    // 建议折扣永远不会自动下发，预告固定用「需改价」口径。
    : buildPriceNotificationPreviewMessage(preview, false, t) ?? t("suggestedDiscount.inputHint");
  const canEdit = editable && loaded && !saving;

  return (
    <View style={styles.container}>
      <View style={styles.row}>
        <Pressable
          disabled={!canEdit}
          onPress={() => setDraft(formatSuggestedDiscountInput(currentRate))}
          accessibilityRole="button"
          accessibilityLabel={t("suggestedDiscount.label")}
          style={[styles.tile, dense ? styles.tileDense : null, canEdit ? styles.tilePressable : null]}
        >
          <Text variant="labelSmall" style={styles.tileLabel} numberOfLines={1}>{t("suggestedDiscount.label")}</Text>
          <Text variant="bodyMedium" style={styles.tileValue} numberOfLines={1}>{valueText}</Text>
        </Pressable>
        <View style={[styles.tile, dense ? styles.tileDense : null]}>
          <Text variant="labelSmall" style={styles.tileLabel} numberOfLines={1}>{t("suggestedDiscount.finalPrice")}</Text>
          <Text variant="bodyMedium" style={styles.tileValue} numberOfLines={1}>{finalPriceText}</Text>
        </View>
      </View>
      <Text variant="bodySmall" style={styles.helper}>{t("suggestedDiscount.helper")}</Text>

      {draft != null ? (
        <NumericInputModal
          visible
          title={t("suggestedDiscount.editorTitle")}
          value={draft}
          allowDecimal
          emptyValueText={t("suggestedDiscount.unset")}
          helperText={helperText}
          confirmLabel={t("common:actions.save")}
          onChangeValue={setDraft}
          onConfirm={() => void handleConfirm()}
          onDismiss={() => {
            if (!saving) setDraft(null);
          }}
        />
      ) : null}
    </View>
  );
}

interface RetailPriceNotificationPreviewProps {
  productCode: string;
  /** 待保存的新零售价（输入框原文）。 */
  retailPrice: string;
  /** 确认弹窗里「同步分店零售价」开关的当前取值；缺省按默认值（开）处理。 */
  syncStoreRetailPrices?: boolean;
  /** 确认弹窗里「生成价格任务」开关的当前取值；缺省按默认值（开）处理。 */
  generateTasks?: boolean;
}

/**
 * 仓库零售价保存前的预告。确认弹窗有「同步分店零售价」与「生成价格任务」两个开关，
 * 预告只展示当前选择的后果：生成价格任务关闭时不会通知任何分店，所以不显示；
 * 开启时按是否同步分店价显示「自动更新 + 待换标签」或「需改价」，受影响分店为 0 时不显示。
 */
export function RetailPriceNotificationPreview({
  productCode,
  retailPrice,
  syncStoreRetailPrices = true,
  generateTasks = true,
}: RetailPriceNotificationPreviewProps) {
  const { t } = useAppTranslation("priceUpdates");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const [preview, setPreview] = useState<PriceNotificationPreview | null>(null);

  useEffect(() => {
    setPreview(null);
    const price = Number(retailPrice);
    if (!isAuthenticated || !productCode || !retailPrice.trim() || !Number.isFinite(price)) {
      return;
    }
    let cancelled = false;
    // 弹窗出现时新价格已经定稿，不需要防抖，立即请求以便用户在点按钮前看到预告。
    getPriceNotificationPreview({ productCode, retailPrice: price })
      .then((result) => {
        if (!cancelled) setPreview(result);
      })
      .catch(() => {
        // 预告只是提示，失败不影响保存。
      });
    return () => {
      cancelled = true;
    };
  }, [isAuthenticated, productCode, retailPrice]);

  // 不生成价格任务就不会通知分店，没有可预告的内容。
  const message = generateTasks ? buildPriceNotificationPreviewMessage(preview, syncStoreRetailPrices, t) : null;
  if (!message) {
    return null;
  }

  return (
    <View style={styles.previewBox}>
      <Text variant="bodySmall" style={styles.previewText}>{message}</Text>
    </View>
  );
}

// 瓦片配色与仓库页 InfoTile 保持一致，嵌在同一张商品信息卡片里不突兀。
const styles = StyleSheet.create({
  container: { gap: 6 },
  row: { flexDirection: "row", gap: 6 },
  tile: { flex: 1, minWidth: 0, backgroundColor: "#F8FAFC", borderRadius: 8, paddingHorizontal: 12, paddingVertical: 10, gap: 4, borderWidth: 1, borderColor: "#E2E8F0" },
  tileDense: { paddingHorizontal: 10, paddingVertical: 7, gap: 2 },
  tilePressable: { borderColor: "#BFDBFE", backgroundColor: "#F8FBFF" },
  tileLabel: { color: "#64748B" },
  tileValue: { color: "#0F172A", fontWeight: "600" },
  helper: { color: "#64748B" },
  previewBox: { gap: 4, padding: 10, borderRadius: 8, backgroundColor: "#FFFAEB" },
  previewText: { color: "#B54708" },
});
