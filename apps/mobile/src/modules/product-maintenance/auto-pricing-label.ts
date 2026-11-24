/**
 * 自动调价确认弹窗里「保存并打印」可选的标签类型。
 * 顺序与商品查询页底部打印条的 Reg / Small / Disc / Large 一致。
 */
export const AUTO_PRICING_LABEL_KINDS = ["product", "small", "discount", "bigDiscount"] as const;

export type AutoPricingLabelKind = (typeof AUTO_PRICING_LABEL_KINDS)[number];

export const DEFAULT_AUTO_PRICING_LABEL_KIND: AutoPricingLabelKind = "product";

/** 折扣类标签（折扣 / 大标签）：只有新价带折扣时才有内容可打。 */
export function isDiscountLabelKind(kind: AutoPricingLabelKind): kind is "discount" | "bigDiscount" {
  return kind === "discount" || kind === "bigDiscount";
}

/**
 * 某种标签此刻能不能选：普通 / 小标签始终可选；折扣 / 大标签要求新价折扣率 > 0。
 * 与底部打印条（canPrintDiscount / canPrintBigDiscount）同一规则。
 */
export function isAutoPricingLabelKindAvailable(
  kind: AutoPricingLabelKind,
  hasDiscount: boolean,
): boolean {
  return isDiscountLabelKind(kind) ? hasDiscount : true;
}

/**
 * 打开弹窗时的默认选项：沿用本页上次选的标签；没有记录、或上次选的折扣类标签这次没有折扣可打，
 * 都回到普通标签，避免默认选中一个点不了的选项。
 */
export function resolveAutoPricingLabelSelection(
  preferred: AutoPricingLabelKind | null | undefined,
  hasDiscount: boolean,
): AutoPricingLabelKind {
  if (preferred && isAutoPricingLabelKindAvailable(preferred, hasDiscount)) {
    return preferred;
  }
  return DEFAULT_AUTO_PRICING_LABEL_KIND;
}

export interface AutoPricingLabelPlan {
  /** 打印忙碌态的动作名，与 PrintAction 里同名取值一致。 */
  action: AutoPricingLabelKind;
  /** 传给打印层的 printType；null 表示普通尺寸。 */
  printType: "small" | null;
  /** 折扣类标签才有值，交给 sendProductLabel 的 labelKind；普通 / 小标签为 undefined。 */
  discountKind: "discount" | "bigDiscount" | undefined;
}

/**
 * 把所选标签换算成打印参数，口径与底部打印条的 handlePrint 一致：
 * - 普通标签、折扣标签跟随打印设置里的「小标签」开关；
 * - 小标签固定 60mm，不看开关；
 * - 大折扣标签打印层不读 printType，固定 null。
 */
export function resolveAutoPricingLabelPlan(
  kind: AutoPricingLabelKind,
  smallLabelSetting: boolean,
): AutoPricingLabelPlan {
  const followSetting: "small" | null = smallLabelSetting ? "small" : null;
  switch (kind) {
    case "small":
      return { action: "small", printType: "small", discountKind: undefined };
    case "discount":
      return { action: "discount", printType: followSetting, discountKind: "discount" };
    case "bigDiscount":
      return { action: "bigDiscount", printType: null, discountKind: "bigDiscount" };
    case "product":
    default:
      return { action: "product", printType: followSetting, discountKind: undefined };
  }
}
