import { useEffect, useState } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { FilterChip } from "@/components/local-supplier-invoices/filter-chip";
import {
  BARCODE_STATUS_FILTERS,
  ACTION_TYPE_FILTERS,
  PRODUCT_STATUS_FILTERS,
  PRODUCT_TYPE_FILTERS,
  SPECIAL_PRODUCT_FILTERS,
  createEmptyDetailMoreFilters,
  toggleFilterValue,
  type DetailFilterCounts,
  type DetailMoreFilters,
} from "@/modules/local-supplier-invoices/detail";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

type Translate = (key: string, options?: Record<string, unknown>) => string;

/**
 * 「更多筛选」底部弹层：商品类型 / 商品状态 / 条码状态 / 操作类型 / 特殊商品。
 * 与 Web 一致：组间 AND，组内多选 OR。草稿在弹层里编辑，点「应用」才生效。
 */
export function MoreFiltersSheet({
  visible,
  value,
  counts,
  countMatches,
  onApply,
  onDismiss,
  t,
}: {
  visible: boolean;
  value: DetailMoreFilters;
  counts: DetailFilterCounts;
  /** 按草稿预估命中行数，显示在「应用」按钮上。 */
  countMatches: (draft: DetailMoreFilters) => number;
  onApply: (next: DetailMoreFilters) => void;
  onDismiss: () => void;
  t: Translate;
}) {
  const [draft, setDraft] = useState<DetailMoreFilters>(value);
  useEffect(() => {
    if (visible) setDraft(value);
  }, [value, visible]);

  const productTypeLabel = (type: (typeof PRODUCT_TYPE_FILTERS)[number]) =>
    type === 0 ? t("detail.productType.single")
      : type === 1 ? t("detail.productType.bundle")
        : type === 2 ? t("detail.productType.multi")
          : t("detail.productType.unknown");
  const barcodeLabel = (status: (typeof BARCODE_STATUS_FILTERS)[number]) => t(`detail.barcodeStatus.${
    status === "normal" ? "filterNormal"
      : status === "noMatch" ? "filterNoMatch"
        : status === "multiMatch" ? "filterMultiMatch" : "notDetected"
  }`);
  const productLabel = (status: (typeof PRODUCT_STATUS_FILTERS)[number]) => t(`detail.productStatus.${
    status === "exists" ? "filterExists"
      : status === "notExists" ? "filterNotExists"
        : status === "createdHere" ? "createdHere" : "notDetected"
  }`);

  return (
    <BusinessSheet
      visible={visible}
      title={t("detail.more.title")}
      onDismiss={onDismiss}
      footer={(
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.footerSide} onPress={() => setDraft(createEmptyDetailMoreFilters())}>
            {t("detail.more.clear")}
          </Button>
          <Button mode="contained" style={styles.footerMain} onPress={() => onApply(draft)}>
            {t("detail.more.apply", { count: countMatches(draft) })}
          </Button>
        </View>
      )}
    >
      <Group title={t("detail.more.productType")}>
        {/* 「未知」类型只在有数据时出现，避免一个永远为 0 的选项（与 Web 一致）。 */}
        {PRODUCT_TYPE_FILTERS.filter((type) => type !== "unknown" || counts.productType.unknown > 0).map((type) => (
          <FilterChip
            key={String(type)}
            label={productTypeLabel(type)}
            count={counts.productType[type]}
            selected={draft.productTypes.includes(type)}
            onPress={() => setDraft({ ...draft, productTypes: toggleFilterValue(draft.productTypes, type) })}
          />
        ))}
      </Group>
      <Group title={t("detail.more.productStatus")}>
        {PRODUCT_STATUS_FILTERS.map((status) => (
          <FilterChip
            key={status}
            label={productLabel(status)}
            count={counts.product[status]}
            selected={draft.productStatuses.includes(status)}
            onPress={() => setDraft({ ...draft, productStatuses: toggleFilterValue(draft.productStatuses, status) })}
          />
        ))}
      </Group>
      <Group title={t("detail.more.barcodeStatus")}>
        {BARCODE_STATUS_FILTERS.map((status) => (
          <FilterChip
            key={status}
            label={barcodeLabel(status)}
            count={counts.barcode[status]}
            selected={draft.barcodeStatuses.includes(status)}
            onPress={() => setDraft({ ...draft, barcodeStatuses: toggleFilterValue(draft.barcodeStatuses, status) })}
          />
        ))}
      </Group>
      <Group title={t("detail.more.activity")}>
        {ACTION_TYPE_FILTERS.map((action) => (
          <FilterChip
            key={action}
            label={t(`detail.action.${action}`)}
            count={counts.action[action]}
            selected={draft.actionTypes.includes(action)}
            onPress={() => setDraft({ ...draft, actionTypes: toggleFilterValue(draft.actionTypes, action) })}
          />
        ))}
      </Group>
      <Group title={t("detail.more.special")}>
        {SPECIAL_PRODUCT_FILTERS.map((flag) => (
          <FilterChip
            key={flag}
            label={t(flag === "yes" ? "detail.more.yes" : "detail.more.no")}
            count={counts.special[flag]}
            selected={draft.special.includes(flag)}
            onPress={() => setDraft({ ...draft, special: toggleFilterValue(draft.special, flag) })}
          />
        ))}
      </Group>
      <Text style={styles.hint}>{t("detail.more.hint")}</Text>
    </BusinessSheet>
  );
}

function Group({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={styles.group}>
      <Text style={styles.groupTitle}>{title}</Text>
      <View style={styles.chips}>{children}</View>
    </View>
  );
}

const styles = StyleSheet.create({
  group: { gap: HB_SPACING.xs, marginBottom: HB_SPACING.sm },
  groupTitle: { fontSize: 12, color: HB_COLORS.textSecondary },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  hint: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerSide: { flex: 1 },
  footerMain: { flex: 2 },
});
