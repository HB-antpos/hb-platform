import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

// 国内商品页重设计里「必须保持」的几条硬约束，用源码字符串断言守住，防止后续改动悄悄退回旧写法。
// 分页请求只认最新一次的约束由 ChinaSuppliers/paginationRace.test.ts 里的国内商品断言负责。

const pageDirectory = 'src/pages/DomesticPurchase/DomesticProducts'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function read(name: string) {
  return readFileSync(resolve(`${pageDirectory}/${name}`), 'utf8')
}

const sourceNames = readdirSync(resolve(pageDirectory)).filter(
  (name) => /\.tsx$/.test(name) && !/\.test\.tsx$/.test(name),
)

// 1) 所有弹窗 / 抽屉都关闭「点遮罩关闭」，避免误点丢数据：
//    每个文件里 <Modal / <Drawer 的出现次数不能多于 maskClosable={false} 的出现次数。
for (const name of sourceNames) {
  const source = read(name)
  const overlayCount = (source.match(/<(?:Modal|Drawer)\b/g) ?? []).length
  const lockedCount = (source.match(/maskClosable=\{false\}/g) ?? []).length
  assert(lockedCount >= overlayCount, `${name} 里有弹窗 / 抽屉没有设置 maskClosable={false}`)
}

const indexSource = read('index.tsx')

// 2) 列表不再写死高度、不再用虚拟滚动，10 列靠弹性商品列适配宽度，不横向滚动。
assert(!/\bvirtual\b/.test(indexSource), '国内商品列表不应再使用虚拟滚动')
assert(!/scroll=\{\{[^}]*\by:/.test(indexSource), '国内商品列表不应再写死滚动高度')
assert(indexSource.includes('scroll={{ x: LIST_TABLE_MIN_WIDTH }}'), '列表 scroll.x 必须取自 LIST_TABLE_MIN_WIDTH（≤ 1280 视口可用宽度）')
assert(indexSource.includes('<MeasuredTable'), '列表必须使用 MeasuredTable')

// 3) 条码画布只在详情抽屉里，列表只留可复制文本。
assert(!indexSource.includes('BarcodePreview'), '列表里不应再渲染条码画布')
assert(read('ProductDetailDrawer.tsx').includes('BarcodePreview'), '详情抽屉里要展示条码画布')

// 4) 整行点击开详情抽屉，且行内自带交互的元素不触发。
assert(indexSource.includes('shouldIgnoreRowClick('), '整行点击必须经 shouldIgnoreRowClick 排除行内交互元素')
assert(indexSource.includes('handleOpenDetail(record)'), '整行点击要打开详情抽屉')
assert(read('ProductDetailDrawer.tsx').includes('width={700}'), '详情抽屉宽度为 700')

// 5) 批量删除与导出选中进 SelectionActionBar，不混在筛选行里。
assert(indexSource.includes('<SelectionActionBar'), '批量操作必须放进 SelectionActionBar')
assert(indexSource.includes('<ActiveFilterBar'), '必须用 ActiveFilterBar 展示已生效条件')
assert(!indexSource.includes('onClick={() => void loadData({ page: 1 })}'), '不应再有手动「查询」按钮，筛选即时生效')

// 6) 套装子项：商品名称后端不保存，名称列只读，也不再提供对它的批量粘贴入口。
const setItemsSource = read('SetItemsModal.tsx')
assert(!/handlePaste\([^)]*'productName'/.test(setItemsSource), '套装子项不应再提供「商品名称」列的批量粘贴')
assert(!/createPasteTitle\([^)]*'productName'/.test(setItemsSource), '套装子项不应再把「商品名称」列头做成粘贴入口')
assert(setItemsSource.includes("Exclude<PasteableSetItemField, 'productName'>"), '可粘贴字段类型必须排除 productName')

// 7) 编辑表单只提交后端保存的字段：界面里不再有包装尺寸 / 材质 / 备注输入，条码仅新建时可填。
const formSource = read('ProductFormModal.tsx')
for (const removed of ['packingSize', 'material', 'remarks']) {
  assert(!formSource.includes(`name="${removed}"`), `编辑表单不应再有 ${removed} 字段（后端不保存）`)
}
assert(formSource.includes('min={1}'), '装箱数 / 中包数量最小值必须是 1')

// 8) 不对列表 total 做任何前端「修正」：total 只来自接口返回。
assert(indexSource.includes('setTotal(result.total)'), '列表 total 必须原样取自接口')
assert(!/setTotal\([^)]*(?:length|Math\.)/.test(indexSource), '不得用当前页条数等去篡改列表 total')

console.log('domesticProductsPageContract.test: ok')
