import { LockOutlined } from '@ant-design/icons'
import { Alert, Button, Form, Input, Select, Spin, Tag, message } from 'antd'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getManagedEmployeeProfile,
  saveManagedEmployeeProfile,
} from '../../../services/employeeProfileService'
import type {
  EmployeeEmploymentType,
  EmployeeProfileGender,
  EmployeeProfileManagedDetailDto,
} from '../../../types/employeeProfile'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import {
  buildManagedProfilePayload,
  getProfileCompletion,
  getRequestErrorCode,
  type ManagedProfileFormValues,
} from './logic'
import './employeeProfiles.css'
import employeeProfilesMessagesEn from './employeeProfilesMessages.en.json'
import employeeProfilesMessagesZh from './employeeProfilesMessages.zh.json'

// 编辑器会被「员工个人信息维护」页和「用户管理」抽屉共用；用户管理页可能先于员工资料页加载，所以这里也要注册（幂等）。
registerPageMessages({ zh: employeeProfilesMessagesZh, en: employeeProfilesMessagesEn })

const SCOPE_FORBIDDEN_CODE = 'EMPLOYEE_PROFILE_MANAGED_SCOPE_FORBIDDEN'
const POSITION_FORBIDDEN_CODE = 'EMPLOYEE_PROFILE_POSITION_TYPE_FORBIDDEN'

export interface ManagedEmployeeProfileEditorProps {
  userGuid: string
  /** 持有 EmployeeProfiles.EditManagedStore；没有则整张表单只读、不显示保存按钮。 */
  canEdit: boolean
  onSaved?: (detail: EmployeeProfileManagedDetailDto) => void
  /** 已修改的字段数变化时通知外层（抽屉关闭前的未保存确认用）。 */
  onDirtyChange?: (count: number) => void
  /** 有值时在「敏感资料」区块下显示跳转到完整编辑器的入口（仅管理员有意义）。 */
  onOpenFullEditor?: () => void
}

function toFormValues(detail: EmployeeProfileManagedDetailDto): ManagedProfileFormValues {
  return {
    phone: detail.phone,
    gender: detail.gender,
    employmentType: detail.employmentType,
    address: detail.address,
  }
}

/**
 * 员工个人信息（非敏感项）编辑器：手机、性别、员工类型、地址可改；
 * 银行 / 公积金 / 生日 / 证件号只展示脱敏摘要。数据范围由后端 /managed 接口强制，这里不做范围判断。
 */
export default function ManagedEmployeeProfileEditor({
  userGuid,
  canEdit,
  onSaved,
  onDirtyChange,
  onOpenFullEditor,
}: ManagedEmployeeProfileEditorProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<ManagedProfileFormValues>()
  const [detail, setDetail] = useState<EmployeeProfileManagedDetailDto | null>(null)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [dirtyCount, setDirtyCount] = useState(0)
  const [loadError, setLoadError] = useState<string | null>(null)
  const initialValuesRef = useRef<ManagedProfileFormValues>({})
  const guardRef = useRef(createLatestRequestGuard())
  const onDirtyChangeRef = useRef(onDirtyChange)
  onDirtyChangeRef.current = onDirtyChange

  const employmentTypeOptions = useMemo(
    () =>
      (['fullTime', 'partTime', 'casual'] as EmployeeEmploymentType[]).map((value) => ({
        value,
        label: t(`system.employeeProfiles.employmentTypes.${value}`),
      })),
    [t],
  )
  const genderOptions = useMemo(
    () =>
      (['male', 'female', 'other', 'unknown'] as EmployeeProfileGender[]).map((value) => ({
        value,
        label: t(`system.employeeProfiles.genders.${value}`),
      })),
    [t],
  )

  const updateDirty = (count: number) => {
    setDirtyCount(count)
    onDirtyChangeRef.current?.(count)
  }

  const applyDetail = (next: EmployeeProfileManagedDetailDto) => {
    const values = toFormValues(next)
    setDetail(next)
    initialValuesRef.current = values
    form.setFieldsValue({ phone: undefined, gender: undefined, employmentType: undefined, address: undefined })
    form.setFieldsValue(values)
    updateDirty(0)
  }

  useEffect(() => {
    const guard = guardRef.current
    setDetail(null)
    setLoadError(null)
    form.resetFields()
    updateDirty(0)
    void runLatestGuardedRequest(guard, () => getManagedEmployeeProfile(userGuid), {
      onStart: () => setLoading(true),
      onSuccess: applyDetail,
      onError: (error) => {
        console.error(error)
        setLoadError(
          getRequestErrorCode(error) === SCOPE_FORBIDDEN_CODE
            ? t('system.employeeProfiles.managed.scopeForbidden')
            : t('system.employeeProfiles.managed.loadFailed'),
        )
      },
      onSettled: () => setLoading(false),
    })
    return () => guard.invalidate()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userGuid])

  const canEditEmploymentType = Boolean(detail?.canEditEmploymentType)

  const handleValuesChange = () => {
    const payload = buildManagedProfilePayload(initialValuesRef.current, form.getFieldsValue(), {
      canEditEmploymentType,
    })
    updateDirty(Object.keys(payload).length)
  }

  const handleSave = async () => {
    if (!detail) {
      return
    }
    const payload = buildManagedProfilePayload(initialValuesRef.current, form.getFieldsValue(), {
      canEditEmploymentType,
    })
    if (Object.keys(payload).length === 0) {
      return
    }
    setSaving(true)
    try {
      const saved = await saveManagedEmployeeProfile(userGuid, payload)
      message.success(t('system.employeeProfiles.managed.saveSuccess'))
      applyDetail(saved)
      onSaved?.(saved)
    } catch (error) {
      console.error(error)
      const code = getRequestErrorCode(error)
      message.error(
        code === SCOPE_FORBIDDEN_CODE
          ? t('system.employeeProfiles.managed.scopeForbidden')
          : code === POSITION_FORBIDDEN_CODE
            ? t('system.employeeProfiles.managed.positionForbidden')
            : t('system.employeeProfiles.managed.saveFailed'),
      )
    } finally {
      setSaving(false)
    }
  }

  if (loading && !detail) {
    return (
      <div style={{ padding: '48px 0', textAlign: 'center' }}>
        <Spin />
      </div>
    )
  }
  if (!detail) {
    return loadError ? <Alert type="error" showIcon message={loadError} /> : null
  }

  const completion = getProfileCompletion(detail)
  const missingText = completion.missing
    .map((part) =>
      t(part === 'bank' ? 'system.employeeProfiles.completion.missingBank' : 'system.employeeProfiles.completion.missingSuper'),
    )
    .join(' · ')
  const notFilled = <span className="sys-emp-missing">{t('system.employeeProfiles.notFilled')}</span>

  return (
    <div className="sys-emp-managed">
      {completion.status === 'incomplete' ? (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('system.employeeProfiles.managed.incompleteNotice', { missing: missingText })}
        />
      ) : null}
      {!canEdit ? (
        <Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('system.employeeProfiles.managed.readOnlyNotice')} />
      ) : null}

      <div className="sys-emp-managed-tags">
        <span>
          <span className="sys-emp-sub">{t('system.employeeProfiles.managed.stores')}：</span>
          {detail.storeNames.length ? (
            detail.storeNames.map((name) => (
              <Tag key={name} bordered={false} color="blue">
                {name}
              </Tag>
            ))
          ) : (
            <span className="sys-emp-faint">{t('system.employeeProfiles.managed.noStore')}</span>
          )}
        </span>
        <span>
          <span className="sys-emp-sub">{t('system.employeeProfiles.managed.roles')}：</span>
          {detail.roleNames.map((name) => (
            <Tag key={name} bordered={false}>
              {name}
            </Tag>
          ))}
        </span>
      </div>

      <Form form={form} layout="vertical" requiredMark={false} disabled={!canEdit || saving} onValuesChange={handleValuesChange}>
        <section className="sys-emp-section">
          <h4 className="sys-emp-section-title">{t('system.employeeProfiles.managed.basic')}</h4>
          <div className="sys-emp-grid-2">
            <Form.Item name="phone" label={t('system.employeeProfiles.managed.phone')} rules={[{ max: 50 }]}>
              <Input placeholder={t('system.employeeProfiles.managed.phonePlaceholder')} />
            </Form.Item>
            <Form.Item name="gender" label={t('system.employeeProfiles.gender')}>
              <Select allowClear options={genderOptions} placeholder={t('system.employeeProfiles.placeholders.gender')} />
            </Form.Item>
            <Form.Item
              name="employmentType"
              label={t('system.employeeProfiles.employmentType')}
              extra={canEditEmploymentType ? undefined : t('system.employeeProfiles.managed.employmentLocked')}
            >
              <Select
                allowClear
                disabled={!canEdit || saving || !canEditEmploymentType}
                options={employmentTypeOptions}
                placeholder={t('system.employeeProfiles.placeholders.employmentType')}
              />
            </Form.Item>
            <Form.Item className="sys-emp-span-all" name="address" label={t('system.employeeProfiles.address')} rules={[{ max: 500 }]}>
              <Input.TextArea autoSize={{ minRows: 2, maxRows: 4 }} placeholder={t('system.employeeProfiles.placeholders.address')} />
            </Form.Item>
          </div>
        </section>
      </Form>

      {/* 敏感资料只读，收进一张卡片与上面可编辑的表单区分开。 */}
      <section className="sys-emp-managed-sensitive">
        <h4 className="sys-emp-section-title sys-emp-managed-sensitive-head">
          {t('system.employeeProfiles.managed.sensitiveTitle')}
          <span className="sys-emp-sensitive">
            <LockOutlined style={{ fontSize: 11 }} />
            {t('system.employeeProfiles.drawer.sensitive')}
          </span>
        </h4>
        <div className="sys-emp-grid-2 sys-emp-managed-sensitive-body">
          <ReadonlyField label={t('system.employeeProfiles.bankBsb')} mono>
            {detail.bankBsb ?? notFilled}
          </ReadonlyField>
          <ReadonlyField label={t('system.employeeProfiles.bankAccountNumber')} mono>
            {detail.bankAccountNumber ?? notFilled}
          </ReadonlyField>
          <ReadonlyField label={t('system.employeeProfiles.superannuationCompanyName')}>
            {detail.superannuationCompanyName ?? notFilled}
          </ReadonlyField>
          <ReadonlyField label={t('system.employeeProfiles.superannuationAccountNumber')} mono>
            {detail.superannuationAccountNumber ?? notFilled}
          </ReadonlyField>
          <ReadonlyField label={t('system.employeeProfiles.managed.birthday')}>
            {detail.hasBirthday ? t('system.employeeProfiles.managed.filled') : notFilled}
          </ReadonlyField>
          <ReadonlyField label={`${t('system.employeeProfiles.identityType')} / ${t('system.employeeProfiles.identityId')}`} mono>
            {detail.identityType || detail.identityId
              ? [detail.identityType, detail.identityId].filter(Boolean).join(' · ')
              : notFilled}
          </ReadonlyField>
        </div>
      </section>
      <Alert
        type="info"
        showIcon
        style={{ marginTop: 16 }}
        message={t('system.employeeProfiles.managed.sensitiveNotice')}
        action={
          onOpenFullEditor ? (
            <Button size="small" type="link" onClick={onOpenFullEditor}>
              {t('system.employeeProfiles.managed.openFullEditor')}
            </Button>
          ) : undefined
        }
      />

      {canEdit ? (
        <div className="sys-emp-drawer-footer sys-emp-managed-footer">
          {dirtyCount > 0 ? (
            <span className="sys-emp-dirty">{t('system.employeeProfiles.managed.dirty', { count: dirtyCount })}</span>
          ) : null}
          <span className="sys-emp-drawer-footer-spacer" />
          <Button
            disabled={dirtyCount === 0 || saving}
            onClick={() => {
              form.setFieldsValue(initialValuesRef.current)
              updateDirty(0)
            }}
          >
            {t('system.employeeProfiles.managed.reset')}
          </Button>
          <Button type="primary" loading={saving} disabled={dirtyCount === 0} onClick={() => void handleSave()}>
            {t('system.employeeProfiles.managed.save')}
          </Button>
        </div>
      ) : null}
    </div>
  )
}

function ReadonlyField({
  label,
  mono,
  children,
}: {
  label: string
  mono?: boolean
  children: ReactNode
}) {
  return (
    <div className="ant-form-item" style={{ marginBottom: 12 }}>
      <div className="sys-emp-sub" style={{ marginBottom: 4 }}>
        {label}
      </div>
      <div className={mono ? 'sys-emp-mono' : undefined}>{children}</div>
    </div>
  )
}
