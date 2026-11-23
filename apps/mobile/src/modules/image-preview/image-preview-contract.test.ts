import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// 源码契约：商品查询、商品进销查询、季节商品查询三处的商品图都必须走可点放大的 PreviewableImage，
// 不能再退回不可点的裸 <Image>。组件本身的手势几何由 zoom-logic.test.ts 覆盖。
const directory = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(resolve(directory, relative), "utf8");

const hero = read("../../components/product-maintenance/ProductHeroCard.tsx");
const insights = read("../../components/product-insights/ProductInsightsView.tsx");
const seasonalBox = read("../../components/seasonal-product-insights/ProductImageBox.tsx");
const seasonalView = read("../../components/seasonal-product-insights/SeasonalProductInsightsView.tsx");
const seasonalList = read("../../components/seasonal-product-insights/SeasonalCandidateList.tsx");
const previewable = read("../../components/ui/PreviewableImage.tsx");

// 商品查询：完整版 60px 与「编码」页签下的精简版 40px 两处都可点。
assert.equal((hero.match(/<PreviewableImage/g) ?? []).length, 2, "ProductHeroCard 的完整版与精简版都要用 PreviewableImage");
assert.doesNotMatch(hero, /<Image\b/, "ProductHeroCard 不应再直接渲染 <Image>");

// 商品进销查询。
assert.match(insights, /<PreviewableImage/, "商品进销查询的商品图要用 PreviewableImage");
assert.doesNotMatch(insights, /<Image\b/, "ProductInsightsView 不应再直接渲染 <Image>");

// 季节商品查询：详情里的商品图与多商品卡片里的缩略图都经 ProductImageBox。
assert.match(seasonalBox, /<PreviewableImage/, "ProductImageBox 必须委托给 PreviewableImage");
assert.match(seasonalView, /<ProductImageBox/);
assert.match(seasonalList, /<ProductImageBox/);

// 首次引导气泡只挂在主商品图上（三页各一处），列表缩略图不挂。
assert.match(hero, /introHint/, "商品查询完整版商品图要带首次气泡");
assert.match(insights, /introHint/, "商品进销查询商品头要带首次气泡");
assert.match(seasonalView, /introHint/, "季节详情主商品图要带首次气泡");
assert.doesNotMatch(seasonalList, /introHint/, "候选列表的缩略图不能带首次气泡");
assert.match(previewable, /claimImageTooltip/, "气泡必须走「每台设备一次」的领取逻辑");

// 没图或加载失败时不可点、不显示放大角标；有图时点击打开预览层。
assert.match(previewable, /if \(!uri \|\| failed\)/, "没有图片 / 加载失败要走不可点的占位分支");
assert.match(previewable, /accessibilityRole="imagebutton"/);
assert.match(previewable, /<ImagePreviewModal/);
