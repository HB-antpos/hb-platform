import policyEn from '../../content/pos-privacy-policy.en.json'
import policyZh from '../../content/pos-privacy-policy.zh.json'

export type PosPrivacyLanguage = 'en' | 'zh'

type PosPrivacySection = {
  id: string
  title: string
  paragraphs: string[]
  items: string[]
}

export type PosPrivacyPolicy = {
  policyVersion: string
  language: string
  title: string
  subtitle: string
  effectiveDateLabel: string
  effectiveDate: string
  summary: string
  organization: {
    label: string
    name: string
    contactLabel: string
    email: string
  }
  sections: PosPrivacySection[]
  footer: {
    backLabel: string
    publicCopy: string
    publicUrl: string
    emailLabel: string
    openFailedTitle: string
    openFailedMessage: string
  }
}

// 以显式契约约束两种语言的 JSON，避免类型断言掩盖字段缺失（结构与 /privacy/mobile 一致）。
const POS_PRIVACY_POLICIES = {
  en: policyEn,
  zh: policyZh,
} satisfies Record<PosPrivacyLanguage, PosPrivacyPolicy>

export function resolvePosPrivacyLanguage(language?: string | null): PosPrivacyLanguage {
  return language?.toLowerCase().startsWith('zh') ? 'zh' : 'en'
}

export function getPosPrivacyPolicy(language: PosPrivacyLanguage): PosPrivacyPolicy {
  return POS_PRIVACY_POLICIES[language]
}
