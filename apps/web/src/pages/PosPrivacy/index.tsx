import { useEffect, useMemo, useState } from 'react'

// 复用移动端隐私页的文档元数据工具与样式，保证两张隐私页外观和语义一致。
import {
  preserveMobilePrivacyDocumentMetadata,
  updateMobilePrivacyDocumentMetadata,
} from '../MobilePrivacy/documentMetadata'
import '../MobilePrivacy/mobilePrivacy.css'

import {
  getPosPrivacyPolicy,
  resolvePosPrivacyLanguage,
  type PosPrivacyLanguage,
} from './posPrivacyPolicy'

export default function PosPrivacyPage() {
  const [language, setLanguage] = useState<PosPrivacyLanguage>(() =>
    resolvePosPrivacyLanguage(window.navigator.language),
  )
  const policy = useMemo(() => getPosPrivacyPolicy(language), [language])

  useEffect(() => {
    return preserveMobilePrivacyDocumentMetadata(document)
  }, [])

  useEffect(() => {
    updateMobilePrivacyDocumentMetadata(document, language, policy.title)
  }, [language, policy.title])

  return (
    <main className="mobile-privacy-page">
      <header className="mobile-privacy-header">
        <a className="mobile-privacy-brand" href="/privacy/pos" aria-label="Hot Bargain">
          <span className="mobile-privacy-mark" aria-hidden="true">HB</span>
          <span>Hot Bargain</span>
        </a>
        <div className="mobile-privacy-language" role="group" aria-label="Language / 语言">
          <button
            type="button"
            className={language === 'zh' ? 'is-active' : undefined}
            aria-pressed={language === 'zh'}
            onClick={() => setLanguage('zh')}
          >
            中文
          </button>
          <button
            type="button"
            className={language === 'en' ? 'is-active' : undefined}
            aria-pressed={language === 'en'}
            onClick={() => setLanguage('en')}
          >
            English
          </button>
        </div>
      </header>

      <article className="mobile-privacy-document">
        <div className="mobile-privacy-intro">
          <p className="mobile-privacy-eyebrow">{policy.subtitle}</p>
          <h1>{policy.title}</h1>
          <p className="mobile-privacy-effective">
            {policy.effectiveDateLabel}: {policy.effectiveDate}
          </p>
          <p className="mobile-privacy-summary">{policy.summary}</p>
        </div>

        <dl className="mobile-privacy-organization">
          <div>
            <dt>{policy.organization.label}</dt>
            <dd>{policy.organization.name}</dd>
          </div>
          <div>
            <dt>{policy.organization.contactLabel}</dt>
            <dd>
              <a href={`mailto:${policy.organization.email}`}>{policy.organization.email}</a>
            </dd>
          </div>
        </dl>

        <div className="mobile-privacy-sections">
          {policy.sections.map((section) => (
            <section key={section.id} id={section.id}>
              <h2>{section.title}</h2>
              {section.paragraphs.map((paragraph) => <p key={paragraph}>{paragraph}</p>)}
              {section.items.length ? (
                <ul>
                  {section.items.map((item) => <li key={item}>{item}</li>)}
                </ul>
              ) : null}
            </section>
          ))}
        </div>

        <footer className="mobile-privacy-footer">
          <span>© {new Date().getFullYear()} {policy.organization.name}</span>
          <a href={`mailto:${policy.organization.email}`}>{policy.footer.emailLabel}</a>
        </footer>
      </article>
    </main>
  )
}
