import { CheckCircleOutlined, CloseCircleOutlined, ExclamationCircleOutlined } from '@ant-design/icons'
import type { TFunction } from 'i18next'

import type { TemplateCheck, TemplateCheckId, TemplateCheckStatus } from './staffNotificationLogic'

/** 校验提示的文案 key 与中文默认值：按「校验项 + 状态」查，写成字面量便于静态核对键名。 */
interface CheckText {
  key: string
  fallback: string
}

type CheckTextTable = Partial<Record<TemplateCheckId, Partial<Record<TemplateCheckStatus | 'empty', CheckText>>>>

const CHECK_TEXT: CheckTextTable = {
  'name.length': {
    fail: { key: 'system.staffNotifications.templates.check.name.length.fail', fallback: '模板名称需要 1–{{max}} 个字符（现在 {{length}} 个）' },
  },
  'name.duplicate': {
    fail: { key: 'system.staffNotifications.templates.check.name.duplicate.fail', fallback: '已有同名模板，请换一个名称' },
  },
  'email.loginBox': {
    pass: { key: 'system.staffNotifications.templates.check.email.loginBox.pass', fallback: '含 {{variable}}（这个模板必须有，删掉不能保存）' },
    fail: { key: 'system.staffNotifications.templates.check.email.loginBox.fail', fallback: '缺少 {{variable}}：这个模板会发放随机密码，必须放进邮件正文，否则员工收不到密码，不能保存' },
  },
  'email.loginBoxNotIssued': {
    warn: { key: 'system.staffNotifications.templates.check.email.loginBoxNotIssued.warn', fallback: '普通通知模板不发放密码，{{variable}} 不会生效，建议删掉' },
  },
  'email.download': {
    pass: { key: 'system.staffNotifications.templates.check.email.download.pass', fallback: '含下载入口' },
    warn: { key: 'system.staffNotifications.templates.check.email.download.warn', fallback: '没有下载入口：可放 {{buttons}} 或 {{link}}（不放也能保存）' },
  },
  'email.english': {
    pass: { key: 'system.staffNotifications.templates.check.email.english.pass', fallback: '全英文，没有中文字符' },
    fail: { key: 'system.staffNotifications.templates.check.email.english.fail', fallback: '邮件只能用英文，主题和正文里不能有中文或全角标点' },
  },
  'email.variables': {
    fail: { key: 'system.staffNotifications.templates.check.email.variables.fail', fallback: '有不认识的变量：{{variables}}，只能用页面上列出的变量' },
  },
  'email.length': {
    fail: { key: 'system.staffNotifications.templates.check.email.length.fail', fallback: '邮件超长：主题 {{subject}} / {{subjectMax}}，正文 {{body}} / {{bodyMax}} 字符' },
  },
  'sms.length': {
    empty: { key: 'system.staffNotifications.templates.check.sms.length.empty', fallback: '没有写短信：这个模板不会发短信' },
    pass: { key: 'system.staffNotifications.templates.check.sms.length.pass', fallback: '{{length}} / {{limit}} 字符，按 {{segments}} 条计费' },
    fail: { key: 'system.staffNotifications.templates.check.sms.length.fail', fallback: '估算 {{length}} 字符，按 {{segments}} 条计费，超过最多 3 条（{{maxLength}} 字符）的上限' },
  },
  'sms.mailOnlyVariables': {
    pass: { key: 'system.staffNotifications.templates.check.sms.mailOnlyVariables.pass', fallback: '不含密码（{{variable}} 不能放进短信）' },
    fail: { key: 'system.staffNotifications.templates.check.sms.mailOnlyVariables.fail', fallback: '短信里不能放 {{variables}}，它们只能放进邮件' },
  },
  'sms.charset': {
    pass: { key: 'system.staffNotifications.templates.check.sms.charset.pass', fallback: '只用英文和常用标点' },
    fail: { key: 'system.staffNotifications.templates.check.sms.charset.fail', fallback: '有短信不支持的字符：{{chars}}（中文、弯引号、emoji 会让一条短信变成 70 字符、费用翻倍）' },
  },
  'sms.variables': {
    fail: { key: 'system.staffNotifications.templates.check.sms.variables.fail', fallback: '有不认识的变量：{{variables}}，短信只能用 {{allowed}}' },
  },
}

/** 一条校验提示的文字；没有登记的组合（不应出现）返回空串，不渲染。 */
export function getTemplateCheckText(check: TemplateCheck, t: TFunction): string {
  const status: TemplateCheckStatus | 'empty' =
    check.id === 'sms.length' && check.status === 'pass' && check.params?.segments === 0 ? 'empty' : check.status
  const entry = CHECK_TEXT[check.id]?.[status]
  if (!entry) return ''
  const params: Record<string, string | number> = { ...check.params }
  // 变量名当参数传，避免 {{login_box}} 被 i18n 当成插值占位符。
  params.variable = '{{login_box}}'
  params.buttons = '{{download_buttons}}'
  params.link = '{{app_link}}'
  params.allowed = '{{name}} {{email}} {{store}} {{app_link}}'
  return t(entry.key, { defaultValue: entry.fallback, ...params })
}

function CheckIcon({ status }: { status: TemplateCheckStatus }) {
  if (status === 'pass') return <CheckCircleOutlined className="staff-notify-check-icon staff-notify-check-pass" />
  if (status === 'warn') return <ExclamationCircleOutlined className="staff-notify-check-icon staff-notify-check-warn" />
  return <CloseCircleOutlined className="staff-notify-check-icon staff-notify-check-fail" />
}

interface TemplateChecksProps {
  checks: TemplateCheck[]
  t: TFunction
  /** 通过项是否也显示：邮件 / 短信的清单全显示，名称只显示出问题的。 */
  onlyProblems?: boolean
}

/** 实时校验清单：绿勾通过、红叉不能保存、黄色感叹只是提醒。 */
export function TemplateChecks({ checks, t, onlyProblems = false }: TemplateChecksProps) {
  const visible = checks.filter((check) => {
    if (onlyProblems && check.status === 'pass') return false
    // 名称还没填时不提前报红，保存按钮本身是禁用的。
    if (check.id === 'name.length' && check.params?.length === 0) return false
    return true
  })
  if (!visible.length) return null
  return (
    <ul className="staff-notify-checks" aria-live="polite">
      {visible.map((check) => {
        const text = getTemplateCheckText(check, t)
        if (!text) return null
        return (
          <li key={check.id} className={`staff-notify-check staff-notify-check-${check.status}`}>
            <CheckIcon status={check.status} />
            <span>{text}</span>
          </li>
        )
      })}
    </ul>
  )
}
