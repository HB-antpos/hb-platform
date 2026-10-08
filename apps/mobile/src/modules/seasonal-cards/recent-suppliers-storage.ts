import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  buildSeasonalCardRecentSuppliersKey,
  normalizeSeasonalCardRecentSuppliers,
  pushSeasonalCardRecentSupplier,
} from "@/modules/seasonal-cards/recent-suppliers";
import type { SeasonalCardSupplierOption } from "@/modules/seasonal-cards/types";

/** 读取某分店最近使用的供应商；存储损坏或读取失败时按空列表处理，不影响填报。 */
export async function loadSeasonalCardRecentSuppliers(storeCode: string) {
  if (!storeCode.trim()) {
    return [];
  }
  try {
    const raw = await AppAsyncStorage.getObject(buildSeasonalCardRecentSuppliersKey(storeCode));
    return normalizeSeasonalCardRecentSuppliers(raw);
  } catch {
    return [];
  }
}

export async function rememberSeasonalCardRecentSupplier(
  storeCode: string,
  supplier: SeasonalCardSupplierOption
) {
  if (!storeCode.trim()) {
    return [];
  }
  const current = await loadSeasonalCardRecentSuppliers(storeCode);
  const next = pushSeasonalCardRecentSupplier(current, supplier);
  try {
    await AppAsyncStorage.setObject(buildSeasonalCardRecentSuppliersKey(storeCode), next);
  } catch {
    // 写失败只影响「最近使用」，不打断提交流程。
  }
  return next;
}
