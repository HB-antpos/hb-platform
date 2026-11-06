import { MailOutlined, MessageOutlined, PlusOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Input, Modal, Space, Spin, Switch, Tag, Typography, message } from 'antd'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { FocusEvent } from 'react'
import { useTranslation } from 'react-i18next'

import {
  createStaffNotificationTemplate,
  getStaffNotificationTemplates,
  resetStaffNotificationTemplate,
  saveStaffNotificationTemplate,
} from '../../../services/staffNotificationService'
import { useAuthStore } from '../../../store/auth'
import type { StaffNotificationTemplate } from '../../../types/staffNotification'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../listPagination'

import { TemplateChecks } from './TemplateChecks'
import {
  getStaffNotificationErrorMessage,
  insertTextAtSelection,
  validateTemplateDraft,
  type TemplateVariable,
} from './staffNotificationLogic'
import {
  NEW_TEMPLATE_KEY,
  canInsertVariable,
  createEmptyTemplateDraft,
  getTemplateCardMeta,
  getTemplateVariableChips,
  isTemplateDraftDirty,
  toTemplateDraft,
  toTemplateSavePayload,
  type TemplateEditorDraft,
  type TemplateEditorField,
} from './staffNotificationPageLogic'

interface TemplatesTabProps {
  /** 编辑区有未保存的修改时通知外层（切页签前要提示放弃）。 */
  onDirtyChange: (dirty: boolean) => void
}

/** 变量条里「说明」的中文默认值；键名写成字面量。 */
function variableLabel(variable: TemplateVariable, t: (key: string, fallback: string) => string): string {
  switch (variable) {
    case 'name':
      return t('system.staffNotifications.templates.variable.name', '员工姓名')
    case 'email':
      return t('system.staffNotifications.templates.variable.email', '登录邮箱')
    case 'store':
      return t('system.staffNotifications.templates.variable.store', '所属分店')
    case 'app_link':
      return t('system.staffNotifications.templates.variable.app_link', '下载短链')
    case 'login_box':
      return t('system.staffNotifications.templates.variable.login_box', '邮箱 + 随机密码框')
    default:
      return t('system.staffNotifications.templates.variable.download_buttons', 'iPhone / Android 按钮')
  }
}

type FocusedField = { field: TemplateEditorField; element: HTMLInputElement | HTMLTextAreaElement }

/** 模板页签：左侧模板列表，右侧编辑区（变量条、邮件 / 短信文案、实时校验）。非管理员只读。 */
export default function TemplatesTab({ onDirtyChange }: TemplatesTabProps) {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const readOnly = !access.isAdmin

  const [templates, setTemplates] = useState<StaffNotificationTemplate[]>([])
  const [loading, setLoading] = useState(true)
  const [loadFailed, setLoadFailed] = useState(false)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [draft, setDraft] = useState<TemplateEditorDraft>(createEmptyTemplateDraft)
  const [saving, setSaving] = useState(false)
  const [resetting, setResetting] = useState(false)

  const requestGuardRef = useRef(createLatestRequestGuard())
  const selectedKeyRef = useRef<string | null>(null)
  const lastFocusRef = useRef<FocusedField | null>(null)
  const pendingCaretRef = useRef<{ element: HTMLInputElement | HTMLTextAreaElement; caret: number } | null>(null)
  selectedKeyRef.current = selectedKey

  const isNew = selectedKey === NEW_TEMPLATE_KEY
  const selectedTemplate = useMemo(
    () => (selectedKey && !isNew ? templates.find((template) => template.key === selectedKey) ?? null : null),
    [isNew, selectedKey, templates],
  )
  const baseline = useMemo(
    () => (selectedTemplate ? toTemplateDraft(selectedTemplate) : createEmptyTemplateDraft()),
    [selectedTemplate],
  )
  const dirty = selectedKey !== null && isTemplateDraftDirty(draft, baseline)
  // 新建的模板是普通通知模板：不发放随机密码，也就没有 {{login_box}}。
  const issuesPassword = selectedTemplate?.issuesPassword ?? false

  const validation = useMemo(
    () => validateTemplateDraft(draft, {
      issuesPassword,
      checkDuplicateName: selectedTemplate?.kind !== 'system',
      otherTemplateNames: templates.filter((template) => template.key !== selectedKey).map((template) => template.name),
    }),
    [draft, issuesPassword, selectedKey, selectedTemplate?.kind, templates],
  )

  // 变量插入后的光标恢复：每次渲染提交后检查一次，有待恢复的光标就落到插入内容之后。
  useLayoutEffect(() => {
    const pending = pendingCaretRef.current
    if (!pending) return
    pendingCaretRef.current = null
    if (!pending.element.isConnected) return
    pending.element.focus()
    pending.element.setSelectionRange(pending.caret, pending.caret)
  })

  useEffect(() => {
    onDirtyChange(dirty)
  }, [dirty, onDirtyChange])
  // 页签被销毁（放弃修改后重挂载）时不能把「有未保存修改」留在外层。
  useEffect(() => () => onDirtyChange(false), [onDirtyChange])

  const applySelection = useCallback((key: string | null, list: StaffNotificationTemplate[]) => {
    setSelectedKey(key)
    lastFocusRef.current = null
    const target = key && key !== NEW_TEMPLATE_KEY ? list.find((template) => template.key === key) : undefined
    setDraft(target ? toTemplateDraft(target) : createEmptyTemplateDraft())
  }, [])

  const loadTemplates = useCallback(async (preferKey?: string) => {
    await runLatestGuardedRequest(requestGuardRef.current, getStaffNotificationTemplates, {
      onStart: () => {
        setLoading(true)
        setLoadFailed(false)
      },
      onSuccess: (list) => {
        setTemplates(list)
        const current = selectedKeyRef.current
        const wanted = preferKey ?? current
        const nextKey = wanted && wanted !== NEW_TEMPLATE_KEY && list.some((template) => template.key === wanted)
          ? wanted
          : (list[0]?.key ?? null)
        // 刷新列表时保留还停留在新建草稿上的编辑内容，只在选中项变化时才重置草稿。
        if (!(current === NEW_TEMPLATE_KEY && !preferKey)) applySelection(nextKey, list)
      },
      onError: (error) => {
        console.error(error)
        setLoadFailed(true)
        message.error(t('system.staffNotifications.templates.loadFailed', '加载模板失败'))
      },
      onSettled: () => setLoading(false),
    })
  }, [applySelection, t])

  useEffect(() => {
    void loadTemplates()
    const guard = requestGuardRef.current
    return () => guard.invalidate()
  }, [loadTemplates])

  /** 有未保存的修改时先确认，确认放弃再执行 action。 */
  const confirmDiscard = useCallback((action: () => void) => {
    if (!dirty) {
      action()
      return
    }
    Modal.confirm({
      title: t('system.staffNotifications.templates.switchDiscardTitle', '放弃未保存的修改？'),
      content: t('system.staffNotifications.templates.switchDiscardContent', '当前模板还有没保存的修改，切换后这些修改会丢失。'),
      okText: t('system.staffNotifications.templates.switchDiscardOk', '放弃修改'),
      okButtonProps: { danger: true },
      cancelText: t('system.staffNotifications.templates.switchDiscardCancel', '继续编辑'),
      onOk: action,
    })
  }, [dirty, t])

  const handleSelect = (key: string) => {
    if (key === selectedKey) return
    confirmDiscard(() => applySelection(key, templates))
  }

  const handleNew = () => {
    if (isNew) return
    confirmDiscard(() => applySelection(NEW_TEMPLATE_KEY, templates))
  }

  const updateDraft = (patch: Partial<TemplateEditorDraft>) => setDraft((previous) => ({ ...previous, ...patch }))

  /** 记录最后聚焦的输入框：变量条按钮不抢焦点，插入时读它的光标位置。 */
  const trackFocus = (field: TemplateEditorField) => (event: FocusEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    lastFocusRef.current = { field, element: event.currentTarget }
  }

  const handleInsert = (variable: TemplateVariable) => {
    if (readOnly) return
    const focused = lastFocusRef.current?.element.isConnected ? lastFocusRef.current : null
    // 还没点过任何输入框时，默认插到邮件正文末尾。
    const field: TemplateEditorField = focused?.field ?? 'emailBody'
    if (!canInsertVariable(variable, field)) {
      message.warning(t('system.staffNotifications.templates.insertOnlyEmailBody', '这个变量是整块内容，只能放进邮件正文。'))
      return
    }
    const { value, caret } = insertTextAtSelection(
      draft[field],
      focused?.element.selectionStart,
      focused?.element.selectionEnd,
      `{{${variable}}}`,
    )
    // 受控值写回 DOM 会把光标顶到末尾：记下目标光标，渲染提交后（useLayoutEffect）再恢复，让用户可以接着输入。
    if (focused) pendingCaretRef.current = { element: focused.element, caret }
    updateDraft({ [field]: value })
  }

  const handleSave = async () => {
    if (readOnly || saving || !selectedKey || !validation.canSave) return
    setSaving(true)
    try {
      const payload = toTemplateSavePayload(draft)
      if (isNew) {
        const created = await createStaffNotificationTemplate(payload)
        message.success(t('system.staffNotifications.templates.createSuccess', '模板已创建'))
        await loadTemplates(created.key)
      } else {
        const saved = await saveStaffNotificationTemplate(selectedKey, payload)
        message.success(t('system.staffNotifications.templates.saveSuccess', '模板已保存'))
        setTemplates((previous) => previous.map((template) => (template.key === saved.key ? saved : template)))
        setDraft(toTemplateDraft(saved))
      }
    } catch (error) {
      console.error(error)
      // 服务端校验为准：TEMPLATE_INVALID 等失败直接展示服务端给的中文原因。
      message.error(getStaffNotificationErrorMessage(error, t('system.staffNotifications.templates.saveFailed', '保存模板失败')))
    } finally {
      setSaving(false)
    }
  }

  const handleReset = () => {
    if (readOnly || !selectedTemplate) return
    Modal.confirm({
      title: t('system.staffNotifications.templates.resetConfirmTitle', '恢复「{{name}}」的默认文案？', { name: selectedTemplate.name }),
      content: t('system.staffNotifications.templates.resetConfirmContent', '当前的主题、正文和短信文案会被默认版本覆盖，名称和启用状态保持不变。'),
      okText: t('system.staffNotifications.templates.resetOk', '恢复默认'),
      okButtonProps: { danger: true },
      cancelText: t('system.staffNotifications.templates.switchDiscardCancel', '继续编辑'),
      onOk: async () => {
        setResetting(true)
        try {
          const restored = await resetStaffNotificationTemplate(selectedTemplate.key)
          message.success(t('system.staffNotifications.templates.resetSuccess', '已恢复默认文案'))
          setTemplates((previous) => previous.map((template) => (template.key === restored.key ? restored : template)))
          setDraft(toTemplateDraft(restored))
        } catch (error) {
          console.error(error)
          message.error(getStaffNotificationErrorMessage(error, t('system.staffNotifications.templates.resetFailed', '恢复默认文案失败')))
        } finally {
          setResetting(false)
        }
      },
    })
  }

  const chips = getTemplateVariableChips(issuesPassword)
  const translate = (key: string, fallback: string) => t(key, fallback)

  const renderCard = (template: StaffNotificationTemplate) => {
    const meta = getTemplateCardMeta(template)
    const active = template.key === selectedKey
    return (
      <button
        type="button"
        key={template.key}
        className={`staff-notify-card${active ? ' staff-notify-card-active' : ''}${template.isEnabled ? '' : ' staff-notify-card-disabled'}`}
        aria-pressed={active}
        onClick={() => handleSelect(template.key)}
      >
        <span className="staff-notify-card-title">
          <span className="staff-notify-card-name">{template.name}</span>
          <span className="staff-notify-card-tags">
            {!template.isEnabled ? <Tag style={{ marginInlineEnd: 0 }}>{t('system.staffNotifications.templates.cardDisabled', '已停用')}</Tag> : null}
            {template.kind === 'system' ? (
              <Tag color="blue" style={{ marginInlineEnd: 0 }}>{t('system.staffNotifications.templates.cardSystem', '系统')}</Tag>
            ) : null}
          </span>
        </span>
        <span className="staff-notify-card-channels">
          <Tag icon={<MailOutlined />}>{t('system.staffNotifications.templates.cardEmail', '邮件')}</Tag>
          {template.smsBody.trim() ? <Tag icon={<MessageOutlined />}>{t('system.staffNotifications.templates.cardSms', '短信')}</Tag> : null}
        </span>
        <span className="staff-notify-card-meta">
          {meta.kind === 'issuesPassword'
            ? t('system.staffNotifications.templates.cardMetaIssuesPassword', '发放随机密码')
            : t('system.staffNotifications.templates.cardMetaGeneral', '普通通知')}
          {meta.updatedOn ? ` · ${t('system.staffNotifications.templates.cardUpdated', '{{date}} 更新', { date: meta.updatedOn })}` : ''}
        </span>
      </button>
    )
  }

  if (loading && templates.length === 0) {
    return <div className="staff-notify-loading"><Spin /></div>
  }

  if (loadFailed && templates.length === 0) {
    return (
      <Alert
        type="error"
        showIcon
        message={t('system.staffNotifications.templates.loadFailed', '加载模板失败')}
        action={<Button size="small" onClick={() => void loadTemplates()}>{t('system.staffNotifications.templates.retry', '重试')}</Button>}
      />
    )
  }

  const canReset = !readOnly && selectedTemplate?.kind === 'system' && selectedTemplate.isCustomized
  const nameChecks = validation.name

  return (
    <div className="staff-notify-templates">
      {readOnly ? (
        <Alert
          type="info"
          showIcon
          className="staff-notify-readonly-alert"
          message={t('system.staffNotifications.templates.adminOnly', '只有管理员能修改模板，你可以查看现有文案。')}
        />
      ) : null}
      <div className="staff-notify-grid">
        <div className="staff-notify-list">
          {isNew ? (
            <div className="staff-notify-card staff-notify-card-active staff-notify-card-draft">
              <span className="staff-notify-card-title">
                <span className="staff-notify-card-name">{draft.name.trim() || t('system.staffNotifications.templates.newDraftName', '新建模板（未保存）')}</span>
              </span>
              <span className="staff-notify-card-meta">{t('system.staffNotifications.templates.cardMetaGeneral', '普通通知')}</span>
            </div>
          ) : null}
          {templates.map(renderCard)}
          {templates.length === 0 && !isNew ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.staffNotifications.templates.empty', '还没有模板')} />
          ) : null}
          {!readOnly ? (
            <Button block type="dashed" icon={<PlusOutlined />} disabled={isNew} onClick={handleNew}>
              {t('system.staffNotifications.templates.newTemplate', '新建模板')}
            </Button>
          ) : null}
        </div>

        {selectedKey === null ? (
          <div className="staff-notify-editor-empty">
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.staffNotifications.templates.empty', '还没有模板')} />
          </div>
        ) : (
          <div className="staff-notify-editor">
            <div className="staff-notify-editor-head">
              <div>
                <Input
                  addonBefore={t('system.staffNotifications.templates.nameLabel', '模板名称')}
                  aria-label={t('system.staffNotifications.templates.nameLabel', '模板名称')}
                  placeholder={t('system.staffNotifications.templates.namePlaceholder', '例如：门店培训通知')}
                  value={draft.name}
                  maxLength={200}
                  disabled={readOnly}
                  onChange={(event) => updateDraft({ name: event.target.value })}
                />
                <TemplateChecks checks={nameChecks} t={t} onlyProblems />
              </div>
              <Space size={8}>
                <Typography.Text type="secondary">{t('system.staffNotifications.templates.enabled', '启用')}</Typography.Text>
                <Switch
                  aria-label={t('system.staffNotifications.templates.enabled', '启用')}
                  checked={draft.isEnabled}
                  disabled={readOnly}
                  onChange={(checked) => updateDraft({ isEnabled: checked })}
                />
              </Space>
            </div>

            {!readOnly ? (
              <div className="staff-notify-vars">
                <Typography.Text type="secondary" className="staff-notify-vars-hint">
                  {t('system.staffNotifications.templates.insertHint', '点击插入')}
                </Typography.Text>
                {chips.map((chip) => (
                  <button
                    type="button"
                    key={chip.variable}
                    className={`staff-notify-var${chip.mailOnly ? ' staff-notify-var-block' : ''}`}
                    // 按下时不让输入框失焦，插入位置才是用户刚才点的光标处。
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => handleInsert(chip.variable)}
                  >
                    <code>{`{{${chip.variable}}}`}</code> {variableLabel(chip.variable, translate)}
                    {chip.mailOnly ? t('system.staffNotifications.templates.mailOnlySuffix', '（仅邮件）') : ''}
                  </button>
                ))}
              </div>
            ) : null}

            <div className="staff-notify-channels">
              <section className="staff-notify-channel">
                <div className="staff-notify-channel-title"><MailOutlined />{t('system.staffNotifications.templates.emailTitle', '邮件')}</div>
                <label className="staff-notify-field-label" htmlFor="staff-notify-email-subject">
                  {t('system.staffNotifications.templates.emailSubject', '主题')}
                </label>
                <Input
                  id="staff-notify-email-subject"
                  value={draft.emailSubject}
                  placeholder={t('system.staffNotifications.templates.emailSubjectPlaceholder', '邮件主题，只能用英文')}
                  disabled={readOnly}
                  onFocus={trackFocus('emailSubject')}
                  onChange={(event) => updateDraft({ emailSubject: event.target.value })}
                  style={{ marginBottom: 10 }}
                />
                <label className="staff-notify-field-label" htmlFor="staff-notify-email-body">
                  {t('system.staffNotifications.templates.emailBody', '正文')}
                </label>
                <Input.TextArea
                  id="staff-notify-email-body"
                  className="staff-notify-text"
                  rows={17}
                  value={draft.emailBody}
                  placeholder={t('system.staffNotifications.templates.emailBodyPlaceholder', '邮件正文，只能用英文；点上面的变量插入姓名、下载按钮等')}
                  disabled={readOnly}
                  onFocus={trackFocus('emailBody')}
                  onChange={(event) => updateDraft({ emailBody: event.target.value })}
                />
                <TemplateChecks checks={validation.email} t={t} />
              </section>

              <section className="staff-notify-channel">
                <div className="staff-notify-channel-title"><MessageOutlined />{t('system.staffNotifications.templates.smsTitle', '短信')}</div>
                <label className="staff-notify-field-label" htmlFor="staff-notify-sms-body">
                  {t('system.staffNotifications.templates.smsBody', '正文')}
                </label>
                <Input.TextArea
                  id="staff-notify-sms-body"
                  className="staff-notify-text"
                  rows={7}
                  value={draft.smsBody}
                  placeholder={t('system.staffNotifications.templates.smsBodyPlaceholder', '短信正文，只能用英文和常用标点；留空则这个模板不发短信')}
                  disabled={readOnly}
                  onFocus={trackFocus('smsBody')}
                  onChange={(event) => updateDraft({ smsBody: event.target.value })}
                />
                <TemplateChecks checks={validation.sms} t={t} />
                <Alert
                  type="info"
                  showIcon
                  style={{ marginTop: 12 }}
                  message={t('system.staffNotifications.templates.smsInfo', '超过 160 字符会拆成多条计费；出现中文、弯引号或 emoji 时不能保存。')}
                />
              </section>
            </div>

            {!readOnly ? (
              <div className="staff-notify-actions">
                {selectedTemplate?.kind === 'system' ? (
                  <Button disabled={!canReset} loading={resetting} onClick={handleReset}>
                    {t('system.staffNotifications.templates.resetDefault', '恢复默认文案')}
                  </Button>
                ) : null}
                <Button type="primary" loading={saving} disabled={!validation.canSave || (!isNew && !dirty)} onClick={() => void handleSave()}>
                  {t('system.staffNotifications.templates.save', '保存模板')}
                </Button>
              </div>
            ) : null}
          </div>
        )}
      </div>
    </div>
  )
}
