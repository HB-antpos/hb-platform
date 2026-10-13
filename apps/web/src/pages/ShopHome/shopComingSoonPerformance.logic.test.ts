import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

import { getComingSoonDateTone, resolveWarehouseDate } from './components/comingSoonWarehouseDate'

const source = readFileSync('src/pages/ShopHome/components/ComingSoonSection.tsx', 'utf8')
const dateSource = readFileSync('src/pages/ShopHome/components/comingSoonWarehouseDate.ts', 'utf8')
const css = readFileSync('src/pages/ShopHome/components/ComingSoonSection.module.css', 'utf8')
const locales = ['en', 'zh'].map((language) =>
  JSON.parse(readFileSync(`src/i18n/locales/${language}.json`, 'utf8')),
)

// 新工作区以选柜按需加载和有界分页替代横向卡片内虚拟列表，保持接口及过滤契约。
assert.ok(source.includes('getComingSoonContainerSummaries'))
assert.doesNotMatch(source, /getComingSoonContainers\(\)/, '首屏不能请求全部货柜商品')
assert.match(
  source,
  /if \(selectedContainer\) void loadContainerProducts\(selectedContainer.hguid\)/,
  '过滤隐藏原货柜后，应加载实际显示的 fallback 货柜',
)
assert.match(source, /loadStartedRef.current.has\(containerGuid\)/, '同一货柜不能重复并发加载')
assert.match(source, /loadStartedRef.current.delete\(containerGuid\)/, '失败货柜必须能重试')
assert.match(source, /summaryGenerationRef.current !== generation/, '废弃摘要结果不得回写商品')
assert.match(source, /requestTokensRef.current.get\(containerGuid\) !== token/, '失效请求不得覆盖新结果')
assert.match(source, /const PRODUCT_PAGE_SIZE = 24/, '单页商品DOM应有明确上限')
assert.match(
  source,
  /selectedProducts.slice\([\s\S]*?\(productPage - 1\) \* pageSize,[\s\S]*?productPage \* pageSize/,
)
assert.match(source, /pagedProducts.map\(/, '只渲染当前页，不能重新全量渲染商品')
assert.doesNotMatch(source, /selectedProducts.map\(/)
assert.match(source, /Math.ceil\(selectedProducts.length \/ pageSize\)/)
assert.match(source, /disabled=\{productPage >= pageCount\}/, '最后一页必须正确禁用下一页')
assert.match(source, /changeProductPage\(productPage \+ 1\)/, '下一页必须可达')
assert.match(source, /changeProductPage\(productPage - 1\)/, '上一页必须可达')
assert.match(source, /scrollIntoView\(/, '翻页后应回到商品区，不能停在长页底部')
assert.match(
  source,
  /setProductPage\(1\)[\s\S]*?\[selectedContainer\?\.hguid, selectedFilterMode\]/,
  '换柜或单柜过滤后应回到第一页',
)
assert.match(source, /const \[filterMode, setFilterMode\] = useState<FilterMode>\('all'\)/)
assert.match(
  source,
  /containerFilterModes\[selectedContainer.hguid\] \?\? filterMode/,
  '单柜覆盖应优先于顶部全局筛选',
)
assert.match(source, /setContainerFilterModes\(\{\}\)/, '全局切换必须清空单柜过滤覆盖')
assert.match(
  source,
  /state.status !== 'loaded' \|\|[\s\S]*?state.products.some\(\(product\) => matchesFilter\(product, filterMode\)\)/,
  '只隐藏已加载无匹配货柜，未知货柜不能被当成空数据',
)
assert.doesNotMatch(source, /container.合计数量/, '单位数量不能冒充商品行数')
assert.match(source, /state.status === 'loaded'\s*\?/, '未加载货柜需显示待加载状态')
// 日期色调与到仓库日期规则放在独立纯函数模块，组件只引用它
assert.match(source, /from '\.\/comingSoonWarehouseDate'/)
assert.match(source, /getComingSoonDateTone/)
for (const tone of ['arrived', 'soon', 'future', 'unknown']) {
  assert.ok(dateSource.includes(`return '${tone}'`))
  assert.ok(css.includes(`.date_${tone}`))
}
// 到仓库日期口径：实际到货日期优先，没有时用预计到岸日期（与后端、移动端 HB新品页一致）
const ARRIVED = '2026-10-10T00:00:00'
const warehouseNow = new Date(2026, 9, 14, 10, 30)
assert.equal(resolveWarehouseDate({ 实际到货日期: ARRIVED, 预计到岸日期: '2026-12-01T00:00:00' })?.getDate(), 10, '有实际日期时以实际日期为准')
assert.equal(resolveWarehouseDate({ 预计到岸日期: '2026-10-20T00:00:00' })?.getDate(), 20, '没有实际日期时用预计到岸日期兜底')
assert.equal(resolveWarehouseDate({}), null, '两个日期都没有时没有到仓库日期')
assert.equal(getComingSoonDateTone({ 实际到货日期: ARRIVED, 预计到岸日期: '2026-12-01T00:00:00' }, warehouseNow), 'arrived', '已有实际到货日期 = 已到仓')
assert.equal(getComingSoonDateTone({ 预计到岸日期: '2026-10-14T00:00:00' }, warehouseNow), 'soon', '预计今天到仓库 = 即将')
assert.equal(getComingSoonDateTone({ 预计到岸日期: '2026-10-21T00:00:00' }, warehouseNow), 'soon', '7 天内到仓库 = 即将')
assert.equal(getComingSoonDateTone({ 预计到岸日期: '2026-10-22T00:00:00' }, warehouseNow), 'future', '超过 7 天 = 较远')
assert.equal(getComingSoonDateTone({ 预计到岸日期: '2026-10-11T00:00:00' }, warehouseNow), 'soon', '窗口含过去 7 天内预计到仓但未登记实际到货的货柜，不再落到 unknown')
assert.equal(getComingSoonDateTone({}, warehouseNow), 'unknown', '没有任何日期 = 未知')
assert.match(source, /loading="lazy"/)
assert.match(source, /preview=\{false\}/)
assert.match(source, /BarcodePreview[\s\S]*value=\{product.barcode\}/)
assert.match(source, /textNoWrap/)
assert.match(source, /copyable className=\{styles.itemNumber\}/)
assert.match(css, /\.itemNumber\s*\{[^}]*white-space: nowrap/)
assert.match(source, /formatComingSoonRetailPrice\(product.retailPrice\)/)
assert.match(source, /typeof price !== 'number'[\s\S]*return ''/, '缺失RRP仍显示为空')
assert.match(css, /\.productGrid\s*\{[^}]*repeat\(4, minmax\(0, 1fr\)\)/)
assert.match(
  css,
  /@media \(max-width: 480px\)[\s\S]*grid-template-columns: minmax\(0, 1fr\)/,
  '手机需保留可读商品宽度',
)
assert.match(css, /\.indexItem:focus-visible/, '货柜索引需可见键盘焦点')
// 日期文案统一为「到仓库」口径（键名不变，只改值），与移动端 HB新品页措辞一致
assert.match(source, /'shop\.comingSoonWorkspace\.arrivalDate'/)
assert.match(source, /'shop\.comingSoonWorkspace\.eta'/)
const [en, zh] = locales
assert.equal(zh.shop.comingSoonWorkspace.arrivalDate, '到仓库')
assert.equal(zh.shop.comingSoonWorkspace.eta, '预计到仓库')
assert.equal(zh.shop.comingSoonWorkspace.arrived, '已到仓')
assert.equal(zh.shop.comingSoonWorkspace.incoming, '在途')
assert.ok(zh.shop.comingSoonBannerSubtitle.includes('到仓库日期'))
assert.equal(en.shop.comingSoonWorkspace.arrivalDate, 'In warehouse')
assert.equal(en.shop.comingSoonWorkspace.eta, 'Est. in warehouse')
assert.equal(en.shop.comingSoonWorkspace.arrived, 'At warehouse')
assert.equal(en.shop.comingSoonWorkspace.incoming, 'In transit')
assert.ok(en.shop.comingSoonBannerSubtitle.includes('warehouse arrival'))
for (const locale of locales) {
  for (const key of ['comingSoonFilterAll', 'comingSoonFilterReorder', 'comingSoonFilterNew']) {
    assert.equal(typeof locale.shop[key], 'string')
    assert.ok(source.includes(`'shop.${key}'`))
  }
  for (const key of ['globalFilter', 'containerFilter', 'showBarcodes', 'next', 'previous', 'pageInfo']) {
    assert.equal(typeof locale.shop.comingSoonWorkspace[key], 'string')
  }
}
console.log('shopComingSoonPerformance.logic.test: ok')
