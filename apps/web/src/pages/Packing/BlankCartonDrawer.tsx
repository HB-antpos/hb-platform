import { PrinterOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, InputNumber, Segmented, Space, Typography, message } from 'antd'
import { useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { createPackBlankBatch, readPackingError } from '../../services/packingService'
import type {
  PackBlankBatchResult,
  PackBlankLimitData,
  PackCartonSummary,
  PackDestination,
  PackDestinationRequest,
} from '../../types/packing'
import { PACK_DESTINATION_TYPE } from '../../types/packing'

import { BlankBatchDetail, BlankBatchList } from './BlankBatchViews'
import BlankLabelPrintModal from './BlankLabelPrintModal'
import {
  BLANK_BATCH_DEFAULT,
  BLANK_BATCH_MAX,
  BLANK_COPIES_MAX,
  BLANK_QUICK_COUNTS,
  BLANK_UNACTIVATED_LIMIT,
  blankRequestSignature,
  clampBlankCount,
  clampCopies,
  generateRequestGuid,
  remainingBlankQuota,
  resolveBlankLimitError,
  resolveClientRequestId,
  sampleBlankCartonNo,
} from './blankCartons'
import type { ClientRequestMemo } from './blankCartons'
import DestinationSelect, { useDestinationProblemText } from './DestinationSelect'
import { UNASSIGNED_DESTINATION, destinationToRequest, validateDestination } from './packingLogic'
import type { PackDestinationValue } from './packingLogic'
import { PackLabelPreview, usePackingPage } from './packingShared'
import { buildBlankCartonLabelFields } from './packLabel'

type DrawerView = 'create' | 'batches'
type Submitting = 'create' | 'print' | null

interface PrintTarget {
  key: number
  cartons: PackCartonSummary[]
  copies: number
  autoStart: boolean
}

interface BlankCartonDrawerProps {
  open: boolean
  /** 当前地点未启用的空箱标张数（列表计数里的 blankCount），只用于提示额度。 */
  blankCount: number
  onClose: () => void
}

/** 「批量箱标」抽屉：批量创建空箱标（只创建 / 创建并打印）+ 最近批次与批次详情（补打 / 全部重打）。 */
export default function BlankCartonDrawer({ open, blankCount, onClose }: BlankCartonDrawerProps) {
  const { t } = useTranslation()
  const { ownerCode, owner, isWarehouse, stores, carriers, notifyChanged } = usePackingPage()
  const problemText = useDestinationProblemText()
  const [view, setView] = useState<DrawerView>('create')
  const [batchGuid, setBatchGuid] = useState<string | null>(null)
  const [count, setCount] = useState(BLANK_BATCH_DEFAULT)
  const [copies, setCopies] = useState(1)
  const [destination, setDestination] = useState<PackDestinationValue>(UNASSIGNED_DESTINATION)
  const [submitting, setSubmitting] = useState<Submitting>(null)
  const [created, setCreated] = useState<PackBlankBatchResult | null>(null)
  const [limitError, setLimitError] = useState<{ existing: number; limit: number } | null>(null)
  const [refreshKey, setRefreshKey] = useState(0)
  const [printTarget, setPrintTarget] = useState<PrintTarget | null>(null)
  // 同一次点击（含失败重试）固定 clientRequestId；参数变了就换新的，创建成功后清掉。
  const requestMemoRef = useRef<ClientRequestMemo | null>(null)
  const printKeyRef = useRef(0)

  const openPrint = (cartons: PackCartonSummary[], printCopies: number, autoStart: boolean) => {
    printKeyRef.current += 1
    setPrintTarget({ key: printKeyRef.current, cartons, copies: printCopies, autoStart })
  }

  const bumpRefresh = () => {
    setRefreshKey((value) => value + 1)
    notifyChanged()
  }

  // 预览：用当前去向拼一张示例箱，箱号是样式示例，真实箱号以创建结果为准。
  const previewFields = useMemo(() => {
    const preview: PackDestination = {
      destinationType: destination.destinationType,
      code: destination.destinationCode ?? null,
      name: stores.find((store) => store.value === destination.destinationCode)?.label ?? null,
      carrierGuid: destination.carrierGuid ?? null,
      carrierName: carriers.find((carrier) => carrier.carrierGuid === destination.carrierGuid)?.name ?? null,
    }
    const sample = {
      cartonGuid: 'preview',
      cartonNo: sampleBlankCartonNo(),
      status: 1,
      ownerCode,
      ownerName: owner.name,
      originCode: ownerCode,
      originName: owner.name,
      destination: preview,
      skuCount: 0,
      pieceCount: 0,
      createdByName: '',
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isBlank: true,
      batchSeq: 1,
      batchCount: count,
    } satisfies PackCartonSummary
    return buildBlankCartonLabelFields(sample)
  }, [carriers, count, destination, owner.name, ownerCode, stores])

  const submit = async (print: boolean) => {
    const problem = validateDestination(destination, isWarehouse)
    if (problem) {
      void message.warning(problemText(problem))
      return
    }
    const safeCount = clampBlankCount(count)
    const unassigned = destination.destinationType === PACK_DESTINATION_TYPE.Unassigned
    const destinationRequest: Partial<PackDestinationRequest> = unassigned ? {} : destinationToRequest(destination)
    const signature = blankRequestSignature({
      ownerCode,
      count: safeCount,
      destinationType: unassigned ? undefined : destinationRequest.destinationType,
      destinationCode: unassigned ? null : destinationRequest.destinationCode,
      carrierGuid: unassigned ? null : destinationRequest.carrierGuid,
    })
    requestMemoRef.current = resolveClientRequestId(requestMemoRef.current, signature, generateRequestGuid)
    setSubmitting(print ? 'print' : 'create')
    setLimitError(null)
    try {
      const result = await createPackBlankBatch({
        ownerCode,
        count: safeCount,
        clientRequestId: requestMemoRef.current.id,
        ...destinationRequest,
      })
      requestMemoRef.current = null
      setCreated(result)
      bumpRefresh()
      void message.success(
        result.duplicate
          ? t('packingUi.blank.create.duplicate', { count: result.count })
          : t('packingUi.blank.create.created', { count: result.count, first: result.firstCartonNo, last: result.lastCartonNo }),
      )
      if (print) {
        openPrint(result.cartons, clampCopies(copies), true)
      }
    } catch (error) {
      const info = readPackingError<PackBlankLimitData>(error)
      const limit = resolveBlankLimitError(info)
      if (limit) {
        // 已满是确定的业务拒绝，下次换新的请求号。
        requestMemoRef.current = null
        setLimitError(limit)
      } else {
        void message.error(info.message)
      }
    } finally {
      setSubmitting(null)
    }
  }

  const quota = remainingBlankQuota(blankCount)
  const busy = submitting !== null

  const createForm = (
    <div className="wh-packing-blank-form">
      <section className="wh-packing-blank-section">
        <Typography.Text strong>{t('packingUi.blank.create.count')}</Typography.Text>
        <Space wrap>
          <InputNumber
            min={1}
            max={BLANK_BATCH_MAX}
            precision={0}
            value={count}
            disabled={busy}
            onChange={(value) => setCount(clampBlankCount(value))}
            aria-label={t('packingUi.blank.create.count')}
          />
          <Segmented
            value={BLANK_QUICK_COUNTS.includes(count as (typeof BLANK_QUICK_COUNTS)[number]) ? count : undefined}
            disabled={busy}
            options={BLANK_QUICK_COUNTS.map((value) => ({ value, label: String(value) }))}
            onChange={(value) => setCount(Number(value))}
          />
        </Space>
        <Typography.Text type="secondary">
          {t('packingUi.blank.create.countHint', { max: BLANK_BATCH_MAX })}
          {t('packingUi.blank.create.quota', { count: blankCount, limit: BLANK_UNACTIVATED_LIMIT })}
        </Typography.Text>
        {quota < count ? (
          <Alert
            type="warning"
            showIcon
            message={t('packingUi.blank.create.limitExceeded', { existing: blankCount, limit: BLANK_UNACTIVATED_LIMIT })}
          />
        ) : null}
        {limitError ? (
          <Alert type="error" showIcon message={t('packingUi.blank.create.limitExceeded', limitError)} />
        ) : null}
      </section>
      <section className="wh-packing-blank-section">
        <Typography.Text strong>{t('packingUi.blank.create.destination')}</Typography.Text>
        <DestinationSelect value={destination} onChange={setDestination} disabled={busy} />
        <Typography.Text type="secondary">{t('packingUi.blank.create.destinationHint')}</Typography.Text>
      </section>
      <section className="wh-packing-blank-section">
        <Typography.Text strong>{t('packingUi.blank.create.copies')}</Typography.Text>
        <InputNumber
          min={1}
          max={BLANK_COPIES_MAX}
          precision={0}
          value={copies}
          disabled={busy}
          onChange={(value) => setCopies(clampCopies(value))}
          addonAfter={t('packingUi.blank.create.copiesUnit')}
        />
      </section>
      <section className="wh-packing-blank-section">
        <Typography.Text strong>{t('packingUi.blank.create.preview')}</Typography.Text>
        <div className="wh-packing-label-stack">
          <PackLabelPreview fields={previewFields} scale={0.78} />
        </div>
        <Typography.Text type="secondary">{t('packingUi.blank.create.previewHint', { count })}</Typography.Text>
        <Typography.Text type="secondary">{t('packingUi.blank.create.paperHint')}</Typography.Text>
      </section>
      {created ? (
        <Alert
          type={created.duplicate ? 'info' : 'success'}
          showIcon
          message={
            created.duplicate
              ? t('packingUi.blank.create.duplicate', { count: created.count })
              : t('packingUi.blank.create.created', { count: created.count, first: created.firstCartonNo, last: created.lastCartonNo })
          }
          action={
            <Space direction="vertical" size={4}>
              <Button size="small" icon={<PrinterOutlined />} onClick={() => openPrint(created.cartons, clampCopies(copies), false)}>
                {t('packingUi.blank.create.printBatch')}
              </Button>
              <Button
                size="small"
                onClick={() => {
                  setBatchGuid(created.batchGuid)
                  setView('batches')
                }}
              >
                {t('packingUi.blank.create.viewBatch')}
              </Button>
            </Space>
          }
        />
      ) : null}
    </div>
  )

  return (
    <Drawer
      open={open}
      width={560}
      title={t('packingUi.blank.title')}
      onClose={onClose}
      destroyOnClose
      footer={
        view === 'create' ? (
          <div className="wh-packing-drawer-footer">
            <Button disabled={busy} loading={submitting === 'create'} onClick={() => void submit(false)}>
              {t('packingUi.blank.create.createOnly')}
            </Button>
            <Button
              type="primary"
              icon={<PrinterOutlined />}
              disabled={busy && submitting !== 'print'}
              loading={submitting === 'print'}
              onClick={() => void submit(true)}
            >
              {t('packingUi.blank.create.createAndPrint', { count })}
            </Button>
          </div>
        ) : null
      }
    >
      <Segmented<DrawerView>
        block
        value={view}
        options={[
          { value: 'create', label: t('packingUi.blank.view.create') },
          { value: 'batches', label: t('packingUi.blank.view.batches') },
        ]}
        onChange={(next) => {
          setView(next)
          if (next === 'batches') {
            setBatchGuid(null)
          }
        }}
        className="wh-packing-blank-views"
      />
      {view === 'create' ? (
        createForm
      ) : batchGuid ? (
        <BlankBatchDetail
          batchGuid={batchGuid}
          refreshKey={refreshKey}
          onBack={() => setBatchGuid(null)}
          onPrint={(cartons) => openPrint(cartons, 1, false)}
        />
      ) : (
        <BlankBatchList ownerCode={ownerCode} refreshKey={refreshKey} onOpen={setBatchGuid} />
      )}
      {printTarget ? (
        <BlankLabelPrintModal
          key={printTarget.key}
          cartons={printTarget.cartons}
          defaultCopies={printTarget.copies}
          autoStart={printTarget.autoStart}
          onClose={() => setPrintTarget(null)}
          onPrinted={bumpRefresh}
        />
      ) : null}
    </Drawer>
  )
}
