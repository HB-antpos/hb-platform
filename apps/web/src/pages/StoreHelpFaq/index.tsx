import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'

import {
  preserveMobilePrivacyDocumentMetadata,
  updateMobilePrivacyDocumentMetadata,
} from '../MobilePrivacy/documentMetadata'

import {
  STORE_HELP_FAQ_COPY,
  STORE_HELP_FAQ_MAPS,
  STORE_HELP_FAQ_PATH,
  type StoreHelpFaqGroup,
  type StoreHelpFaqLanguage,
  type StoreHelpFaqQuestion,
  type StoreHelpFaqTab,
} from './faqContent'
import {
  buildStoreHelpFaqNumbering,
  filterStoreHelpFaqGroups,
  findStoreHelpFaqTabByQuestionId,
  fromStoreHelpFaqAnchor,
  isStoreHelpFaqHeading,
  parseStoreHelpFaqText,
  resolveStoreHelpFaqLanguage,
  resolveStoreHelpFaqTab,
  toStoreHelpFaqAnchor,
} from './faqText'
import { useStoreHelpFaqGroups } from './useStoreHelpFaqGroups'
import './storeHelpFaq.css'

const TABS: StoreHelpFaqTab[] = ['pos', 'mobile']
const COMPACT_QUERY = '(max-width: 767px)'
// 数据未到达时用同一个空数组引用，避免 useMemo 依赖每次变化
const NO_GROUPS: StoreHelpFaqGroup[] = []

function useCompactLayout() {
  const [compact, setCompact] = useState(() =>
    typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia(COMPACT_QUERY).matches)
  useEffect(() => {
    if (typeof window.matchMedia !== 'function') {
      return undefined
    }
    const media = window.matchMedia(COMPACT_QUERY)
    const update = () => setCompact(media.matches)
    update()
    media.addEventListener('change', update)
    return () => media.removeEventListener('change', update)
  }, [])
  return compact
}

function RichText({ text, language }: { text: string; language: StoreHelpFaqLanguage }) {
  return (
    <>
      {parseStoreHelpFaqText(text, language).map((segment, index) => (
        segment.kind === 'text'
          ? <Fragment key={index}>{segment.text}</Fragment>
          : (
              <span key={index} className={`store-faq-control store-faq-control-${segment.variant}`}>
                {segment.label}
                {segment.secondary ? <em lang="en">{segment.secondary}</em> : null}
              </span>
            )
      ))}
    </>
  )
}

function PosMap({ language }: { language: StoreHelpFaqLanguage }) {
  const map = STORE_HELP_FAQ_MAPS.pos
  const actions = map.actions[language]
  return (
    <div className="store-faq-pos-map" aria-hidden="true">
      {/* 标题栏横跨三列：界面配色、客显、中英切换都在这里 */}
      <div className="store-faq-titlebar">
        <span>{map.titleBar.label[language]}</span>
        {map.titleBar.items[language].map((item) => <div key={item} className="store-faq-mini is-highlighted">{item}</div>)}
      </div>
      <div className="store-faq-zone">
        <span className="store-faq-zone-label"><i>1</i>{map.zones[language][0]}</span>
        <div className="store-faq-mini store-faq-mini-search">{map.search[language]}</div>
        <div className="store-faq-cart-row" />
        <div className="store-faq-cart-row is-selected" />
        <div className="store-faq-cart-row is-swiped"><span>{map.remove[language]}</span></div>
        <div className="store-faq-cart-row" />
        <div className="store-faq-zone-spacer" />
        <div className="store-faq-mini is-primary is-highlighted">{map.payNow[language]}</div>
      </div>
      <div className="store-faq-zone">
        <span className="store-faq-zone-label"><i>2</i>{map.zones[language][1]}</span>
        <div className="store-faq-keypad">{Array.from({ length: 12 }, (_, index) => <span key={index} />)}</div>
        <div className="store-faq-grid-2">
          <div className="store-faq-mini">{actions[0]}</div>
          <div className="store-faq-mini is-highlighted">{actions[1]}</div>
        </div>
        <div className="store-faq-grid-3">
          <div className="store-faq-mini is-toggle is-highlighted">{actions[2]}</div>
          <div className="store-faq-mini is-highlighted">{actions[3]}</div>
          <div className="store-faq-mini is-highlighted">{actions[4]}</div>
        </div>
        <div className="store-faq-grid-5">
          {['10%', '20%', '30%', '40%', '50%'].map((value) => <div key={value} className="store-faq-mini">{value}</div>)}
        </div>
      </div>
      <div className="store-faq-zone">
        <span className="store-faq-zone-label"><i>3</i>{map.zones[language][2]}</span>
        <div className="store-faq-grid-2">
          {map.tiles[language].map((tile, index) => (
            <div key={tile} className={`store-faq-mini${(map.highlightedTiles as readonly number[]).includes(index) ? ' is-highlighted' : ''}`}>{tile}</div>
          ))}
        </div>
      </div>
    </div>
  )
}

function MobileMap({ language }: { language: StoreHelpFaqLanguage }) {
  const map = STORE_HELP_FAQ_MAPS.mobile
  return (
    <div className="store-faq-mobile-map">
      <div className="store-faq-phone" aria-hidden="true">
        <div className="store-faq-phone-title">{map.home[language]}</div>
        {map.groups.map((group) => (
          <div key={group.title.en} className="store-faq-phone-group">
            <div>{group.title[language]}</div>
            <div className="store-faq-phone-tiles">
              {group.tiles.map((tile) => (
                <span key={tile.en} className={'hl' in tile && tile.hl ? 'is-highlighted' : undefined}>{tile[language]}</span>
              ))}
            </div>
          </div>
        ))}
        <div className="store-faq-phone-bar">
          {map.bottomTabs[language].map((tab, index) => <span key={tab} className={index === 0 ? 'is-active' : undefined}>{tab}</span>)}
        </div>
      </div>
      <ol className="store-faq-legend">
        {map.legend[language].map((line) => <li key={line}>{line}</li>)}
      </ol>
    </div>
  )
}

/** 问题列表加载中的占位卡片，版式与问题卡一致，避免数据到达时页面跳动。 */
function QuestionSkeleton({ label }: { label: string }) {
  return (
    <div className="store-faq-skeleton" role="status" aria-busy="true" aria-label={label}>
      {[0, 1, 2].map((index) => (
        <div key={index} className="store-faq-card store-faq-skeleton-card" aria-hidden="true">
          <span className="store-faq-skeleton-bar is-title" />
          <span className="store-faq-skeleton-bar" />
          <span className="store-faq-skeleton-bar is-short" />
        </div>
      ))}
    </div>
  )
}

type QuestionCardProps = {
  question: StoreHelpFaqQuestion
  number: number
  language: StoreHelpFaqLanguage
  compact: boolean
  open: boolean
  onToggle: (questionId: string) => void
}

function QuestionCard({ question, number, language, compact, open, onToggle }: QuestionCardProps) {
  const copy = STORE_HELP_FAQ_COPY[language]
  const anchor = toStoreHelpFaqAnchor(question.id)
  const bodyId = `${anchor}-body`
  const expanded = !compact || open
  let stepNumber = 0
  const tips = question.tips?.[language] ?? []
  const rules = question.rules?.[language] ?? []
  const related = question.related?.[language] ?? []

  const heading = (
    <>
      <span className="store-faq-q-number" aria-hidden="true">{number}</span>
      <span className="store-faq-q-title">{question.title[language]}</span>
    </>
  )

  return (
    <section id={anchor} className={`store-faq-card store-faq-q${expanded ? ' is-open' : ''}`} aria-labelledby={`${anchor}-title`}>
      <h2 id={`${anchor}-title`} className="store-faq-q-heading">
        {compact
          ? (
              // 手机宽度下标题本身就是折叠开关
              <button type="button" aria-expanded={open} aria-controls={bodyId} onClick={() => onToggle(question.id)}>
                {heading}
                <span className="store-faq-chevron" aria-hidden="true" />
              </button>
            )
          : heading}
      </h2>
      {expanded ? (
        <div id={bodyId} className="store-faq-q-body">
          <p className="store-faq-where">
            <b>{copy.where}</b>
            <span><RichText text={question.where[language]} language={language} /></span>
          </p>
          <ol className="store-faq-steps">
            {question.steps[language].map((step) => {
              if (isStoreHelpFaqHeading(step)) {
                return <li key={step} className="store-faq-step-heading">{step.slice(1)}</li>
              }
              stepNumber += 1
              return (
                <li key={step} data-step={stepNumber}>
                  <RichText text={step} language={language} />
                </li>
              )
            })}
          </ol>
          {(rules.length > 0 || tips.length > 0 || question.permissions) ? (
            <div className="store-faq-meta">
              {rules.length > 0 ? (
                <ul className="store-faq-rules">{rules.map((rule) => <li key={rule}>{rule}</li>)}</ul>
              ) : null}
              {tips.map((tip) => (
                <p key={tip} className="store-faq-tip"><RichText text={tip} language={language} /></p>
              ))}
              {question.permissions ? (
                <p className="store-faq-permissions">
                  <span>{copy.permission}</span>
                  {question.permissions.map((code) => <code key={code}>{code}</code>)}
                </p>
              ) : null}
            </div>
          ) : null}
          {related.length > 0 ? (
            <div className="store-faq-related">
              {related.map((item) => (
                <div key={item.title}>
                  <b>{item.title}</b>
                  <RichText text={item.body} language={language} />
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}
    </section>
  )
}

export default function StoreHelpFaqPage() {
  const initialParams = useMemo(() => new URLSearchParams(window.location.search), [])
  const [language, setLanguage] = useState<StoreHelpFaqLanguage>(() =>
    resolveStoreHelpFaqLanguage(initialParams.get('lang'), window.navigator.language))
  // 初始标签只看 ?tab=；带问题锚点的分享链接要等问题数据到达后才能知道锚点属于哪个标签（见下方锚点 effect）
  const [tab, setTab] = useState<StoreHelpFaqTab>(() => resolveStoreHelpFaqTab(initialParams.get('tab')))
  const [query, setQuery] = useState('')
  const compact = useCompactLayout()
  const [openIds, setOpenIds] = useState<Set<string>>(() => new Set())
  const [activeId, setActiveId] = useState<string>()
  const tabRefs = useRef<Record<StoreHelpFaqTab, HTMLButtonElement | null>>({ pos: null, mobile: null })
  const { state: faqState, reload: reloadFaq } = useStoreHelpFaqGroups()
  const groupsByTab = faqState.status === 'ready' ? faqState.groups : undefined
  // 进入页面时 URL 里的问题锚点，数据到达后只处理这一次
  const [initialAnchorId] = useState(() => fromStoreHelpFaqAnchor(window.location.hash))
  const anchorHandledRef = useRef(false)
  const [scrollTargetId, setScrollTargetId] = useState<string>()

  const copy = STORE_HELP_FAQ_COPY[language]
  const allGroups = groupsByTab?.[tab] ?? NO_GROUPS
  const numbering = useMemo(() => buildStoreHelpFaqNumbering(allGroups), [allGroups])
  const groups = useMemo(() => filterStoreHelpFaqGroups(allGroups, query, language), [allGroups, language, query])
  const searching = query.trim().length > 0
  const firstQuestionId = allGroups[0]?.questions[0]?.id

  useEffect(() => {
    const restoreMetadata = preserveMobilePrivacyDocumentMetadata(document)
    updateMobilePrivacyDocumentMetadata(document, language, copy.documentTitle)
    return restoreMetadata
  }, [copy.documentTitle, language])

  // 语言与标签写回地址栏，复制链接给同事时能打开同一视图。
  // 数据到达前不写：带锚点的链接此时标签还没按锚点切换，提前写会把 ?tab= 改成默认值
  const ready = groupsByTab !== undefined
  useEffect(() => {
    if (!ready) {
      return
    }
    const params = new URLSearchParams(window.location.search)
    params.set('lang', language)
    params.set('tab', tab)
    window.history.replaceState(window.history.state, '', `${STORE_HELP_FAQ_PATH}?${params.toString()}${window.location.hash}`)
  }, [language, ready, tab])

  // 问题数据到达后处理 #faq-<id> 深链：锚点所在标签优先于 ?tab=，并展开、定位该问题。
  // 锚点在数据里找不到（题被下线或链接过期）就保持默认标签，不报错
  useEffect(() => {
    if (!groupsByTab || anchorHandledRef.current) {
      return
    }
    anchorHandledRef.current = true
    const anchoredTab = initialAnchorId ? findStoreHelpFaqTabByQuestionId(groupsByTab, initialAnchorId) : undefined
    if (initialAnchorId && anchoredTab) {
      setTab(anchoredTab)
      setOpenIds(new Set([initialAnchorId]))
      setActiveId(initialAnchorId)
      setScrollTargetId(initialAnchorId)
    }
  }, [groupsByTab, initialAnchorId])

  // 标签切换后的内容渲染完成，再滚到锚点问题
  useEffect(() => {
    if (!scrollTargetId) {
      return
    }
    document.getElementById(toStoreHelpFaqAnchor(scrollTargetId))?.scrollIntoView({ block: 'start' })
    setScrollTargetId(undefined)
  }, [scrollTargetId, tab])

  // 桌面端目录高亮当前阅读的问题
  useEffect(() => {
    if (compact || typeof IntersectionObserver === 'undefined') {
      return undefined
    }
    const observer = new IntersectionObserver((entries) => {
      const visible = entries
        .filter((entry) => entry.isIntersecting)
        .sort((left, right) => left.boundingClientRect.top - right.boundingClientRect.top)
      const id = visible[0] ? fromStoreHelpFaqAnchor(visible[0].target.id) : undefined
      if (id) {
        setActiveId(id)
      }
    }, { rootMargin: '-80px 0px -60% 0px' })
    document.querySelectorAll('.store-faq-q').forEach((element) => observer.observe(element))
    return () => observer.disconnect()
  }, [compact, groups])

  const toggleQuestion = useCallback((questionId: string) => {
    setOpenIds((current) => {
      const next = new Set(current)
      if (next.has(questionId)) {
        next.delete(questionId)
      } else {
        next.add(questionId)
      }
      return next
    })
  }, [])

  const selectTab = (nextTab: StoreHelpFaqTab) => {
    setTab(nextTab)
    setActiveId(undefined)
    if (window.location.hash) {
      window.history.replaceState(window.history.state, '', `${window.location.pathname}${window.location.search}`)
    }
  }

  const handleTabKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
      return
    }
    event.preventDefault()
    const nextTab = TABS[(TABS.indexOf(tab) + (event.key === 'ArrowRight' ? 1 : TABS.length - 1)) % TABS.length]
    selectTab(nextTab)
    tabRefs.current[nextTab]?.focus()
  }

  const isOpen = (questionId: string) =>
    searching || openIds.has(questionId) || (openIds.size === 0 && questionId === firstQuestionId)

  return (
    <div className="store-faq-page">
      <header className="store-faq-header">
        <a className="store-faq-brand" href={STORE_HELP_FAQ_PATH} aria-label="HB Help">
          <span className="store-faq-mark" aria-hidden="true">HB</span>
          <span>HB Help<small> · {copy.brandSub}</small></span>
        </a>
        <div className="store-faq-language" role="group" aria-label="Language / 语言">
          <button type="button" className={language === 'en' ? 'is-active' : ''} aria-pressed={language === 'en'} onClick={() => setLanguage('en')}>
            English
          </button>
          <button type="button" className={language === 'zh' ? 'is-active' : ''} aria-pressed={language === 'zh'} onClick={() => setLanguage('zh')}>
            简体中文
          </button>
        </div>
      </header>

      <div className="store-faq-hero">
        <p className="store-faq-eyebrow">{copy.eyebrow}</p>
        <h1>{copy.title}</h1>
        <p className="store-faq-summary">{copy.summary}</p>
        <div className="store-faq-toolbar">
          <div className="store-faq-tabs" role="tablist" aria-label={copy.tabListLabel}>
            {TABS.map((item) => (
              <button
                key={item}
                ref={(element) => { tabRefs.current[item] = element }}
                type="button"
                role="tab"
                id={`store-faq-tab-${item}`}
                aria-selected={tab === item}
                aria-controls="store-faq-panel"
                tabIndex={tab === item ? 0 : -1}
                className={`store-faq-tab${tab === item ? ' is-active' : ''}`}
                onClick={() => selectTab(item)}
                onKeyDown={handleTabKeyDown}
              >
                <span className={`store-faq-tab-icon store-faq-tab-icon-${item}`} aria-hidden="true" />
                {copy.tabs[item]}
                {groupsByTab ? (
                  <span className="store-faq-tab-count">
                    {groupsByTab[item].reduce((total, group) => total + group.questions.length, 0)}
                  </span>
                ) : null}
              </button>
            ))}
          </div>
          <div className="store-faq-search">
            <span className="store-faq-search-icon" aria-hidden="true" />
            <input
              type="search"
              value={query}
              aria-label={copy.searchLabel}
              placeholder={copy.searchPlaceholder}
              onChange={(event) => setQuery(event.target.value)}
            />
            {searching ? (
              <button type="button" className="store-faq-search-clear" aria-label={copy.clearSearch} onClick={() => setQuery('')}>×</button>
            ) : null}
          </div>
        </div>
      </div>

      <div className="store-faq-layout" id="store-faq-panel" role="tabpanel" aria-labelledby={`store-faq-tab-${tab}`}>
        <aside className="store-faq-toc">
          <nav className="store-faq-card store-faq-toc-box" aria-label={copy.tocTitle}>
            <h2>{copy.tocTitle}</h2>
            {!ready ? (
              <div className="store-faq-toc-skeleton" aria-hidden="true">
                {[0, 1, 2, 3, 4].map((index) => <span key={index} className="store-faq-skeleton-bar" />)}
              </div>
            ) : null}
            {groups.map((group) => (
              <Fragment key={group.id}>
                <h3>{group.title[language]}<span>{group.questions.length}</span></h3>
                {group.questions.map((question) => (
                  <a
                    key={question.id}
                    href={`#${toStoreHelpFaqAnchor(question.id)}`}
                    className={activeId === question.id ? 'is-active' : undefined}
                    aria-current={activeId === question.id ? 'location' : undefined}
                    onClick={() => setActiveId(question.id)}
                  >
                    <b>{String(numbering.get(question.id) ?? 0).padStart(2, '0')}</b>
                    {question.title[language]}
                  </a>
                ))}
              </Fragment>
            ))}
          </nav>
          <div className="store-faq-help">
            <strong>{copy.helpTitle}</strong>
            {copy.helpBody}
          </div>
        </aside>

        <main className="store-faq-main">
          {!searching ? (
            <details className="store-faq-card store-faq-map" open>
              <summary>
                <span>
                  <strong>{tab === 'pos' ? copy.posMapTitle : copy.mobileMapTitle}</strong>
                  <small>{tab === 'pos' ? copy.posMapBody : copy.mobileMapBody}</small>
                </span>
                <span className="store-faq-chevron" aria-hidden="true" />
              </summary>
              {tab === 'pos' ? <PosMap language={language} /> : <MobileMap language={language} />}
            </details>
          ) : null}

          <p className="store-faq-note">
            <span aria-hidden="true">i</span>
            <span><RichText text={tab === 'pos' ? copy.posNote : copy.mobileNote} language={language} /></span>
          </p>

          {faqState.status === 'loading' ? (
            <QuestionSkeleton label={copy.loadingLabel} />
          ) : faqState.status === 'error' ? (
            <div className="store-faq-card store-faq-empty" role="alert">
              <strong>{copy.loadFailedTitle}</strong>
              <span>{copy.loadFailedBody}</span>
              <button type="button" className="store-faq-retry" onClick={reloadFaq}>{copy.retry}</button>
            </div>
          ) : groups.length === 0 ? (
            <div className="store-faq-card store-faq-empty" role="status">
              <strong>{copy.noResultsTitle}</strong>
              <span>{copy.noResultsBody}</span>
            </div>
          ) : groups.map((group) => (
            <Fragment key={group.id}>
              <h2 className="store-faq-group-title">{group.title[language]}</h2>
              {group.questions.map((question) => (
                <QuestionCard
                  key={question.id}
                  question={question}
                  number={numbering.get(question.id) ?? 0}
                  language={language}
                  compact={compact}
                  open={isOpen(question.id)}
                  onToggle={toggleQuestion}
                />
              ))}
            </Fragment>
          ))}

          <footer className="store-faq-footer">© {new Date().getFullYear()} {copy.footer}</footer>
        </main>
      </div>
    </div>
  )
}
