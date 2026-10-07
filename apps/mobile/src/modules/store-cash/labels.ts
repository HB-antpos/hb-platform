import { CASH_EXPENSE_CATEGORIES } from "./constants";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

/** 类别展示名：「现金工资」「现金购物」「T2」「其他」；未知类别码原样显示。 */
export function categoryLabel(t: TranslateFn, category: string): string {
  return (CASH_EXPENSE_CATEGORIES as readonly string[]).includes(category) ? t(`categories.${category}`) : category;
}
