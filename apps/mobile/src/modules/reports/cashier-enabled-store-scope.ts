import { isStoreSelected } from "./store-selection";

interface StoreCodeOption {
  value: string;
}

/** 将后端已筛选的收银启用门店整理为稳定、可直接传给报表接口的代码范围。 */
export function getCashierEnabledStoreCodes(options: readonly StoreCodeOption[]): string[] {
  const seen = new Set<string>();
  const codes: string[] = [];

  options.forEach((option) => {
    const code = option.value.trim();
    const normalizedCode = code.toLocaleLowerCase();
    if (!code || seen.has(normalizedCode)) return;
    seen.add(normalizedCode);
    codes.push(code);
  });

  return codes;
}

/**
 * 单店筛选也必须落在启用白名单内；返回空数组代表无安全查询范围，调用方应禁用请求。
 */
export function getCashierScopedBranchCodes(
  enabledCodes: readonly string[],
  selectedStoreCode?: string | null,
): string[] {
  if (!selectedStoreCode) return [...enabledCodes];

  const normalizedSelection = selectedStoreCode.trim().toLocaleLowerCase();
  const matchedCode = enabledCodes.find(
    (code) => code.toLocaleLowerCase() === normalizedSelection,
  );
  return matchedCode ? [matchedCode] : [];
}

/**
 * 多店筛选的查询范围：未选（空数组）代表全部启用分店；已选分店只保留仍在白名单内的（按白名单顺序）。
 * 已选的全部失效时返回空数组，调用方应禁用请求，不得回退到全店。
 */
export function getCashierScopedBranchSelection(
  enabledCodes: readonly string[],
  selectedStoreCodes: readonly string[],
): string[] {
  if (selectedStoreCodes.length === 0) return [...enabledCodes];
  return enabledCodes.filter((code) => isStoreSelected(selectedStoreCodes, code));
}
