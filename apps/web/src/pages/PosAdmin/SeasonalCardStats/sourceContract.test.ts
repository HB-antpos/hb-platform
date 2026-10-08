import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

// 页面接线契约：不能让测试 import index.tsx / CSS（CI 把 .css 打成空模块），所以读源码静态核对关键约定。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/PosAdmin/SeasonalCardStats'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
/** 去掉注释，避免注释里的说明文字被当成代码。 */
const stripComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')

const page = stripComments(read(`${directory}/index.tsx`))
const drawer = stripComments(read(`${directory}/StoreDetailDrawer.tsx`))
const logic = stripComments(read(`${directory}/logic.ts`))
const exporter = stripComments(read(`${directory}/export.ts`))
const service = stripComments(read('src/services/seasonalCardStatsService.ts'))
const css = read(`${directory}/seasonalCardStats.css`)

// 数据：只读，经服务层，路径固定；数组参数交给 request.ts 按重复键序列化
assert(service.includes("'/api/react/v1/seasonal-card-remaining/admin'"), '服务必须请求约定的接口路径')
assert(service.includes('/summary`') && service.includes('/stores/${encodeURIComponent(storeCode)}`'), '汇总与单店明细两个接口')
assert(!/request\.(post|put|patch|delete)\(/.test(service), '统计页只读：服务层不能有写操作')
assert(!/storeCodes\s*:\s*[^,\n]*\.join\(/.test(service), 'storeCodes 不能拼成逗号字符串（后端 List<string> 只认重复键）')
assert(!page.includes('/api/react/v1/'), '页面必须经服务层调用接口')
assert(
  page.includes('getSeasonalCardStatsSummary(') && page.includes('getSeasonalCardStatsStoreDetail('),
  '页面必须使用服务层读取汇总与单店明细',
)
assert(page.includes('getActiveLocalSuppliers('), '供应商下拉来自已有的启用供应商接口')
assert(!page.includes('getActiveStores('), '分店清单接口需要 Stores.View，只有统计权限的账号会 403；分店选项取自汇总结果')
assert(!/Modal\.confirm|popconfirm|Popconfirm/i.test(page + drawer), '只读页面不应有确认类写操作入口')

// 表格：必须用 MeasuredTable，metricId 是稳定字符串；合计行
assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(page + drawer), '页面不能直接从 antd 引入 Table')
assert(page.includes("import { MeasuredTable } from '../../../components/MeasuredTable'"), '表格必须使用 MeasuredTable')
assert(page.includes('metricId="pos-admin.seasonal-card-stats.table-1"'), 'metricId 必须是稳定字符串')
assert(page.includes('MeasuredTable.Summary'), '分店明细表必须有合计行')
assert(page.includes("'is-unfilled'"), '未填报行要有单独样式（浅黄底）')

// 页头与筛选
assert(/<PageContainer\s+compact/.test(page), '页头使用紧凑 PageContainer')
assert(page.includes('createDefaultFilters(currentYear)'), '默认年份今年、节日圣诞节')
assert(page.includes('formatSydneyIsoDate'), '「今年」取悉尼日历年')
assert(page.includes('[filters, listGuard, reloadTick]'), '筛选一变就重新请求')
assert(page.includes('filterRowsByStatus(') && page.includes('STATUS_FILTERS.map('), '填报状态在前端筛选并带计数')
assert(page.includes('showSearch') && page.includes('filterOption='), '供应商下拉可搜索（编码 + 名称）')
assert(page.includes('copyTextToClipboard(') && page.includes('buildUnfilledCopyText('), '未填报名单可复制')
assert(page.includes('summary.excludedStores'), '口径说明里注明被排除的分店')

// 时间按浏览器本地时区显示（formatLocalTime 不传时区），不在页面里自己拼
assert(page.includes('formatLocalTime(') && drawer.includes('formatLocalTime('), '时间统一经 formatLocalTime')
assert(!/\.toLocale(Date|Time)?String\(/.test(page + drawer + logic + exporter), '时间与金额格式不依赖浏览器区域设置')

// keepAlive：被缓存时抽屉必须收起，且只在激活期间读地址栏
assert(page.includes("from 'keepalive-for-react'") && page.includes('useKeepAliveContext()'), '必须读取 keepAlive 激活状态')
assert(page.includes('open={Boolean(selectedStore) && active}'), '页面被缓存时抽屉不能挂在 body 上盖住别的页面')
assert(page.includes('if (!active) return'), '页面未激活时不能按别的页面的地址栏改本页状态')
assert(page.includes("parseStoreParam(searchParams.get('store'))"), '抽屉状态读自地址栏 ?store=，并经校验')
assert(page.includes('withStoreParam(') && page.includes('{ replace: true }'), '抽屉状态写回地址栏（replace，不堆历史记录）')

// 请求：新请求取消旧请求，卸载时取消
assert(page.includes('createAbortableRequestGuard') && page.includes('isAbortError('), '请求必须可取消并忽略 Abort')
assert(page.includes('listGuard.abort()') && page.includes('detailGuard.abort()'), '卸载时取消在途请求')

// 抽屉：区块
for (const key of [
  'seasonalCardStats.drawer.matrixTitle',
  'seasonalCardStats.drawer.historyTitle',
  'seasonalCardStats.drawer.compareTitle',
]) {
  assert(drawer.includes(`'${key}'`), `抽屉必须包含区块 ${key}`)
}
assert(drawer.includes('previousYearTotalQuantity'), '抽屉显示与去年同节日对比')
assert(drawer.includes('closable={{ placement:'), '抽屉关闭按钮在右侧')
assert(drawer.includes('destroyOnHidden'), '抽屉关闭后销毁内容，避免残留上一家分店')

// 导出：exceljs 只能 import type，用到时动态加载
assert(/import type ExcelJS from 'exceljs'/.test(exporter), 'export.ts 只能 import type exceljs')
assert(!/^import\s+(?!type)[^\n]*from 'exceljs'/m.test(exporter), 'export.ts 不能静态引入 exceljs（会进页面包）')
assert(exporter.includes("await import('exceljs')"), 'exceljs 用到时才动态加载')
assert(!/from 'exceljs'/.test(page + drawer + logic), '页面、抽屉、logic 不能引入 exceljs')
assert(exporter.includes('fillStoreSheet(') && exporter.includes('fillUnfilledSheet('), '导出两个工作表')

// 样式：普通 CSS + 前缀类名（CI 测试把 .css 打成空模块，不能用 CSS Modules）
assert(page.includes("import './seasonalCardStats.css'"), '页面引入普通 CSS')
assert(!readdirSync(resolve(directory)).some((name) => name.endsWith('.module.css')), '不能使用 CSS Modules')
assert(
  !/^\.(?!seasonal-card-stats-|ant-|is-)[a-z]/m.test(css.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\.ant-[\w-]+/g, '')),
  '自定义类名都必须带 seasonal-card-stats- 前缀',
)
assert(css.includes('font-variant-numeric: tabular-nums'), '数量与金额必须用等宽数字')
assert(css.includes('@media (max-width: 767px)'), '必须有较窄宽度处理')
assert(page.includes('scroll={{ x: TABLE_MIN_WIDTH }}'), '窄屏下表格横向滚动')
assert(existsSync(resolve(`${directory}/messages.zh.json`)) && existsSync(resolve(`${directory}/messages.en.json`)), '中英文页面消息文件都要有')

// 金额规范
assert(logic.includes(String.fromCharCode(0x2212)), '负数使用 U+2212 减号（不是连字符）')

console.log('SeasonalCardStats sourceContract.test: ok')
