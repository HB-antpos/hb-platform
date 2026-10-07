import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

// 页面接线契约：不能让测试 import index.tsx / CSS（CI 把 .css 打成空模块），所以读源码静态核对关键约定。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/PosAdmin/DailyCloses'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
/** 去掉注释，避免注释里的说明文字被当成代码。 */
const stripComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')

const page = stripComments(read(`${directory}/index.tsx`))
const drawer = stripComments(read(`${directory}/DailyCloseDrawer.tsx`))
const logic = stripComments(read(`${directory}/logic.ts`))
const service = stripComments(read('src/services/dailyCloseService.ts'))
const css = read(`${directory}/dailyCloses.css`)

// 数据：只读，经服务层，路径固定
assert(service.includes("'/api/react/v1/pos-daily-closes'"), '服务必须请求约定的接口路径')
assert(!/request\.(post|put|patch|delete)\(/.test(service), '日结记录只读：服务层不能有写操作')
assert(!page.includes('/api/react/v1/pos-daily-closes'), '页面必须经 dailyCloseService 调用接口')
assert(page.includes('getDailyCloses(') && page.includes('getDailyCloseDetail('), '页面必须使用服务层读取列表与详情')
assert(!/Modal\.confirm|popconfirm|Popconfirm/i.test(page + drawer), '只读页面不应有确认类写操作入口')

// 表格：必须用 MeasuredTable，metricId 是稳定字符串
assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(page), '页面不能直接从 antd 引入 Table')
assert(page.includes("import { MeasuredTable } from '../../../components/MeasuredTable'"), '表格必须使用 MeasuredTable')
assert(page.includes('metricId="pos-admin.daily-closes.table-1"'), 'metricId 必须是稳定字符串 pos-admin.daily-closes.table-1')

// 页头与形态
assert(/<PageContainer\s+compact/.test(page), '页头使用紧凑 PageContainer')
assert(page.includes('presets={datePresets}') && page.includes('disabledDate='), '营业日 RangePicker 必须有快捷项与 93 天限制')
assert(page.includes('isBusinessDateSelectable('), '日期禁用规则来自 logic.ts（与后端同口径）')
assert(page.includes('validateBusinessDateRange('), '查询前必须校验区间')
assert(page.includes('STATUS_TABS.map('), '状态页签由 STATUS_TABS 生成')
assert(page.includes('pageSizeOptions={[...PAGE_SIZE_OPTIONS]}'), '分页档位来自 logic.ts')

// 分店范围：用「全部关联分店」口径，不是只含主分店的可管理口径
assert(page.includes('buildStoreOptionsFromUserStores(currentUser?.stores)'), '分店选项必须取账号全部关联分店')
assert(!page.includes('manageableOnly'), '不能用 manageableOnly（那是只含主分店的口径）')

// 时间必须按门店时区、日期按悉尼，不能用浏览器本地时间推导
assert(page.includes('formatSydneyIsoDate'), '默认营业日区间取悉尼日历日')
assert(page.includes('formatInStoreTime('), '保存时间按门店本地时区显示')
assert(!/new Date\(\s*\)/.test(page) && !/Date\.now\(/.test(page), '页面不应用浏览器本地时间推导业务日期')
assert(!/\.toLocale(Date|Time)?String\(/.test(page + drawer + logic), '时间与金额格式不依赖浏览器区域设置')

// keepAlive：被缓存时抽屉必须收起，且只在激活期间读地址栏
assert(page.includes("from 'keepalive-for-react'") && page.includes('useKeepAliveContext()'), '必须读取 keepAlive 激活状态')
assert(page.includes('open={Boolean(selectedId) && active}'), '页面被缓存时抽屉不能挂在 body 上盖住别的页面')
assert(page.includes('if (!active) return'), '页面未激活时不能按别的页面的地址栏改本页状态')
assert(page.includes('parseDetailIdParam(searchParams.get('), '抽屉状态读自地址栏 ?id=，并经 GUID 校验')
assert(page.includes('withDetailIdParam(') && page.includes('{ replace: true }'), '抽屉状态写回地址栏（replace，不堆历史记录）')

// 请求：新请求取消旧请求，卸载时取消
assert(page.includes('createAbortableRequestGuard') && page.includes('isAbortError('), '请求必须可取消并忽略 Abort')
assert(page.includes('listGuard.abort()') && page.includes('detailGuard.abort()'), '卸载时取消在途请求')

// 抽屉：区块与状态
for (const key of ['dailyCloses.recon.title', 'dailyCloses.tenders.title', 'dailyCloses.cashCounts.title', 'dailyCloses.info.title']) {
  assert(drawer.includes(`'${key}'`), `抽屉必须包含区块 ${key}`)
}
assert((drawer.match(/<PlaceholderBox\s*\/>/g) ?? []).length === 2, '支付方式与盘点明细在无数据时都用虚线占位框，不整块隐藏')
assert(drawer.includes('closable={{ placement:'), '抽屉关闭按钮在右侧')
assert(drawer.includes('destroyOnHidden'), '抽屉关闭后销毁内容，避免残留上一条记录')

// 样式：普通 CSS + 前缀类名（CI 测试把 .css 打成空模块，不能用 CSS Modules）
assert(page.includes("import './dailyCloses.css'"), '页面引入普通 CSS')
assert(!readdirSync(resolve(directory)).some((name) => name.endsWith('.module.css')), '不能使用 CSS Modules')
assert(!/^\.(?!daily-closes-|ant-|is-)[a-z]/m.test(css.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\.ant-[\w-]+/g, '')), '自定义类名都必须带 daily-closes- 前缀')
assert(css.includes('font-variant-numeric: tabular-nums'), '金额必须用等宽数字')
assert(css.includes('@media (max-width: 767px)'), '必须有较窄宽度处理')
assert(existsSync(resolve(`${directory}/messages.zh.json`)) && existsSync(resolve(`${directory}/messages.en.json`)), '中英文页面消息文件都要有')

// 金额、差额规范
assert(logic.includes(String.fromCharCode(0x2212)), '负数使用 U+2212 减号（不是连字符）')
assert(logic.includes('▼') && logic.includes('▲') && logic.includes('✓'), '差额状态必须有图标（不只靠颜色）')

console.log('DailyCloses sourceContract.test: ok')
