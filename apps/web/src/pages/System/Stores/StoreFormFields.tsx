import { ReloadOutlined } from '@ant-design/icons'
import { Button, Form, Input, Select, Switch } from 'antd'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { storeTimeZoneOptions } from './timeZoneOptions'
import './stores.css'

interface StoreFormFieldsProps {
  /** 仅新建分店时传入：编码由系统生成，需要「重新生成」入口；编辑时不传。 */
  onRegenerateStoreCode?: () => void
  storeCodeLoading?: boolean
}

/** 标签右侧的字数计数 `18 / 100`：超出上限即时变红，不必等到提交后才收到 400。 */
function CountedLabel({ name, label, max }: { name: string; label: ReactNode; max: number }) {
  return (
    <span className="sys-store-label">
      <span>{label}</span>
      <Form.Item noStyle shouldUpdate={(previous, next) => previous[name] !== next[name]}>
        {({ getFieldValue }) => {
          const length = String(getFieldValue(name) ?? '').length
          return (
            <span className={length > max ? 'sys-store-count sys-store-count-over' : 'sys-store-count'}>
              {length} / {max}
            </span>
          )
        }}
      </Form.Item>
    </span>
  )
}

/** 带说明的整行开关：Form.Item 通过 valuePropName="checked" 注入 checked / onChange。 */
function CashRegisterSwitch({ checked, onChange }: { checked?: boolean; onChange?: (checked: boolean) => void }) {
  const { t } = useTranslation()
  return (
    <div className="sys-store-switch-row">
      <span>{t('system.stores.cashRegisterToggleHint')}</span>
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
 * 新建与编辑分店共用的表单主体（三个分区、两列网格）。
 * 前端长度限制与后端 CreateStoreDto / UpdateStoreDto 保持一致，尽量在提交前拦住必填和长度错误。
 */
export default function StoreFormFields({ onRegenerateStoreCode, storeCodeLoading }: StoreFormFieldsProps) {
  const { t } = useTranslation()

  return (
    <>
      <section className="sys-store-section">
        <h4 className="sys-store-section-title">{t('system.stores.detailBasic')}</h4>
        <div className="sys-store-grid">
          <Form.Item
            label={<CountedLabel name="storeName" label={t('system.stores.storeName')} max={100} />}
            name="storeName"
            rules={[
              { required: true, message: t('system.stores.storeNameRequired') },
              { max: 100, message: t('system.stores.storeNameMaxLength') },
            ]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={<CountedLabel name="storeCode" label={t('system.stores.storeCode')} max={20} />}
            name="storeCode"
            rules={[
              { required: true, message: t('system.stores.storeCodeRequired') },
              { max: 20, message: t('system.stores.storeCodeMaxLength') },
            ]}
          >
            <Input
              autoComplete="off"
              addonAfter={onRegenerateStoreCode ? (
                <Button
                  type="link"
                  size="small"
                  icon={<ReloadOutlined />}
                  loading={storeCodeLoading}
                  onClick={onRegenerateStoreCode}
                >
                  {t('system.stores.regenerateStoreCode')}
                </Button>
              ) : undefined}
            />
          </Form.Item>
          <Form.Item
            label={<CountedLabel name="brandName" label={t('system.stores.brandName')} max={100} />}
            name="brandName"
            rules={[{ max: 100, message: t('system.stores.brandNameMaxLength') }]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={<CountedLabel name="abn" label={t('system.stores.abn')} max={20} />}
            name="abn"
            rules={[{ max: 20, message: t('system.stores.abnMaxLength') }]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={t('system.stores.timeZone')}
            name="timeZoneId"
            rules={[{ required: true, message: t('system.stores.timeZoneRequired') }]}
          >
            <Select options={storeTimeZoneOptions} />
          </Form.Item>
          <Form.Item label={t('system.stores.cashRegisterEnabled')} name="isActive" valuePropName="checked">
            <CashRegisterSwitch />
          </Form.Item>
        </div>
      </section>

      <section className="sys-store-section">
        <h4 className="sys-store-section-title">{t('system.stores.formContact')}</h4>
        <div className="sys-store-grid">
          <Form.Item
            label={<CountedLabel name="contactPhone" label={t('system.stores.contactPhone')} max={20} />}
            name="contactPhone"
            rules={[{ max: 20, message: t('system.stores.contactPhoneMaxLength') }]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={t('system.stores.contactEmail')}
            name="contactEmail"
            rules={[{ type: 'email', message: t('system.users.emailInvalid') }]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            className="sys-store-span-all"
            label={<CountedLabel name="address" label={t('system.stores.address')} max={200} />}
            name="address"
            rules={[{ max: 200, message: t('system.stores.addressMaxLength') }]}
          >
            <Input autoComplete="off" />
          </Form.Item>
        </div>
      </section>

      <section className="sys-store-section">
        <h4 className="sys-store-section-title">{t('system.stores.formNotes')}</h4>
        <div className="sys-store-grid">
          <Form.Item
            label={<CountedLabel name="description" label={t('column.description')} max={500} />}
            name="description"
            rules={[{ max: 500, message: t('system.stores.descriptionMaxLength') }]}
          >
            <Input.TextArea rows={4} autoComplete="off" />
          </Form.Item>
          <Form.Item
            label={<CountedLabel name="returnPolicy" label={t('system.stores.returnPolicy')} max={500} />}
            name="returnPolicy"
            rules={[{ max: 500, message: t('system.stores.returnPolicyMaxLength') }]}
          >
            <Input.TextArea rows={4} autoComplete="off" />
          </Form.Item>
        </div>
      </section>
    </>
  )
}
