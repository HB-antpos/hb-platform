import { Form, Input, Modal, Switch, Tag } from 'antd'
import type { ReactNode } from 'react'
import { useEffect, useLayoutEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { checkSupplierCodeExists, generateNextSupplierCode } from '../../../services/chinaSupplierService'
import type { ChinaSupplierItem } from '../../../types/chinaSupplier'
import { normalizePhotoUrl, toFormValues, type SupplierFormValues } from './chinaSuppliersLogic'
import './chinaSuppliers.css'

interface SupplierFormModalProps {
  open: boolean
  /** null 表示新建；否则编辑该供应商（编码只读）。 */
  editing: ChinaSupplierItem | null
  saving: boolean
  onCancel: () => void
  onSubmit: (values: SupplierFormValues) => void | Promise<void>
}

/** 标签右侧的字数计数 `18 / 100`：超出上限即时变红，不必等到提交后才收到 400。 */
function CountedLabel({ name, label, max }: { name: string; label: ReactNode; max: number }) {
  return (
    <span className="china-sup-label">
      <span>{label}</span>
      <Form.Item noStyle shouldUpdate={(previous, next) => previous[name] !== next[name]}>
        {({ getFieldValue }) => {
          const length = String(getFieldValue(name) ?? '').length
          return (
            <span className={length > max ? 'china-sup-count china-sup-count-over' : 'china-sup-count'}>
              {length} / {max}
            </span>
          )
        }}
      </Form.Item>
    </span>
  )
}

/** 带说明的整行状态开关：Form.Item 注入 value(1/0) / onChange，对外仍是后端使用的数字状态。 */
function StatusSwitch({ value, onChange }: { value?: number; onChange?: (value: number) => void }) {
  const { t } = useTranslation()
  return (
    <div className="china-sup-switch-row">
      <span>{t('chinaSuppliers.statusSwitchHint')}</span>
      <Switch
        checked={value !== 0}
        onChange={(checked) => onChange?.(checked ? 1 : 0)}
        checkedChildren={t('common.enabled')}
        unCheckedChildren={t('common.disabled')}
      />
    </div>
  )
}

/**
 * 输入链接时每个字符都会换一次 img.src，等于对第三方图床连发请求；
 * 所以编辑过程中延迟 delay 毫秒再更新，但「从空变成一条完整可用的链接」（打开编辑、整条粘贴）立即生效。
 */
function usePreviewUrl(value: string | undefined, delay: number) {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    if (!normalizePhotoUrl(debounced) && normalizePhotoUrl(value)) {
      setDebounced(value)
      return undefined
    }
    const timer = window.setTimeout(() => setDebounced(value), delay)
    return () => window.clearTimeout(timer)
    // 只在输入值变化时重新计时；debounced 只用来判断「当前没有可用预览」。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, delay])
  return debounced
}

function StorefrontPhotoPreview({ url }: { url?: string }) {
  const { t } = useTranslation()
  const previewUrl = usePreviewUrl(url, 500)
  const [failedUrl, setFailedUrl] = useState<string>()
  const safeUrl = normalizePhotoUrl(previewUrl)

  let content: ReactNode
  if (!previewUrl?.trim()) {
    content = t('chinaSuppliers.photoPreviewEmpty')
  } else if (!safeUrl) {
    // 只渲染 http(s) 链接，其它协议不当图片加载。
    content = t('chinaSuppliers.photoPreviewInvalid')
  } else if (failedUrl === safeUrl) {
    content = t('chinaSuppliers.photoPreviewFailed')
  } else {
    content = (
      <img
        key={safeUrl}
        src={safeUrl}
        alt={t('chinaSuppliers.photoAlt')}
        referrerPolicy="no-referrer"
        onError={() => setFailedUrl(safeUrl)}
      />
    )
  }

  return <div className="china-sup-photo-box" data-testid="china-suppliers-photo-preview">{content}</div>
}

/**
 * 新建 / 编辑供应商弹窗：基本信息 / 联系方式 / 其他 三个分区。
 * 前端长度限制与后端 CreateChinaSupplierDto 保持一致，尽量在提交前拦住必填、长度和格式错误。
 */
export default function SupplierFormModal({ open, editing, saving, onCancel, onSubmit }: SupplierFormModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<SupplierFormValues>()
  const photoUrl = Form.useWatch('storefrontPhoto', form)
  const isEditing = Boolean(editing)

  // 每次打开都先清空再灌入初值：Form 随弹窗销毁，但 store 里的旧值会保留，
  // 用 layout effect 保证首帧就是新值，避免闪出上一次编辑的内容。
  useLayoutEffect(() => {
    if (!open) {
      return undefined
    }
    form.resetFields()
    if (editing) {
      form.setFieldsValue(toFormValues(editing))
      return undefined
    }
    form.setFieldsValue({ status: 1 })

    // 新建：向后端申请下一个可用编码作为默认值；失败只记日志，用户仍可手填。
    let cancelled = false
    generateNextSupplierCode()
      .then((code) => {
        // 用户已经自己改过编码就不再覆盖。
        if (!cancelled && !form.isFieldTouched('supplierCode')) {
          form.setFieldValue('supplierCode', code)
        }
      })
      .catch((error: unknown) => console.error(error))
    return () => {
      cancelled = true
    }
  }, [open, editing, form])

  return (
    <Modal
      title={(
        <span className="china-sup-modal-title">
          <span>{isEditing ? t('chinaSuppliers.formEditTitle') : t('chinaSuppliers.formCreateTitle')}</span>
          {editing ? <Tag bordered={false} className="china-sup-mono" style={{ marginInlineEnd: 0 }}>{editing.supplierCode}</Tag> : null}
        </span>
      )}
      open={open}
      width={780}
      destroyOnHidden
      // 表单里有未保存的输入，点遮罩误关会丢数据；保存中也不允许关闭。
      maskClosable={false}
      closable={!saving}
      keyboard={!saving}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={saving}
      cancelButtonProps={{ disabled: saving }}
      onOk={() => form.submit()}
      onCancel={onCancel}
      afterClose={() => form.resetFields()}
      styles={{ body: { maxHeight: '70vh', overflowY: 'auto', paddingInline: 4 } }}
      data-testid="china-suppliers-form-modal"
    >
      <Form
        form={form}
        layout="vertical"
        autoComplete="off"
        className="china-sup-form"
        initialValues={{ status: 1 }}
        onFinish={(values) => void onSubmit(values)}
      >
        <section className="china-sup-section">
          <h4 className="china-sup-section-title">{t('chinaSuppliers.sectionBasic')}</h4>
          <div className="china-sup-grid-2">
            <Form.Item
              label={t('chinaSuppliers.supplierCode')}
              name="supplierCode"
              // 先跑本地规则，再发唯一性校验请求；输入停顿 500ms 后才校验，避免每敲一个字符就请求一次。
              validateFirst
              validateDebounce={500}
              extra={<span className="china-sup-sub">{isEditing ? t('chinaSuppliers.codeLockedHint') : t('chinaSuppliers.codeAutoHint')}</span>}
              rules={[
                { required: true, whitespace: true, message: t('chinaSuppliers.enterSupplierCode') },
                { max: 50, message: t('chinaSuppliers.supplierCodeMaxLength') },
                {
                  validator: async (_, value: string | undefined) => {
                    const code = value?.trim()
                    // 编辑时编码只读且本来就属于自己，无需再查重。
                    if (!code || isEditing) {
                      return
                    }
                    let exists = false
                    try {
                      exists = await checkSupplierCodeExists(code)
                    } catch (error) {
                      // 查重接口失败不拦截用户：后端创建时仍会以 409 拒绝重复编码。
                      console.error(error)
                      return
                    }
                    if (exists) {
                      throw new Error(t('chinaSuppliers.supplierCodeExists'))
                    }
                  },
                },
              ]}
            >
              <Input className="china-sup-mono" disabled={isEditing} />
            </Form.Item>
            <Form.Item
              label={<CountedLabel name="supplierName" label={t('chinaSuppliers.supplierName')} max={200} />}
              name="supplierName"
              rules={[
                { required: true, whitespace: true, message: t('chinaSuppliers.enterSupplierName') },
                { max: 200, message: t('chinaSuppliers.supplierNameMaxLength') },
              ]}
            >
              <Input />
            </Form.Item>
            <Form.Item
              label={<CountedLabel name="shopNumber" label={t('chinaSuppliers.shopNumber')} max={50} />}
              name="shopNumber"
              rules={[{ max: 50, message: t('chinaSuppliers.shopNumberMaxLength') }]}
            >
              <Input />
            </Form.Item>
            <Form.Item label={t('common.status')} name="status">
              <StatusSwitch />
            </Form.Item>
          </div>
        </section>

        <section className="china-sup-section">
          <h4 className="china-sup-section-title">{t('chinaSuppliers.sectionContact')}</h4>
          <div className="china-sup-grid-3">
            <Form.Item
              label={<CountedLabel name="contactPerson" label={t('chinaSuppliers.contactPerson')} max={100} />}
              name="contactPerson"
              rules={[{ max: 100, message: t('chinaSuppliers.contactPersonMaxLength') }]}
            >
              <Input />
            </Form.Item>
            <Form.Item
              label={<CountedLabel name="phone" label={t('chinaSuppliers.phoneLabel')} max={20} />}
              name="phone"
              rules={[
                { max: 20, message: t('chinaSuppliers.phoneMaxLength') },
                { pattern: /^[\d\s\-+()]+$/, message: t('chinaSuppliers.invalidPhone') },
              ]}
            >
              <Input className="china-sup-mono" />
            </Form.Item>
            <Form.Item
              label={t('chinaSuppliers.email')}
              name="email"
              rules={[
                { type: 'email', message: t('chinaSuppliers.invalidEmail') },
                { max: 100, message: t('chinaSuppliers.emailMaxLength') },
              ]}
            >
              <Input />
            </Form.Item>
          </div>
        </section>

        <section className="china-sup-section">
          <h4 className="china-sup-section-title">{t('chinaSuppliers.sectionOther')}</h4>
          <div className="china-sup-grid-2">
            <Form.Item
              label={t('chinaSuppliers.photoUrlLabel')}
              name="storefrontPhoto"
              rules={[{ max: 500, message: t('chinaSuppliers.photoUrlMaxLength') }]}
            >
              <Input className="china-sup-mono" placeholder={t('chinaSuppliers.photoUrlPlaceholder')} />
            </Form.Item>
            <Form.Item label={t('chinaSuppliers.photoPreviewLabel')}>
              <StorefrontPhotoPreview url={photoUrl} />
            </Form.Item>
            <Form.Item
              className="china-sup-span-all"
              label={<CountedLabel name="remarks" label={t('common.remarks')} max={1000} />}
              name="remarks"
              rules={[{ max: 1000, message: t('chinaSuppliers.remarksMaxLength') }]}
            >
              <Input.TextArea rows={4} />
            </Form.Item>
          </div>
        </section>
      </Form>
    </Modal>
  )
}
