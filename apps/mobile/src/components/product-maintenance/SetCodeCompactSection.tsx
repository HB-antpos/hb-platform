import type { Ref } from "react";
import type { View } from "react-native";
import { CodeTableCard } from "@/components/product-maintenance/CodeTableCard";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { ProductSetCodeItem } from "@/modules/product-maintenance/types";

interface SetCodeCompactSectionProps {
  items: ProductSetCodeItem[];
  savingItemId?: string | null;
  printingItemId?: string | null;
  /** 相对基线有改动的套装行 setCodeId。 */
  dirtyItemIds?: ReadonlySet<string>;
  totalCount?: number;
  loading?: boolean;
  loadingMore?: boolean;
  hasMore?: boolean;
  /** 离线模式：隐藏新增与加载更多，条码/价格不可编辑，打印保留。 */
  readOnly?: boolean;
  /** 本次扫到的套装条码：按条码匹配行（服务端目标的 codeId 与行 id 口径一致，但条码匹配更稳）。 */
  highlightedBarcode?: string | null;
  highlightedRowRef?: Ref<View>;
  onEditItemBarcode: (setCodeId: string) => void;
  onEditItemRetailPrice: (setCodeId: string) => void;
  onSaveItem: (setCodeId: string) => void;
  onPrintItem: (setCodeId: string) => void;
  onAddItem: () => void;
  onLoadMore?: () => void;
}

export function SetCodeCompactSection({
  items,
  savingItemId,
  printingItemId,
  dirtyItemIds,
  totalCount,
  loading,
  loadingMore,
  hasMore,
  readOnly = false,
  highlightedBarcode,
  highlightedRowRef,
  onEditItemBarcode,
  onEditItemRetailPrice,
  onSaveItem,
  onPrintItem,
  onAddItem,
  onLoadMore,
}: SetCodeCompactSectionProps) {
  const { t } = useAppTranslation("productQuery");
  const highlightedCode = highlightedBarcode?.trim().toUpperCase();
  const highlightedId = highlightedCode
    ? items.find((item) => item.setBarcode?.trim().toUpperCase() === highlightedCode)?.setCodeId ?? null
    : null;

  return (
    <CodeTableCard
      title={t("setCode.title")}
      priceColumnLabel={t("codes.setPriceColumn")}
      loadingText={t("setCode.loading")}
      rows={items.map((item) => ({
        id: item.setCodeId,
        barcode: item.setBarcode,
        price: item.setRetailPrice == null ? null : item.setRetailPrice.toFixed(2),
        dirty: dirtyItemIds?.has(item.setCodeId) ?? false,
      }))}
      totalCount={totalCount}
      savingItemId={savingItemId}
      printingItemId={printingItemId}
      adding={savingItemId === "new-set"}
      loading={loading}
      loadingMore={loadingMore}
      hasMore={hasMore}
      onEditItemBarcode={onEditItemBarcode}
      onEditItemRetailPrice={onEditItemRetailPrice}
      onSaveItem={onSaveItem}
      onPrintItem={onPrintItem}
      onAddItem={onAddItem}
      onLoadMore={onLoadMore}
      readOnly={readOnly}
      highlightedId={highlightedId}
      highlightedRowRef={highlightedRowRef}
    />
  );
}
