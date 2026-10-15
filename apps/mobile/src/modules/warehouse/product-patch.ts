import type { SupplyNoticeInput } from "@/modules/supply-notice/types";
import type { WarehouseProductPatchRequest } from "@/modules/warehouse/types";

export interface WarehouseProductFormSnapshot {
  purchasePrice: string;
  retailPrice: string;
  domesticPrice: string;
  stockQuantity: string;
  middlePackageQuantity: string;
  packingQuantity: string;
  volume: string;
  grade: string;
  warehouseIsActive: boolean;
}

export type WarehouseProductPatchField =
  | "purchasePrice"
  | "retailPrice"
  | "domesticPrice"
  | "stockQuantity"
  | "middlePackageQuantity"
  | "packingQuantity"
  | "volume"
  | "grade"
  | "warehouseIsActive";

interface WarehouseProductPatchOptions {
  field?: WarehouseProductPatchField;
  statusOnly?: boolean;
  /** 仅 retailPrice 单字段保存使用；缺省 = 同步分店零售价。 */
  syncStoreRetailPrices?: boolean;
  /** 仅 retailPrice 单字段保存使用；缺省 = 给分店生成价格任务。 */
  generateTasks?: boolean;
  supplyNotice?: SupplyNoticeInput;
}

export function buildWarehouseProductPatchRequest(
  form: WarehouseProductFormSnapshot,
  parseNullableNumber: (value: string) => number | null,
  options?: WarehouseProductPatchOptions
): WarehouseProductPatchRequest {
  if (options?.statusOnly || options?.field === "warehouseIsActive") {
    // 供货说明只在下架时有意义；上架不传。
    return form.warehouseIsActive || !options?.supplyNotice
      ? { warehouseIsActive: form.warehouseIsActive }
      : { warehouseIsActive: false, supplyNotice: options.supplyNotice };
  }

  if (options?.field) {
    switch (options.field) {
      case "purchasePrice": {
        const purchasePrice = parseNullableNumber(form.purchasePrice);
        return { purchasePrice, importPrice: purchasePrice };
      }
      case "retailPrice": {
        const retailPrice = parseNullableNumber(form.retailPrice);
        // 默认直接同步分店零售价并生成价格任务；只有操作人在确认弹窗里关掉开关才会传 false。
        // 字段只在零售价保存时携带，进货价等其它字段的保存不带这两个字段。
        return {
          retailPrice,
          oemPrice: retailPrice,
          syncStoreRetailPrices: options.syncStoreRetailPrices ?? true,
          generateTasks: options.generateTasks ?? true,
        };
      }
      case "domesticPrice":
        return { domesticPrice: parseNullableNumber(form.domesticPrice) };
      case "stockQuantity":
        return { stockQuantity: parseNullableNumber(form.stockQuantity) };
      case "middlePackageQuantity":
        return { middlePackageQuantity: parseNullableNumber(form.middlePackageQuantity) };
      case "packingQuantity":
        return { packingQuantity: parseNullableNumber(form.packingQuantity) };
      case "volume":
        return { volume: parseNullableNumber(form.volume) };
      case "grade":
        return { grade: form.grade || null };
    }
  }

  return {
    purchasePrice: parseNullableNumber(form.purchasePrice),
    importPrice: parseNullableNumber(form.purchasePrice),
    retailPrice: parseNullableNumber(form.retailPrice),
    oemPrice: parseNullableNumber(form.retailPrice),
    // 整表保存没有确认弹窗，不能依赖后端「不传 = true」的新默认值去改分店价；
    // 显式保持原行为：不动分店价，由分店收到改价任务后自行确认。
    syncStoreRetailPrices: false,
    generateTasks: true,
    domesticPrice: parseNullableNumber(form.domesticPrice),
    stockQuantity: parseNullableNumber(form.stockQuantity),
    middlePackageQuantity: parseNullableNumber(form.middlePackageQuantity),
    packingQuantity: parseNullableNumber(form.packingQuantity),
    volume: parseNullableNumber(form.volume),
    grade: form.grade || null,
    warehouseIsActive: form.warehouseIsActive,
  };
}

export function isWarehouseStatusOnlyPatch(patch: Partial<WarehouseProductFormSnapshot>) {
  const keys = Object.keys(patch);
  return keys.length === 1 && keys[0] === "warehouseIsActive";
}
