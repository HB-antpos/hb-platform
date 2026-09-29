/**
 * 下架前是否必须先填写供货说明。
 *
 * 必填只在 Web 端强制：后端对不带说明的下架保持兼容（旧客户端、App 等），分店端按“后续计划待确认”展示。
 * - nextIsActive 不是 false（上架，或批量修改里留空不改）时不需要；
 * - 原本已明确是下架（previousIsActive === false）且保持下架时不强制，已有说明原样保留；
 * - 批量场景选中商品的原状态混杂，previousIsActive 不传，一律按需要填写处理。
 */
export function requiresDelistSupplyNotice(
  nextIsActive: boolean | null | undefined,
  previousIsActive?: boolean | null,
): boolean {
  return nextIsActive === false && previousIsActive !== false
}
