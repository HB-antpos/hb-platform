import { Form, Input, InputNumber, Modal, Select, Switch, message } from 'antd'
import { useRef, useState } from 'react'
import type { ChangeEvent, ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import type { SavePrefixCodePayload } from '../../../types/productPrefixCode'
import prefixCodeMessagesEn from './prefixCodeMessages.en.json'
import prefixCodeMessagesZh from './prefixCodeMessages.zh.json'
import {
  PREFIX_CODE_MAX_LENGTH,
  PREFIX_DESCRIPTION_MAX_LENGTH,
  PREFIX_SORT_ORDER_MAX,
  buildPrefixPayload,
  getPrefixApiErrorCode,
  isFormValidationError,
  validatePrefixCode,
} from './prefixCodeRules'
import type { PrefixCodeFormValues, PrefixCodeRuleError } from './prefixCodeRules'
import './prefixCode.css'

// 前缀相关文案（前缀管理页 + 创建页里的前缀弹窗）随这个共用组件一起注册：任何用到它的页面都能拿到文案，
// 且不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。重复注册幂等。
registerPageMessages({ zh: prefixCodeMessagesZh, en: prefixCodeMessagesEn })

export interface PrefixSupplierOption {
  label: string
  value: string
}

export interface PrefixCodeFormModalProps {
  open: boolean
  mode: 'create' | 'edit'
  /** 打开时的初始值；编辑时来自当前行，新增时一般只给默认启用与排序。 */
  initialValues?: PrefixCodeFormValues
  /** 新增且未固定供应商时可选的供应商列表。 */
  supplierOptions?: PrefixSupplierOption[]
  supplierLoading?: boolean
  /**
   * 固定的供应商：只读展示。创建页里「管理某供应商的前缀」和编辑已有前缀（后端不允许改供应商）都走这里。
   * 新增时 payload 的 supplierCode 取自它。
   */
  fixedSupplier?: { code: string; name?: string }
  /**
   * 提交：调用方在这里发请求；失败请抛出 Error（弹窗会就地提示并保持打开），
   * 成功后由调用方自己把 open 置为 false 并刷新列表。
   */
  onSubmit: (payload: SavePrefixCodePayload) => Promise<void>
  onCancel: () => void
}

/** 标签右侧的字数计数 `3 / 10`：超出上限即时变红，不必等到提交后才收到 400。 */
function CountedLabel({ name, label, max }: { name: string; label: ReactNode; max: number }) {
  return (
    <span className="prefix-code-label">
      <span>{label}</span>
      <Form.Item noStyle shouldUpdate={(previous, next) => previous[name] !== next[name]}>
        {({ getFieldValue }) => {
          const length = String(getFieldValue(name) ?? '').trim().length
          return (
            <span className={length > max ? 'prefix-code-count prefix-code-count-over' : 'prefix-code-count'}>
              {length} / {max}
            </span>
          )
        }}
      </Form.Item>
    </span>
  )
}

/** 带说明的整行开关：Form.Item 通过 valuePropName="checked" 注入 checked / onChange。 */
function ActiveSwitchRow({ checked, onChange }: { checked?: boolean; onChange?: (checked: boolean) => void }) {
  const { t } = useTranslation()
  return (
    <div className="prefix-code-switch-row">
      <span>{t('prefixCode.activeHint')}</span>
      <Switch
        checked={checked}
        onChange={onChange}
        checkedChildren={t('common.active')}
        unCheckedChildren={t('common.inactive')}
      />
    </div>
  )
}

/**
 * 新增 / 编辑前缀的共用弹窗：前缀管理页和「货号条码创建」里的「管理前缀」共用同一份表单与规则。
 * 规则：仅字母数字、长度 ≤ 10、保存自动转大写（见 prefixCodeRules.ts）。
 */
export default function PrefixCodeFormModal({
  open,
  mode,
  initialValues,
  supplierOptions = [],
  supplierLoading = false,
  fixedSupplier,
  onSubmit,
  onCancel,
}: PrefixCodeFormModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<PrefixCodeFormValues>()
  const [submitting, setSubmitting] = useState(false)
  // state 在同一帧内不会立刻变，快速连按 Enter 会重复提交，用 ref 同步拦住。
  const submittingRef = useRef(false)

  const showSupplierSelect = mode === 'create' && !fixedSupplier

  const getRuleMessage = (error: PrefixCodeRuleError) => {
    switch (error) {
      case 'required':
        return t('prefixCode.enterPrefixName')
      case 'pattern':
        return t('prefixCode.prefixNameAlphaNum')
      default:
        return t('prefixCode.prefixTooLong', { max: PREFIX_CODE_MAX_LENGTH })
    }
  }

  const handleOk = async () => {
    if (submittingRef.current) {
      return
    }

    try {
      const values = await form.validateFields()
      submittingRef.current = true
      setSubmitting(true)
      const supplierCode = mode === 'create' ? fixedSupplier?.code ?? values.supplierCode : undefined
      await onSubmit(buildPrefixPayload(values, supplierCode))
    } catch (error) {
      if (isFormValidationError(error)) {
        return
      }

      if (getPrefixApiErrorCode(error) === 'PREFIX_NAME_EXISTS') {
        // 同一供应商下前缀重复：直接标在前缀字段上，比全局提示更好定位。
        form.setFields([{ name: 'prefixName', errors: [t('prefixCode.duplicateName')] }])
        return
      }

      console.error(error)
      message.error(error instanceof Error && error.message ? error.message : t('prefixCode.saveFailed'))
    } finally {
      submittingRef.current = false
      setSubmitting(false)
    }
  }

  const supplierText = fixedSupplier
    ? fixedSupplier.name
      ? `${fixedSupplier.name} · ${fixedSupplier.code}`
      : fixedSupplier.code
    : ''

  return (
    <Modal
      title={mode === 'create' ? t('prefixCode.addPrefix') : t('prefixCode.editTitle', { name: initialValues?.prefixName ?? '' })}
      open={open}
      onCancel={onCancel}
      onOk={() => void handleOk()}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={submitting}
      cancelButtonProps={{ disabled: submitting }}
      closable={!submitting}
      keyboard={!submitting}
      // 误点遮罩会丢掉已填内容，统一关闭点遮罩关闭。
      maskClosable={false}
      width={560}
      destroyOnHidden
      data-testid="prefix-code-form-modal"
    >
      {/* preserve={false}：弹窗关闭后清空字段，下次打开按 initialValues 重新初始化，不会残留上次的输入。 */}
      <Form form={form} layout="vertical" initialValues={initialValues} autoComplete="off" preserve={false}>
        <div className="prefix-code-form-grid">
          {showSupplierSelect ? (
            <Form.Item
              className="prefix-code-span-all"
              label={t('prefixCode.formSupplier')}
              name="supplierCode"
              rules={[{ required: true, message: t('domesticProducts.selectSupplier') }]}
            >
              <Select
                showSearch
                optionFilterProp="label"
                options={supplierOptions}
                loading={supplierLoading}
                placeholder={t('prefixCode.supplierPlaceholder')}
              />
            </Form.Item>
          ) : (
            <Form.Item className="prefix-code-span-all" label={t('prefixCode.formSupplier')}>
              <Input disabled value={supplierText} />
            </Form.Item>
          )}

          <Form.Item
            label={<CountedLabel name="prefixName" label={t('prefixCode.prefixName')} max={PREFIX_CODE_MAX_LENGTH} />}
            name="prefixName"
            required
            // 输入时就转大写：所见即所存；保存时 normalizePrefixCode 还会再整理一次（含首尾空白）。
            getValueFromEvent={(event: ChangeEvent<HTMLInputElement>) => event.target.value.toUpperCase()}
            extra={<span className="prefix-code-sub">{t('prefixCode.prefixHint')}</span>}
            rules={[
              {
                validator: (_rule, value) => {
                  const error = validatePrefixCode(value)
                  return error ? Promise.reject(new Error(getRuleMessage(error))) : Promise.resolve()
                },
              },
            ]}
          >
            <Input
              className="prefix-code-upper"
              autoFocus
              autoComplete="off"
              onPressEnter={() => void handleOk()}
              data-testid="prefix-code-form-prefix"
            />
          </Form.Item>

          <Form.Item
            label={t('prefixCode.formSort')}
            name="sortOrder"
            extra={<span className="prefix-code-sub">{t('prefixCode.sortHint')}</span>}
          >
            <InputNumber
              min={0}
              max={PREFIX_SORT_ORDER_MAX}
              precision={0}
              style={{ width: '100%' }}
              data-testid="prefix-code-form-sort"
            />
          </Form.Item>

          <Form.Item
            className="prefix-code-span-all"
            label={<CountedLabel name="prefixDescription" label={t('prefixCode.prefixDescription')} max={PREFIX_DESCRIPTION_MAX_LENGTH} />}
            name="prefixDescription"
            rules={[{ max: PREFIX_DESCRIPTION_MAX_LENGTH, message: t('prefixCode.descriptionTooLong', { max: PREFIX_DESCRIPTION_MAX_LENGTH }) }]}
          >
            {/* 说明常用中文输入法：Safari 里确认候选词的回车会触发 onPressEnter，误提交，所以这里不绑回车提交。 */}
            <Input autoComplete="off" />
          </Form.Item>

          <Form.Item className="prefix-code-span-all" name="isActive" valuePropName="checked">
            <ActiveSwitchRow />
          </Form.Item>
        </div>
      </Form>
    </Modal>
  )
}
