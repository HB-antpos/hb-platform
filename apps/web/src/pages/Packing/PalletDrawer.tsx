import { EditOutlined, PrinterOutlined, SendOutlined } from '@ant-design/icons'
import { Button, Drawer, Input, InputNumber, Modal, Popconfirm, Spin, Steps, Tabs, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import {
  addCartonToPackPallet,
  dispatchPackPallet,
  getPackPallet,
  listPackPalletEvents,
  readPackingError,
  removeCartonFromPackPallet,
  reopenPackPallet,
  sealPackPallet,
  setPackPalletLocation,
  updatePackPalletHeader,
  voidPackPallet,
} from '../../services/packingService'
import type { PackCartonSummary, PackEvent, PackPalletDetail, PackPalletProduct } from '../../types/packing'
import { PACK_CARTON_STATUS, PACK_ERROR_CODE, PACK_PALLET_STATUS } from '../../types/packing'

import DestinationSelect, { useDestinationProblemText } from './DestinationSelect'
import LocationSelect from './LocationSelect'
import type { PackDestinationValue } from './packingLogic'
import {
  UNASSIGNED_DESTINATION,
  buildPalletSteps,
  canDispatchPallet,
  destinationPlaceName,
  destinationToRequest,
  destinationToValue,
  formatPackTime,
  isReadOnlyForOwner,
  palletStateKind,
  parseWeightInput,
  validateDestination,
} from './packingLogic'
import {
  DestinationText,
  EventList,
  PackLabelPreview,
  ReceivedPlace,
  StateTag,
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

/**
 * 托盘详情抽屉：箱清单、按商品汇总、操作记录；改去向 / 运单号 / 备注、加箱 / 移出箱、封托、发出、设置存放位置、
 * 重新开托、打印标签。进度是 组托 → 封托 → 发出 → 签收；在路上或已被对方签收的只读。
 */
export default function PalletDrawer({ palletGuid, onClose }: PalletDrawerProps) {
  const { t } = useTranslation()
  const { ownerCode, isWarehouse, isManager, notifyChanged, openCarton } = usePackingPage()
  const problemText = useDestinationProblemText()
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

  /** 执行一个会改动托盘的操作；失败时照实提示后端原因（如 403「只有店长或仓库经理可以重新开托」）。 */
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

  /** 扫描或输入箱号加入托盘；去向不一致时询问是否放行（只有店长能放行）。 */
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
          content: t('packingUi.station.mismatchConfirm', { cartonNo: code.trim().toUpperCase() }),
          okText: t('packingUi.station.mismatchAllow'),
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
    const sealed = await act(() => sealPackPallet(pallet.palletGuid), t('packingUi.message.palletSealed'))
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
    const problem = validateDestination(draft.destination, isWarehouse)
    if (problem) {
      void message.warning(problemText(problem))
      return
    }
    const weightKg = parseWeightInput(draft.weight)
    if (weightKg === undefined) {
      void message.warning(t('packingUi.station.weightInvalid'))
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
      applyResult(detail, t('packingUi.message.palletSaved'))
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
  const readOnly = pallet ? isReadOnlyForOwner(pallet, ownerCode) : true
  const stateKind = pallet ? palletStateKind(pallet, ownerCode) : 'voided'
  const canSetLocation = Boolean(pallet && isSealed && !readOnly)
  const placeName = pallet ? destinationPlaceName(pallet.destination) : null

  const cartonColumns = useMemo<ColumnsType<PackCartonSummary>>(
    () => [
      {
        title: t('packingUi.columns.cartonNo'),
        dataIndex: 'cartonNo',
        render: (value: string) => <span className="wh-packing-mono">{value}</span>,
      },
      { title: t('packingUi.columns.sku'), dataIndex: 'skuCount', width: 64, align: 'right' },
      { title: t('packingUi.columns.pieces'), dataIndex: 'pieceCount', width: 72, align: 'right' },
      {
        title: t('packingUi.columns.weight'),
        dataIndex: 'weightKg',
        width: 84,
        align: 'right',
        render: (value: number | null | undefined) => (value === null || value === undefined ? '—' : `${value} kg`),
      },
      {
        title: t('packingUi.columns.sealedBy'),
        key: 'sealed',
        width: 150,
        render: (_value, record) => (
          <Typography.Text type="secondary">
            {[record.sealedByName, formatPackTime(record.sealedAtUtc, '')].filter(Boolean).join(' ')}
          </Typography.Text>
        ),
      },
      // 发出后逐箱显示签收情况，发出方看得到差了哪几箱。
      ...(pallet && (pallet.status === PACK_PALLET_STATUS.InTransit || pallet.receivedAtUtc || pallet.dispatchedAtUtc)
        ? ([
            {
              title: t('common.status'),
              key: 'received',
              width: 90,
              render: (_value, record) =>
                record.status === PACK_CARTON_STATUS.InTransit ? (
                  <StateTag kind="transit" t={t} />
                ) : (
                  <Typography.Text type="success">{t('packingUi.status.receivedShort')}</Typography.Text>
                ),
            },
          ] as ColumnsType<PackCartonSummary>)
        : []),
      {
        key: 'actions',
        width: 120,
        render: (_value, record) => (
          <>
            <Button type="link" size="small" onClick={() => openCarton(record.cartonGuid)}>
              {t('packingUi.action.items')}
            </Button>
            {isBuilding && pallet && !readOnly ? (
              <Popconfirm
                title={t('packingUi.confirm.removeCarton', { no: record.cartonNo })}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() =>
                  act(() => removeCartonFromPackPallet(pallet.palletGuid, record.cartonGuid), t('packingUi.message.cartonRemoved'))
                }
              >
                <Button type="link" size="small" danger disabled={acting}>
                  {t('packingUi.action.removeCarton')}
                </Button>
              </Popconfirm>
            ) : null}
          </>
        ),
      },
    ],
    // act 每次渲染都是新函数，这里只依赖影响列内容的值。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [acting, isBuilding, openCarton, pallet, readOnly, t],
  )

  const productColumns = useMemo<ColumnsType<PackPalletProduct>>(
    () => [
      {
        title: t('packingUi.columns.product'),
        dataIndex: 'productName',
        ellipsis: true,
        render: (value: string | null, record) => value || record.productCode,
      },
      {
        title: t('packingUi.columns.itemNumber'),
        dataIndex: 'itemNumber',
        width: 130,
        render: (value: string | null) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      { title: t('packingUi.columns.quantity'), dataIndex: 'quantity', width: 80, align: 'right' },
      { title: t('packingUi.columns.cartons'), dataIndex: 'cartonCount', width: 64, align: 'right' },
    ],
    [t],
  )

  const labelFields = useMemo(() => (pallet ? buildPalletLabelFields(pallet) : []), [pallet])
  const steps = useMemo(() => (pallet ? buildPalletSteps(pallet) : []), [pallet])

  return (
    <Drawer
      open={palletGuid !== null}
      width={720}
      onClose={onClose}
      destroyOnClose
      title={
        <span className="wh-packing-drawer-title">
          {t('packingUi.pallet')}
          <span className="wh-packing-mono">{pallet?.palletNo ?? ''}</span>
          {pallet ? <StateTag kind={stateKind} t={t} /> : null}
        </span>
      }
      footer={
        pallet && !isVoided ? (
          <div className="wh-packing-drawer-footer">
            {isBuilding && pallet.cartonCount === 0 && !readOnly ? (
              <Popconfirm
                title={t('packingUi.confirm.voidPallet')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => voidPackPallet(pallet.palletGuid), t('packingUi.message.palletVoided'))}
              >
                <Button danger disabled={acting}>
                  {t('packingUi.action.void')}
                </Button>
              </Popconfirm>
            ) : null}
            {isSealed && !readOnly && isManager ? (
              <Popconfirm
                title={t('packingUi.confirm.reopenPallet')}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => reopenPackPallet(pallet.palletGuid), t('packingUi.message.palletReopened'))}
              >
                <Button disabled={acting}>{t('packingUi.action.reopenPallet')}</Button>
              </Popconfirm>
            ) : null}
            {!readOnly ? (
              <Button icon={<EditOutlined />} disabled={acting} onClick={openHeaderEditor}>
                {t('packingUi.action.editPallet')}
              </Button>
            ) : null}
            {canDispatchPallet(pallet, ownerCode) ? (
              <Popconfirm
                title={t('packingUi.confirm.dispatchPallet', { name: placeName ?? '' })}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() => act(() => dispatchPackPallet(pallet.palletGuid), t('packingUi.message.palletDispatched'))}
              >
                <Button type="primary" icon={<SendOutlined />} disabled={acting}>
                  {t('packingUi.action.dispatchPallet')}
                </Button>
              </Popconfirm>
            ) : null}
            {isBuilding && !readOnly ? (
              <Button type="primary" disabled={acting || pallet.cartonCount === 0} onClick={() => void sealAndPrint()}>
                {t('packingUi.action.sealPalletAndPrint')}
              </Button>
            ) : (
              <Button
                type={canDispatchPallet(pallet, ownerCode) ? 'default' : 'primary'}
                icon={<PrinterOutlined />}
                onClick={() => printPackLabels(labelFields)}
              >
                {t('packingUi.action.printPalletLabel')}
              </Button>
            )}
          </div>
        ) : null
      }
    >
      <Spin spinning={loading}>
        {pallet ? (
          <div className="wh-packing-drawer-body">
            {/* 进度：组托 → 封托 → 发出 → 签收；有时间的步骤点亮。 */}
            <Steps
              size="small"
              items={steps.map((step) => ({
                title: t(`packingUi.step.${step.key}`),
                status: step.done ? 'finish' : 'wait',
                description: step.done
                  ? `${formatPackTime(step.time)}${step.by ? ` · ${step.by}` : ''}`
                  : t(`packingUi.step.${step.key}Pending`),
              }))}
            />

            <div className="wh-packing-desc wh-packing-desc-boxed">
              <div>
                <span>{t('packingUi.columns.destination')}</span>
                {stateKind === 'received' ? (
                  <ReceivedPlace ownerName={pallet.ownerName} />
                ) : (
                  <DestinationText destination={pallet.destination} t={t} />
                )}
              </div>
              <div>
                <span>{t('packingUi.columns.consignmentNo')}</span>
                {pallet.consignmentNo ? <span className="wh-packing-mono">{pallet.consignmentNo}</span> : '—'}
              </div>
              <div>
                <span>{t('packingUi.columns.owner')}</span>
                {pallet.ownerName}
              </div>
              <div className="wh-packing-desc-location">
                <span>{t('packingUi.columns.location')}</span>
                {canSetLocation ? (
                  <LocationSelect
                    className="wh-packing-location-select"
                    value={pallet.locationCode}
                    disabled={acting}
                    onChange={(value) =>
                      void act(() => setPackPalletLocation(pallet.palletGuid, value), t('packingUi.message.locationSaved'))
                    }
                  />
                ) : (
                  pallet.locationCode || '—'
                )}
              </div>
              <div>
                <span>{t('packingUi.columns.total')}</span>
                {t('packingUi.summary.palletTotal', {
                  cartons: pallet.cartonCount,
                  sku: pallet.skuCount,
                  pieces: pallet.pieceCount,
                })}
              </div>
              <div>
                <span>{t('packingUi.columns.weight')}</span>
                {pallet.weightKg === null || pallet.weightKg === undefined ? '—' : `${pallet.weightKg} kg`}
              </div>
              <div>
                <span>{t('packingUi.columns.remark')}</span>
                {pallet.remark || '—'}
              </div>
            </div>

            {isBuilding && !readOnly ? (
              <Input.Search
                allowClear
                value={addCode}
                disabled={acting}
                placeholder={t('packingUi.pallets.addCartonPlaceholder')}
                enterButton={t('packingUi.pallets.addCarton')}
                onChange={(event) => setAddCode(event.target.value)}
                onSearch={(value) => void addCarton(value)}
              />
            ) : null}

            <Tabs
              size="small"
              items={[
                {
                  key: 'cartons',
                  label: t('packingUi.tab.cartonsCount', { count: pallet.cartons.length }),
                  children: (
                    <MeasuredTable<PackCartonSummary>
                      metricId="packing.pallet-drawer-cartons"
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
                  label: t('packingUi.tab.productsCount', { count: pallet.products.length }),
                  children: (
                    <MeasuredTable<PackPalletProduct>
                      metricId="packing.pallet-drawer-products"
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
                  label: t('packingUi.tab.eventsCount', { count: events.length }),
                  children: <EventList events={events} t={t} />,
                },
                {
                  key: 'label',
                  label: t('packingUi.tab.label'),
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

            {stateKind === 'transit' ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('packingUi.note.palletInTransit')}
              </Typography.Paragraph>
            ) : stateKind === 'received' ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('packingUi.note.palletReceived')}
              </Typography.Paragraph>
            ) : isSealed && isManager ? (
              <Typography.Paragraph type="secondary" className="wh-packing-drawer-note">
                {t('packingUi.note.reopenPallet')}
              </Typography.Paragraph>
            ) : null}
          </div>
        ) : (
          <div className="wh-packing-drawer-placeholder" />
        )}
      </Spin>

      <Modal
        open={draft !== null}
        title={t('packingUi.action.editPallet')}
        width={460}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={savingHeader}
        onOk={() => void saveHeader()}
        onCancel={() => setDraft(null)}
        destroyOnClose
      >
        {draft ? (
          <div className="wh-packing-form">
            <label>{t('packingUi.columns.destination')}</label>
            <DestinationSelect
              value={draft.destination ?? UNASSIGNED_DESTINATION}
              onChange={(destination) => setDraft({ ...draft, destination })}
              disabled={savingHeader}
            />
            <label>{t('packingUi.columns.consignmentNo')}</label>
            <Input
              maxLength={100}
              value={draft.consignmentNo}
              onChange={(event) => setDraft({ ...draft, consignmentNo: event.target.value })}
            />
            <label>{t('packingUi.columns.weight')}</label>
            <InputNumber
              min={0}
              max={9999.99}
              precision={2}
              controls={false}
              addonAfter="kg"
              value={draft.weight}
              onChange={(value) => setDraft({ ...draft, weight: typeof value === 'number' ? value : null })}
            />
            <label>{t('packingUi.columns.remark')}</label>
            <Input.TextArea
              rows={2}
              maxLength={200}
              value={draft.remark}
              onChange={(event) => setDraft({ ...draft, remark: event.target.value })}
            />
            {isSealed ? <Typography.Text type="secondary">{t('packingUi.note.reprintAfterDestination')}</Typography.Text> : null}
          </div>
        ) : null}
      </Modal>
    </Drawer>
  )
}
