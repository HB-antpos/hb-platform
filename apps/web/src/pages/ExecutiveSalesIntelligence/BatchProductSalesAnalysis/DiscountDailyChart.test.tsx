import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import i18next from 'i18next'
import { renderToStaticMarkup } from 'react-dom/server'
import { initReactI18next } from 'react-i18next'

import DiscountDailyChart from './DiscountDailyChart'
import messagesEn from './messages.en.json'
import messagesZh from './messages.zh.json'

await i18next.use(initReactI18next).init({ lng: 'zh', resources: { zh: { translation: messagesZh }, en: { translation: messagesEn } }, interpolation: { escapeValue: false } })

const metrics = (quantity: number) => ({
  quantity, regularQuantity: quantity, discountQuantity: 0, unknownQuantity: 0, returnQuantity: 0, salesAmount: quantity * 2.5,
  discountStatus: 'complete' as const, originalPriceMin: 2, originalPriceMax: 2.99, discountPriceMin: null, discountPriceMax: null,
})
const days = [12, 0, 230, 170, 362, 38].map((quantity, index) => ({ date: `2026-05-0${index + 4}`, metrics: metrics(quantity) }))
const markup = renderToStaticMarkup(<DiscountDailyChart data={days} ariaLabel="Greenhills · 每日销量" />)

assert(!markup.includes('<title'), 'SVG 不得带 <title>：浏览器会把它当原生提示弹出并盖住日期轴')
assert(markup.includes('aria-label="Greenhills · 每日销量"'), '读屏名称仍由 SVG 的 aria-label 提供')
assert(markup.includes('显示数值') && /role="switch"[^>]*aria-checked="true"/.test(markup), '「显示数值」开关默认打开')
assert(/class="batch-product-sales-chart-values"[^>]*pointer-events="none"/.test(markup), '数值标签层不得接收鼠标')
for (const value of ['12', '230', '170', '362', '38']) assert(markup.includes(`>${value}</text>`), `默认应在柱顶显示数值 ${value}`)
assert(!markup.includes('aria-live'), '未悬停时不渲染提示框')

// CI 把测试打包到缓存目录后在 apps/web 下运行，按 apps/web 相对路径读源码
const source = readFileSync(resolve('src/pages/ExecutiveSalesIntelligence/BatchProductSalesAnalysis/DiscountDailyChart.tsx'), 'utf8')
assert(/<g aria-live="polite" pointerEvents="none"/.test(source), '悬浮提示框必须设 pointerEvents="none"，否则压到光标时会触发 mouseleave 循环闪烁')
assert(!source.includes('plotRight - 184'), '不得再用把提示框夹回当前列的旧定位公式')
