/**
 * 扫码 / 商品查询页手动修改分店「零售价」（原价）前的门禁。
 *
 * 自动定价开启时，零售价由规则（进货价、倍率、策略）计算：下一次扫码或评估会按规则重算并弹出「自动零售价有更新」，
 * 手改的值随后会被覆盖，等于白改。所以要求先关闭自动定价，再改价；不替用户悄悄关掉开关，避免误改定价策略。
 */
export type RetailPriceEditBlock = "autoPricingOn";

export function getRetailPriceEditBlock(
  storePrice: { isAutoPricing?: boolean | null } | null | undefined,
): RetailPriceEditBlock | null {
  return storePrice?.isAutoPricing === true ? "autoPricingOn" : null;
}
