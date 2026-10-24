import type {
  StoreHelpFaqGroup,
  StoreHelpFaqLanguage,
  StoreHelpFaqQuestion,
  StoreHelpFaqTab,
} from './faqContent'

export type StoreHelpFaqControlVariant = 'k' | 'p' | 't' | 'd' | 'i' | 'g' | 'tab'

export type StoreHelpFaqSegment =
  | { kind: 'text'; text: string }
  | { kind: 'control'; variant: StoreHelpFaqControlVariant; label: string; secondary?: string }

const CONTROL_PATTERN = /\[(k|p|t|d|i|g|tab):([^\]|]+)(?:\|([^\]]+))?\]/g

/**
 * 把 `[类型:English|中文]` 记号拆成文本段与界面元素段。
 * 中文页主显中文、附英文小字（收银端默认英文界面，员工要能对上屏幕上的字）；英文页只显示英文。
 * 英文不以字母开头（如 `10%`、`→`、`中 / EN`）时不附小字，避免重复。
 */
export function parseStoreHelpFaqText(text: string, language: StoreHelpFaqLanguage): StoreHelpFaqSegment[] {
  const segments: StoreHelpFaqSegment[] = []
  let cursor = 0
  for (const match of text.matchAll(CONTROL_PATTERN)) {
    const index = match.index ?? 0
    if (index > cursor) {
      segments.push({ kind: 'text', text: text.slice(cursor, index) })
    }
    const variant = match[1] as StoreHelpFaqControlVariant
    const english = match[2]
    const chinese = match[3]
    if (language === 'zh' && chinese) {
      const showEnglish = chinese !== english && /^[A-Za-z]/.test(english)
      segments.push(showEnglish
        ? { kind: 'control', variant, label: chinese, secondary: english }
        : { kind: 'control', variant, label: chinese })
    } else {
      segments.push({ kind: 'control', variant, label: english })
    }
    cursor = index + match[0].length
  }
  if (cursor < text.length) {
    segments.push({ kind: 'text', text: text.slice(cursor) })
  }
  return segments
}

/** 去掉记号后的纯文本，同时保留中英两种按钮名，便于用任一语言的按钮名搜索。 */
function toSearchText(text: string) {
  return text.replace(CONTROL_PATTERN, (_match, _variant, english: string, chinese?: string) =>
    chinese ? `${english} ${chinese}` : english)
}

export function isStoreHelpFaqHeading(step: string) {
  return step.startsWith('§')
}

export function buildStoreHelpFaqSearchText(question: StoreHelpFaqQuestion, language: StoreHelpFaqLanguage) {
  const parts = [
    question.title[language],
    question.where[language],
    ...question.steps[language],
    ...(question.tips?.[language] ?? []),
    ...(question.rules?.[language] ?? []),
    ...(question.related?.[language].flatMap((item) => [item.title, item.body]) ?? []),
  ]
  return parts.map(toSearchText).join(' ').toLowerCase()
}

/** 按关键词过滤分组；空关键词返回原分组。多个词需全部命中（空格分隔）。 */
export function filterStoreHelpFaqGroups(
  groups: StoreHelpFaqGroup[],
  query: string,
  language: StoreHelpFaqLanguage,
): StoreHelpFaqGroup[] {
  const terms = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (terms.length === 0) {
    return groups
  }
  return groups
    .map((group) => ({
      ...group,
      questions: group.questions.filter((question) => {
        const haystack = buildStoreHelpFaqSearchText(question, language)
        return terms.every((term) => haystack.includes(term))
      }),
    }))
    .filter((group) => group.questions.length > 0)
}

/** 问题在整个标签内的序号（1 起），过滤后也保持原编号，方便口头指引「看第 5 问」。 */
export function buildStoreHelpFaqNumbering(groups: StoreHelpFaqGroup[]) {
  const numbering = new Map<string, number>()
  groups.flatMap((group) => group.questions).forEach((question, index) => {
    numbering.set(question.id, index + 1)
  })
  return numbering
}

export function findStoreHelpFaqTabByQuestionId(
  groupsByTab: Record<StoreHelpFaqTab, StoreHelpFaqGroup[]>,
  questionId: string,
): StoreHelpFaqTab | undefined {
  return (Object.keys(groupsByTab) as StoreHelpFaqTab[]).find((tab) =>
    groupsByTab[tab].some((group) => group.questions.some((question) => question.id === questionId)))
}

export function resolveStoreHelpFaqLanguage(queryValue: string | null, navigatorLanguage?: string | null): StoreHelpFaqLanguage {
  if (queryValue === 'zh' || queryValue === 'en') {
    return queryValue
  }
  return navigatorLanguage?.toLowerCase().startsWith('zh') ? 'zh' : 'en'
}

export function resolveStoreHelpFaqTab(queryValue: string | null): StoreHelpFaqTab {
  return queryValue === 'mobile' ? 'mobile' : 'pos'
}

export const STORE_HELP_FAQ_ANCHOR_PREFIX = 'faq-'

export function toStoreHelpFaqAnchor(questionId: string) {
  return `${STORE_HELP_FAQ_ANCHOR_PREFIX}${questionId}`
}

export function fromStoreHelpFaqAnchor(hash: string) {
  const value = hash.replace(/^#/, '')
  return value.startsWith(STORE_HELP_FAQ_ANCHOR_PREFIX) ? value.slice(STORE_HELP_FAQ_ANCHOR_PREFIX.length) : undefined
}
