import { EditOutlined, PrinterOutlined } from '@ant-design/icons'
import { Button, Drawer, Input, InputNumber, Modal, Popconfirm, Spin, Steps, Tabs, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  addCartonToPackPallet,
  getPackPallet,
  listPackPalletEvents,
  readPackingError,
  removeCartonFromPackPallet,
  reopenPackPallet,
  sealPackPallet,
  updatePackPalletHeader,
  voidPackPallet,
} from '../../../services/warehousePackingService'
import type { PackCartonSummary, PackEvent, PackPalletDetail, PackPalletProduct } from '../../../types/warehousePacking'
import { PACK_ERROR_CODE, PACK_PALLET_STATUS } from '../../../types/warehousePacking'

import DestinationSelect from './DestinationSelect'
import type { PackDestinationValue } from './packingLogic'
import {
  UNASSIGNED_DESTINATION,
  destinationToRequest,
  destinationToValue,
  formatPackTime,
  parseWeightInput,
  validateDestination,
} from './packingLogic'
import {
  DestinationText,
  EventList,
  PackLabelPreview,
  PalletStatusTag,
  showPackingError,
  usePackingPage,
} from './packingShared'
import { buildPalletLabelFields, printPackLabels } from './packLabel'

interface PalletDrawerProps {
  palletGuid: string | null
  onClose: () => void
}

interface HeaderDraft {
  destination: PackDestinationValue
  consignmentNo: string
  weight: number | null
  remark: string
}

/** 托盘详情抽屉：箱清单、按商品汇总、操作记录；改去向 / 运单号 / 备注、加箱 / 移出箱、封托、重新开托、打印标签。 */
export default function PalletDrawer({ palletGuid, onClose }: PalletDrawerProps) {
  const { t } = useTranslation()
  const { notifyChanged, openCarton } = usePackingPage()
  const [pallet, setPallet] = useState<PackPalletDetail | null>(null)
  const [events, setEvents] = useState<PackEvent[]>([])
  const [loading, setLoading] = useState(false)
  const [acting, setActing] = useState(false)
  const [addCode, setAddCode] = useState('')
  const [draft, setDraft] = useState<HeaderDraft | null>(null)
  const [savingHeader, setSavingHeader] = useState(false)

  const load = useCallback(async (guid: string) => {
    setLoading(true)
    try {
      const [detail, eventList] = await Promise.all([getPackPallet(guid), listPackPalletEvents(guid)])
      setPallet(detail)
      setEvents(eventList)
    } catch (error) {
      showPackingError(error)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    setPallet(null)
    setEvents([])
    setAddCode('')
    if (palletGuid) {
      void load(palletGuid)
    }
  }, [palletGuid, load])

  const applyResult = (detail: PackPalletDetail, successText?: string) => {
    setPallet(detail)
    if (successText) {
      void message.success(successText)
    }
    notifyChanged()
    void listPackPalletEvents(detail.palletGuid).then(setEvents).catch(() => undefined)
  }

  /** 执行一个会改动托盘的操作；失败时照实提示后端原因（如 403「只有仓库经理可以重新开托」）。 */
  const act = async (action: () => Promise<PackPalletDetail>, successText?: string): Promise<PackPalletDetail | null> => {
    setActing(true)
    try {
      const detail = await action()
      applyResult(detail, successText)
      return detail
    } catch (error) {
      showPackingError(error)
      return null
    } finally {
      setActing(false)
    }
  }

  /** 扫描或输入箱号加入托盘；去向不一致时询问是否放行（只有仓库经理能放行）。 */
  const addCarton = async (code: string, allowMismatch = false) => {
    if (!pallet || !code.trim()) {
      return
    }
    setActing(true)
    try {
      applyResult(await addCartonToPackPallet(pallet.palletGuid, code.trim(), allowMismatch))
      setAddCode('')
    } catch (error) {
      const info = readPackingError(error)
      if (info.errorCode === PACK_ERROR_CODE.DestinationMismatch && !allowMismatch) {
        Modal.confirm({
          title: info.message,
          content: t('warehouseUi.packing.station.mismatchConfirm', { cartonNo: code.trim().toUpperCase() }),
          okText: t('warehouseUi.packing.station.mismatchAllow'),
          cancelText: t('common.cancel'),
          onOk: () => addCarton(code, true),
        })
      } else {
        showPackingError(error)
      }
    } finally {
      setActing(false)
    }
  }

  const sealAndPrint = async () => {
    if (!pallet) {
      return
    }
    const sealed = await act(() => sealPackPallet(pallet.palletGuid), t('warehouseUi.packing.message.palletSealed'))
    if (sealed) {
      printPackLabels(buildPalletLabelFields(sealed))
    }
  }

  const openHeaderEditor = () => {
    if (!pallet) {
      return
    }
    setDraft({
      destination: destinationToValue(pallet.destination),
      consignmentNo: pallet.consignmentNo ?? '',
      weight: pallet.weightKg ?? null,
      remark: pallet.remark ?? '',
    })
  }

  const saveHeader = async () => {
    if (!pallet || !draft) {
      return
    }
    const problem = validateDestination(draft.destination)
    if (problem) {
      void message.warning(
        problem === 'storeRequired'
          ? t('warehouseUi.packing.destination.storeRequired')
          : t('warehouseUi.packing.destination.carrierRequired'),
      )
      return
    }
    const weightKg = parseWeightInput(draft.weight)
    if (weightKg === undefined) {
      void message.warning(t('warehouseUi.packing.station.weightInvalid'))
      return
    }
    setSavingHeader(true)
    try {
      const detail = await updatePackPalletHeader(pallet.palletGuid, {
        ...destinationToRequest(draft.destination),
        consignmentNo: draft.consignmentNo.trim() || null,
        weightKg,
        remark: draft.remark.trim() || null,
      })
      applyResult(detail, t('warehouseUi.packing.message.palletSaved'))
      setDraft(null)
    } catch (error) {
      showPackingError(error)
    } finally {
      setSavingHeader(false)
    }
  }

  const isBuilding = pallet?.status === PACK_PALLET_STATUS.Building
  const isSealed = pallet?.status === PACK_PALLET_STATUS.Sealed
  const isVoided = pallet?.status === PACK_PALLET_STATUS.Voided

  const cartonColumns = useMemo<ColumnsType<PackCartonSummary>>(
    () => [
      {
        title: t('warehouseUi.packing.columns.cartonNo'),
        dataIndex: 'cartonNo',
        render: (value: string) => <span className="wh-packing-mono">{value}</span>,
      },
      { title: t('warehouseUi.packing.columns.sku'), dataIndex: 'skuCount', width: 64, align: 'right' },
      { title: t('warehouseUi.packing.columns.pieces'), dataIndex: 'pieceCount', width: 72, align: 'right' },
      {
        title: t('warehouseUi.packing.columns.weight'),
        dataIndex: 'weightKg',
        width: 84,
        align: 'right',
        render: (value: number | null | undefined) => (value === null || value === undefined ? '—' : `${value} kg`),
      },
      {
        title: t('warehouseUi.packing.columns.sealedBy'),
        key: 'sealed',
        width: 150,
        render: (_value, record) => (
          <Typography.Text type="secondary">
            {[record.sealedByName, formatPackTime(record.sealedAtUtc, '')].filter(Boolean).join(' ')}
          </Typography.Text>
        ),
      },
      {
        key: 'actions',
        width: 120,
        render: (_value, record) => (
          <>
            <Button type="link" size="small" onClick={() => openCarton(record.cartonGuid)}>
              {t('warehouseUi.packing.action.items')}
            </Button>
            {isBuilding && pallet ? (
              <Popconfirm
                title={t('warehouseUi.packing.confirm.removeCarton', { no: record.cartonNo })}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() =>
                  act(
                    () => removeCartonFromPackPallet(pallet.palletGuid, record.cartonGuid),
                    t('warehouseUi.packing.message.cartonRemoved'),
                  )
                }
              >
                <Button type="link" size="small" danger disabled={acting}>
                  {t('warehouseUi.packing.action.removeCarton')}
                </Button>
              </Popconfirm>
            ) : null}
          </>
        ),
      },
    ],
    // act 每次渲染都是新函数，这里只依赖影响列内容的值。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [acting, isBuilding, openCarton, pallet, t],
  )

  const productColumns = useMemo<ColumnsType<PackPalletProduct>>(
    () => [
      {
        title: t('warehouseUi.packing.columns.product'),
        dataIndex: 'productName',
        ellipsis: true,
        render: (value: string | null, record) => value || record.productCode,
      },
      {
        title: t('warehouseUi.packing.columns.itemNumber'),
        dataIndex: 'itemNumber',
        width: 130,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      { title: t('warehouseUi.packing.columns.quantity'), dataIndex: 'quantity', width: 80, align: 'right' },
      { title: t('warehouseUi.packing.columns.cartons'), dataIndex: 'cartonCount', width: 64, align: 'right' },
    ],
    [t],
  )

  const labelFields = useMemo(() => (pallet ? buildPalletLabelFields(pallet) : []), [pallet])

  return (
    <Drawer
      open={palletGuid !== null}
      width={720}
      onClose={onClose}
      destroyOnClose
      title={
        <span className="wh-packing-drawer-title">
          {t('warehouseUi.packing.pallet')}
          <span className="wh-packing-mono">{pallet?.palletNo ?? ''}</span>
          {pallet ? <PalletStatusTag status={pallet.status} t={t} /> : null}
        </span>
      }
      footer={
        pallet && !isVoided ? (
          <div className="wh-packing-drawer-footer">
            {isBuilding && pallet.cartonCount === 0 ? (
              <Popconfirm
                title={t('warehouseUi.packing.confirm.voidPallet')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => voidPackPallet(pallet.palletGuid), t('warehouseUi.packing.message.palletVoided'))}
              >
                <Button danger disabled={acting}>
                  {t('warehouseUi.packing.action.void')}
                </Button>
              </Popconfirm>
            ) : null}
            {isSealed ? (
              <Popconfirm
                title={t('warehouseUi.packing.confirm.reopenPallet')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() =>
                  act(() => reopenPackPallet(pallet.palletGuid), t('warehouseUi.packing.message.palletReopened'))
                }
              >
                <Button disabled={acting}>{t('warehouseUi.packing.action.reopenPallet')}</Button>
              </Popconfirm>
            ) : null}
            <Button icon={<EditOutlined />} disabled={acting} onClick={openHeaderEditor}>
              {t('warehouseUi.packing.action.editPallet')}
            </Button>
            {isBuilding ? (
              <Button type="primary" disabled={acting || pallet.cartonCount === 0} onClick={() => void sealAndPrint()}>
                {t('warehouseUi.packing.action.sealPalletAndPrint')}
              </Button>
            ) : (
              <Button type="primary" icon={<PrinterOutlined />} onClick={() => printPackLabels(labelFields)}>
                {t('warehouseUi.packing.action.printPalletLabel')}
              </Button>
            )}
          </div>
        ) : null
      }
    >
      <Spin spinning={loading}>
        {pallet ? (
          <div className="wh-packing-drawer-body">
            {/* 进度只画「组托 → 封托」两步；发运、签收属于二期。 */}
            <Steps
              size="small"
              current={isSealed ? 2 : 1}
              items={[
                {
                  title: t('warehouseUi.packing.step.build'),
                  description: `${formatPackTime(pallet.createdAtUtc)} · ${pallet.createdByName}`,
                },
                {
                  title: t('warehouseUi.packing.step.seal'),
                  description: pallet.sealedAtUtc
                    ? `${formatPackTime(pallet.sealedAtUtc)} · ${pallet.sealedByName ?? ''}`
                    : t('warehouseUi.packing.step.sealPending'),
                },
              ]}
            />

            <div className="wh-packing-desc wh-packing-desc-boxed">
              <div>
                <span>{t('warehouseUi.packing.columns.destination')}</span>
                <DestinationText destination={pallet.destination} t={t} />
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.consignmentNo')}</span>
                {pallet.consignmentNo ? <span className="wh-packing-mono">{pallet.consignmentNo}</span> : '—'}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.location')}</span>
                {pallet.locationCode || '—'}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.total')}</span>
                {t('warehouseUi.packing.summary.palletTotal', {
                  cartons: pallet.cartonCount,
                  sku: pallet.skuCount,
                  pieces: pallet.pieceCount,
                })}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.weight')}</span>
                {pallet.weightKg === null || pallet.weightKg === undefined ? '—' : `${pallet.weightKg} kg`}
              </div>
              <div>
                <span>{t('warehouseUi.packing.columns.remark')}</span>
                {pallet.remark || '—'}
              </div>
            </div>

            {isBuilding ? (
              <Input.Search
                allowClear
                value={addCode}
                disabled={acting}
                placeholder={t('warehouseUi.packing.pallets.addCartonPlaceholder')}
                enterButton={t('warehouseUi.packing.pallets.addCarton')}
                onChange={(event) => setAddCode(event.target.value)}
                onSearch={(value) => void addCarton(value)}
              />
            ) : null}

            <Tabs
              size="small"
              items={[
                {
                  key: 'cartons',
                  label: t('warehouseUi.packing.tab.cartonsCount', { count: pallet.cartons.length }),
                  children: (
                    <MeasuredTable<PackCartonSummary>
                      metricId="warehouse.packing.pallet-drawer-cartons"
                      size="small"
                      rowKey="cartonGuid"
                      columns={cartonColumns}
                      dataSource={pallet.cartons}
                      pagination={false}
                    />
                  ),
                },
                {
                  key: 'products',
                  label: t('warehouseUi.packing.tab.productsCount', { count: pallet.products.length }),
                  children: (
                    <MeasuredTable<PackPalletProduct>
                      metricId="warehouse.packing.pallet-drawer-products"
                      size="small"
                      rowKey="productCode"
                      columns={productColumns}
                      dataSource={pallet.products}
                      pagination={false}
                    />
                  ),
                },
                {
                  key: 'events',
                  label: t('warehouseUi.packing.tab.eventsCount', { count: events.length }),
                  children: <EventList events={events} t={t} />,
                },
                {
                  key: 'label',
                  label: t('warehouseUi.packing.tab.label'),
                  children: (
                    <div className="wh-packing-label-stack">
                      {labelFields.map((fields) => (
                        <PackLabelPreview key={fields.copy ?? '1'} fields={fields} scale={0.9} />
                      ))}
                    </div>
                  ),
                },
              ]}
            />

            {isSealed ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('warehouseUi.packing.note.reopenPallet')}
              </Typography.Paragraph>
            ) : null}
          </div>
        ) : (
          <div className="wh-packing-drawer-placeholder" />
        )}
      </Spin>

      <Modal
        open={draft !== null}
        title={t('warehouseUi.packing.action.editPallet')}
        width={440}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={savingHeader}
        onOk={() => void saveHeader()}
        onCancel={() => setDraft(null)}
        destroyOnClose
      >
        {draft ? (
          <div className="wh-packing-form">
            <label>{t('warehouseUi.packing.columns.destination')}</label>
            <DestinationSelect
              value={draft.destination ?? UNASSIGNED_DESTINATION}
              onChange={(destination) => setDraft({ ...draft, destination })}
              disabled={savingHeader}
            />
            <label>{t('warehouseUi.packing.columns.consignmentNo')}</label>
            <Input
              maxLength={100}
              value={draft.consignmentNo}
              onChange={(event) => setDraft({ ...draft, consignmentNo: event.target.value })}
            />
            <label>{t('warehouseUi.packing.columns.weight')}</label>
            <InputNumber
              min={0}
              max={9999.99}
              precision={2}
              controls={false}
              addonAfter="kg"
              value={draft.weight}
              onChange={(value) => setDraft({ ...draft, weight: typeof value === 'number' ? value : null })}
            />
            <label>{t('warehouseUi.packing.columns.remark')}</label>
            <Input.TextArea
              rows={2}
              maxLength={200}
              value={draft.remark}
              onChange={(event) => setDraft({ ...draft, remark: event.target.value })}
            />
            {isSealed ? (
              <Typography.Text type="secondary">{t('warehouseUi.packing.note.reprintAfterDestination')}</Typography.Text>
            ) : null}
          </div>
        ) : null}
      </Modal>
    </Drawer>
  )
}
