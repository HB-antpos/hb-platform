/**
 * 收银员手工输入单价的统一上限（含上限本身），与 WPF `PosPriceInputRules.MaxUnitPrice = 9999`、
 * 平板收银端同名常量保持一致。
 * 起因：2026-10-31 / 11-02 1014 店把开价商品单价误输成 1 亿、10 亿，操作日志上传时超出 POSM decimal(10,2) 而整批 500。
 * 开价商品（OPENITEM）与购物车改价共用；只拦新输入，已在购物车、挂单里的行不受影响。
 */
export const MAX_MANUAL_UNIT_PRICE_CENTS = 999_900;

/** 文案里展示的上限金额（整数澳元，例如「9999」），与常量同源，避免各处写死。 */
export const MAX_MANUAL_UNIT_PRICE_DISPLAY = String(
  MAX_MANUAL_UNIT_PRICE_CENTS / 100,
);

export function isManualUnitPriceWithinMaximum(unitPriceCents: number): boolean {
  return unitPriceCents <= MAX_MANUAL_UNIT_PRICE_CENTS;
}
