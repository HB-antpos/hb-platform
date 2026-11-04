import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 商品陈列图页接线契约：测试不能 import 页面组件 / CSS，所以读源码静态核对关键约定。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/PosAdmin/Planogram'
const read = (path: string) => readFileSync(resolve(path), 'utf8')

const page = read(`${directory}/index.tsx`)
const logic = read(`${directory}/planogramLogic.ts`)
const formParts = read(`${directory}/formParts.tsx`)
const modelsTab = read(`${directory}/Models/ModelsTab.tsx`)
const shelfList = read(`${directory}/Models/ShelfModels.tsx`)
const shelfDrawer = read(`${directory}/Models/ShelfModelDrawer.tsx`)
const hookList = read(`${directory}/Models/HookModels.tsx`)
const hookDrawer = read(`${directory}/Models/HookModelDrawer.tsx`)
const modelList = read(`${directory}/Models/modelList.tsx`)
const specList = read(`${directory}/ProductSpecs/ProductSpecs.tsx`)
const specDrawer = read(`${directory}/ProductSpecs/ProductSpecDrawer.tsx`)
const shelfPreview = read(`${directory}/preview/ShelfFrontPreview.tsx`)
const hookPreview = read(`${directory}/preview/HookSidePreview.tsx`)
const dimPreview = read(`${directory}/preview/ProductDimPreview.tsx`)
const engineQty = read(`${directory}/engine/hookQty.ts`)
const engineGrid = read(`${directory}/engine/holeGrid.ts`)
const engineGeometry = read(`${directory}/engine/geometry.ts`)
const css = read(`${directory}/planogram.css`)
const service = read('src/services/planogramService.ts')
// 批 2：方案列表与设计器
const planList = read(`${directory}/PlanList/PlanList.tsx`)
const newPlanModal = read(`${directory}/PlanList/NewPlanModal.tsx`)
const planListLogic = read(`${directory}/PlanList/planListLogic.ts`)
const designerPage = read(`${directory}/Designer/index.tsx`)
const workspace = read(`${directory}/Designer/DesignerWorkspace.tsx`)
const designerHeader = read(`${directory}/Designer/DesignerHeader.tsx`)
const designerToolbar = read(`${directory}/Designer/DesignerToolbar.tsx`)
const canvas = read(`${directory}/Designer/BoardCanvas.tsx`)
const library = read(`${directory}/Designer/ProductLibraryPanel.tsx`)
const inspector = read(`${directory}/Designer/InspectorPanel.tsx`)
const designerStore = read(`${directory}/Designer/useDesignerStore.ts`)
const designerLayout = read(`${directory}/Designer/useDesignerLayout.ts`)
const designerLogic = read(`${directory}/Designer/designerLogic.ts`)
const conflictTextSource = read(`${directory}/Designer/conflictText.ts`)
const designerCss = read(`${directory}/Designer/designer.css`)
// 批 3：发布 / 版本 / 预览 / PDF 导出 / JSON 导入导出
const publishModal = read(`${directory}/Designer/PublishModal.tsx`)
const versionsDrawer = read(`${directory}/Designer/VersionsDrawer.tsx`)
const versionPreview = read(`${directory}/Designer/VersionPreview.tsx`)
const importModal = read(`${directory}/PlanList/ImportPlanModal.tsx`)
const exportModal = read(`${directory}/export/ExportPdfModal.tsx`)
const printPages = read(`${directory}/export/PlanogramPrintPages.tsx`)
const pdfLogic = read(`${directory}/export/sheetLogic.ts`)
const pdfImages = read(`${directory}/export/sheetImages.ts`)
const pdfGenerate = read(`${directory}/export/exportPlanogramPdf.ts`)
const pdfText = read(`${directory}/export/sheetText.ts`)
const downloadFile = read(`${directory}/export/downloadFile.ts`)
const transferLogic = read(`${directory}/transferLogic.ts`)
const pdfPagesCss = read(`${directory}/export/sheetPages.css`)
const exportModalCss = read(`${directory}/export/exportModal.css`)
const printUtilsSource = read('src/pages/Warehouse/StoreOrders/printUtils.ts')
const batch3Sources = [publishModal, versionsDrawer, versionPreview, importModal, exportModal, printPages, pdfLogic, pdfImages, pdfGenerate, pdfText, downloadFile, transferLogic]
const designerSources = [designerPage, workspace, designerHeader, designerToolbar, canvas, library, inspector, designerStore, designerLayout, designerLogic, conflictTextSource, publishModal, versionsDrawer, versionPreview]
const planSources = [planList, newPlanModal, planListLogic, importModal]
const tsxAndLogic = [page, logic, formParts, modelsTab, shelfList, shelfDrawer, hookList, hookDrawer, modelList, specList, specDrawer, ...planSources, ...designerSources, exportModal, printPages, pdfLogic, pdfImages, pdfGenerate, pdfText, downloadFile, transferLogic]
const allSources = [...tsxAndLogic, shelfPreview, hookPreview, dimPreview, engineQty, engineGrid, engineGeometry]

// 数据：只经 planogramService 调用约定的接口（附录 A 前缀）
assert(service.includes("'/api/react/v1/planogram'"), '服务必须请求 api/react/v1/planogram')
for (const source of allSources) {
  assert(!source.includes('/api/react/v1/planogram'), '页面必须经 planogramService 调用接口')
  assert(!/from ['"][^'"]*utils\/request['"]/.test(source), '页面不直接用 request，统一走 planogramService')
}

// 页面：保活、页签与型号子页签写进地址栏（replace）、文案随页面懒注册、样式随页面
assert(page.includes('useKeepAliveContext') && page.includes('!active || location.pathname !== PLANOGRAM_PATH'), '保活页面只在激活且地址是本页时同步地址栏')
assert(page.includes('{ replace: true }'), '地址栏同步用 replace，不堆历史')
assert(page.includes('registerPageMessages({ zh, en })') && page.includes("from './planogramMessages.zh.json'") && page.includes("from './planogramMessages.en.json'"), '页面文案随页面懒注册')
assert(page.includes("import './planogram.css'"), '样式随页面加载')
assert(page.includes("t('planogram.tabs.plans')") && page.includes("t('planogram.tabs.specs')") && page.includes("t('planogram.tabs.models')"), '页签：陈列方案 / 商品陈列参数 / 货架与挂钩型号')
assert(
  page.indexOf("key: 'plans'") >= 0 && page.indexOf("key: 'plans'") < page.indexOf("key: 'specs'") && page.indexOf("key: 'specs'") < page.indexOf("key: 'models'"),
  '「陈列方案」放在最前，其后是「商品陈列参数」「货架与挂钩型号」',
)
assert(logic.includes("tab === 'models' ? 'models' : tab === 'specs' ? 'specs' : 'plans'"), '默认页签是陈列方案（地址栏没有 tab 参数时）')
assert(!/敬请期待|Coming soon|占位/i.test(page + modelsTab + planList), '不放「敬请期待」之类的占位')
assert(page.includes('<PlanList canView={access.canViewPlanogram} canEdit={access.canEditPlanogram} canManage={access.canManagePlanogram}'), '方案列表按 canViewPlanogram / canEditPlanogram / canManagePlanogram 控制按钮')
assert(page.includes('setRefreshSignal') && planList.includes('refreshSignal'), '从设计器回到本页时刷新方案列表')
assert(planList.includes('reloadShelfModels()') && page.includes("if (tab === 'plans') setRefreshSignal"), '切回陈列方案页签与点新建方案时刷新货架型号目录（联调缺陷：保活页面里目录过期）')

// 权限：编辑类按 canEditPlanogram，型号维护 / 删除 / 清除参数按 canManagePlanogram；不按角色推断
assert(page.includes('access.canEditPlanogram') && page.includes('access.canManagePlanogram'), '页面按 canEditPlanogram / canManagePlanogram 取权限')
for (const source of tsxAndLogic) {
  assert(!/hasRole\(|isAdmin|isStoreManager/.test(source), '页面不能按角色推断按钮显隐')
}
assert(shelfList.includes('createLabel={canManage ?') && hookList.includes('createLabel={canManage ?'), '「新建」按钮只对型号维护权限显示')
assert(shelfList.includes('readOnly={!canManage}') && hookList.includes('readOnly={!canManage}'), '没有型号维护权限时抽屉只读')
assert(modelList.includes('if (!canManage)') && modelList.includes("t('planogram.common.view')"), '只读账号的行操作只有「查看」')
assert(shelfDrawer.includes('disabled={readOnly}') && hookDrawer.includes('disabled={readOnly}'), '只读时整个表单禁用')
// antd 的 Segmented 不读取 Form 的 disabled：每个 Segmented 都要显式传 disabled，否则只读账号仍能切换
for (const [name, source, expected] of [['货架抽屉', shelfDrawer, 'disabled={readOnly}'], ['挂钩抽屉', hookDrawer, 'disabled={readOnly}'], ['参数抽屉', specDrawer, 'disabled={!canEdit}']] as const) {
  const segmentedCount = source.split('<Segmented').length - 1
  const disabledCount = [...source.matchAll(/<Segmented[\s\S]*?\/>|<Segmented[\s\S]*?\n\s*options=/g)].filter((match) => match[0].includes(expected)).length
  assert(segmentedCount > 0 && disabledCount === segmentedCount, `${name} 的 ${segmentedCount} 个 Segmented 都要显式 ${expected}`)
}
assert(shelfDrawer.includes('if (!target || readOnly) return') && hookDrawer.includes('if (!target || readOnly) return'), '只读时不能提交')
assert(specDrawer.includes('disabled={!canEdit}') && specDrawer.includes('if (!target || !canEdit) return'), '没有编辑权限时参数抽屉只读且不能提交')
assert(specDrawer.includes('canManage && target?.hasSpec'), '「清除参数」只对型号维护权限且已设参数的商品显示')
assert(specList.includes('canEdit') && specList.includes("t('planogram.spec.addSpec')"), '未设参数的行有「补充参数」入口，受 canEdit 控制')

// 保存安全：挂钩目录没加载好不能保存（否则覆盖 / 默认挂钩会被当成不存在而清掉）；保存前剔除无效覆盖
assert(specDrawer.includes('disabled={!hooksReady}'), '挂钩型号目录没加载好时禁用保存')
assert(specDrawer.includes('specFormToDto('), '保存走 specFormToDto 剔除无效覆盖')
assert(specDrawer.includes("from '../engine/hookQty'"), '挂数换算与承重核对用引擎里的纯函数，不在组件里重写公式')
assert(!/Math\.floor\(/.test(specDrawer + specList), '组件里不再手写 ⌊⌋ 换算')

// 表格：只用 MeasuredTable，metricId 唯一
const metricIds = [shelfList, hookList, specList, specDrawer, planList]
  .flatMap((source) => [...source.matchAll(/metricId="([^"]+)"/g)].map((match) => match[1]))
assert(metricIds.length === 5, `每张表都要有 metricId：${metricIds.join(',')}`)
assert(new Set(metricIds).size === metricIds.length, `metricId 必须唯一：${metricIds.join(',')}`)
assert(metricIds.every((id) => id.startsWith('pos-admin.planogram.')), 'metricId 统一前缀 pos-admin.planogram.')
for (const source of [shelfList, hookList, specList, specDrawer, planList]) {
  assert(source.includes('<MeasuredTable'), '表格必须用 MeasuredTable')
}
for (const source of allSources) {
  assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(source), '不能直接从 antd 引入 Table')
}

// 商品缩略图：用现有 ProductListImage（缩略图 + 懒加载 + 点开预览原图）
assert(specList.includes("from '../../../../components/ProductListImage'") && specList.includes('<ProductListImage'), '列表缩略图用 ProductListImage')
assert(specDrawer.includes('<ProductListImage'), '抽屉头部商品图用 ProductListImage')

// 列表：状态筛选默认「全部」（停用的型号也要能看到并重新启用）；货号 / 条码 / 名称搜索；已设 / 未设筛选
assert(modelList.includes("useState<ModelStatusFilter>('all')"), '型号状态筛选默认全部')
assert(specList.includes("useState<ProductSpecStatusFilter>('all')") && specList.includes("'set'") && specList.includes("'unset'"), '参数状态筛选：全部 / 已设 / 未设')
assert(modelList.includes('SEARCH_DEBOUNCE_MS') && specList.includes('SEARCH_DEBOUNCE_MS'), '搜索防抖，不是每敲一个字请求一次')
assert(modelList.includes('effectivePage(paging, filterKey)') && specList.includes('effectivePage(paging, filterKey)'), '筛选条件变化时有效页码回到第 1 页（派生，不用 effect 二次请求）')

// 删除：被引用时按 PLANOGRAM_MODEL_IN_USE 提示改为停用
assert(modelList.includes("'PLANOGRAM_MODEL_IN_USE'") && modelList.includes("t('planogram.common.inUse.disable')"), '删除被引用的型号时提示改为停用')
assert(logic.includes("PLANOGRAM_CODE_DUPLICATE: 'planogram.errors.codeDuplicate'"), '编码重复有专门提示')
assert(formParts.includes("resolved.code === 'PLANOGRAM_CODE_DUPLICATE'"), '编码重复标在编码字段上')

// 抽屉：关闭即销毁表单（每次打开重新取初值）、不点遮罩误关、小数孔距不被取整
for (const drawer of [shelfDrawer, hookDrawer, specDrawer]) {
  assert(drawer.includes('destroyOnHidden') && drawer.includes('maskClosable={false}'), '抽屉关闭即销毁，且点遮罩不误关')
  assert(drawer.includes("isMobile ? '100%'"), '窄屏抽屉占满宽度')
  // 关闭动画期间（约 0.3 秒）重新打开：destroyOnHidden 还没销毁表单，必须靠 key 强制重建，否则残留上一次输入并带着新目标的 guid 提交
  assert(/const openKey = useOpenKey\(props\.target\)\s+return <[A-Za-z]+Inner key=\{openKey\} \{\.\.\.props\} \/>/.test(drawer), '整个抽屉按打开编号 key 重建（独立的表单实例），关闭动画期间重新打开也不会残留上一次的输入')
}
assert(specList.includes('setDrawer({ ...item })'), '参数列表每次打开都复制行对象，同一行再次打开也是新的 target')
assert(/name="holePitchXMm"[^>]*min=\{0\.5\}[^>]*max=\{200\}/.test(shelfDrawer) && /name="holePitchYMm"[^>]*min=\{0\.5\}[^>]*max=\{200\}/.test(shelfDrawer), '孔距范围 0.5–200（附录 A）')
assert(!/name="holePitch[XY]Mm"[^>]*precision/.test(shelfDrawer), '孔距不设小数位数限制（允许 25.4）')
for (const field of ['bayWidthMm', 'totalHeightMm', 'baseDepthMm', 'baseTopHeightMm', 'uprightWidthMm']) {
  assert(new RegExp(`name="${field}"[\\s\\S]{0,120}?precision=\\{0\\}`).test(shelfDrawer), `${field} 后端是 int 列，只能填整数`)
}
assert(!/name="(firstHoleLeftMm|firstHoleTopMm|holeDiameterMm)"[^>]*precision/.test(shelfDrawer), '首孔边距 / 孔径允许小数')
assert(/name="holeCols"[\s\S]*precision=\{0\}/.test(shelfDrawer), '孔数只能是整数')

// 预览组件可复用：纯 SVG、不碰 i18n / 请求（批 2 设计器直接复用）
for (const preview of [shelfPreview, hookPreview, dimPreview]) {
  assert(!/react-i18next|services\/|useTranslation/.test(preview), '预览组件不碰 i18n 与接口，文字由调用方传入')
  assert(preview.includes('<svg') && preview.includes('role="img"') && preview.includes('aria-label={ariaLabel}'), '预览是带读屏标签的 SVG')
}
assert(shelfPreview.includes("from '../engine/holeGrid'") && hookPreview.includes("from '../engine/hookQty'"), '预览几何用引擎里的同一套公式')
assert(shelfPreview.includes('useId'), '孔阵 pattern id 用 useId，同页多个预览不冲突')

// i18n：可见文案都走 i18n。去掉注释后，页面代码里不能再有中文字符（图示符号、× ⌊⌋ 等不算）
function stripComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/.*$/gm, '$1')
}
for (const [name, source] of [['index', page], ['logic', logic], ['formParts', formParts], ['modelsTab', modelsTab], ['shelfList', shelfList], ['shelfDrawer', shelfDrawer], ['hookList', hookList], ['hookDrawer', hookDrawer], ['modelList', modelList], ['specList', specList], ['specDrawer', specDrawer], ['shelfPreview', shelfPreview], ['hookPreview', hookPreview], ['dimPreview', dimPreview],
  ['importModal', importModal], ['publishModal', publishModal], ['versionsDrawer', versionsDrawer], ['versionPreview', versionPreview], ['exportModal', exportModal], ['printPages', printPages], ['pdfLogic', pdfLogic], ['pdfImages', pdfImages], ['pdfGenerate', pdfGenerate], ['pdfText', pdfText], ['downloadFile', downloadFile], ['transferLogic', transferLogic],
  ['planList', planList], ['newPlanModal', newPlanModal], ['planListLogic', planListLogic], ['designerPage', designerPage], ['workspace', workspace], ['designerHeader', designerHeader], ['designerToolbar', designerToolbar], ['canvas', canvas], ['library', library], ['inspector', inspector], ['designerStore', designerStore], ['designerLayout', designerLayout], ['designerLogic', designerLogic], ['conflictText', conflictTextSource], ['engineGeometry', engineGeometry]] as const) {
  const code = stripComments(source)
  const cjk = code.match(/[　-〿㐀-鿿＀-￯]+/g)
  assert(!cjk, `${name} 里不能写死中文文案：${cjk?.join(' / ')}`)
}

// 英文用词（方案 11 节）：Bay / Col / Row / Hook / Per hook
const en = JSON.stringify(JSON.parse(read(`${directory}/planogramMessages.en.json`)))
for (const word of ['Bay', 'Col', 'Row', 'Hook', 'Per hook']) {
  assert(en.includes(word), `英文文案里要用 ${word}`)
}

// ---------------------------------------------------------------------------
// 批 2：方案列表
// ---------------------------------------------------------------------------
assert(planList.includes("from '../../../../services/planogramService'") && planList.includes('listPlans(query, signal)'), '列表只经 planogramService 取数')
assert(planList.includes('effectivePage(paging, filterKey)') && planList.includes('SEARCH_DEBOUNCE_MS'), '列表搜索防抖、筛选变化回到第 1 页')
assert(planList.includes('shelfModelGuid: shelfGuid') && !/storeCode|StoreCode|分店/i.test(stripComments(planList + planListLogic)), '方案不绑定分店：只有货架型号 / 状态 / 关键字筛选，没有分店筛选')
assert(planList.includes('planRowActions(row, { canEdit, canManage, canView })'), '行操作按权限与状态决定（planRowActions）')
assert(planList.includes("t('planogram.plans.conflictTag'") && planList.includes('<Tag color="red"'), '「冲突 N」用红色标签')
assert(newPlanModal.includes('BAY_COUNT_RANGE.min') && newPlanModal.includes('GAP_RANGE.max') && newPlanModal.includes('...NEW_PLAN_DEFAULTS'), '新建弹窗：节数 1–12、间距默认 10')
assert(newPlanModal.includes('applyPlanogramFormError('), '新建失败把后端字段错误落到对应输入框')
assert(planListLogic.includes('NEW_PLAN_DEFAULTS') && planListLogic.includes('gapXMm: 10') && planListLogic.includes('gapYMm: 10'), '间距默认 10 / 10（决策 #9）')

// ---------------------------------------------------------------------------
// 批 2：设计器
// ---------------------------------------------------------------------------
// 接线：独立懒加载页、文案懒注册、保活路由上下文、权限、页签标题
assert(designerPage.includes('registerPageMessages({ zh, en })') && designerPage.includes("from '../planogramMessages.zh.json'") && designerPage.includes("from '../planogramMessages.en.json'"), '设计器可深链直达，自己也要注册页面文案（幂等）')
assert(designerPage.includes("import './designer.css'") && !page.includes('designer.css') && !page.includes('./Designer'), '设计器样式与代码只随设计器块加载，方案列表页不引用')
assert(designerPage.includes('useStableRouteContext()') && designerPage.includes('route?.params.guid'), '方案 guid 经保活路由上下文读取（保活隐藏后全局地址变化不会换成别人的）')
assert(designerPage.includes('access.canEditPlanogram') && !/hasRole\(|isAdmin|isStoreManager/.test(designerSources.join('\n')), '编辑能力看 canEditPlanogram，不按角色推断')
assert(designerPage.includes('useDynamicTabTitle(') && designerPage.includes("t('planogram.designer.tabTitle'"), '页签标题跟随方案名')
assert(designerPage.includes('createDesignerStore(request.data)'), '载入成功后创建本页专属 store（保活的多个方案互不干扰）')
// 数据：只经 service
assert(designerStore.includes("from '../../../../services/planogramService'") && library.includes("listProductSpecs"), '设计器的接口都经 planogramService')
assert(workspace.includes('duplicatePlan(') && workspace.includes('listShelfModels('), '另存为 / 货架型号目录走 service')
// 离页保护：useBlocker + 保活隐藏时不拦别的页面 + 浏览器关闭确认
assert(workspace.includes('useBlocker('), '站内离页用 useBlocker 拦截')
assert(workspace.includes('stateRef.current.active && stateRef.current.dirty') && workspace.includes('currentLocation.pathname === ownPathRef.current'), '只在本页激活、有未保存改动、且是从本页离开时才拦（保活隐藏的设计器不拦别的页面之间的跳转）')
assert(workspace.includes("'beforeunload'"), '关闭 / 刷新浏览器标签走原生确认')
assert(workspace.includes('Modal.confirm(') && workspace.includes('blocker.proceed') && workspace.includes('blocker.reset'), '拦截后弹确认：离开 / 继续编辑')
// 状态：每个页面实例一个 store；撤销栈 50；保存带 rowVersion；失败不丢本地改动；并发冲突提示重新载入
assert(designerStore.includes("from 'zustand/vanilla'") && designerStore.includes('createStore<DesignerStore>()') && !/\bcreate<|from 'zustand'\s*$/m.test(designerStore.replace("import { useStore } from 'zustand'", '')), '用 zustand vanilla 为每个页面实例创建 store，不是模块级单例')
assert(designerLogic.includes('export const HISTORY_LIMIT = 50') && designerStore.includes('pushHistory(state.past, state.doc)'), '撤销 / 重做快照栈上限 50')
assert(designerStore.includes('buildSaveInput(sentDoc, state.rowVersion)'), '保存带 rowVersion')
assert(/catch \(error\) \{\s*set\(\{ saveStatus: 'idle' \}\)/.test(designerStore) && !/catch \(error\) \{[^}]*doc:/.test(designerStore), '保存失败只复位保存状态，不动 doc（本地改动不丢）')
assert(designerStore.includes("resolved.code === 'PLANOGRAM_STALE'") && designerStore.includes('set({ stale: true })'), 'PLANOGRAM_STALE：标记方案已被他人更新')
assert(workspace.includes("open={stale}") && workspace.includes("t('planogram.designer.stale.reload')"), 'PLANOGRAM_STALE 提示并提供重新载入')
assert(designerStore.includes('fromSave: true') && designerLayout.includes('serverResult.fromSave'), '冲突：本地实时复算为准，保存后以服务端返回为准')
assert(workspace.includes('outcome.differs') && workspace.includes("t('planogram.designer.messages.saveDiff'"), '保存后本地与服务端口径不一致时提示差异')
assert(workspace.includes("header.status === 2") && workspace.includes('const readOnly = !canEdit || archived || narrow'), '已归档 / 无编辑权限 / 窄屏时只读')
assert(designerStore.includes('if (guard()) return') && designerStore.includes('setReadOnly'), 'store 在只读时拒绝一切编辑动作')
assert(designerStore.includes('specChanged') && canvas.includes('view.item.specChanged') && inspector.includes('item.specChanged'), '「参数已变」标记在画布与右栏都有显示')
// 窄屏
assert(workspace.includes('DESIGNER_MIN_EDIT_WIDTH') && workspace.includes('window.matchMedia') && workspace.includes("t('planogram.designer.narrow')"), '窄屏（< 1100）只读并提示「请在更宽屏幕上编辑」')
assert(designerLogic.includes('DESIGNER_MIN_EDIT_WIDTH = 1100'), '设计器最小可编辑宽度 1100')
// 画布：纯 SVG、几何来自引擎、指针捕获拖动、Ctrl 滚轮缩放
assert(canvas.includes('<svg') && !canvas.includes('<canvas'), '画布是纯 SVG')
assert(canvas.includes("from '../engine/geometry'") && canvas.includes('computePlacement(') && canvas.includes('buildDropHint('), '画布几何与落点判定复用引擎')
assert(!/Math\.floor\(/.test(canvas) || !/armLengthMm\s*-\s*[a-z.]*tipReserveMm/.test(canvas), '画布不自己重写每钩挂数公式')
assert(canvas.includes('setPointerCapture(') && canvas.includes('onPointerMove={handlePointerMove}') && canvas.includes('onPointerUp={handlePointerUp}'), '画布内拖动用 pointer capture（同 IntervalEditor 写法）')
assert(canvas.includes("addEventListener('wheel'") && canvas.includes('passive: false') && canvas.includes('event.ctrlKey || event.metaKey'), 'Ctrl / ⌘ + 滚轮缩放，用非被动监听阻止页面缩放')
assert(canvas.includes('MIN_DRAG') === false && canvas.includes('DRAG_THRESHOLD_PX'), '拖动有位移阈值，点选不会误触发拖动')
assert(/<g[\s\S]{0,300}role="button"/.test(canvas) && canvas.includes('<title>{title}</title>'), '画布里的商品有读屏名称与悬浮提示')
assert(canvas.includes('tabIndex={0}') && canvas.includes('aria-label={labels.group}'), '画布可聚焦、有读屏标签（键盘操作的前提）')
// 左栏：dnd-kit 拖入、只列已设参数的商品、未设的灰显并给「补充参数」入口
assert(library.includes("from '@dnd-kit/core'") && library.includes('useDraggable(') && workspace.includes('DragOverlay') && workspace.includes('<DndContext'), '左栏卡片用 dnd-kit 拖到画布，DragOverlay 跟手')
assert(library.includes("useState<LibraryStatus>('set')") && library.includes("status: LibraryStatus"), '商品库默认只列已设参数的商品')
assert(library.includes('item.hasSpec') && library.includes("t('planogram.designer.library.addSpec')") && library.includes('onGoSpecs'), '未设参数的商品灰显并给「补充参数」入口')
assert(library.includes('<ProductListImage'), '商品缩略图用 ProductListImage')
assert(library.includes('aria-label={t(\'planogram.designer.library.addAria\'') || library.includes("t('planogram.designer.library.addAria'"), '拖拽的键盘替代：「＋」按钮 / 回车添加到第一个空位')
assert(workspace.includes('pickHookForProduct(item.spec?.defaultHookGuid, activeHooks, currentHookGuid)'), '拖入默认挂钩：参数的默认挂钩，否则左栏当前挂钩')
// 工作区：吸附、向右复制、整行均分、键盘、撤销重做
assert(workspace.includes('findCopyRightSlot(') && workspace.includes('distributeRow(') && workspace.includes('findFirstFreeSlot('), '向右复制 / 整行均分 / 找空位都走 designerLogic')
assert(workspace.includes("event.key === 'Delete'") && workspace.includes('ArrowLeft') && workspace.includes("closest('input, textarea, select"), 'Delete 与方向键移动一个孔位；输入框里按键不被抢走')
assert(workspace.includes("mod && event.key.toLowerCase() === 'z'") && workspace.includes("mod && event.key.toLowerCase() === 's'"), '撤销 / 重做 / 保存快捷键')
assert(workspace.includes('role="status" aria-live="polite"'), '键盘移动 / 添加后用 aria-live 播报')
assert(designerToolbar.includes('MIN_ZOOM') && designerToolbar.includes('MAX_ZOOM') && designerLogic.includes('MIN_ZOOM = 25') && designerLogic.includes('MAX_ZOOM = 100'), '缩放 25%–100%')
assert(designerToolbar.includes("t('planogram.designer.toolbar.snapHole')") && designerToolbar.includes("t('planogram.designer.toolbar.snapFree')"), '吸附：孔位 / 自由')
assert(designerHeader.includes("t('planogram.designer.saveAs')") && designerHeader.includes('canEdit ?'), '另存为只对有编辑权限的账号显示')
// 间距输入：方案信息里的左右 / 上下间距，范围 0–200
assert(inspector.includes('MAX_GAP_MM') && inspector.includes('gapXMm: value') && inspector.includes('gapYMm: value'), '右栏方案信息里可改左右 / 上下间距')
// 样式：窄屏、减少动画、焦点
assert(designerCss.includes('@media (max-width: 1099px)') && designerCss.includes('prefers-reduced-motion'), '设计器样式有窄屏与减少动画处理')
assert(!/outline\s*:\s*(none|0)/.test(designerCss), '设计器不能去掉焦点轮廓')
assert(designerCss.includes(':focus-visible'), '设计器交互元素有焦点态')
// 几何引擎：纯函数，不碰 React / 请求
assert(!/from 'react'|services\/|react-i18next/.test(engineGeometry + designerLogic), '引擎与设计器纯逻辑不依赖 React / i18n / 请求')
for (const source of [css, designerCss]) {
  assert(!source.includes(['window', 'location', 'reload'].join('.')), '样式里不能出现自动 hard reload 字面量')
}

// ---------------------------------------------------------------------------
// 批 3：发布 / 版本历史 / 预览 / PDF 导出 / JSON 导入导出
// ---------------------------------------------------------------------------
// 接口只经 service（附录 C 路径 / 方法 / 请求体）
for (const needle of ["planogramUrl(`planograms/${encodeURIComponent(guid)}/publish`)", "planogramUrl(`planograms/${encodeURIComponent(guid)}/versions`)", "/versions/${encodeURIComponent(String(version))}", "planogramUrl(`planograms/${encodeURIComponent(guid)}/export`)", "planogramUrl('planograms/import')"]) {
  assert(service.includes(needle), `服务里要有接口：${needle}`)
}
assert(service.includes('data: { rowVersion }'), '发布请求体是 { rowVersion }')
assert(service.includes('hasUnpublishedChanges: r.hasUnpublishedChanges === true'), 'hasUnpublishedChanges 归一化：缺省按 false')
for (const source of batch3Sources) {
  assert(!source.includes('/api/react/v1/planogram'), '批 3 页面代码必须经 planogramService 调用接口')
  assert(!/from ['"][^'"]*utils\/request['"]/.test(source), '批 3 页面代码不直接用 request')
}
assert(workspace.includes('publishPlan(') && workspace.includes('exportPlan(') && workspace.includes('getPlanVersion('), '设计器的发布 / 导出 JSON / 版本预览都走 service')
assert(versionsDrawer.includes('listPlanVersions(') && importModal.includes('importPlan(') && exportModal.includes('getPlanVersion(') && exportModal.includes('getPlan('), '版本列表 / 导入 / 导出弹窗的数据都走 service')

// 权限：发布 / 归档类 = Manage（canManagePlanogram）且未归档；导出 / 导入 JSON = Edit；导出 PDF 与版本 = View
assert(designerPage.includes('canManage={access.canManagePlanogram}') && designerPage.includes('canView={access.canViewPlanogram}') && designerPage.includes('canEdit={access.canEditPlanogram}'), '设计器入口传 Manage / View / Edit 三种权限')
assert(workspace.includes('canPublish={canManage && !archived}'), '发布按钮：仅 Manage 且未归档')
assert(workspace.includes('canView={canView}'), '版本 / 导出 PDF 按查看权限')
assert(designerHeader.includes('canPublish && !previewing') && designerHeader.includes('{canView ? <Button icon={<HistoryOutlined />}') && designerHeader.includes('{canView ? <Button icon={<FilePdfOutlined />}'), '页头：发布看 canPublish，版本 / 导出 PDF 看 canView')
assert(designerHeader.includes("canEdit ? { key: 'json'") && designerHeader.includes("canView ? { key: 'versions'") && designerHeader.includes("canView ? { key: 'pdf'"), '「⋯」菜单：导出 JSON（Edit）、版本、导出 PDF（View）')
assert(planList.includes('{canEdit ? (\n            <>\n              <input') && planList.includes("t('planogram.transfer.import')"), '「导入方案 JSON」只对有编辑权限的账号显示')
assert(planList.includes('actions.canExportPdf') && planListLogic.includes('canExportPdf: permissions.canView !== false'), '列表「导出 PDF」按查看权限')
assert(workspace.includes('previewing') && designerHeader.includes('readOnly || previewing'), '预览历史版本时隐藏撤销 / 重做 / 保存')

// 发布流程
assert(workspace.includes('if (!dirty) {') && workspace.includes("t('planogram.publish.saveFirstTitle')") && workspace.includes('if (await handleSave())'), '有未保存改动先提示保存，保存成功再弹发布确认')
assert(workspace.includes('publishBlockedReason') && workspace.includes("t('planogram.publish.blockedConflicts'") && workspace.includes("t('planogram.publish.blockedEmpty')"), '有 error 级冲突 / 没有商品时禁用发布并给出原因')
assert(designerHeader.includes('publishDisabledReason !== null') && designerHeader.includes('<Tooltip title={publishDisabledReason}>'), '禁用的发布按钮用悬浮提示说明原因')
for (const code of ['PLANOGRAM_HAS_CONFLICTS', 'PLANOGRAM_EMPTY', 'PLANOGRAM_ALREADY_PUBLISHED', 'PLANOGRAM_STALE', 'PLANOGRAM_ARCHIVED']) {
  assert(workspace.includes(`case '${code}'`), `发布按错误码处理 ${code}`)
}
assert(workspace.includes('storeApi.getState().markStale()') && workspace.includes('onOk={onReload}'), '发布遇到 PLANOGRAM_STALE 沿用批 2 的重新载入流程')
assert(workspace.includes('storeApi.getState().applyServerDetail(detail)') && designerStore.includes('applyServerDetail:'), '发布成功后刷新详情（不动编辑内容）')
assert(publishModal.includes('MAX_LISTED') && publishModal.includes('onLocate(entry.guid)') && publishModal.includes("t('planogram.publish.willCreate'"), '发布确认显示「将生成版本 vN」，冲突列表可「定位」')
for (const key of ['sku', 'hooks', 'units', 'retail', 'fill']) assert(publishModal.includes(`planogram.publish.stat.${key}`), `发布统计：${key}`)
assert(designerHeader.includes('planStatusView(header)') && planListLogic.includes("'planogram.plans.status.publishedDirty'") && planListLogic.includes("'planogram.plans.status.publishedVersion'"), '页头状态标签：草稿 / 已发布 vN / 已发布 vN · 有未发布修改')
assert(planList.includes('status.hasUnpublishedChanges') && planList.includes("t('planogram.plans.unpublishedTag'"), '列表状态列同步「有未发布修改」')
assert(!/publishPlan\(/.test(stripComments(planList + planListLogic)), '「发布」入口只在设计器里，列表不放')

// 版本历史与只读预览
assert(workspace.includes('<VersionPreview') && versionPreview.includes('<BoardCanvas') && versionPreview.includes('readOnly\n') && versionPreview.includes('buildPdfModel(sourceFromSnapshot(snapshot))'), '预览：用 getPlanVersion 的快照，在同一个 BoardCanvas 只读模式里显示')
assert(workspace.includes('hidden={previewing}') && workspace.includes('setPreview(null)'), '预览期间设计器主体只是被隐藏（草稿状态原样保留），可返回当前草稿')
assert(workspace.includes('if (previewing) return'), '预览时编辑快捷键不作用到隐藏的草稿')
assert(versionsDrawer.includes("t('planogram.versions.exportPdf')") && versionsDrawer.includes("t('planogram.versions.preview')"), '版本抽屉每行：预览 / 导出 PDF')

// PDF：动态 import、复用 printUtils、不新增依赖、不进首屏
for (const source of [...allSources, ...batch3Sources, exportModal]) {
  assert(!/from ['"](html2canvas|jspdf)['"]/.test(source), 'PDF 依赖（html2canvas / jspdf）不能被页面代码静态引入')
  assert(!/(?:require|import)\(['"](html2canvas|jspdf)['"]\)/.test(source), '页面代码不直接动态引入 html2canvas / jspdf，统一走 printUtils')
}
assert(pdfGenerate.includes("from '../../../Warehouse/StoreOrders/printUtils'") && pdfGenerate.includes('downloadElementPagesAsPdf(') && pdfGenerate.includes('printElementPagesAsPdf('), 'PDF 出图复用 printUtils 的分页下载 / 打印')
assert(printUtilsSource.includes("import('html2canvas')") && printUtilsSource.includes("import('jspdf')") && !/^import .* from ['"](html2canvas|jspdf)['"]/m.test(printUtilsSource), 'printUtils 里 html2canvas / jspdf 都是动态 import')
assert(printUtilsSource.includes('export interface PdfPageFormat') && printUtilsSource.includes('export function resolvePdfPageGeometry'), 'printUtils 支持纸张规格（默认 A4 纵向不变）')
assert(workspace.includes("lazy(() => import('../export/ExportPdfModal'))") && planList.includes("lazy(() => import('../export/ExportPdfModal'))") && workspace.includes("lazy(() => import('./VersionPreview'))"), '导出弹窗 / 版本预览是独立异步块，不进设计器 / 列表首次加载')
assert(!page.includes('./export/') && !page.includes('PlanogramPrintPages'), '方案页入口不直接引用 PDF 页面代码')
assert(printPages.includes('BoardPrintSvg') && canvas.includes('export function BoardPrintSvg') && canvas.includes('export function buildItemViews') && canvas.includes('print?: ItemPrintOptions'), '版面图打印模式复用 BoardCanvas 的渲染函数（几何同一套），不另写一份')
assert(!/computePlacement\(|holeCenter\(/.test(printPages + pdfLogic), 'PDF 页面与逻辑里不重写几何')
assert(pdfLogic.includes('sortChecklistItems(items)') && workspace.includes('sortChecklistItems(doc.items)'), '序号 = 节 → 行 → 列排序，设计器与 PDF 共用同一个排序函数')
assert(pdfLogic.includes('PDF_PAGE_SELECTOR') && pdfPagesCss.includes('.planogram-pdf-page') && printPages.includes('className="planogram-pdf-page"'), '页面根元素 .planogram-pdf-page')
assert(!/var\(--|prefers-color-scheme|\.dark/.test(pdfPagesCss), '打印页样式不依赖应用的 CSS 变量 / 暗色主题（永远白底黑字）')
assert(pdfPagesCss.includes('background: #fff'), '打印页白底')
assert(exportModal.includes("pageFilter={(page) => page.pageNo === 1}") && exportModal.includes('createPortal(') && exportModal.includes('planogram-pdf-stage'), '缩略图只渲染第 1 页；出图时才把全部页面挂到屏幕外容器')
assert(exportModalCss.includes('.planogram-pdf-stage') && /left:\s*-\d{4,}px/.test(exportModalCss), '出图容器在屏幕外')
assert(exportModal.includes('tRef.current') && exportModal.includes('abortRef.current?.abort()'), '界面语言变化不会重启出图；关闭弹窗中止图片加载')
assert(exportModal.includes('DEFAULT_PDF_OPTIONS') && pdfLogic.includes("paper: 'a3-landscape'") && pdfLogic.includes("scale: 'auto'") && pdfLogic.includes("lang: 'zh'") && pdfLogic.includes('images: false') && pdfLogic.includes('barcode: false') && pdfLogic.includes('checkColumn: false'), '导出默认：A3 横向 / 自动适配 / 中文；实拍图、条码、检查栏默认不勾')
assert(exportModal.includes('disabled: dirty') && exportModal.includes("t('planogram.export.dirtyHint')"), '有未保存改动时「当前已保存版本」置灰并提示先保存')
assert(exportModal.includes("t('planogram.export.conflictWarn'") && printPages.includes("'planogram.pdf.validation.conflicts'"), '有冲突时弹窗提示「导出会在图上标红」，PDF 校验行写明冲突数')
assert(exportModal.includes('buildPdfFileName(') && exportModal.includes('normalizePdfFileName('), '文件名按 Planogram_<方案>_v<版本>_<语言>_<日期>.pdf 生成且可编辑')
// 实拍图：经图片代理（getImageDownloadCandidates）、8 秒超时、回退色块
assert(pdfImages.includes('getImageDownloadCandidates') && pdfImages.includes("import('../../../../services/exportService')") && pdfImages.includes('PDF_IMAGE_TIMEOUT_MS = 8000'), '实拍图经 exportService.getImageDownloadCandidates（image-proxy）加载，超时 8 秒')
assert(exportModal.includes('loadPdfImages(') && exportModal.includes('setJob(') && exportModal.indexOf('loadPdfImages(') < exportModal.indexOf('setJob({ mode'), '等图片加载完再渲染页面 / 截图')
// 英文用词与文案：PDF 文案全部来自 planogram.pdf.*，按导出语言取词
assert(pdfText.includes('createPdfTranslator(') && pdfText.includes('planogramMessages.en.json') && pdfText.includes('planogramMessages.zh.json'), 'PDF 按导出语言取词（读消息文件，与界面语言无关）')
assert(!/useTranslation|i18n\.t\(/.test(printPages + pdfLogic), 'PDF 页面不用界面语言的 t()')
const zhPdf = JSON.stringify((JSON.parse(read(`${directory}/planogramMessages.zh.json`)) as { planogram: { pdf: unknown } }).planogram.pdf)
const enPdf = JSON.stringify((JSON.parse(read(`${directory}/planogramMessages.en.json`)) as { planogram: { pdf: unknown } }).planogram.pdf)
for (const word of ['Bay', 'Col/Row', 'Hook', 'Per hook']) assert(enPdf.includes(word), `英文 PDF 文案用 ${word}`)
assert(zhPdf.includes('商品陈列图') && !/[㐀-鿿]/.test(enPdf), '英文 PDF 文案里没有中文')

// JSON 导入导出
assert(transferLogic.includes('MAX_IMPORT_BYTES = 5 * 1024 * 1024'), '导入文件上限 5 MB')
assert(planList.includes("accept=\".json,application/json\"") && planList.includes('parseImportFile(') && planList.includes("'planogram.transfer.errors.notJson'") && planList.includes("'planogram.transfer.errors.tooLarge'"), '导入：选文件 → 解析，非 JSON / 过大给出明确提示')
assert(importModal.includes('isDuplicatePlanName(') && importModal.includes("t('planogram.transfer.nameDuplicate')") && importModal.includes('duplicate ||'), '名称重名让用户改名（重名时禁用导入）')
assert(importModal.includes("'PLANOGRAM_IMPORT_UNRESOLVED'") && importModal.includes('extractImportUnresolved(') && importModal.includes('groupUnresolved('), '导入失败按 details 列出缺失的货架 / 挂钩 / 商品编码')
assert(workspace.includes('exportJsonFileName(') && transferLogic.includes('.planogram.json') && workspace.includes('downloadTextFile('), '导出 JSON：<方案名>.planogram.json')
assert(planList.includes('navigate(planogramDesignerPath(detail.header.guid))'), '导入成功直接打开新方案')

// 全局契约（noAutomaticReload）会扫描 pages 下所有文件里的该字面量，所以这里拆开拼接。
for (const source of [...allSources, css]) {
  assert(!source.includes(['window', 'location', 'reload'].join('.')), '页面不得自动 hard reload')
}

// 样式：窄屏与减少动画偏好；预览有主题之外的固定色，但交互元素不自绘焦点
assert(css.includes('@media (max-width: 767px)') && css.includes('prefers-reduced-motion'), '必须有窄屏与减少动画处理')
assert(!/outline\s*:\s*(none|0)/.test(css), '不能去掉焦点轮廓')

console.log('sourceContract.test: ok')
