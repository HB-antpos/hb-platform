import assert from 'node:assert/strict'

import { AUTO_PRICING_MIN_PURCHASE_PRICE, isAutoPricingBelowMinimum } from './autoPricingEligibility'

assert.equal(AUTO_PRICING_MIN_PURCHASE_PRICE, 0.1, '下限与后端 AutoPricingRules.MinPurchasePrice 一致')
assert.equal(isAutoPricingBelowMinimum({ autoPricing: true, purchasePrice: 0.023 }), true, '0.023 低于下限要提示')
assert.equal(isAutoPricingBelowMinimum({ autoPricing: true, purchasePrice: 0.1 }), false, '正好 0.10 可以自动定价')
assert.equal(isAutoPricingBelowMinimum({ autoPricing: true, purchasePrice: 4 }), false, '正常进货价不提示')
assert.equal(isAutoPricingBelowMinimum({ autoPricing: true, purchasePrice: 0 }), false, '0 走「无进货价」提示，不算低价')
assert.equal(isAutoPricingBelowMinimum({ autoPricing: true, purchasePrice: undefined }), false, '没填进货价不提示')
assert.equal(isAutoPricingBelowMinimum({ autoPricing: false, purchasePrice: 0.05 }), false, '手动定价不受下限约束')
assert.equal(isAutoPricingBelowMinimum({ autoPricing: undefined, purchasePrice: 0.05 }), false, '未设置定价方式不提示')

console.log('autoPricingEligibility tests passed')
