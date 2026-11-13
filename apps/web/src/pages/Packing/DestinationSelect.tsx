import { PlusOutlined } from '@ant-design/icons'
import { Alert, Button, Divider, Input, Modal, Segmented, Select, Space, Typography, message } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { savePackCarrier } from '../../services/packingService'
import { PACK_DESTINATION_TYPE } from '../../types/packing'

import type { PackDestinationValue } from './packingLogic'
import { isShippableDestinationType, validateDestination } from './packingLogic'
import { showPackingError, usePackingPage } from './packingShared'

interface DestinationSelectProps {
  value: PackDestinationValue
  onChange: (value: PackDestinationValue) => void
  disabled?: boolean
}

/**
 * 去向选择（箱和托盘共用）：本地存放 / 其他分店 / 总仓 / 暂不指定，
 * 要发出的去向再选「怎么送」：自己送（对方来取）或交运输公司（任何人可现场新增运输公司）。
 */
export default function DestinationSelect({ value, onChange, disabled }: DestinationSelectProps) {
  const { t } = useTranslation()
  const { stores, warehouseSelectable, storesError, carriers, reloadCarriers } = usePackingPage()
  const type = value.destinationType
  const shippable = isShippableDestinationType(type)
  const viaCarrier = Boolean(value.viaCarrier || value.carrierGuid)
  const [newCarrierName, setNewCarrierName] = useState('')
  const [addingCarrier, setAddingCarrier] = useState(false)

  const options = [
    { value: PACK_DESTINATION_TYPE.Local, label: t('packingUi.destination.local') },
    { value: PACK_DESTINATION_TYPE.Store, label: t('packingUi.destination.store') },
    // 总仓不能发给总仓自己：候选接口里没有总仓时不显示（当前所属地点已经是总仓，或候选没加载出来）。
    ...(warehouseSelectable ? [{ value: PACK_DESTINATION_TYPE.Warehouse, label: t('packingUi.destination.warehouse') }] : []),
    { value: PACK_DESTINATION_TYPE.Unassigned, label: t('packingUi.destination.unassigned') },
  ]

  const addCarrier = async () => {
    const name = newCarrierName.trim()
    if (!name) {
      return
    }
    setAddingCarrier(true)
    try {
      const created = await savePackCarrier(null, { name, remark: null, isActive: true })
      setNewCarrierName('')
      reloadCarriers()
      onChange({ ...value, viaCarrier: true, carrierGuid: created.carrierGuid })
      void message.success(t('packingUi.message.carrierSaved'))
    } catch (error) {
      showPackingError(error)
    } finally {
      setAddingCarrier(false)
    }
  }

  return (
    <div className="wh-packing-dest-select">
      <Segmented
        block
        disabled={disabled}
        value={type}
        options={options}
        // 切换类型：本地存放 / 未指定不带地点和运输公司；其他分店保留已选分店。
        onChange={(next) => {
          const nextType = Number(next)
          onChange({
            destinationType: nextType,
            destinationCode: nextType === PACK_DESTINATION_TYPE.Store ? value.destinationCode ?? null : null,
            carrierGuid: isShippableDestinationType(nextType) ? value.carrierGuid ?? null : null,
            viaCarrier: isShippableDestinationType(nextType) ? viaCarrier : false,
          })
        }}
      />
      {type === PACK_DESTINATION_TYPE.Store ? (
        <>
          <Select
            showSearch
            disabled={disabled}
            optionFilterProp="label"
            placeholder={t('packingUi.destination.storePlaceholder')}
            value={value.destinationCode ?? undefined}
            options={stores}
            onChange={(destinationCode) => onChange({ ...value, destinationCode })}
          />
          {storesError ? <Alert type="warning" showIcon message={storesError} /> : null}
        </>
      ) : null}
      {shippable ? (
        <div className="wh-packing-dest-how">
          <Typography.Text type="secondary">{t('packingUi.destination.how')}</Typography.Text>
          <Segmented
            block
            disabled={disabled}
            value={viaCarrier ? 'carrier' : 'self'}
            options={[
              { value: 'self', label: t('packingUi.destination.howSelf') },
              { value: 'carrier', label: t('packingUi.destination.howCarrier') },
            ]}
            onChange={(next) =>
              onChange(next === 'carrier' ? { ...value, viaCarrier: true } : { ...value, viaCarrier: false, carrierGuid: null })
            }
          />
          {viaCarrier ? (
            <Select
              showSearch
              disabled={disabled}
              optionFilterProp="label"
              placeholder={t('packingUi.destination.carrierPlaceholder')}
              value={value.carrierGuid ?? undefined}
              options={carriers.map((carrier) => ({ value: carrier.carrierGuid, label: carrier.name }))}
              onChange={(carrierGuid) => onChange({ ...value, carrierGuid, viaCarrier: true })}
              notFoundContent={t('packingUi.destination.noCarrier')}
              dropdownRender={(menu) => (
                <>
                  {menu}
                  <Divider style={{ margin: '8px 0' }} />
                  <Space style={{ padding: '0 8px 8px' }}>
                    <Input
                      placeholder={t('packingUi.destination.newCarrierPlaceholder')}
                      value={newCarrierName}
                      maxLength={100}
                      onChange={(event) => setNewCarrierName(event.target.value)}
                      onKeyDown={(event) => event.stopPropagation()}
                      onPressEnter={() => void addCarrier()}
                    />
                    <Button type="text" icon={<PlusOutlined />} loading={addingCarrier} onClick={() => void addCarrier()}>
                      {t('packingUi.destination.addCarrier')}
                    </Button>
                  </Space>
                </>
              )}
            />
          ) : (
            <Typography.Text type="secondary" className="wh-packing-dest-hint">
              {t('packingUi.destination.howSelfHint')}
            </Typography.Text>
          )}
        </div>
      ) : null}
    </div>
  )
}

/** 去向校验失败的提示文字。 */
export function useDestinationProblemText() {
  const { t } = useTranslation()
  return (problem: ReturnType<typeof validateDestination>) => {
    switch (problem) {
      case 'storeRequired':
        return t('packingUi.destination.storeRequired')
      case 'carrierRequired':
        return t('packingUi.destination.carrierRequired')
      case 'warehouseFromWarehouse':
        return t('packingUi.destination.warehouseFromWarehouse')
      default:
        return ''
    }
  }
}

interface DestinationModalProps {
  open: boolean
  title: string
  initialValue: PackDestinationValue
  /** 额外说明，例如「改去向后需要补打标签」。 */
  hint?: string
  onCancel: () => void
  /** 返回 Promise；失败时由调用方提示并 reject，弹窗保持打开。 */
  onSubmit: (value: PackDestinationValue) => Promise<void>
}

/** 改去向 / 新建时选去向的弹窗。 */
export function DestinationModal({ open, title, initialValue, hint, onCancel, onSubmit }: DestinationModalProps) {
  const { t } = useTranslation()
  const { isWarehouse } = usePackingPage()
  const problemText = useDestinationProblemText()
  const [value, setValue] = useState(initialValue)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (open) {
      setValue(initialValue)
    }
    // 只在打开时取初值；打开期间外部刷新不覆盖用户正在选的内容。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const handleOk = async () => {
    const problem = validateDestination(value, isWarehouse)
    if (problem) {
      void message.warning(problemText(problem))
      return
    }
    setSaving(true)
    try {
      await onSubmit(value)
    } catch {
      // 调用方已提示。
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      open={open}
      title={title}
      width={440}
      okText={t('common.confirm')}
      cancelText={t('common.cancel')}
      confirmLoading={saving}
      onOk={() => void handleOk()}
      onCancel={onCancel}
      destroyOnClose
    >
      <DestinationSelect value={value} onChange={setValue} disabled={saving} />
      {hint ? (
        <Typography.Paragraph type="secondary" style={{ margin: '12px 0 0' }}>
          {hint}
        </Typography.Paragraph>
      ) : null}
    </Modal>
  )
}
