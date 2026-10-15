import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

// 页面接线与安全契约：不能让测试 import index.tsx（含 JSON 与 antd），所以读源码静态核对关键约定。
// 重点是敏感生物识别信息：复核图片 / 登记照预览只以 objectURL 存在且会撤销，不进存储、地址栏、日志、请求缓存；
// 页面只经服务层调用接口；用词固定；写入类操作都有二次确认；客流页标注估算。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/System/FaceMatch'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
/** 去掉注释，避免注释里的说明文字被当成代码。 */
const stripComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')

const names = readdirSync(resolve(directory)).filter((name) => /\.(ts|tsx)$/.test(name) && !/\.test\.ts$/.test(name))
const files: Record<string, string> = {}
for (const name of names) files[name] = stripComments(read(`${directory}/${name}`))
const service = stripComments(read('src/services/faceMatchService.ts'))
const everything = [...Object.entries(files), ['faceMatchService.ts', service] as [string, string]]
const route = read('src/router/routes.tsx')
const zhRaw = read(`${directory}/messages.zh.json`)
const enRaw = read(`${directory}/messages.en.json`)
const zh = JSON.parse(zhRaw) as { faceMatch: Record<string, Record<string, Record<string, string> | string>> }
const f = (name: string) => {
  assert(files[name] !== undefined, `缺少源码文件 ${name}`)
  return files[name]
}

// ───────── 数据：经服务层，路径固定，页面不直接拼接口 ─────────
assert(service.includes("'/api/react/v1/face-match'"), '服务必须请求约定的接口路径')
for (const [name, text] of Object.entries(files)) {
  assert(!text.includes('/api/react/v1/face-match'), `${name} 必须经 faceMatchService 调用接口`)
  assert(!/\bfetch\s*\(/.test(text) && !/\brequest\s*\./.test(text), `${name} 不得直接发请求`)
}
assert((service.match(/cache: 'no-store'/g) ?? []).length >= 1 && service.includes('NO_STORE'), '请求必须带 cache: no-store')
for (const call of ['getFaceMatchConfig(']) assert(f('index.tsx').includes(call), `页面必须使用服务层 ${call}`)
assert(f('ProfilesTab.tsx').includes('checkFaceStoreProfile(') && f('ProfilesTab.tsx').includes('updateFaceStoreProfile('), '档案页使用 check 与保存接口')
assert(f('TrafficTab.tsx').includes('getFaceTraffic('), '客流页使用客流接口')
assert(f('AuditTab.tsx').includes('getFaceAudit('), '审计页使用审计接口')
assert(f('ConsentModal.tsx').includes('createFaceConsent(') && f('EnrollmentModal.tsx').includes('createFaceEnrollment('), '同意与登记走服务层')
assert(f('UnwelcomeEntryModal.tsx').includes('createUnwelcomeEntry('), '提交条目走服务层')
assert(f('ReviewTaskModal.tsx').includes('reviewUnwelcomeTask('), '复核判断走服务层')
// multipart：字段名与契约一致，且不手动设置 Content-Type（要让运行时补 boundary）
assert(service.includes("form.append('scan'") && service.includes("form.append('photos'"), 'multipart 文件字段名为 scan / photos')
assert(!/['"]Content-Type['"]\s*:/i.test(service) && !/headers:\s*\{[^}]*content-type/i.test(service), '服务层不得手动设置 Content-Type 请求头')

// ───────── 安全：不写浏览器存储 / 地址栏 / 日志 / 查询缓存 ─────────
for (const [name, text] of everything) {
  assert(!/\b(localStorage|sessionStorage|indexedDB|document\.cookie)\b/.test(text), `${name} 不得使用浏览器存储`)
  assert(!/\bconsole\s*\./.test(text), `${name} 不得出现 console.*`)
  assert(!/@tanstack\/react-query|from 'swr'|from "swr"|useQuery|useMutation/.test(text), `${name} 不得使用查询缓存库`)
  assert(!/reportRuntimeError|reportRequestError|sendCenterLog|postCenterLogAuthorizedJson|Sentry/.test(text), `${name} 不得自行做日志 / 错误上报`)
  assert(!/new Image\(|rel=["']?(preload|prefetch)|<link\b|createImageBitmap|<canvas|getContext\(|toDataURL|FileReader|readAsDataURL/.test(text), `${name} 不得预加载图片、使用 canvas 读取像素或转 dataURL`)
  assert(!/<picture\b|<source\b|srcSet|loading=["']lazy/.test(text), `${name} 不得做缩略图 / 响应式图片列表`)
}
for (const [name, text] of Object.entries(files)) {
  assert(!/react-router|useSearchParams|useNavigate|window\.location|history\./.test(text), `${name} 不得读写地址栏`)
  assert(!/window\.open|navigator\.clipboard|document\.execCommand|\.download\b|a\.href|URL\.createObjectURL|URL\.revokeObjectURL/.test(text), `${name} 不得下载 / 复制 / 直接操作 objectURL`)
  assert(!/\bcreateObjectURL\b|\brevokeObjectURL\b/.test(text), `${name} 不得直接操作 objectURL（统一走登记表）`)
}
assert(!/error\.payload|\.payload\b/.test(Object.entries(files).filter(([name]) => name.endsWith('.tsx')).map(([, text]) => text).join('\n')), '组件不直接读取错误对象里的响应体')
for (const [name, text] of Object.entries(files)) {
  if (name.endsWith('.tsx')) assert(!/^(let|var) /m.test(text), `${name} 不得有可变模块级变量`)
}

// ───────── 图片：只有这几处 <img>，src 只能是 objectURL ─────────
const imgOwners = Object.entries(files).filter(([, text]) => /<img\b/.test(text)).map(([name]) => name).sort()
assert(
  imgOwners.join(',') === 'HitThumbnail.tsx,PhotoPicker.tsx,ResultDetailDrawer.tsx,ReviewTaskModal.tsx',
  `只有 HitThumbnail、PhotoPicker、ResultDetailDrawer 与 ReviewTaskModal 可以有 <img>，实际: ${imgOwners.join(',')}`,
)
// 名单命中图缩略图（列表里唯一的真实图片）：只在「名单命中」视图里出现；一页一次批量请求（服务端一条审计），
// base64 只在内存里解码成 Blob → objectURL（登记表创建，换页 / 卸载 dispose 撤销），不拼 data: 地址。
{
  const thumb = f('HitThumbnail.tsx')
  assert((thumb.match(/<img\b/g) ?? []).length === 1 && /<img[^>]*\bsrc=\{state\.url\}/.test(thumb), '缩略图只有一处 <img>，src 是会话里的 objectURL')
  assert(thumb.includes('draggable={false}') && !/src=["']/.test(thumb) && !/\/api\//.test(thumb) && !/https?:|data:/.test(thumb), '缩略图地址不能是后端地址、外链或 data: 地址')
  for (const [name, text] of Object.entries(files)) {
    if (name !== 'useHitThumbnails.ts') assert(!text.includes('fetchFaceHitThumbnails'), `${name} 不得直接请求缩略图（只经 useHitThumbnails）`)
    if (name !== 'HitsView.tsx' && name !== 'useHitThumbnails.ts') assert(!/<HitThumbnail\b/.test(text) && !text.includes('useHitThumbnails('), `${name} 不得放命中图缩略图（只有名单命中视图有）`)
  }
  assert(
    /useEffect\(\(\) => \{[\s\S]*createHitThumbnailSession\([\s\S]*void session\.start\(\)[\s\S]*session\.dispose\(\)[\s\S]*\}, \[ids\]\)/.test(f('useHitThumbnails.ts')),
    '缩略图会话在编号变化 / 卸载时 dispose（中止请求、撤销 objectURL）',
  )
  assert((f('HitsView.tsx').match(/useHitThumbnails\(/g) ?? []).length === 1, '名单命中视图每页只取一批缩略图')
  assert(files['imageLogic.ts'].includes('export function createHitThumbnailSession(') && files['imageLogic.ts'].includes('registry.set(taskId, new Blob('), '缩略图经登记表转 objectURL')
  assert(/fetchFaceHitThumbnails[\s\S]*?kind: 'hit'/.test(service) && service.includes('HIT_THUMBNAIL_BATCH_MAX = 30'), '缩略图只请求命中图（kind=hit），一批最多 30 项')
}
// 命中图原图：只有详情抽屉（从名单命中视图打开时）按需读取，走 results/hits/{taskId}/image（服务端每次读取写审计）。
assert(/\/results\/hits\/\$\{taskId\}\/image/.test(service) && /export async function fetchFaceHitImage[\s\S]*?IMAGE_PATH_TASK_ID\.test\(taskId\)/.test(service), '命中图走专用内容接口且编号先校验')
for (const [name, text] of Object.entries(files)) {
  if (name !== 'ResultDetailDrawer.tsx') assert(!text.includes('fetchFaceHitImage'), `${name} 不得直接读取命中图（只能由详情抽屉按需读取）`)
}
assert(
  /useEffect\(\(\) => \{[\s\S]*createHitImageSession\([\s\S]*void session\.start\(\)[\s\S]*session\.dispose\(\)[\s\S]*\}, \[hitImageTaskId\]\)/.test(f('ResultDetailDrawer.tsx')),
  '抽屉的命中图会话卸载 / 切换时 dispose',
)
assert(f('ResultDetailDrawer.tsx').includes("const hitOnly = origin === 'hits'") && f('ResultDetailDrawer.tsx').includes('hitOnly && result.hit?.imageAvailable'), '只有从名单命中视图打开且命中图还在时才读命中图')
assert((f('ReviewTaskModal.tsx').match(/<img\b/g) ?? []).length === 1, '复核弹框只有一处 <img>')
assert(/<img[^>]*\bsrc=\{state\.url\}/.test(f('ReviewTaskModal.tsx')), '复核 <img> 的 src 必须是 state 里的 objectURL')
assert((f('PhotoPicker.tsx').match(/<img\b/g) ?? []).length === 1, '照片选择器只有一处 <img>')
assert(/const url = urls\.get\(item\.uid\)/.test(f('PhotoPicker.tsx')) && /<img[^>]*\bsrc=\{url\}/.test(f('PhotoPicker.tsx')), '预览 <img> 的 src 必须是登记表里的 objectURL')
for (const name of ['ReviewTaskModal.tsx', 'PhotoPicker.tsx', 'ResultDetailDrawer.tsx']) {
  assert(!/src=["']/.test(f(name)) && !/\/api\//.test(f(name)) && !/https?:/.test(f(name)), `${name} 图片地址不能写成后端地址或外链`)
  assert(!/\.(src|href)\s*=/.test(f(name)), `${name} 不得用命令式方式设置图片地址`)
}
assert(!/useState<[^>]*Blob/.test(f('ReviewTaskModal.tsx')), '复核弹框 state 里不保存 Blob')

// 读取：只经服务层专用函数；命中抓拍图只有复核弹框读，名单参考照片只有复核弹框与抓拍详情抽屉（名单目标图，见下方）读；其它文件都不直接读图
assert(service.includes('export async function fetchUnwelcomeTaskImage(') && service.includes('export async function fetchUnwelcomeReferenceImage('), '服务层提供按需读取单张图片的函数')
assert(service.includes("credentials: 'include'") && service.includes('...NO_STORE'), '读取图片带登录 Cookie 且 no-store')
assert(/\/unwelcome\/tasks\/\$\{taskId\}\/image/.test(service) && /\/unwelcome\/tasks\/\$\{taskId\}\/reference\/\$\{index\}/.test(service), '图片只走两个专用内容接口')
for (const [name, text] of Object.entries(files)) {
  if (name === 'ReviewTaskModal.tsx') continue
  assert(!text.includes('fetchUnwelcomeTaskImage'), `${name} 不得直接读取命中抓拍图（只能由复核弹框按需读取）`)
  if (name === 'ResultDetailDrawer.tsx') continue
  assert(!text.includes('fetchUnwelcomeReferenceImage'), `${name} 不得直接读取名单参考照片（只能由复核弹框与抓拍详情抽屉按需读取）`)
}
assert(f('ReviewTaskModal.tsx').includes('fetchUnwelcomeTaskImage(') && f('ReviewTaskModal.tsx').includes('fetchUnwelcomeReferenceImage('), '复核弹框读取抓拍与参考照片')

// 生命周期：objectURL 只由登记表创建；关闭 / 切换 / 卸载都要撤销并清空
const faceCamerasImageLogic = stripComments(read('src/pages/System/FaceCameras/imageLogic.ts'))
assert((faceCamerasImageLogic.match(/URL\.createObjectURL/g) ?? []).length === 1, 'objectURL 只在共用登记表里创建')
assert(files['imageLogic.ts'].includes("from '../FaceCameras/imageLogic'") && files['imageLogic.ts'].includes('createObjectUrlRegistry('), '复核与预览复用共用登记表，不复制')
assert(
  /useEffect\(\(\) => \{[\s\S]*createReviewImageSession\([\s\S]*void session\.start\(\)[\s\S]*session\.dispose\(\)[\s\S]*\}, \[taskId, referenceCount, imageAvailable\]\)/.test(f('ReviewTaskModal.tsx')),
  '复核弹框卸载 / 切换任务时 effect 清理函数必须 dispose 会话',
)
assert(files['imageLogic.ts'].includes('controller.abort()') && files['imageLogic.ts'].includes('registry.dispose()') && files['imageLogic.ts'].includes('states = new Map()'), 'dispose 要中止请求、撤销 URL 并清空状态')
assert(files['imageLogic.ts'].includes('if (disposed || url === null) return'), '会话 dispose 后拒绝再登记（晚到的响应不泄漏 URL）')
assert(
  /createPreviewManager\(\)[\s\S]*manager\.dispose\(\)/.test(f('PhotoPicker.tsx')) && f('PhotoPicker.tsx').includes('managerRef.current.sync(items)'),
  '照片选择器卸载时撤销全部预览，列表变化时同步撤销移除的预览',
)
assert(f('ReviewTasksTab.tsx').includes('key={target.taskId}') && f('ReviewTasksTab.tsx').includes('{target ? ('), '点击「复核」才挂载弹框，切换任务重新挂载')
assert(f('ReviewTasksTab.tsx').includes('onClose={() => setTarget(null)}'), '关闭弹框要卸载组件')
assert(f('ReviewTaskModal.tsx').includes('if (!imageAvailable) return'), '命中图已删除时不发任何图片请求')
assert(f('ReviewTaskModal.tsx').includes('void sessionRef.current?.loadReference(index)'), '参考照片切换时才按需读取')
// 抓拍与比对（全部抓拍）：列表里没有任何真实照片（只有占位图标），详情抽屉里才有图片位（人脸小图 / 背景图 / 命中图共用同一个组件，源码里只有一处 <img>）。
assert((f('ResultDetailDrawer.tsx').match(/<img\b/g) ?? []).length === 1, '结果抽屉只有一处 <img>（人脸小图与背景图共用 ResultImageView）')
assert(/<img[^>]*\bsrc=\{state\.url\}/.test(f('ResultDetailDrawer.tsx')), '结果抽屉 <img> 的 src 必须是 state 里的 objectURL')
assert(!/useState<[^>]*Blob/.test(f('ResultDetailDrawer.tsx')), '结果抽屉 state 里不保存 Blob')
assert(!/<img\b/.test(f('ResultsTab.tsx')) && f('ResultsTab.tsx').includes('PictureOutlined'), '结果列表不放真实照片，缩略图位置只是占位图标')
assert(service.includes('export async function fetchFaceCaptureResultImage(') && service.includes('export async function getFaceCaptureResults('), '服务层提供结果列表与按需读取单张图片的函数')
assert(/\/results\/\$\{captureId\}\/images\/\$\{imageId\}/.test(service) && service.includes('IMAGE_PATH_TASK_ID.test(captureId)') && service.includes('IMAGE_PATH_TASK_ID.test(imageId)'), '结果图片只走专用内容接口，且编号先校验')
for (const [name, text] of Object.entries(files)) {
  if (name !== 'ResultDetailDrawer.tsx') assert(!text.includes('fetchFaceCaptureResultImage'), `${name} 不得直接读取结果图片（只能由详情抽屉按需读取）`)
  if (name !== 'ResultsTab.tsx') assert(!text.includes('getFaceCaptureResults'), `${name} 不得直接请求结果列表`)
}
assert(f('ResultDetailDrawer.tsx').includes('fetchFaceCaptureResultImage(') && f('ResultsTab.tsx').includes('getFaceCaptureResults('), '抽屉读取图片、列表页签读取列表')
assert(
  /useEffect\(\(\) => \{[\s\S]*createResultImageSession\([\s\S]*void session\.start\(\)[\s\S]*session\.dispose\(\)[\s\S]*\}, \[captureId, faceImageId, sceneImageId\]\)/.test(f('ResultDetailDrawer.tsx')),
  '结果抽屉卸载 / 切换抓拍时 effect 清理函数必须 dispose 会话',
)
assert(f('ResultsTab.tsx').includes('key={target.captureId}') && f('ResultsTab.tsx').includes('{target ? ('), '点击「查看」才挂载抽屉，切换抓拍重新挂载')
assert(f('ResultsTab.tsx').includes('onClose={() => setTarget(null)}'), '关闭抽屉要卸载组件')
assert(files['imageLogic.ts'].includes('createResultImageSession') && (files['imageLogic.ts'].match(/registry\.dispose\(\)/g) ?? []).length >= 2, '结果图片会话 dispose 同样撤销全部 objectURL')
// 「去复核」只切换页签：不读写地址栏（上面的全局规则已禁），由页面级 goToReview 切到 review，命中图已删除或没有复核页签权限时按钮禁用。
assert(f('index.tsx').includes("setSelectedTab('review')") && f('index.tsx').includes('activeKey={activeTab}'), '去复核通过受控页签切到复核任务')
assert(f('ResultDetailDrawer.tsx').includes('canGoReview(result)') && f('ResultDetailDrawer.tsx').includes('disabled={!reviewable || !onGoReview}'), '去复核仅在有命中且命中图还在、且有复核页签时可点')
assert(f('index.tsx').includes("case 'results':") && f('index.tsx').includes('<ResultsTab context={tabContext} />'), '页签渲染接入抓拍与比对')
// 命中通知：列表（复核任务 / 抓拍结果）只显示人数；收件人姓名只出现在抓拍详情抽屉的「接收人」列表里，
// 且只来自详情接口的 displayName（姓名 + 已读状态 + 已读时间），任何文件都不得出现账号标识类字段；通知文案是固定英文原文，来自逻辑层常量。
assert(f('ReviewTasksTab.tsx').includes('<NotifyStateCell') && f('ResultsTab.tsx').includes('<NotifyStateCell'), '复核任务与抓拍结果共用命中通知单元格')
for (const name of ['ResultsTab.tsx', 'shared.tsx']) {
  assert(!/recipientName|recipients\.map|readerName|userName|userGuid/.test(f(name)), `${name} 只显示通知人数，不得出现通知收件人姓名等字段`)
}
const ACCOUNT_IDENTIFIER = /user(guid|name|id)|readerName|recipientName|\bemail\b/i
assert(!ACCOUNT_IDENTIFIER.test(f('ResultDetailDrawer.tsx')), '抽屉的接收人只显示姓名与已读状态，不得出现账号标识类字段')
assert(
  f('ResultDetailDrawer.tsx').includes('recipient.displayName') && f('ResultDetailDrawer.tsx').includes('recipient.read') && f('ResultDetailDrawer.tsx').includes('recipient.readAtUtc'),
  '接收人列表显示姓名（displayName）、已读状态与已读时间',
)
{
  const recipientType = read('src/types/faceMatch.ts').match(/export interface FaceHitRecipient \{[\s\S]*?\n\}/)?.[0] ?? ''
  assert(recipientType !== '' && !ACCOUNT_IDENTIFIER.test(recipientType), '收件人类型只有展示名 / 已读状态 / 已读时间，没有账号标识')
  const recipientNormalizer = service.match(/function normalizeFaceHitRecipient[\s\S]*?\n\}/)?.[0] ?? ''
  assert(recipientNormalizer !== '' && !ACCOUNT_IDENTIFIER.test(recipientNormalizer), '收件人归一化不读取账号标识类字段')
}
// 命中详情（接收人 + 目标图张数）：抽屉里只有一处读取（挂在有命中时才渲染的 HitSections 里），服务函数先校验编号并带 no-store；其它文件不得读取。
assert(service.includes('export async function fetchFaceHitDetail(') && /\/results\/hits\/\$\{taskId\}\/detail/.test(service), '服务层提供命中详情读取函数，路径固定')
assert(/export async function fetchFaceHitDetail[\s\S]*?IMAGE_PATH_TASK_ID\.test\(taskId\)[\s\S]*?\.\.\.NO_STORE/.test(service), '命中详情先校验任务编号，请求带 no-store')
for (const [name, text] of Object.entries(files)) {
  if (name !== 'ResultDetailDrawer.tsx') assert(!text.includes('fetchFaceHitDetail'), `${name} 不得直接读取命中详情（只能由抓拍详情抽屉读取）`)
}
assert((f('ResultDetailDrawer.tsx').match(/fetchFaceHitDetail\(/g) ?? []).length === 1, '抽屉只在一处读取命中详情')
assert(
  /const loader = useCallback\(\(signal: AbortSignal\) => fetchFaceHitDetail\(taskId, signal\), \[taskId\]\)\s*const remote = useRemote<FaceHitDetail>\(loader\)/.test(f('ResultDetailDrawer.tsx')),
  '命中详情经 useRemote 读取（挂载后一次、可中止、卸载 / 换抓拍即中止）',
)
assert(
  /\{hit \? \(\s*<HitSections /.test(f('ResultDetailDrawer.tsx')) && (f('ResultDetailDrawer.tsx').match(/<HitSections\b/g) ?? []).length === 1,
  '只有这条抓拍有命中时才挂载读取详情的 HitSections',
)
// 名单目标图：只在有复核权限（canViewTarget）且详情给出张数大于 0 时才挂载并读取；读取复用复核任务的参考照片接口；objectURL 随组件卸载撤销。
{
  const drawer = f('ResultDetailDrawer.tsx')
  assert(drawer.includes('canViewTarget: boolean') && drawer.includes('resolveTargetAreaView({ canViewTarget'), '目标图是否显示由 canViewTarget 与详情张数共同决定')
  assert((drawer.match(/<TargetPhotoBlock\b/g) ?? []).length === 1 && /targetView === 'photos' \? \(\s*<TargetPhotoBlock /.test(drawer), '只有判定为「有目标图」时才挂载目标图组件')
  assert((drawer.match(/fetchUnwelcomeReferenceImage\(/g) ?? []).length === 1, '抽屉只在目标图组件里读取参考照片')
  assert(/function TargetPhotoBlock[\s\S]*fetchUnwelcomeReferenceImage\(taskId, referenceIndex, signal\)/.test(drawer), '目标图读取的是这条命中的任务编号 + 下标')
  assert(
    /useEffect\(\(\) => \{[\s\S]*createReferenceImageSession\([\s\S]*void session\.start\(\)[\s\S]*session\.dispose\(\)[\s\S]*\}, \[taskId, count\]\)/.test(drawer),
    '目标图组件卸载 / 切换抓拍时 effect 清理函数必须 dispose 会话（撤销 objectURL、中止在途请求）',
  )
  assert(drawer.includes('void sessionRef.current?.loadReference(next)') && !/for \(let|forEach\([^)]*loadReference/.test(drawer), '切换目标图时才按需读取对应那张，不预取')
  assert(drawer.includes('errorKeys={REVIEW_IMAGE_ERROR_KEYS}') && drawer.includes('errorKeys={RESULT_IMAGE_ERROR_KEYS}'), '目标图错误提示与复核弹框同一套分类，抓拍图沿用抓拍的分类')
  assert(drawer.includes('draggable={false}'), '图片不可拖拽')
  assert(
    files['imageLogic.ts'].includes('export function createReferenceImageSession(') && files['imageLogic.ts'].includes('if (options.fetchCapture) await load(CAPTURE_SLOT, options.fetchCapture)'),
    '目标图会话复用复核会话（不带现场抓拍），不复制生命周期代码',
  )
  assert(f('ResultsTab.tsx').includes('canViewTarget={canViewTarget}') && f('index.tsx').includes('canViewTarget: hasReviewTab'), '目标图权限 = 复核页签权限，由页面经 ResultsTab 传给抽屉')
}
// 名单命中视图：未读红数来自 summary.unread（本人未读命中通知）；「全部标为已读」必须带 type 只标命中通知；「复核」按复核权限显示。
assert(f('ResultsTab.tsx').includes('getFaceHitSummary({}, signal)') && f('ResultsTab.tsx').includes('unreadSummary.data?.unread'), '分段上的红色数字是本人未读命中通知数')
assert(f('ResultsTab.tsx').includes('<Segmented') && f('ResultsTab.tsx').includes('<HitsView '), '顶部分段切换全部抓拍 / 名单命中')
assert(/markAllFaceHitsRead[\s\S]*?params: \{ type: FACE_HIT_NOTIFICATION_TYPE \}/.test(service) && service.includes("FACE_HIT_NOTIFICATION_TYPE = 'face.unwelcome-hit'"), '全部已读只标命中通知类型')
assert(f('HitsView.tsx').includes('<Popconfirm') && f('HitsView.tsx').includes('markAllFaceHitsRead()'), '全部标为已读要二次确认')
assert(f('HitsView.tsx').includes('canReviewPermission && goToReview') && f('HitsView.tsx').includes('config.permissions.unwelcomeReview'), '「复核」只给有复核权限的人')
for (const name of ['HitsView.tsx']) {
  assert(!/recipientName|recipients\.map|readerName|userName|userGuid/.test(f(name)), `${name} 只显示通知人数，不得出现通知收件人姓名等字段`)
}
assert(f('ResultDetailDrawer.tsx').includes('HIT_NOTIFICATION_TITLE') && f('ResultDetailDrawer.tsx').includes('buildHitNotificationBody(hit.scorePercent)'), '通知文案区使用固定英文原文')
assert(f('logic.ts').includes("'Possible match alert'") && f('logic.ts').includes('The score is not the probability that this is the same person; a reviewer must check it.'), '通知英文原文固定在逻辑层常量')
// 命中行淡红底；档位不自己算（上面的 highBandScore 规则已守住）。
assert(f('ResultsTab.tsx').includes('token.colorErrorBg') && f('ResultsTab.tsx').includes('record.hit ?'), '有名单命中的行淡红底')
assert(f('ResultsTab.tsx').includes('record.hit.band ?') && f('ResultDetailDrawer.tsx').includes('hit.band ?'), '档位只显示后端给出的 band，没有就不显示')

// 条件挂载：登记照 / 名单照片只在弹框打开时存在
assert(f('StaffDetailDrawer.tsx').includes('{enrollOpen ? <EnrollmentModal') && f('UnwelcomeEntriesTab.tsx').includes('{createOpen ? <UnwelcomeEntryModal'), '登记 / 提交弹框按需挂载，关闭即卸载')
assert(f('index.tsx').includes('destroyOnHidden'), '页签切走即卸载')

// ───────── 复核页：只显示相似度，不显示姓名与事件细节，档位与可复核由后端字段决定 ─────────
for (const name of ['ReviewTasksTab.tsx', 'ReviewTaskModal.tsx']) {
  assert(!/entryId|evidenceRef|categoryNote|incidentCategory|submittedBy|approvedBy|disputeNote/.test(f(name)), `${name} 不得出现条目的事件细节 / 提交人等字段`)
}
assert(!/highBandScore/.test(Object.values(files).join('\n')), '页面不自己用阈值划档位')
assert(f('ReviewTaskModal.tsx').includes("t('faceMatch.review.suspectedNotice', { percent })") && f('ReviewTaskModal.tsx').includes("t('faceMatch.review.similarity', { percent })"), '复核弹框固定使用「疑似匹配」与相似度文案')
assert(f('ReviewTaskModal.tsx').includes('BAND_LABEL_KEYS[task.band]') && f('ReviewTasksTab.tsx').includes('BAND_LABEL_KEYS[record.band]'), '档位用后端 band 字段')
assert(f('ReviewTaskModal.tsx').includes('getVerdictBlockReasonKey(task)') && f('logic.ts').includes('return task.canReview && task.imageAvailable'), '提交判断受后端 canReview 控制')
assert(f('ReviewTaskModal.tsx').includes('modal.confirm(') && f('ReviewTaskModal.tsx').includes("reviewUnwelcomeTask(taskId, verdict)"), '复核判断提交前二次确认')
// 单人复核：不再有二审流程——弹框不依赖 firstReviewerName 做提示，步骤提示只有「待复核」「已结束」，
// 旧版遗留的 nextStep=second 与 first 同样按待复核处理；按钮是否可点只看后端 canReview。
assert(!f('ReviewTaskModal.tsx').includes('firstReviewerName') && !f('ReviewTaskModal.tsx').includes('faceMatch.review.firstReviewer'), '复核弹框不再展示「前一位复核人已判断」横幅')
assert(!f('logic.ts').includes('faceMatch.review.step.second') && f('logic.ts').includes("task.nextStep === 'second'"), '步骤提示没有待二审一项，旧的 second 当作待复核')
assert(!/isFirstReviewer|SAME_REVIEWER/.test(f('ReviewTaskModal.tsx') + f('ReviewTasksTab.tsx')), '复核页不自己推算「是不是已判断过的人」，也不依赖 SAME_REVIEWER 流程')

// 命中自动确认：列表上能看出哪些任务是系统自动确认的，且不给它加复核入口（入口完全由后端 canReview 决定）。
assert(f('ReviewTasksTab.tsx').includes('record.autoConfirmed') && f('ReviewTasksTab.tsx').includes("t('faceMatch.review.autoConfirmedTag')"), '状态列在「已确认」旁加「自动确认」小标签')
assert(f('ReviewTasksTab.tsx').includes("t('faceMatch.review.autoConfirmedBy')"), '自动确认的任务复核人列显示「系统自动确认」')
assert(!/autoConfirmed/.test(f('ReviewTaskModal.tsx')) && !/autoConfirmed/.test(f('logic.ts')), '自动确认只影响列表展示，不改变复核入口 / 提交判断的规则（仍只看 canReview）')
assert(f('ReviewTasksTab.tsx').includes('record.canReview && !writeBlocked'), '复核入口仍只由后端 canReview 决定')

// 误报统计：自动确认单独一列，总体汇总 / 条目表 / 相似度区间表三处都有；驳回率仍由人工判断的任务算出（页面不把自动确认算进去）。
const statsTab = f('StatsTab.tsx')
assert((statsTab.match(/dataIndex: 'autoConfirmedCount'/g) ?? []).length === 2 && statsTab.includes('value={data.autoConfirmedCount}'), '汇总卡、条目表、相似度区间表都有「自动确认」')
assert((statsTab.match(/faceMatch\.stats\.confirmedCount/g) ?? []).length === 3 && (statsTab.match(/faceMatch\.stats\.autoConfirmedCount/g) ?? []).length === 3, '「人工确认」与「自动确认」在三处成对出现')
assert(statsTab.includes('computeDismissRate(record.dismissedCount, record.confirmedCount)') && !/computeDismissRate\([^)]*autoConfirmedCount/.test(statsTab), '区间驳回率只用已驳回与人工确认，不含自动确认')

// ───────── 错误文案：统一经 describeFaceMatchError（已知码 → 状态码 → 通用），不直接展示后端消息文本 ─────────
for (const [name, text] of Object.entries(files)) {
  if (!name.endsWith('.tsx')) continue
  assert(!/t\(classifyFaceMatchError/.test(text), `${name} 错误提示必须经 describeFaceMatchError`)
  assert(!/error\.message|\.message\b[^(]*\}/.test(text.replace(/message\.(success|error|warning)/g, '')), `${name} 不得直接展示错误对象的消息文本`)
}
assert(f('UnwelcomeEntriesTab.tsx').includes('record.cleanupPending'), '名单条目列表要提示「清理中」')

// ───────── 写入类操作都有二次确认 ─────────
assert((f('StaffDetailDrawer.tsx').match(/<ReasonModal/g) ?? []).length === 2, '撤回同意与撤回登记都用原因弹框二次确认')
assert(f('UnwelcomeEntriesTab.tsx').includes('<ReasonModal') && f('UnwelcomeEntriesTab.tsx').includes('modal.confirm('), '撤回条目 / 批准条目都要二次确认')
assert(f('ReasonModal.tsx').includes('validateRequiredText(reason, maxLength)'), '原因必填')
assert(f('StaffDetailDrawer.tsx').includes('pendingRemoteDeletion'), '撤回后腾讯云侧删除待完成要有提示')
assert(f('StaffDetailDrawer.tsx').includes("t('faceMatch.enrollment.pendingRemoteDeletion')"), '待完成提示使用约定文案')

// ───────── 前端上传校验 ─────────
assert(/ENROLLMENT_PHOTO_MIN = 3/.test(f('logic.ts')) && /ENROLLMENT_PHOTO_MAX = 5/.test(f('logic.ts')), '登记照 3–5 张')
assert(/UNWELCOME_PHOTO_MIN = 1/.test(f('logic.ts')) && /UNWELCOME_PHOTO_MAX = 5/.test(f('logic.ts')), '名单参考照片 1–5 张')
assert(/PHOTO_MAX_BYTES = 5 \* MB/.test(f('logic.ts')) && /CONSENT_SCAN_MAX_BYTES = 10 \* MB/.test(f('logic.ts')), '照片 ≤5MB、同意书 ≤10MB')
assert(f('EnrollmentModal.tsx').includes('validatePhotoSet(') && f('ConsentModal.tsx').includes('validateConsentForm('), '提交前先在前端校验')
assert(f('PhotoPicker.tsx').includes('pickAcceptableFiles(') && f('PhotoPicker.tsx').includes('accept="image/jpeg,image/png"'), '选图时先筛选类型 / 张数 / 大小')
assert(f('UnwelcomeEntryModal.tsx').includes('validateUnwelcomeEntryForm(') && f('UnwelcomeEntryModal.tsx').includes('maxExpiryDay()'), '名单条目提交前校验，到期日限制在 12 个月内')

// ───────── 客流页：唯一访客数标注估算 ─────────
assert(f('TrafficTab.tsx').includes("t('faceMatch.traffic.estimateNotice')") && /<Alert type="warning"/.test(f('TrafficTab.tsx')), '客流页顶部常驻估算说明')
assert((f('TrafficTab.tsx').match(/faceMatch\.traffic\.uniqueVisitorEstimate/g) ?? []).length >= 1 && f('TrafficTab.tsx').includes('faceMatch.traffic.estimateTag'), '唯一访客数列头与统计卡都标注估算')
assert(f('TrafficTab.tsx').includes('getFaceTraffic(query.store'), '客流查询带门店')

// ───────── 表格、页面形态 ─────────
for (const [name, text] of Object.entries(files)) {
  assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(text) || name === 'TrafficTab.tsx', `${name} 不能直接从 antd 引入 Table（客流页只为 Table.Summary 引入）`)
}
const metricIds = Object.values(files).flatMap((text) => [...text.matchAll(/metricId="(system\.face-match\.[a-z0-9-]+)"/g)].map((match) => match[1]))
assert(metricIds.length >= 10 && new Set(metricIds).size === metricIds.length, 'metricId 必须是互不相同的稳定字符串')
assert(/<Alert type="warning" showIcon message=\{t\('faceMatch\.legalNotice'\)\}/.test(f('index.tsx')), '顶部常驻法律提示（闸门不能替代法律判断）')
assert(f('index.tsx').includes('registerPageMessages({ zh, en })'), '页面文案懒注册')
assert(f('index.tsx').includes("t('faceMatch.disabled.title')") && f('logic.ts').includes('export function isWriteBlocked'), '功能未开启时显示说明并禁用写入')
assert(f('shared.tsx').includes('storeOptionsFailed') && f('index.tsx').includes('getActiveStores()'), '分店选项来自分店接口，失败降级为手填')
assert(f('ProfilesTab.tsx').includes('CHECK_DEBOUNCE_MS') && f('ProfilesTab.tsx').includes('abortRef.current?.abort()'), '闸门检查防抖且可取消')
assert(f('ProfilesTab.tsx').includes('getGateReportFromError(error)'), '保存被 409 拒绝时展示 details 里的闸门报告')
assert(f('ProfilesTab.tsx').includes('profileToFormValues(profile, config.region)'), '跨境审查地域预填当前配置地域')
// 命中自动确认阈值：数字输入 + 客户端校验（输入框下提示，保存前先校验并阻止保存）；服务层保存时始终带该字段。
assert(f('ProfilesTab.tsx').includes('<InputNumber') && f('ProfilesTab.tsx').includes("t('faceMatch.profile.fields.unwelcomeAutoConfirmScore')"), '档案表单有命中自动确认阈值输入框')
assert((f('ProfilesTab.tsx').match(/validateAutoConfirmScore\(/g) ?? []).length >= 2, '阈值校验同时用于字段提示与保存前拦截')
assert(
  /const handleSave = async \(\) => \{[\s\S]*validateAutoConfirmScore\([\s\S]*?return[\s\S]*?updateFaceStoreProfile\(/.test(f('ProfilesTab.tsx')),
  '保存前先校验阈值，不合法就 return，不会走到保存接口',
)
assert(service.includes('buildProfileRequestBody(payload)') && (service.match(/buildProfileRequestBody\(payload\)/g) ?? []).length === 2, '保存与闸门检查共用请求体构造，始终带 unwelcomeAutoConfirmScore')
assert(existsSync(resolve(`${directory}/messages.zh.json`)) && existsSync(resolve(`${directory}/messages.en.json`)), '中英文页面消息文件都要有')

// ───────── 用词：固定使用「不受欢迎人员名单」 ─────────
for (const [name, text] of Object.entries(files)) {
  assert(!/黑名单|blacklist/i.test(text), `${name} 不得出现「黑名单」`)
}
assert(!/黑名单/.test(zhRaw) && !/blacklist/i.test(enRaw), '消息文件不得出现「黑名单」')
assert(zh.faceMatch.tabs !== undefined && (zh.faceMatch.tabs as Record<string, string>).unwelcome === '不受欢迎人员名单', '页签名固定为「不受欢迎人员名单」')

// ───────── 路由：懒加载，且不做 keepAlive ─────────
const routeStart = route.indexOf("path: '/system/face-match'")
assert(routeStart >= 0, '必须登记路由 /system/face-match')
const routeBlock = route.slice(routeStart, route.indexOf('element:', routeStart))
assert(route.includes("const FaceMatchPage = lazy(() => import('../pages/System/FaceMatch'))"), '页面必须懒加载，不进首屏包')
assert(routeBlock.includes('keepAlive: false'), '含图片 / 预览的页面不能 keepAlive')
assert(routeBlock.includes("accessKey: 'canAccessFaceMatch'") && routeBlock.includes("title: 'menu.faceMatch'"), '路由使用独立访问键与全局菜单键')

console.log('FaceMatch sourceContract.test: ok')
