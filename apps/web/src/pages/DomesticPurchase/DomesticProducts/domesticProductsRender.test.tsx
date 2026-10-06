import assert from 'node:assert/strict'
import { Form } from 'antd'
import i18next from 'i18next'
import type { TFunction } from 'i18next'
import { renderToStaticMarkup } from 'react-dom/server'
import { initReactI18next } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import zh from '../../../i18n/locales/zh.json'
import { ProductType, type DomesticProductItem, type DomesticProductSetItem } from '../../../types/domesticProduct'
import { ProductDetailBody, ProductDetailHeader } from './ProductDetailDrawer'
import { ProductFormFields } from './ProductFormModal'
import { buildProductColumns } from './productColumns'
import { LIST_TABLE_MIN_WIDTH, productToFormValues } from './domesticProductsLogic'
import domesticProductsMessagesZh from './domesticProductsMessages.zh.json'

// 服务端渲染冒烟测试：Node 里没有 DOM，用 renderToStaticMarkup 把列表列、抽屉主体、表单主体各渲染一遍，
// 能提前暴露「渲染即抛错」「列数不对」「条码画布回到列表」这类问题。
// 弹窗 / 抽屉本身依赖 Portal，Node 里渲染不出来，所以测试的是拆出来的内部组件。
// 商品缩略图（ProductListImage）用了没有 server snapshot 的 useSyncExternalStore，不能走服务端渲染，
// 因此示例商品都不带图片，走「首字方块」分支；有图分支复用的是仓库里已有的列表缩略图组件。

await i18next.use(initReactI18next).init({
  lng: 'zh',
  fallbackLng: 'zh',
  resources: {
    zh: {
      translation: {
        ...zh,
        domesticProducts: { ...zh.domesticProducts, ...domesticProductsMessagesZh.domesticProducts },
      },
    },
  },
  interpolation: { escapeValue: false },
})

// MeasuredTable 用 useLayoutEffect 记录渲染性能，服务端渲染时 React 会为它打印告警；与本测试无关，过滤掉避免刷屏。
const originalConsoleError = console.error
console.error = (...args: unknown[]) => {
  if (String(args[0]).includes('useLayoutEffect does nothing on the server')) {
    return
  }
  originalConsoleError(...args)
}

const t = i18next.t.bind(i18next) as TFunction
const noop = () => undefined

const setProduct: DomesticProductItem = {
  id: 'P2',
  rowNumber: 2,
  supplierCode: 'HB012',
  supplierName: '义乌市嘉悦日用品商行',
  name: '玻璃密封罐 3 件套',
  nameEn: 'Glass Storage Jar Set of 3',
  itemNumber: 'HB012-8002',
  barcode: '6901234567908',
  specs: '12×12×14 cm',
  productType: ProductType.SET,
  domesticPrice: 42,
  labelPrice: 14.99,
  importPrice: 9.8,
  packingQty: 24,
  volume: 0.0021,
  middlePackQty: 6,
  isActive: true,
  createdAt: '2026-08-21T10:00:00',
  updatedAt: '2026-10-04T14:20:11',
  updatedBy: 'Amy',
}

const normalProduct: DomesticProductItem = {
  ...setProduct,
  id: 'P5',
  name: '多功能收纳盒 大号',
  nameEn: undefined,
  itemNumber: 'HB017-8012',
  barcode: '6901234567939',
  supplierCode: 'HB017',
  supplierName: '义乌市恒美塑料制品厂',
  productType: ProductType.NORMAL,
  domesticPrice: 9.9,
  labelPrice: 3.99,
  isActive: false,
  updatedAt: '2025-12-30T09:05:00',
  updatedBy: undefined,
  specs: undefined,
  volume: undefined,
}

// —— 列表列：勾选 + 序号 + 9 列 = 11 列，没有条码画布 ——
function renderTable(canWrite: boolean, page = 1) {
  return renderToStaticMarkup(
    <MeasuredTable
      metricId="domestic-products-render-test"
      rowKey="id"
      tableLayout="fixed"
      scroll={{ x: LIST_TABLE_MIN_WIDTH }}
      pagination={false}
      rowSelection={{ columnWidth: 44 }}
      dataSource={[setProduct, normalProduct]}
      columns={buildProductColumns({
        t,
        sortField: 'updatedAt',
        sortOrder: 'descend',
        canWrite,
        currentYear: 2026,
        page,
        pageSize: 50,
        productWidth: 300,
        supplierWidth: 160,
        onEdit: noop,
        getRowMenu: () => ({ items: [{ key: 'detail', label: '查看详情' }] }),
      })}
    />,
  )
}

const tableMarkup = renderTable(true)
// antd 在定宽布局下还会渲染一行不可见的 measure-cell 表头（aria-hidden），只数真正的表头单元格。
assert.equal(
  (tableMarkup.match(/<th\b[^>]*class="ant-table-cell\b/g) ?? []).length,
  11,
  '列表应是 11 列（勾选 + 序号 + 商品/货号/条码/供应商/类型/价格/状态/更新/操作）',
)
// 序号跨页连续编号：第 1 页从 1 开始，第 3 页（每页 50）从 101 开始。
assert.deepEqual(
  [...tableMarkup.matchAll(/<span class="dp-serial">(\d+)<\/span>/g)].map((match) => match[1]),
  ['1', '2'],
  '第 1 页序号从 1 开始',
)
assert.deepEqual(
  [...renderTable(true, 3).matchAll(/<span class="dp-serial">(\d+)<\/span>/g)].map((match) => match[1]),
  ['101', '102'],
  '第 3 页每页 50 条，序号从 101 开始（跨页连续）',
)
assert.ok(!tableMarkup.includes('<canvas'), '列表里不再渲染条码画布，只留可复制文本')
assert.ok(tableMarkup.includes('玻璃密封罐 3 件套') && tableMarkup.includes('Glass Storage Jar Set of 3'), '商品列显示名称与英文名')
assert.ok(tableMarkup.includes('HB012-8002') && tableMarkup.includes('6901234567908'), '货号与条码以文本显示')
assert.ok(tableMarkup.includes('aria-label="复制 HB货号"') && tableMarkup.includes('aria-label="复制 条码"'), '货号 / 条码带可读屏识别的复制按钮')
assert.ok(tableMarkup.includes('义乌市嘉悦日用品商行') && tableMarkup.includes('>HB012<'), '供应商列显示名称 + 编码')
assert.ok(tableMarkup.includes('dp-type-set') && tableMarkup.includes('>套装<'), '套装商品显示紫色「套装」标签')
assert.ok(tableMarkup.includes('dp-type-normal') && tableMarkup.includes('>普通<'), '普通商品显示「普通」标签')
assert.ok(tableMarkup.includes('¥ 42.00') && tableMarkup.includes('$ 14.99'), '价格两行：国内 ¥ / 零售 $')
assert.ok(tableMarkup.includes('dp-status-on') && tableMarkup.includes('>启用<'), '启用状态显示绿点')
assert.ok(tableMarkup.includes('>停用<'), '停用状态显示「停用」而不是「禁用」')
assert.ok(tableMarkup.includes('>10-04<') && tableMarkup.includes('>Amy<'), '当年更新显示 MM-DD + 更新人')
assert.ok(tableMarkup.includes('>2025-12-30<') && tableMarkup.includes('>09:05<'), '跨年显示完整日期；没有更新人时第二行退回显示时间')
assert.ok(tableMarkup.includes('>编辑<'), '有权限时行内显示「编辑」')
assert.ok(tableMarkup.includes('aria-label="更多操作"'), '更多菜单按钮带 aria-label')
assert.equal((tableMarkup.match(/ant-table-column-has-sorters/g) ?? []).length, 4, '恰好四列可排序（商品 / 货号 / 供应商 / 更新）')
assert.ok(tableMarkup.includes('aria-sort="descending"'), '默认按更新降序，当前排序列带 aria-sort')
assert.ok(!renderTable(false).includes('>编辑<'), '无编辑权限时不显示「编辑」')

// —— 抽屉：头部 / 主体 ——
const headerMarkup = renderToStaticMarkup(<ProductDetailHeader product={setProduct} />)
assert.ok(headerMarkup.includes('玻璃密封罐 3 件套') && headerMarkup.includes('>套装<') && headerMarkup.includes('>启用<'), '抽屉头部：名称 + 类型标签 + 状态')
assert.ok(headerMarkup.includes('Glass Storage Jar Set of 3'), '抽屉头部显示英文名')

const setItems: DomesticProductSetItem[] = [
  { id: 's1', setProductNo: 'HB012-8003', productName: '玻璃罐 大', domesticPrice: 18, oemPrice: 6.99 },
  { id: 's2', setProductNo: 'HB012-8004', productName: '玻璃罐 中', domesticPrice: 14, oemPrice: 4.99 },
  { id: 's3', setProductNo: 'HB012-8005', productName: '玻璃罐 小', domesticPrice: 10, oemPrice: 3.01 },
]
const bodyMarkup = renderToStaticMarkup(
  <ProductDetailBody product={setProduct} setItems={setItems} setItemsLoading={false} setItemsFailed={false} />,
)
assert.ok(bodyMarkup.includes('¥ 42.00') && bodyMarkup.includes('$ 14.99') && bodyMarkup.includes('$ 9.80'), '抽屉指标：国内价 / 零售价 / 进口价')
assert.ok(bodyMarkup.includes('<canvas'), '条码画布只在抽屉里渲染')
assert.ok(bodyMarkup.includes('6901234567908'), '抽屉里条码带文本')
assert.ok(bodyMarkup.includes('规格 12×12×14 cm'), '包装标题旁显示规格')
assert.ok(bodyMarkup.includes('24') && bodyMarkup.includes('0.0021 m³') && bodyMarkup.includes('>6<'), '包装：装箱数 / 单件体积 / 中包数量')
assert.ok(bodyMarkup.includes('共 3 件'), '套装子项标题带数量')
assert.ok(bodyMarkup.includes('HB012-8003') && bodyMarkup.includes('玻璃罐 小'), '套装子项迷你表列出货号与名称')
assert.ok(bodyMarkup.includes('>合计<'), '套装子项有合计行')
assert.ok(bodyMarkup.includes('data-testid="domestic-product-drawer-set-items"'), '套装子项区域带稳定的 data-testid')
// 合计行：国内价 18+14+10 = 42.00，零售价 6.99+4.99+3.01 = 14.99（浮点求和已四舍五入到分）
const summaryStart = bodyMarkup.indexOf('>合计<')
assert.ok(bodyMarkup.slice(summaryStart).includes('¥ 42.00') && bodyMarkup.slice(summaryStart).includes('$ 14.99'), '合计行金额正确')

const normalBodyMarkup = renderToStaticMarkup(
  <ProductDetailBody product={normalProduct} setItems={[]} setItemsLoading={false} setItemsFailed={false} />,
)
assert.ok(!normalBodyMarkup.includes('套装子项'), '非套装商品不显示套装子项区域')
assert.ok(normalBodyMarkup.includes('--'), '缺失的规格 / 体积显示占位')

const failedBodyMarkup = renderToStaticMarkup(
  <ProductDetailBody product={setProduct} setItems={[]} setItemsLoading={false} setItemsFailed />,
)
assert.ok(failedBodyMarkup.includes('加载套装子项失败'), '子项加载失败时给出明确提示')
assert.ok(!failedBodyMarkup.includes('>合计<'), '加载失败时不显示合计')

// —— 表单主体：编辑 / 新建 ——
const editMarkup = renderToStaticMarkup(
  <Form initialValues={productToFormValues(normalProduct)}>
    <ProductFormFields isEdit suppliers={[]} />
  </Form>,
)
for (const label of ['基本信息', '价格', '包装', '商品图片链接', '预览', '国内价（¥）', '零售价（$）', '进口价（$）', '单件体积（m³）']) {
  assert.ok(editMarkup.includes(label), `编辑表单应包含 ${label}`)
}
assert.equal((editMarkup.match(/最小 1/g) ?? []).length, 2, '装箱数 / 中包数量各有一条「最小 1」提示')
assert.ok(editMarkup.includes('HB 货号与条码在创建后不可修改'), '编辑表单提示货号 / 条码创建后不可修改')
assert.ok(editMarkup.includes('role="switch"'), '编辑表单保留状态开关（否则保存会把停用商品重新启用）')
assert.ok(editMarkup.includes('/ 200') && editMarkup.includes('/ 500'), '名称类字段带字数计数')
for (const absent of ['id="supplierCode"', 'id="hbProductNo"', 'id="barcode"', 'id="packingSize"', 'id="material"', 'id="remarks"']) {
  assert.ok(!editMarkup.includes(absent), `编辑表单不应出现 ${absent}（后端不保存或创建后不可改）`)
}

const createMarkup = renderToStaticMarkup(
  <Form initialValues={{ productName: '', productType: ProductType.NORMAL, isActive: true }}>
    <ProductFormFields isEdit={false} suppliers={[{ code: 'HB012', name: '义乌市嘉悦日用品商行' }]} />
  </Form>,
)
for (const present of ['id="supplierCode"', 'id="hbProductNo"', 'id="barcode"']) {
  assert.ok(createMarkup.includes(present), `新建表单应出现 ${present}`)
}
assert.ok(createMarkup.includes('不填则后端自动生成'), '货号 / 条码留空由后端分配的提示')
assert.ok(!createMarkup.includes('HB 货号与条码在创建后不可修改'), '新建表单不显示「创建后不可修改」提示')
for (const absent of ['id="packingSize"', 'id="material"', 'id="remarks"']) {
  assert.ok(!createMarkup.includes(absent), `新建表单也不应出现 ${absent}`)
}

console.log('domesticProductsRender.test: ok')
