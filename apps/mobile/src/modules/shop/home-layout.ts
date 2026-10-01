// 商品改为单列行布局：手机与 Zebra TC26（约 360dp）都是一行一个商品，
// 平板等宽屏（≥700dp）才并排两列，避免行内货号与价格被挤压。
const WIDE_SCREEN_MIN_WIDTH = 700;

export function resolveHomeProductColumns(windowWidth: number) {
  return windowWidth >= WIDE_SCREEN_MIN_WIDTH ? 2 : 1;
}
