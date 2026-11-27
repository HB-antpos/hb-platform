import { POS_DESTINATION_ROUTES } from "./pos-destination-access";

/**
 * 根路由栈的切换动画。
 *
 * 中文注释：
 * 1. 每个页面都自带顶栏 + 左侧栏（PosScreenFrame），系统默认的整屏滑入 / 滑出会让左侧栏
 *    跟着滑走再滑回，看着像导航栏消失又出现，整屏一起动容易头晕（11-27 特价商品页反馈）。
 * 2. 显示左侧栏的页面（收银、特价商品、侧栏各功能页）之间切换一律不做动画：两页左侧栏位置相同，
 *    看起来侧栏固定、只换内容区。返回收银（dismissTo）弹出的也是这些页面，同样不动。
 * 3. 付款等子流程页面不在名单内，保留默认滑入，仍能看出「进入了一步流程」。
 */
const RAIL_SHELL_ROUTE_NAMES: ReadonlySet<string> = new Set([
  "sales",
  "special-products",
  ...Object.values(POS_DESTINATION_ROUTES).map((path) => path.replace(/^\//u, "")),
]);

export function posStackAnimationFor(routeName: string): "none" | "default" {
  return RAIL_SHELL_ROUTE_NAMES.has(routeName) ? "none" : "default";
}
