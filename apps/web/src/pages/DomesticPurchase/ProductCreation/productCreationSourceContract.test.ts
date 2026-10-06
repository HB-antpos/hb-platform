import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 货号条码创建页的源码契约：这些约定一旦被改回去，用户会直接丢数据或看到旧缺陷，
// 但从行为测试里很难发现（需要渲染页面），所以在源码层面锁定业务意图。

const directory = 'src/pages/DomesticPurchase/ProductCreation'
const read = (name: string) => readFileSync(resolve(directory, name), 'utf8')
const countMatches = (text: string, pattern: RegExp) => text.match(pattern)?.length ?? 0

// 1) 页内工作台取代了 3 步弹窗：旧弹窗文件必须已删除，页面不能再引用。
assert.equal(existsSync(resolve(directory, 'BatchCreateModal.tsx')), false, 'BatchCreateModal 已被工作台取代，应删除')
assert.equal(existsSync(resolve(directory, 'BatchDetailModal.tsx')), false, 'BatchDetailModal 已被明细抽屉取代，应删除')
const indexSource = read('index.tsx')
assert.ok(indexSource.includes("from './BatchWorkspace'"), '列表页必须在页内切换到工作台（不新增路由）')
assert.doesNotMatch(indexSource, /BatchCreateModal|BatchDetailModal/)
assert.ok(read('BatchWorkspace.tsx').includes("from './PrefixCodeManageModal'"), '「管理前缀」继续使用共用的前缀管理弹窗')

// 2) 所有弹窗/抽屉不允许点遮罩关闭，避免误点丢掉正在填写的内容；静态确认框同理。
for (const name of ['BatchDetailDrawer.tsx', 'SetTemplateDrawer.tsx', 'PreviewCodesModal.tsx', 'SaveSetTemplateModal.tsx']) {
  const source = read(name)
  const overlays = countMatches(source, /<(?:Modal|Drawer)\b/g)
  assert.ok(overlays > 0, `${name} 应包含弹窗或抽屉`)
  assert.equal(countMatches(source, /maskClosable=\{false\}/g), overlays, `${name} 的每个弹窗/抽屉都必须 maskClosable={false}`)
}
for (const name of ['BatchWorkspace.tsx', 'BatchDetailDrawer.tsx']) {
  const source = read(name)
  assert.equal(
    countMatches(source, /maskClosable:\s*false/g),
    countMatches(source, /Modal\.confirm\(/g),
    `${name} 的每个 Modal.confirm 都必须 maskClosable: false`,
  )
}

// 3) 批次列表：请求只认最新一次（快速切换筛选/翻页时旧响应不得覆盖新结果），且不再用 as any 绕过类型。
assert.ok(indexSource.includes('createLatestRequestGuard('), '列表请求必须有「最新请求」守卫')
assert.ok(indexSource.includes('runLatestGuardedRequest('), '列表请求必须通过 runLatestGuardedRequest 发出')
assert.ok(indexSource.includes('.invalidate()'), '卸载时必须让在途请求作废')
assert.doesNotMatch(indexSource, /as any/, '列表页不得再用 as any 绕过 BatchListParams 类型')
assert.ok(/loadData = async \(overrides: Partial<BatchListQuery> = \{\}\)/.test(indexSource), '查询参数用对象合并，不能用会吞掉显式 undefined 的默认参数')

// 4) 明细抽屉：只提交改动过的价格；清空不得被当成 0。
const drawerSource = read('BatchDetailDrawer.tsx')
assert.ok(drawerSource.includes('computeChangedPrices('), '保存只能提交 computeChangedPrices 算出的改动项')
assert.ok(drawerSource.includes('updatePrivateLabelPrice(batchNumber, changedPrices)'), '保存请求必须使用改动项列表')
assert.doesNotMatch(drawerSource, /value\s*\|\|\s*0/, '清空价格不得被存成 0')
assert.equal(countMatches(drawerSource, /exportProductCreationBatchToExcel\(/g), 1, '明细抽屉只保留一个导出入口')

// 5) 工作台提交：必须带防重复提交，并且校验只来自 validateDraft（不得在页面里再加名称/零售价必填）。
const workspaceSource = read('BatchWorkspace.tsx')
assert.ok(workspaceSource.includes('submittingRef'), '提交必须有同步防重复锁')
assert.ok(workspaceSource.includes('validateDraft('), '提交前校验统一走 validateDraft')
assert.doesNotMatch(workspaceSource, /productName\?*\.trim\(\)[^\n]*(?:message\.error|issue)/, '工作台不得对商品名称加必填校验')

// 6) 预览货号必须标注「示意」，不能让用户误以为是最终货号。
const previewSource = read('PreviewCodesModal.tsx')
assert.ok(previewSource.includes('productCreation.illustrative') && previewSource.includes('productCreation.previewBanner'), '预览货号界面必须明确标注「示意」')

console.log('productCreationSourceContract.test: ok')
