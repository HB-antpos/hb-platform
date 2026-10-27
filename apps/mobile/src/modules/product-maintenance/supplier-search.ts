import { sortSuppliersByName } from "../../shared/utils/name-sort";
import type { LocalSupplierOption } from "./types";

export function searchCreateSuppliers(
  suppliers: LocalSupplierOption[],
  query: string,
  language: string,
) {
  const term = query.trim().toLocaleLowerCase();
  // 先过滤，再用统一的名字排序工具排序（返回新数组，不修改入参）。
  return sortSuppliersByName(
    suppliers.filter(
      (supplier) =>
        !term ||
        `${supplier.supplierName}\n${supplier.supplierCode}`
          .toLocaleLowerCase()
          .includes(term),
    ),
    language,
  );
}
