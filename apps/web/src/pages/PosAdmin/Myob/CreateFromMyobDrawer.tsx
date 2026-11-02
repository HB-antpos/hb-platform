import { ArrowLeftOutlined, ArrowRightOutlined, CloseOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Drawer, Form, Input, Popconfirm, Radio, Result, Select, Skeleton, Steps, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { createMyobEmployeeFromCard, getMyobCreateDraft } from '../../../services/myobService'
import type { MyobCreateDraft, MyobCreateFromMyobResult, MyobEmployeeCard, MyobEmploymentType } from '../../../types/myob'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'
import { NewStaffBirthdayAndGuardian, formatBirthdayValue } from '../../System/Users/NewStaffProfileFields'
import { GUARDIAN_RELATIONSHIP_OPTIONS, isStaffPhoneValid } from '../../System/Users/newStaffProfileRules'
import usersMessagesEn from '../../System/Users/usersPageMessages.en.json'
import usersMessagesZh from '../../System/Users/usersPageMessages.zh.json'

import { Avatar } from './MyobBadges'
import {
  MYOB_EMPLOYMENT_TYPES,
  buildCreateRequest,
  buildCreateReviewRows,
  buildInitialCreateFields,
  countEditedRows,
  getBindExistingCandidate,
  getCreateErrorField,
  getCreateWarningKey,
  stripErrorCodePrefix,
  type MyobCreateFieldSource,
  type MyobCreateFormFields,
  type MyobCreatePasswordMode,
  type MyobCreateReviewRow,
} from './myobCreateLogic'
import { formatBirthday, joinSubtitle } from './myobEmployeeLogic'
import { extractMyobError } from './myobLogic'

// 生日 / 监护人字段直接复用「创建用户」的组件（同一套必填与校验），它的文案在用户页的消息文件里，这里一并注册。
registerPageMessages({ zh: usersMessagesZh, en: usersMessagesEn })

type Step = 'fill' | 'review' | 'done'
const STEP_INDEX: Record<Step, number> = { fill: 0, review: 1, done: 2 }

/** 表单里的值：生日是 Dayjs（DatePicker），其余同 MyobCreateFormFields。 */
type FormInputs = Omit<MyobCreateFormFields, 'birthday'> & { birthday?: Dayjs | null }

const EMPLOYMENT_TYPE_KEYS: Record<MyobEmploymentType, string> = {
  FullTime: 'myob.create.employmentTypes.FullTime',
  PartTime: 'myob.create.employmentTypes.PartTime',
  Temporary: 'myob.create.employmentTypes.Temporary',
}

const PASSWORD_MODE_KEYS: Record<MyobCreatePasswordMode, string> = {
  email: 'myob.create.password.emailShort',
  manual: 'myob.create.password.manualShort',
}

const SOURCE_TAGS: Record<MyobCreateFieldSource, { color: string; key: string }> = {
  myob: { color: 'blue', key: 'myob.create.review.source.myob' },
  edited: { color: 'orange', key: 'myob.create.review.source.edited' },
  filled: { color: 'purple', key: 'myob.create.review.source.filled' },
  system: { color: 'default', key: 'myob.create.review.source.system' },
}

const REVIEW_LABEL_KEYS: Record<MyobCreateReviewRow['key'], string> = {
  givenName: 'myob.create.fields.givenName',
  familyName: 'myob.create.fields.familyName',
  username: 'myob.create.fields.username',
  email: 'myob.create.fields.email',
  phone: 'myob.create.fields.phone',
  birthday: 'myob.create.fields.birthday',
  employmentType: 'myob.create.fields.employmentType',
  store: 'myob.create.fields.store',
  guardianName: 'myob.create.fields.guardianName',
  guardianRelationship: 'myob.create.fields.guardianRelationship',
  guardianPhone: 'myob.create.fields.guardianPhone',
  guardianEmail: 'myob.create.fields.guardianEmail',
  password: 'myob.create.fields.password',
}

function toFields(values: FormInputs): MyobCreateFormFields {
  return { ...values, birthday: formatBirthdayValue(values.birthday ?? null) || undefined }
}

interface CreateFromMyobDrawerProps {
  open: boolean
  connectionGuid: string
  connectionName: string
  /** 「MYOB 有、我方没有」里点的那张卡。 */
  card: MyobEmployeeCard
  onClose: () => void
  /** 已建档（不论是否绑上卡）：让列表重新加载。 */
  onCreated: () => void
  /** 邮箱已属于我方在职账号时「改为绑定该账号」；返回 true 表示已绑定，抽屉随即关闭。 */
  onBindExisting: (userGuid: string, userName: string) => Promise<boolean>
}

/**
 * 「我方建档」三步抽屉：填写（MYOB 资料预填，可改）→ 核对（MYOB 原值 / 将写入 / 来源）→ 完成。
 * 建账号走手机端「新建员工」同一套后端规则（StoreStaff 角色、分店范围、未成年监护人、首次登录改密），随后人工绑定这张卡。
 */
export default function CreateFromMyobDrawer({
  open,
  connectionGuid,
  connectionName,
  card,
  onClose,
  onCreated,
  onBindExisting,
}: CreateFromMyobDrawerProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<FormInputs>()
  const [step, setStep] = useState<Step>('fill')
  const [draft, setDraft] = useState<MyobCreateDraft | null>(null)
  const [draftError, setDraftError] = useState<string | null>(null)
  const [reviewFields, setReviewFields] = useState<MyobCreateFormFields | null>(null)
  const [acknowledged, setAcknowledged] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [result, setResult] = useState<MyobCreateFromMyobResult | null>(null)
  const [bindingExisting, setBindingExisting] = useState(false)
  const passwordMode = Form.useWatch('passwordMode', form)
  const draftSeq = useRef(0)
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  const loadDraft = useCallback(async () => {
    const seq = ++draftSeq.current
    setDraft(null)
    setDraftError(null)
    try {
      const data = await getMyobCreateDraft(connectionGuid, card.uid)
      if (!alive.current || seq !== draftSeq.current) return
      setDraft(data)
      const initial = buildInitialCreateFields(data)
      form.setFieldsValue({ ...initial, birthday: initial.birthday ? dayjs(initial.birthday) : null })
    } catch (err) {
      if (!alive.current || seq !== draftSeq.current) return
      setDraftError(extractMyobError(err, t('myob.create.loadFailed')).message)
    }
  }, [card.uid, connectionGuid, form, t])

  useEffect(() => {
    if (open) {
      void loadDraft()
    }
  }, [open, loadDraft])

  const storeLabel = useCallback(
    (code: string) => {
      const store = draft?.stores.find((item) => item.storeCode === code)
      return store ? `${store.storeCode} ${store.storeName}` : code
    },
    [draft],
  )

  const reviewRows = useMemo(() => {
    if (!draft || !reviewFields) return []
    return buildCreateReviewRows(draft, reviewFields, formatSydneyIsoDate(), {
      employmentType: (type) => t(EMPLOYMENT_TYPE_KEYS[type]),
      store: storeLabel,
      relationship: (value) => {
        const option = GUARDIAN_RELATIONSHIP_OPTIONS.find((item) => item.value === value)
        return option ? t(option.labelKey, option.defaultLabel) : value
      },
      passwordMode: (mode: MyobCreatePasswordMode) => t(PASSWORD_MODE_KEYS[mode]),
    })
  }, [draft, reviewFields, storeLabel, t])

  const goReview = async () => {
    try {
      const values = await form.validateFields()
      setReviewFields(toFields(values))
      setAcknowledged(false)
      setSubmitError(null)
      setStep('review')
    } catch (err) {
      // 校验失败：滚到第一个标红的字段
      const first = (err as { errorFields?: { name: (string | number)[] }[] }).errorFields?.[0]?.name
      if (first) form.scrollToField(first, { block: 'center' })
    }
  }

  const handleSubmit = async () => {
    if (!draft || !reviewFields || submitting) return
    setSubmitting(true)
    setSubmitError(null)
    try {
      const created = await createMyobEmployeeFromCard(buildCreateRequest(draft, reviewFields, formatSydneyIsoDate()))
      if (!alive.current) return
      setResult(created)
      setStep('done')
      onCreated()
    } catch (err) {
      if (!alive.current) return
      const parsed = extractMyobError(err, t('myob.create.submitFailed'))
      const text = stripErrorCodePrefix(parsed.message, parsed.code)
      const field = getCreateErrorField(parsed.code ?? undefined, text)
      if (field) {
        // 与某个字段有关的错误（用户名 / 邮箱重复、监护人不合规等）：退回第 1 步并把错误标在该字段上
        setStep('fill')
        form.setFields([{ name: field, errors: [text] }])
        window.setTimeout(() => form.scrollToField(field, { block: 'center' }), 0)
      } else {
        setSubmitError(text)
      }
    } finally {
      if (alive.current) setSubmitting(false)
    }
  }

  const bindCandidate = draft ? getBindExistingCandidate(draft.warnings) : null
  const handleBindExisting = async () => {
    if (!bindCandidate?.userGuid) return
    setBindingExisting(true)
    try {
      const bound = await onBindExisting(bindCandidate.userGuid, bindCandidate.userName ?? '')
      if (bound) onClose()
    } finally {
      if (alive.current) setBindingExisting(false)
    }
  }

  const closeDisabled = submitting || bindingExisting

  // ───────────────────────── 第 1 步：填写 ─────────────────────────

  const myobHint = (value: string | null | undefined) =>
    value ? <span className="myob-sub">{t('myob.create.myobValue', { value })}</span> : null

  const employmentHint = draft
    ? draft.employmentType
      ? t('myob.create.hints.employmentMapped', {
          status: draft.card.employmentStatus ?? '—',
          type: t(EMPLOYMENT_TYPE_KEYS[draft.employmentType]),
        })
      : t('myob.create.hints.employmentUnmapped', { status: draft.card.employmentStatus ?? '—' })
    : null

  const fillBody = draft ? (
    <div className="myob-drawer-body">
      <div className="myob-summary">
        <div className="myob-summary-row">
          <Avatar name={card.name} />
          <div style={{ minWidth: 0 }}>
            <span className="myob-person-name">{card.name}</span>
            <span className="myob-person-tags">
              {card.isActive ? <Tag>{t('myob.employees.missing.theirs.active')}</Tag> : <Tag color="error">{t('myob.employees.missing.theirs.inactive')}</Tag>}
              {card.displayId ? <Tag>{card.displayId}</Tag> : null}
            </span>
            <span className="myob-sub">
              {joinSubtitle([
                connectionName,
                draft.myobPhone ? t('myob.drawer.phone', { value: draft.myobPhone }) : null,
                draft.birthday ? t('myob.drawer.birthday', { value: formatBirthday(draft.birthday) ?? draft.birthday }) : null,
              ])}
            </span>
          </div>
        </div>
      </div>

      {draft.warnings.map((warning) => {
        const key = getCreateWarningKey(warning)
        const isBindCandidate = bindCandidate !== null && warning === bindCandidate
        return (
          <Alert
            key={warning.code}
            type={warning.code === 'MISSING_FIELDS' || warning.code === 'NO_MAPPED_STORE' ? 'info' : 'warning'}
            showIcon
            message={key ? t(key, { name: warning.userName ?? '' }) : warning.message}
            action={
              isBindCandidate ? (
                <Popconfirm
                  title={t('myob.create.bindExisting.confirmTitle', { name: warning.userName ?? '', card: card.name })}
                  okText={t('myob.create.bindExisting.ok')}
                  cancelText={t('myob.employees.actions.cancel')}
                  onConfirm={() => void handleBindExisting()}
                >
                  <Button size="small" loading={bindingExisting}>
                    {t('myob.create.bindExisting.button', { name: warning.userName ?? '' })}
                  </Button>
                </Popconfirm>
              ) : undefined
            }
          />
        )
      })}

      <Form form={form} layout="vertical" requiredMark className="myob-create-form" disabled={bindingExisting}>
        <h4 className="myob-create-section">{t('myob.create.sections.basic')}</h4>
        <div className="myob-create-grid">
          <Form.Item
            label={t('myob.create.fields.givenName')}
            name="givenName"
            extra={myobHint(draft.givenName)}
            rules={[{ required: true, whitespace: true, message: t('myob.create.rules.givenName') }, { max: 50 }]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={t('myob.create.fields.familyName')}
            name="familyName"
            extra={myobHint(draft.familyName)}
            rules={[{ required: true, whitespace: true, message: t('myob.create.rules.familyName') }, { max: 50 }]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={t('myob.create.fields.email')}
            name="email"
            extra={myobHint(draft.email)}
            rules={[
              { required: true, whitespace: true, message: t('myob.create.rules.email') },
              { type: 'email', message: t('myob.create.rules.emailInvalid') },
            ]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={t('myob.create.fields.phone')}
            name="phone"
            extra={myobHint(draft.myobPhone)}
            rules={[
              { required: true, whitespace: true, message: t('myob.create.rules.phone') },
              {
                validator: (_, value?: string) =>
                  !value?.trim() || isStaffPhoneValid(value) ? Promise.resolve() : Promise.reject(new Error(t('myob.create.rules.phoneInvalid'))),
              },
            ]}
          >
            <Input inputMode="tel" autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={t('myob.create.fields.username')}
            name="username"
            extra={<span className="myob-sub">{t('myob.create.hints.username')}</span>}
            rules={[
              { required: true, whitespace: true, message: t('myob.create.rules.username') },
              { min: 3, max: 50, message: t('myob.create.rules.usernameLength') },
            ]}
          >
            <Input autoComplete="off" />
          </Form.Item>
        </div>
        {/* 生日 + 年龄；未满 18 岁时展开监护人四项（MYOB 没有，只能人工填）。复用的组件不能挂原值提示，MYOB 生日显示在顶部摘要里 */}
        <NewStaffBirthdayAndGuardian />

        <h4 className="myob-create-section">{t('myob.create.sections.employment')}</h4>
        <Form.Item
          label={t('myob.create.fields.employmentType')}
          name="employmentType"
          extra={employmentHint ? <span className="myob-sub">{employmentHint}</span> : null}
          rules={[{ required: true, message: t('myob.create.rules.employmentType') }]}
        >
          <Radio.Group optionType="button">
            {MYOB_EMPLOYMENT_TYPES.map((type) => (
              <Radio.Button key={type} value={type}>
                {t(EMPLOYMENT_TYPE_KEYS[type])}
              </Radio.Button>
            ))}
          </Radio.Group>
        </Form.Item>
        <Form.Item
          label={t('myob.create.fields.store')}
          name="storeCode"
          extra={<span className="myob-sub">{t('myob.create.hints.store')}</span>}
          rules={[{ required: true, message: t('myob.create.rules.store') }]}
        >
          <Select
            showSearch
            optionFilterProp="search"
            placeholder={t('myob.create.storePlaceholder')}
            options={draft.stores.map((store) => ({
              value: store.storeCode,
              search: `${store.storeCode} ${store.storeName}`,
              label: (
                <span className="myob-create-store-option">
                  <span>{`${store.storeCode} ${store.storeName}`}</span>
                  {store.mappedToThisCompany ? <Tag color="blue">{t('myob.create.storeMapped')}</Tag> : null}
                </span>
              ),
            }))}
          />
        </Form.Item>

        <h4 className="myob-create-section">{t('myob.create.sections.login')}</h4>
        <Form.Item name="passwordMode" extra={<span className="myob-sub">{t('myob.create.password.note')}</span>}>
          <Radio.Group className="myob-create-password-mode">
            <Radio value="email">{t('myob.create.password.email')}</Radio>
            <Radio value="manual">{t('myob.create.password.manual')}</Radio>
          </Radio.Group>
        </Form.Item>
        {passwordMode === 'manual' ? (
          <Form.Item
            label={t('myob.create.fields.password')}
            name="password"
            rules={[
              { required: true, message: t('myob.create.rules.password') },
              { min: 6, max: 100, message: t('myob.create.rules.passwordLength') },
            ]}
          >
            <Input.Password autoComplete="new-password" style={{ maxWidth: 320 }} />
          </Form.Item>
        ) : null}
      </Form>
    </div>
  ) : draftError ? (
    <Alert
      type="error"
      showIcon
      message={t('myob.create.loadFailed')}
      description={draftError}
      action={
        <Button size="small" onClick={() => void loadDraft()}>
          {t('myob.create.retry')}
        </Button>
      }
    />
  ) : (
    <Skeleton active paragraph={{ rows: 10 }} />
  )

  // ───────────────────────── 第 2 步：核对 ─────────────────────────

  const columns: ColumnsType<MyobCreateReviewRow> = [
    { title: t('myob.create.review.columns.field'), key: 'field', width: 130, render: (_, row) => t(REVIEW_LABEL_KEYS[row.key]) },
    {
      title: t('myob.create.review.columns.myob'),
      key: 'myob',
      render: (_, row) => (row.myobValue ? <span className="myob-create-myob-value">{row.myobValue}</span> : <span className="myob-sub">—</span>),
    },
    {
      title: t('myob.create.review.columns.value'),
      key: 'value',
      render: (_, row) => <span className="myob-person-name">{row.value || '—'}</span>,
    },
    {
      title: t('myob.create.review.columns.source'),
      key: 'source',
      width: 110,
      render: (_, row) => <Tag color={SOURCE_TAGS[row.source].color}>{t(SOURCE_TAGS[row.source].key)}</Tag>,
    },
  ]
  const editedCount = countEditedRows(reviewRows)

  const reviewBody =
    draft && reviewFields ? (
      <div className="myob-drawer-body">
        <Alert
          type="info"
          showIcon
          message={t('myob.create.review.summary', {
            username: reviewFields.username?.trim() ?? '',
            store: storeLabel(reviewFields.storeCode ?? ''),
            card: card.name,
            company: connectionName,
          })}
          description={editedCount > 0 ? t('myob.create.review.edited', { count: editedCount }) : t('myob.create.review.noEdits')}
        />
        <MeasuredTable<MyobCreateReviewRow>
          metricId="pos-admin.myob.create-review"
          rowKey="key"
          size="small"
          columns={columns}
          dataSource={reviewRows}
          pagination={false}
          scroll={{ x: 'max-content' }}
          rowClassName={(row) => (row.source === 'edited' ? 'myob-create-row-edited' : '')}
        />
        {submitError ? <Alert type="error" showIcon message={t('myob.create.submitFailed')} description={submitError} /> : null}
      </div>
    ) : null

  // ───────────────────────── 第 3 步：完成 ─────────────────────────

  const doneBody = result ? (
    <div className="myob-drawer-body">
      <Result
        status={result.bound ? 'success' : 'warning'}
        title={result.bound ? t('myob.create.done.boundTitle') : t('myob.create.done.unboundTitle')}
        subTitle={t('myob.create.done.subtitle', { username: result.username, store: storeLabel(result.storeCode), card: card.name })}
      />
      {result.bindError ? (
        <Alert type="warning" showIcon message={t('myob.create.done.bindFailed')} description={t('myob.create.done.bindFailedHint', { reason: result.bindError })} />
      ) : null}
      {result.passwordSetupEmailTo ? (
        <Alert type="success" showIcon message={t('myob.create.done.emailSent', { email: result.passwordSetupEmailTo })} />
      ) : null}
      {result.passwordSetupEmailError ? (
        <Alert type="warning" showIcon message={t('myob.create.done.emailFailed', { reason: result.passwordSetupEmailError })} />
      ) : null}
      {reviewFields?.passwordMode === 'manual' ? <Alert type="info" showIcon message={t('myob.create.done.manualPassword')} /> : null}
      <Alert type="info" showIcon message={t('myob.create.done.syncTip')} />
    </div>
  ) : null

  // ───────────────────────── 抽屉 ─────────────────────────

  let footer
  if (step === 'fill') {
    footer = (
      <div className="myob-sync-footer">
        <span className="myob-sync-footer-note">{t('myob.create.fillNote')}</span>
        <Button onClick={onClose} disabled={closeDisabled}>
          {t('myob.employees.actions.cancel')}
        </Button>
        <Button type="primary" disabled={!draft || bindingExisting} onClick={() => void goReview()}>
          {t('myob.create.next')} <ArrowRightOutlined />
        </Button>
      </div>
    )
  } else if (step === 'review') {
    footer = (
      <div className="myob-sync-footer">
        <Checkbox checked={acknowledged} disabled={submitting} onChange={(event) => setAcknowledged(event.target.checked)}>
          {t('myob.create.review.ack')}
        </Checkbox>
        <span className="myob-sync-footer-note" />
        <Button icon={<ArrowLeftOutlined />} disabled={submitting} onClick={() => setStep('fill')}>
          {t('myob.create.back')}
        </Button>
        <Button type="primary" loading={submitting} disabled={!acknowledged} onClick={() => void handleSubmit()}>
          {t('myob.create.confirm')}
        </Button>
      </div>
    )
  } else {
    footer = (
      <div className="myob-sync-footer">
        <span className="myob-sync-footer-note" />
        <Button type="primary" onClick={onClose}>
          {t('myob.create.done.close')}
        </Button>
      </div>
    )
  }

  return (
    <Drawer
      open={open}
      width={760}
      rootClassName="myob-drawer myob-sync-drawer"
      destroyOnClose
      onClose={onClose}
      maskClosable={!closeDisabled}
      keyboard={!closeDisabled}
      closable={false}
      extra={<Button type="text" icon={<CloseOutlined />} disabled={closeDisabled} aria-label={t('myob.create.close')} onClick={onClose} />}
      title={
        <div className="myob-sync-head">
          <div>{t('myob.create.title', { name: card.name })}</div>
          <span className="myob-sub" style={{ fontWeight: 400 }}>
            {t('myob.create.subtitle', { company: connectionName })}
          </span>
          <Steps
            size="small"
            responsive={false}
            current={STEP_INDEX[step]}
            items={[{ title: t('myob.create.steps.fill') }, { title: t('myob.create.steps.review') }, { title: t('myob.create.steps.done') }]}
          />
        </div>
      }
      footer={footer}
    >
      {/* 表单在第 1 步之外仍保持挂载（只隐藏），返回修改时保留已填内容 */}
      <div className="myob-sync-scroll" hidden={step !== 'fill'}>
        {fillBody}
      </div>
      {step === 'review' ? <div className="myob-sync-scroll">{reviewBody}</div> : null}
      {step === 'done' ? <div className="myob-sync-scroll">{doneBody}</div> : null}
    </Drawer>
  )
}
