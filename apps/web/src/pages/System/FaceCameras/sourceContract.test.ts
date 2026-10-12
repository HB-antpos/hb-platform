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
  logic: stripComments(read(`${directory}/logic.ts`)),
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

console.log('FaceCameras sourceContract.test: ok')
