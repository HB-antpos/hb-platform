import { Modal, Segmented, Select, Typography, message } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { PACK_DESTINATION_TYPE } from '../../../types/warehousePacking'

import type { PackDestinationValue } from './packingLogic'
import { validateDestination } from './packingLogic'
import { usePackingPage } from './packingShared'

interface DestinationSelectProps {
  value: PackDestinationValue
  onChange: (value: PackDestinationValue) => void
  disabled?: boolean
}

/** 去向选择（箱和托盘共用）：分店 / 运输公司 / 暂不指定。经运输公司时最终收货分店可先不填。 */
export default function DestinationSelect({ value, onChange, disabled }: DestinationSelectProps) {
  const { t } = useTranslation()
  const { stores, carriers } = usePackingPage()
  const type = value.destinationType

  return (
    <div className="wh-packing-dest-select">
      <Segmented
        block
        disabled={disabled}
        value={type}
        options={[
          { value: PACK_DESTINATION_TYPE.Store, label: t('warehouseUi.packing.destination.store') },
          { value: PACK_DESTINATION_TYPE.Carrier, label: t('warehouseUi.packing.destination.carrier') },
          { value: PACK_DESTINATION_TYPE.Unassigned, label: t('warehouseUi.packing.destination.unassigned') },
        ]}
        // 切换类型时保留已选分店（经运输公司时它是最终收货分店）；直送分店不带运输公司。
        onChange={(next) => onChange({ ...value, destinationType: Number(next) })}
      />
      {type === PACK_DESTINATION_TYPE.Carrier ? (
        <Select
          showSearch
          disabled={disabled}
          optionFilterProp="label"
          placeholder={t('warehouseUi.packing.destination.carrierPlaceholder')}
          value={value.carrierGuid ?? undefined}
          options={carriers.map((carrier) => ({ value: carrier.carrierGuid, label: carrier.name }))}
          onChange={(carrierGuid) => onChange({ ...value, carrierGuid })}
          notFoundContent={t('warehouseUi.packing.destination.noCarrier')}
        />
      ) : null}
      {type !== PACK_DESTINATION_TYPE.Unassigned ? (
        <Select
          showSearch
          disabled={disabled}
          allowClear={type === PACK_DESTINATION_TYPE.Carrier}
          optionFilterProp="label"
          placeholder={
            type === PACK_DESTINATION_TYPE.Carrier
              ? t('warehouseUi.packing.destination.finalStorePlaceholder')
              : t('warehouseUi.packing.destination.storePlaceholder')
          }
          value={value.storeCode ?? undefined}
          options={stores}
          onChange={(storeCode) => onChange({ ...value, storeCode: storeCode ?? null })}
        />
      ) : null}
    </div>
  )
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
    const problem = validateDestination(value)
    if (problem) {
      void message.warning(
        problem === 'storeRequired'
          ? t('warehouseUi.packing.destination.storeRequired')
          : t('warehouseUi.packing.destination.carrierRequired'),
      )
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
      width={420}
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
