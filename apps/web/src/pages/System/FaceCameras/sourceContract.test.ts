import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 页面接线与安全契约：不能让测试 import index.tsx（含 JSON 与 antd），所以读源码静态核对关键约定。
// 重点是「token / 摄像机地址只活在组件局部 state 里」：不进存储、地址栏、日志、错误上报、请求缓存。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/System/FaceCameras'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
/** 去掉注释，避免注释里的说明文字被当成代码。 */
const stripComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')

const files = {
  page: stripComments(read(`${directory}/index.tsx`)),
  form: stripComments(read(`${directory}/CameraFormModal.tsx`)),
  secret: stripComments(read(`${directory}/SecretRevealModal.tsx`)),
  drawer: stripComments(read(`${directory}/CapturesDrawer.tsx`)),
  viewer: stripComments(read(`${directory}/FaceImageViewerModal.tsx`)),
  logic: stripComments(read(`${directory}/logic.ts`)),
  imageLogic: stripComments(read(`${directory}/imageLogic.ts`)),
  service: stripComments(read('src/services/faceCameraService.ts')),
}
const everything = Object.entries(files)
const route = read('src/router/routes.tsx')
const zh = JSON.parse(read(`${directory}/messages.zh.json`)) as { faceCameras: Record<string, Record<string, string>> }

// ───────── 数据：经服务层，路径固定，页面不直接拼接口 ─────────
assert(files.service.includes("'/api/react/v1/face-cameras'"), '服务必须请求约定的接口路径')
assert(!files.page.includes('/api/react/v1/face-cameras'), '页面必须经 faceCameraService 调用接口')
for (const call of ['getFaceCameras(', 'createFaceCamera(', 'updateFaceCamera(', 'rotateFaceCameraToken(']) {
  assert(files.page.includes(call), `页面必须使用服务层 ${call}`)
}
assert(files.drawer.includes('getFaceCameraCaptures('), '抓拍抽屉必须使用服务层读取记录')
assert((files.service.match(/cache: 'no-store'/g) ?? []).length >= 1 && files.service.includes('NO_STORE'), '请求必须带 cache: no-store')

// ───────── 安全：token / 地址不落任何持久位置 ─────────
for (const [name, text] of everything) {
  assert(!/\b(localStorage|sessionStorage|indexedDB|document\.cookie)\b/.test(text), `${name} 不得使用浏览器存储`)
  assert(!/\bconsole\s*\./.test(text), `${name} 不得出现 console.*`)
  assert(!/@tanstack\/react-query|from 'swr'|from "swr"|useQuery|useMutation/.test(text), `${name} 不得使用查询缓存库`)
  assert(!/reportRuntimeError|reportRequestError|sendCenterLog|postCenterLogAuthorizedJson|Sentry/.test(text), `${name} 不得自行做日志 / 错误上报`)
}
for (const name of ['page', 'form', 'secret', 'drawer'] as const) {
  assert(!/react-router|useSearchParams|useNavigate|window\.location|history\./.test(files[name]), `${name} 不得读写地址栏（token 不能进 URL）`)
  assert(!/\.token\b/.test(files[name]), `${name} 不得读取响应里的 token 字段（地址里已包含，单独的 token 不需要也不保存）`)
  assert(!/<img\b|<Image\b|\bImage\b.*from 'antd'/.test(files[name]), `${name} 不得展示图片`)
}
assert(!/\.token\b/.test(files.logic), 'logic 不得读取 token 字段')
// 地址只进 useState，不进 useRef / 模块级变量 / 缓存
assert(!/useRef[^\n]*(secret|cameraUrl|token)/i.test(files.page + files.secret), '地址不得放进 useRef')
assert(/const \[secret, setSecret\] = useState<SecretRevealState>\(SECRET_REVEAL_CLOSED\)/.test(files.page), '一次性地址放在页面局部 state，初始为已清空')
const setSecretCalls = [...files.page.matchAll(/setSecret\(([^\n]*)\)/g)].map((match) => match[1])
assert(
  setSecretCalls.length === 3 &&
    setSecretCalls.filter((arg) => arg.startsWith('openSecretReveal(')).length === 2 &&
    setSecretCalls.filter((arg) => arg.includes('closeSecretReveal()')).length === 1,
  'setSecret 只允许：新增时打开、轮换时打开、关闭时清空',
)
assert(!/^(let|var) /m.test(files.page + files.secret + files.form), '页面不得有可变模块级变量（可能残留地址）')
assert(
  (files.service.match(/new Error\(([^)]*)\)/g) ?? []).join('|') === "new Error('FACE_CAMERA_SECRET_RESPONSE_INVALID')",

  '响应不完整时抛固定标记，错误信息里不带响应内容',
)
assert(!/error\.payload|\.payload\b/.test(files.page + files.form + files.secret + files.drawer), '页面不直接读取错误对象里的响应体')

// ───────── 一次性展示框 ─────────
assert(files.page.includes('{secret.open ? <SecretRevealModal'), '只有持有地址时才挂载展示框，关闭即卸载')
assert(files.page.includes('onClose={() => setSecret(closeSecretReveal())}'), '关闭展示框要清空状态')
assert(
  files.secret.includes('closable={false}') && files.secret.includes('maskClosable={false}') && files.secret.includes('keyboard={false}'),
  '展示框只能通过「我已保存」按钮关闭，避免误触丢失只显示一次的地址',
)
assert(files.secret.includes('readOnly') && files.secret.includes('copyTextToClipboard('), '地址放在只读文本框里，复制走 copyTextToClipboard')
assert(files.secret.includes('textArea.select()') && files.secret.includes('faceCameras.secret.copyFailed'), '复制失败要降级：选中文本并提示手动复制')
assert(files.secret.includes('faceCameras.secret.onceOnly'), '展示框必须有「只显示一次」提示')
assert(
  zh.faceCameras.secret.onceOnly.includes('只显示这一次') && zh.faceCameras.secret.onceOnly.includes('无法再次查看') && zh.faceCameras.secret.onceOnly.includes('重新生成'),
  '中文提示要写清楚只显示一次、关闭后无法再次查看、丢失只能重新生成',
)
assert(files.secret.includes('destroyOnHidden'), '展示框关闭后销毁内容')

// ───────── 危险操作确认 ─────────
assert((files.page.match(/modal\.confirm\(/g) ?? []).length === 2, '停用与重新生成 token 都要二次确认')
assert(files.page.includes("faceCameras.confirmDisable.title") && files.page.includes("faceCameras.confirmRotate.title"), '确认框使用各自的文案')
assert(
  zh.faceCameras.confirmRotate.content.includes('旧 token 立即失效') && zh.faceCameras.confirmRotate.content.includes('重新配置'),
  '轮换确认文案要写清旧 token 立即失效、摄像机需要重新配置',
)
assert(files.page.includes('if (enabled) {') && files.page.includes('void applyEnabled(camera, true)'), '启用直接生效，不弹确认')
assert(files.page.includes('disabled={busyIds.has(record.cameraId)}') && files.page.includes('loading={busyIds.has(record.cameraId)}'), '操作中开关 loading 并禁用，防重复点击')

// ───────── 表格、页面形态 ─────────
assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(files.page + files.drawer), '页面不能直接从 antd 引入 Table')
assert(files.page.includes('metricId="system.face-cameras.table-1"') && files.drawer.includes('metricId="system.face-cameras.captures-table-1"'), 'metricId 必须是稳定字符串')
assert(files.page.includes('scroll={{ x:'), '摄像机表格要有横向滚动')
assert(/<Alert\s+type="info"/.test(files.page), '顶部说明使用 info 提示条')
assert(files.page.includes('registerPageMessages({ zh, en })'), '页面文案懒注册')
assert(files.page.includes('CapturesDrawer key={capturesTarget.cameraId}'), '换摄像机时抽屉重新挂载，页码从头开始')
assert(files.drawer.includes('canGoNextPage(') && files.drawer.includes('canGoPrevPage('), '抓拍记录用上一页 / 下一页（后端没有总数）')
assert(files.drawer.includes('abortRef.current?.abort()'), '抓拍记录请求可取消')
assert(files.page.includes('getActiveStores()') && files.form.includes('storeOptionsFailed'), '分店选项来自分店接口，失败降级为手填')
assert(!files.page.includes('console.'), '页面不输出 console')
assert(existsSync(resolve(`${directory}/messages.zh.json`)) && existsSync(resolve(`${directory}/messages.en.json`)), '中英文页面消息文件都要有')

// ───────── 路由：懒加载，且不做 keepAlive（离开页签即卸载，内存不留地址） ─────────
const routeStart = route.indexOf("path: '/system/face-cameras'")
assert(routeStart >= 0, '必须登记路由 /system/face-cameras')
const routeBlock = route.slice(routeStart, route.indexOf('element:', routeStart))
assert(route.includes("const FaceCamerasPage = lazy(() => import('../pages/System/FaceCameras'))"), '页面必须懒加载，不进首屏包')
assert(routeBlock.includes('keepAlive: false'), '含一次性地址的页面不能 keepAlive')
assert(routeBlock.includes("accessKey: 'canManageFaceCameras'") && routeBlock.includes("title: 'menu.faceCameras'"), '路由使用独立访问键与全局菜单键')

// ───────── 抓拍图片查看（敏感生物识别信息）：按需、不落地、不外泄 ─────────
// 读取：只经服务层的专用函数，且只有查看弹框调用；页面 / 抽屉 / 逻辑文件都不直接请求图片
assert(files.service.includes('export async function fetchFaceCaptureImage('), '服务层必须提供按需读取单张图片的函数')
assert(files.service.includes("credentials: 'include'") && files.service.includes('...NO_STORE'), '读取图片带登录 Cookie 且 no-store')
assert(/\/captures\/\$\{captureId\}\/images\/\$\{imageId\}\/content/.test(files.service), '图片只走专用内容接口')
assert(files.viewer.includes('fetchFaceCaptureImage('), '图片只由查看弹框读取')
for (const name of ['page', 'form', 'secret', 'drawer', 'logic', 'imageLogic'] as const) {
  assert(!files[name].includes('fetchFaceCaptureImage'), `${name} 不得直接读取图片（只能由查看弹框按需读取）`)
}
// 抓拍抽屉：图片列在第 2 列；列宽合计不超过 960 抽屉的内容区（约 912），桌面上不出现横向滚动；
// 顶部常驻最后推送时间，只有本店 Off 时显示黄色提示（Capture / Match 都留存照片，不另提示）。
{
  const widths = files.drawer.match(/const COLUMN_WIDTHS = \{([\s\S]*?)\} as const/)?.[1] ?? ''
  const total = [...widths.matchAll(/:\s*(\d+)/g)].reduce((sum, match) => sum + Number(match[1]), 0)
  assert(total > 0 && total <= 912, `抓拍抽屉列宽合计 ${total} 必须不超过 912`)
  assert(files.drawer.includes('TABLE_WIDTH_WITHOUT_IMAGES + COLUMN_WIDTHS.images') && !/scroll=\{\{ x: canViewImages \? \d+/.test(files.drawer), 'scroll.x 由列宽常量算出')
  const order = ['receivedAtUtc', "key: 'images'", 'cameraEventTime', "dataIndex: 'eventType'", "dataIndex: 'faceCount'", "dataIndex: 'parseStatus'", "dataIndex: 'parseNote'"].map((marker) => files.drawer.indexOf(marker))
  assert(order.every((index, position) => index >= 0 && (position === 0 || index > order[position - 1])), '列顺序：接收时间、图片、事件时间、类型、人脸、解析状态、备注')
  assert(files.drawer.includes('formatShortDateTime(value)'), '抽屉时间列去掉年份')
  assert(files.drawer.includes("result?.storeMode === 'off'") && !files.drawer.includes("storeMode === 'match'"), '只有 Off 显示模式提示')
  assert(files.drawer.includes("t('faceCameras.captures.lastSeen'"), '顶部常驻最后推送时间')
}
// 入口：列表只在后端返回 canViewImages 且该行有图片时才出现入口，点击才挂载弹框
assert(files.drawer.includes('getRowImageAction(canViewImages, record.images)'), '行内入口按 canViewImages 与该行图片元数据判定')
assert(files.drawer.includes('const canViewImages = result?.canViewImages === true'), '入口开关来自后端返回的 canViewImages')
assert(files.drawer.includes('setViewTarget(record)') && files.drawer.includes('{viewTarget ? ('), '点击「查看图片」才挂载弹框')
assert(files.drawer.includes('key={viewTarget.captureId}'), '切换抓拍时弹框重新挂载，旧图片 URL 随之撤销')
assert(files.drawer.includes('onClose={() => setViewTarget(null)}'), '关闭弹框要卸载组件')
// 不预加载：没有任何图片元素 / 预取 / 缩略图
for (const [name, text] of everything) {
  assert(!/new Image\(|rel=["']?(preload|prefetch)|<link\b|\bImage\.prototype|createImageBitmap|<canvas|getContext\(|toDataURL|FileReader|readAsDataURL/.test(text), `${name} 不得预加载图片、使用 canvas 读取像素或转 dataURL`)
  assert(!/<picture\b|<source\b|srcSet|loading=["']lazy/.test(text), `${name} 不得做缩略图 / 响应式图片列表`)
}
// 只有查看弹框可以有 <img>，且 src 只能是 state 里的 objectURL，不是后端地址
assert((files.viewer.match(/<img\b/g) ?? []).length === 1, '查看弹框只有一处 <img>')
assert(/<img[^>]*\bsrc=\{state\.url\}/.test(files.viewer), '<img> 的 src 必须是 state 里的 objectURL')
assert(!/src=["']/.test(files.viewer) && !/\/api\//.test(files.viewer + files.imageLogic) && !/https?:/.test(files.viewer + files.imageLogic), '图片地址不能写成后端地址或外链')
assert(!/\.(src|href)\s*=/.test(files.viewer), '不得用命令式方式设置图片地址')
// 生命周期：objectURL 只由登记表创建，关闭 / 切换 / 卸载都要撤销并清空
assert(!files.viewer.includes('createObjectURL') && !files.viewer.includes('revokeObjectURL'), '弹框不直接操作 objectURL，统一走会话 / 登记表')
assert(files.imageLogic.includes('URL.createObjectURL(blob)') && files.imageLogic.includes('URL.revokeObjectURL(url)'), '登记表封装 objectURL 的创建与撤销')
assert((files.imageLogic.match(/URL\.createObjectURL/g) ?? []).length === 1, 'objectURL 只在登记表里创建')
assert(
  /useEffect\(\(\) => \{[\s\S]*createImageSession\([\s\S]*void session\.start\(\)[\s\S]*return \(\) => session\.dispose\(\)[\s\S]*\}, \[cameraId, capture\]\)/.test(files.viewer),
  '弹框卸载 / 切换抓拍时 effect 清理函数必须 dispose 会话',
)
assert(files.imageLogic.includes('registry.dispose()') && files.imageLogic.includes('controller.abort()') && files.imageLogic.includes('states = new Map()'), 'dispose 要撤销 URL、中止请求并清空状态')
assert(files.imageLogic.includes('return null') && files.imageLogic.includes('if (disposed) return null'), '登记表 dispose 后拒绝再登记（晚到的响应不泄漏 URL）')
// 状态里只放 URL 字符串，不放 Blob
assert(!/useState<[^>]*Blob/.test(files.viewer), '弹框 state 里不保存 Blob')
// 敏感提示与审计提醒
assert(files.viewer.includes('faceCameras.images.sensitiveNotice') && /<Alert\s+type="warning"/.test(files.viewer), '弹框顶部必须有醒目的敏感信息提示')
// 失败文案：按错误码分类，不把响应体原样展示
assert(files.viewer.includes('FACE_IMAGE_ERROR_MESSAGE_KEYS['), '失败文案按错误码分类映射')
assert(!/error\.(message|payload)/.test(files.viewer + files.drawer), '页面不直接展示错误对象里的内容')

console.log('FaceCameras sourceContract.test: ok')
