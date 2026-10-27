import type { ApiResponse } from '../../types/api'
import request, { unwrapApiData } from '../../utils/request'
import type {
  StoreHelpFaqGroup,
  StoreHelpFaqLanguage,
  StoreHelpFaqQuestion,
  StoreHelpFaqRelated,
  StoreHelpFaqTab,
} from './faqContent'

/**
 * FAQ 问答数据层：优先读取后端公开接口 `GET /api/support/faq`（内容在数据库里维护），
 * 接口失败 / 超时 / 结构不合法时回退到打包进前端的 `faqSnapshot.json`，保证页面任何时候都有内容。
 * 快照用动态 import 引入，单独成 chunk，只有兜底时才会下载，不占页面首屏体积。
 */

export type StoreHelpFaqGroupsByTab = Record<StoreHelpFaqTab, StoreHelpFaqGroup[]>

export type StoreHelpFaqSource = 'api' | 'snapshot'

export type StoreHelpFaqLoadResult = {
  groups: StoreHelpFaqGroupsByTab
  source: StoreHelpFaqSource
}

export const STORE_HELP_FAQ_API_PATH = '/api/support/faq'
export const STORE_HELP_FAQ_TIMEOUT_MS = 5000

type Localized<T> = Record<StoreHelpFaqLanguage, T>
type JsonRecord = Record<string, unknown>

function isRecord(value: unknown): value is JsonRecord {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function fail(path: string): never {
  throw new Error(`FAQ 数据结构不合法: ${path}`)
}

function readText(value: unknown, path: string, options: { required?: boolean } = {}): string {
  if (typeof value !== 'string' || (options.required && !value.trim())) {
    fail(path)
  }
  return value
}

function readTextList(value: unknown, path: string): string[] {
  if (!Array.isArray(value) || value.some((item) => typeof item !== 'string')) {
    fail(path)
  }
  return value as string[]
}

function readRelatedList(value: unknown, path: string): StoreHelpFaqRelated[] {
  if (!Array.isArray(value)) {
    fail(path)
  }
  return value.map((item, index) => {
    if (!isRecord(item)) {
      fail(`${path}[${index}]`)
    }
    return { title: readText(item.title, `${path}[${index}].title`), body: readText(item.body, `${path}[${index}].body`) }
  })
}

/** 中英两份都必须存在；缺任何一种语言都视为不合法，避免页面切语言后出现空白。 */
function readLocalized<T>(value: unknown, path: string, read: (item: unknown, itemPath: string) => T): Localized<T> {
  if (!isRecord(value)) {
    fail(path)
  }
  return { en: read(value.en, `${path}.en`), zh: read(value.zh, `${path}.zh`) }
}

/** 可选字段：缺省或 null（后端序列化空值）都当作没有；一旦出现就必须结构正确。 */
function readOptional<T>(value: unknown, read: (item: unknown) => T): T | undefined {
  return value === undefined || value === null ? undefined : read(value)
}

function readQuestion(value: unknown, path: string): StoreHelpFaqQuestion {
  if (!isRecord(value)) {
    fail(path)
  }
  const question: StoreHelpFaqQuestion = {
    id: readText(value.id, `${path}.id`, { required: true }),
    title: readLocalized(value.title, `${path}.title`, (item, itemPath) => readText(item, itemPath, { required: true })),
    where: readLocalized(value.where, `${path}.where`, (item, itemPath) => readText(item, itemPath)),
    steps: readLocalized(value.steps, `${path}.steps`, readTextList),
  }
  // 只拷贝已知字段，后端以后多出来的字段不会混进页面数据
  const tips = readOptional(value.tips, (item) => readLocalized(item, `${path}.tips`, readTextList))
  const rules = readOptional(value.rules, (item) => readLocalized(item, `${path}.rules`, readTextList))
  const permissions = readOptional(value.permissions, (item) => readTextList(item, `${path}.permissions`))
  const related = readOptional(value.related, (item) => readLocalized(item, `${path}.related`, readRelatedList))
  if (tips) question.tips = tips
  if (rules) question.rules = rules
  if (permissions) question.permissions = permissions
  if (related) question.related = related
  return question
}

function readGroups(value: unknown, path: string, seenQuestionIds: Set<string>): StoreHelpFaqGroup[] {
  if (!Array.isArray(value)) {
    fail(path)
  }
  const groups: StoreHelpFaqGroup[] = []
  value.forEach((groupValue, groupIndex) => {
    const groupPath = `${path}[${groupIndex}]`
    if (!isRecord(groupValue)) {
      fail(groupPath)
    }
    if (!Array.isArray(groupValue.questions)) {
      fail(`${groupPath}.questions`)
    }
    const questions = groupValue.questions.map((questionValue, questionIndex) => {
      const question = readQuestion(questionValue, `${groupPath}.questions[${questionIndex}]`)
      // 锚点与 React key 都依赖问题 id 全局唯一
      if (seenQuestionIds.has(question.id)) {
        fail(`${groupPath}.questions[${questionIndex}].id 重复: ${question.id}`)
      }
      seenQuestionIds.add(question.id)
      return question
    })
    // 分组下没有问题（例如全部未发布）就不展示空标题
    if (questions.length > 0) {
      groups.push({
        id: readText(groupValue.id, `${groupPath}.id`, { required: true }),
        title: readLocalized(groupValue.title, `${groupPath}.title`, (item, itemPath) => readText(item, itemPath, { required: true })),
        questions,
      })
    }
  })
  return groups
}

/**
 * 校验并规整接口返回的 `data`（`{ version, tabs: { pos, mobile } }`）。
 * 结构不合法或一道题都没有时抛错，由调用方回退到快照。
 */
export function parseStoreHelpFaqPayload(payload: unknown): StoreHelpFaqGroupsByTab {
  if (!isRecord(payload) || !isRecord(payload.tabs)) {
    fail('tabs')
  }
  const seenQuestionIds = new Set<string>()
  const groups = {
    pos: readGroups(payload.tabs.pos, 'tabs.pos', seenQuestionIds),
    mobile: readGroups(payload.tabs.mobile, 'tabs.mobile', seenQuestionIds),
  }
  if (seenQuestionIds.size === 0) {
    fail('没有任何问题')
  }
  return groups
}

/**
 * 快照是构建时从种子导出的可信数据，只做类型转换，不再逐项校验（页面 chunk 里不放校验开销，
 * 数据正确性由 storeHelpFaq.test.ts 对快照做完整断言）。
 */
export function snapshotToStoreHelpFaqGroups(snapshot: unknown): StoreHelpFaqGroupsByTab {
  const tabs = (snapshot as { tabs: StoreHelpFaqGroupsByTab }).tabs
  return { pos: tabs.pos, mobile: tabs.mobile }
}

/** 动态加载兜底快照（独立 chunk，只在接口失败时才会下载）。 */
export async function loadStoreHelpFaqSnapshot(): Promise<StoreHelpFaqGroupsByTab> {
  const module = await import('./faqSnapshot.json')
  // 不同打包器对 JSON 模块的导出形态不同：有 default 取 default，否则整个模块就是 JSON 对象
  return snapshotToStoreHelpFaqGroups((module as { default?: unknown }).default ?? module)
}

/**
 * 请求公开 FAQ 接口并解包 ApiResponse。
 * 这是免登录页面：必须带 skipAuthRedirect，否则 401 会触发刷新令牌并跳转登录页。
 */
export async function fetchStoreHelpFaqPayload(signal: AbortSignal): Promise<unknown> {
  const response = await request.get<ApiResponse<unknown>>(STORE_HELP_FAQ_API_PATH, { signal, skipAuthRedirect: true })
  return unwrapApiData(response)
}

export type LoadStoreHelpFaqGroupsOptions = {
  /** 请求接口并返回 `data`；应响应 signal 的取消。 */
  fetcher?: (signal: AbortSignal) => Promise<unknown>
  /** 接口不可用时的兜底数据来源。 */
  loadSnapshot?: () => Promise<StoreHelpFaqGroupsByTab>
  timeoutMs?: number
}

/**
 * 加载 FAQ 分组：接口成功且结构合法 → `api`；接口抛错 / 超时 / 结构不合法 → `snapshot`。
 * 只有快照也加载失败时才会抛错（例如离线且分块未缓存），由页面显示重试。
 */
export async function loadStoreHelpFaqGroups(options: LoadStoreHelpFaqGroupsOptions = {}): Promise<StoreHelpFaqLoadResult> {
  const {
    fetcher = fetchStoreHelpFaqPayload,
    loadSnapshot = loadStoreHelpFaqSnapshot,
    timeoutMs = STORE_HELP_FAQ_TIMEOUT_MS,
  } = options

  const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout> | undefined
  try {
    // 超时既取消请求，也用 race 兜住不响应 signal 的 fetcher
    const timeout = new Promise<never>((_resolve, reject) => {
      timer = setTimeout(() => {
        controller.abort()
        reject(new Error(`FAQ 接口超过 ${timeoutMs}ms 未响应`))
      }, timeoutMs)
    })
    const payload = await Promise.race([fetcher(controller.signal), timeout])
    return { groups: parseStoreHelpFaqPayload(payload), source: 'api' }
  } catch {
    return { groups: await loadSnapshot(), source: 'snapshot' }
  } finally {
    if (timer !== undefined) {
      clearTimeout(timer)
    }
  }
}
