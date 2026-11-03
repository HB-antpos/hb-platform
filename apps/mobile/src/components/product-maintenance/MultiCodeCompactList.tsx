import type { Ref } from "react";
import type { View } from "react-native";
import { CodeTableCard } from "@/components/product-maintenance/CodeTableCard";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { MultiCodeEditableItem } from "@/modules/product-maintenance/types";

interface MultiCodeCompactListProps {
  items: MultiCodeEditableItem[];
  savingItemId?: string | null;
  printingItemId?: string | null;
  /** 相对基线有改动的行 id（setCodeId，历史多码为 UUID）。 */
  dirtyItemIds?: ReadonlySet<string>;
  totalCount?: number;
  loading?: boolean;
  loadingMore?: boolean;
  hasMore?: boolean;
  /** 离线模式：隐藏新增与加载更多，条码/价格不可编辑，打印保留。 */
  readOnly?: boolean;
  /** 本次扫到的多码条码：按条码匹配行（服务端目标的 codeId 是门店投影 UUID，与行 id 不一定一致）。 */
  highlightedBarcode?: string | null;
  highlightedRowRef?: Ref<View>;
  onEditItemBarcode: (setCodeId: string) => void;
  onEditItemRetailPrice: (setCodeId: string) => void;
  onSaveItem: (setCodeId: string) => void;
  onPrintItem: (setCodeId: string) => void;
  onAddItem: () => void;
  onLoadMore?: () => void;
}

export function MultiCodeCompactList({
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
}: MultiCodeCompactListProps) {
  const { t } = useAppTranslation("productQuery");
  const highlightedCode = highlightedBarcode?.trim().toUpperCase();
  const highlightedItem = highlightedCode
    ? items.find((item) => item.barcode?.trim().toUpperCase() === highlightedCode)
    : undefined;
  const highlightedId = highlightedItem ? highlightedItem.setCodeId || highlightedItem.uuid : null;

  return (
    <CodeTableCard
      title={t("multiCode.title")}
      priceColumnLabel={t("codes.retailColumn")}
      loadingText={t("multiCode.loading")}
      rows={items.map((item) => {
        // 历史多码可能没有 setCodeId，交互必须稳定地回传 UUID，不能误命中首行。
        const itemId = item.setCodeId || item.uuid;
        return {
          id: itemId,
          barcode: item.barcode,
          price: item.retailPrice == null ? null : item.retailPrice.toFixed(2),
          // 多码零售价为空时打印沿用主条码价，界面显示为「跟随」。
          followsMain: item.retailPrice == null,
          dirty: dirtyItemIds?.has(itemId) ?? false,
        };
      })}
      totalCount={totalCount}
      savingItemId={savingItemId}
      printingItemId={printingItemId}
      adding={savingItemId === "new-multi"}
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
