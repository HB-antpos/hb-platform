/**
 * 时间窗口（过去 1 周 ~ 未来 3 周）按货柜到仓库日期算：后端 windowBasis=warehouse。
 * 旧版后端不认识这个参数会直接忽略（回到按预计到店区间算，且不返回到仓库日期），页面据此平稳降级。
 */
export const CONTAINER_NEW_PRODUCTS_WINDOW_BASIS = "warehouse";

/**
 * 请求参数。页面与工作台角标共用同一个请求函数，所以 windowBasis 始终带上：
 * 这样角标上的新品数与页面里「全部」的数量用的是同一个窗口。
 * includeExisting 只在需要时才带，角标请求（只要新商品）与改动前保持一致。
 */
export function buildContainerNewProductsParams(storeCode: string, includeExisting: boolean) {
  return {
    storeCode,
    ...(includeExisting ? { includeExisting: true } : {}),
    windowBasis: CONTAINER_NEW_PRODUCTS_WINDOW_BASIS,
  };
}
