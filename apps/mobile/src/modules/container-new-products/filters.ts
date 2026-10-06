import type { ContainerNewProductBasis, ContainerNewProductItem } from "./types";

export type ProductTypeFilter = "new" | "existing" | "all";
export type WarehouseFilter = "any" | "arrived" | "transit";

export const PRODUCT_TYPE_FILTERS: readonly ProductTypeFilter[] = ["new", "existing", "all"];
export const WAREHOUSE_FILTERS: readonly WarehouseFilter[] = ["any", "arrived", "transit"];

export interface ContainerNewProductFilters {
  productType: ProductTypeFilter;
  warehouse: WarehouseFilter;
  /** 选中的货柜（ContainerCode）；空数组 = 全部货柜 */
  containerCodes: readonly string[];
}

// 默认只看新商品：与页面标题「HB新品」和工作台角标口径一致
export const DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS: ContainerNewProductFilters = { productType: "new", warehouse: "any", containerCodes: [] };

type FilterableItem = Pick<ContainerNewProductItem, "isNewProduct" | "basis" | "containerCode">;

export function matchesProductType(item: Pick<ContainerNewProductItem, "isNewProduct">, filter: ProductTypeFilter): boolean {
  return filter === "all" || (filter === "new") === item.isNewProduct;
}

// 到仓状态即后端 basis：actual = 货柜已有到仓库日期，estimated = 还在途、按预计到岸推算
export function matchesWarehouse(item: Pick<ContainerNewProductItem, "basis">, filter: WarehouseFilter): boolean {
  return filter === "any" || (filter === "arrived") === (item.basis === "actual");
}

export function matchesContainers(item: Pick<ContainerNewProductItem, "containerCode">, containerCodes: readonly string[]): boolean {
  return containerCodes.length === 0 || containerCodes.some((code) => code.toUpperCase() === item.containerCode.toUpperCase());
}

/**
 * 三类筛选同时满足才显示。ignore 用于分面计数：算某一类选项的数量时忽略它自己，
 * 例如「已有商品 N」按当前到仓状态和货柜统计，而不受当前已选的商品类型影响。
 */
export function matchesFilters(item: FilterableItem, filters: ContainerNewProductFilters, ignore?: keyof ContainerNewProductFilters): boolean {
  return (ignore === "productType" || matchesProductType(item, filters.productType))
    && (ignore === "warehouse" || matchesWarehouse(item, filters.warehouse))
    && (ignore === "containerCodes" || matchesContainers(item, filters.containerCodes));
}

export function countActiveFilters(filters: ContainerNewProductFilters): number {
  return (filters.productType !== DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS.productType ? 1 : 0)
    + (filters.warehouse !== "any" ? 1 : 0)
    + (filters.containerCodes.length > 0 ? 1 : 0);
}

export interface ContainerOption {
  containerCode: string;
  /** 显示用柜号：优先 ContainerNumber，没有时用 ContainerCode */
  label: string;
  basis: ContainerNewProductBasis;
  estimatedStoreArrivalDate: string;
  estimatedStoreArrivalDateEnd: string | null;
  newKinds: number;
  existingKinds: number;
}

// 从商品列表汇总出货柜选项：同柜同商品已由后端合并成一行，这里按商品去重计数（忽略大小写）；
// 按到店起始日、柜号排序，与列表顺序一致
export function summarizeContainers(items: readonly ContainerNewProductItem[]): ContainerOption[] {
  const byCode = new Map<string, { option: Omit<ContainerOption, "newKinds" | "existingKinds">; newCodes: Set<string>; existingCodes: Set<string> }>();
  for (const item of items) {
    const key = item.containerCode.toUpperCase();
    let entry = byCode.get(key);
    if (!entry) {
      entry = {
        option: {
          containerCode: item.containerCode,
          label: item.containerNumber?.trim() || item.containerCode,
          basis: item.basis,
          estimatedStoreArrivalDate: item.estimatedStoreArrivalDate,
          estimatedStoreArrivalDateEnd: item.estimatedStoreArrivalDateEnd,
        },
        newCodes: new Set(),
        existingCodes: new Set(),
      };
      byCode.set(key, entry);
    }
    (item.isNewProduct ? entry.newCodes : entry.existingCodes).add(item.productCode.toUpperCase());
  }
  return [...byCode.values()]
    .map(({ option, newCodes, existingCodes }) => ({ ...option, newKinds: newCodes.size, existingKinds: existingCodes.size }))
    .sort((a, b) => compareOrdinal(a.estimatedStoreArrivalDate, b.estimatedStoreArrivalDate) || compareOrdinal(a.label.toUpperCase(), b.label.toUpperCase()));
}

// 刷新或换店后，已选货柜可能不在新列表里了；只保留仍存在的，避免筛成空列表却看不到选中项
export function pruneSelectedContainers(containerCodes: readonly string[], options: readonly Pick<ContainerOption, "containerCode">[]): string[] {
  const available = new Set(options.map((option) => option.containerCode.toUpperCase()));
  return containerCodes.filter((code) => available.has(code.toUpperCase()));
}

export function matchesContainerSearch(option: Pick<ContainerOption, "label" | "containerCode">, keyword: string): boolean {
  const normalized = keyword.replace(/\s+/g, "").toUpperCase();
  if (!normalized) return true;
  return option.label.replace(/\s+/g, "").toUpperCase().includes(normalized) || option.containerCode.toUpperCase().includes(normalized);
}

function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
