import { ArrowLeftOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Descriptions,
  Empty,
  Space,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import { getLinklySettlementDetail } from '../../../services/linklySettlementService'
import type { LinklySettlementCardTotal, LinklySettlementDetail } from '../../../types/linklySettlement'
import {
  buildReceiptAmountRows,
  formatAmountMinor,
  createLatestAbortableRequestGuard,
  getLinklySettlementRouteIdFromPathname,
  getAmountParseStatusColor,
  getProviderSubmissionColor,
  getSettlementStatusColor,
} from '../LinklySettlements/logic'
import { MeasuredTable } from '../../../components/MeasuredTable'

function formatDateTime(value?: string | null) {
  if (!value) return '--'
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : '--'
}

function displayValue(value: unknown) {
  return value === null || value === undefined || value === '' ? '--' : String(value)
}

export default function LinklySettlementDetailPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const id = getLinklySettlementRouteIdFromPathname(location.pathname)
  const [loading, setLoading] = useState(true)
  const [detail, setDetail] = useState<LinklySettlementDetail | null>(null)
  const [loadFailed, setLoadFailed] = useState(false)
  const requestGuardRef = useRef(createLatestAbortableRequestGuard())

  useEffect(() => {
    if (id === null) {
      requestGuardRef.current.abort()
      setDetail(null)
      setLoading(false)
      setLoadFailed(true)
      return
    }

    const currentRequest = requestGuardRef.current.begin()
    setDetail(null)
    setLoading(true)
    setLoadFailed(false)
    void getLinklySettlementDetail(id, currentRequest.signal)
      .then((result) => {
        if (requestGuardRef.current.isLatest(currentRequest.requestId)) setDetail(result)
      })
      .catch((error) => {
        if (
          !requestGuardRef.current.isLatest(currentRequest.requestId)
          || (error instanceof DOMException && error.name === 'AbortError')
        ) return
        console.error(error)
        setLoadFailed(true)
        message.error(error instanceof Error ? error.message : t('linklySettlements.messages.detailLoadFailed'))
      })
      .finally(() => {
        if (requestGuardRef.current.isLatest(currentRequest.requestId)) setLoading(false)
      })
    return () => requestGuardRef.current.abort()
  }, [id, t])

  const cardColumns = useMemo<ColumnsType<LinklySettlementCardTotal>>(() => [
    {
      title: t('linklySettlements.detail.cardName'),
      dataIndex: 'cardName',
      key: 'cardName',
      width: 180,
    },
    {
      title: t('linklySettlements.columns.purchase'),
      key: 'purchaseAmountMinor',
      align: 'right',
      width: 150,
      render: (_, record) => formatAmountMinor(record.purchaseAmountMinor, detail?.amountParseStatus ?? 'Missing'),
    },
    {
      title: t('linklySettlements.detail.purchaseCount'),
      dataIndex: 'purchaseCount',
      key: 'purchaseCount',
      align: 'right',
      width: 110,
      render: displayValue,
    },
    {
      title: t('linklySettlements.columns.refund'),
      key: 'refundAmountMinor',
      align: 'right',
      width: 150,
      render: (_, record) => formatAmountMinor(record.refundAmountMinor, detail?.amountParseStatus ?? 'Missing'),
    },
    {
      title: t('linklySettlements.detail.refundCount'),
      dataIndex: 'refundCount',
      key: 'refundCount',
      align: 'right',
      width: 110,
      render: displayValue,
    },
    {
      title: t('linklySettlements.columns.cashOut'),
      key: 'cashOutAmountMinor',
      align: 'right',
      width: 150,
      render: (_, record) => formatAmountMinor(record.cashOutAmountMinor, detail?.amountParseStatus ?? 'Missing'),
    },
    {
      title: t('linklySettlements.detail.cashOutCount'),
      dataIndex: 'cashOutCount',
      key: 'cashOutCount',
      align: 'right',
      width: 120,
      render: displayValue,
    },
    {
      title: t('linklySettlements.columns.net'),
      key: 'totalAmountMinor',
      align: 'right',
      width: 150,
      render: (_, record) => formatAmountMinor(record.totalAmountMinor, detail?.amountParseStatus ?? 'Missing'),
    },
    {
      title: t('linklySettlements.detail.totalCount'),
      dataIndex: 'totalCount',
      key: 'totalCount',
      align: 'right',
      width: 100,
      render: displayValue,
    },
  ], [detail?.amountParseStatus, t])

  // 回单区的金额数据：只列金额与笔数，列比「卡种明细」少，窄屏不需要横向滚动。
  const receiptAmountColumns = useMemo<ColumnsType<LinklySettlementCardTotal>>(() => {
    const status = detail?.amountParseStatus ?? 'Missing'
    const amountCell = (amount: number | null | undefined, count: number | null | undefined) => (
      <span style={{ fontVariantNumeric: 'tabular-nums' }}>
        {formatAmountMinor(amount, status)}
        <Typography.Text type="secondary"> × {displayValue(count)}</Typography.Text>
      </span>
    )
    return [
      { title: t('linklySettlements.detail.cardName'), dataIndex: 'cardName', key: 'cardName', width: 120 },
      {
        title: t('linklySettlements.columns.purchase'),
        key: 'purchase',
        align: 'right',
        render: (_, record) => amountCell(record.purchaseAmountMinor, record.purchaseCount),
      },
      {
        title: t('linklySettlements.columns.refund'),
        key: 'refund',
        align: 'right',
        render: (_, record) => amountCell(record.refundAmountMinor, record.refundCount),
      },
      {
        title: t('linklySettlements.columns.cashOut'),
        key: 'cashOut',
        align: 'right',
        render: (_, record) => amountCell(record.cashOutAmountMinor, record.cashOutCount),
      },
      {
        title: t('linklySettlements.columns.net'),
        key: 'net',
        align: 'right',
        render: (_, record) => amountCell(record.totalAmountMinor, record.totalCount),
      },
    ]
  }, [detail?.amountParseStatus, t])
  const receiptAmountRows = useMemo(
    () => (detail?.amountParseStatus === 'Parsed'
      ? buildReceiptAmountRows(detail.cardTotals, detail.amountSummary, t('linklySettlements.detail.totalRow'))
      : []),
    [detail, t],
  )

  const amountStatus = detail?.amountParseStatus ?? 'Missing'
  const summary = detail?.amountSummary

  return (
    <PageContainer
      title={t('linklySettlements.detail.title')}
      subtitle={detail ? `${detail.storeCode} / ${detail.deviceCode} / ${detail.businessDate}` : undefined}
      extra={(
        <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/pos-admin/linkly-settlements')}>
          {t('linklySettlements.actions.backToList')}
        </Button>
      )}
    >
      {loadFailed ? (
        <Alert type="error" showIcon message={t('linklySettlements.messages.detailLoadFailed')} />
      ) : (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Card size="small" title={t('linklySettlements.detail.amountSummary')} loading={loading}>
            {detail ? (
              <Descriptions bordered size="small" column={{ xs: 1, sm: 2, lg: 4 }}>
                <Descriptions.Item label={t('linklySettlements.detail.parseStatus')}>
                  <Tag color={getAmountParseStatusColor(amountStatus)}>
                    {t(`linklySettlements.amountParseStatus.${amountStatus}`)}
                  </Tag>
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.currency')}>
                  {displayValue(summary?.currencyCode)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.purchase')}>
                  {formatAmountMinor(summary?.purchaseAmountMinor, amountStatus)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.purchaseCount')}>
                  {displayValue(summary?.purchaseCount)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.refund')}>
                  {formatAmountMinor(summary?.refundAmountMinor, amountStatus)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.refundCount')}>
                  {displayValue(summary?.refundCount)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.cashOut')}>
                  {formatAmountMinor(summary?.cashOutAmountMinor, amountStatus)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.cashOutCount')}>
                  {displayValue(summary?.cashOutCount)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.net')}>
                  {formatAmountMinor(summary?.totalAmountMinor, amountStatus)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.totalCount')}>
                  {displayValue(summary?.totalCount)}
                </Descriptions.Item>
              </Descriptions>
            ) : null}
          </Card>

          <Card size="small" title={t('linklySettlements.detail.cardTotals')} loading={loading}>
            <MeasuredTable<LinklySettlementCardTotal> metricId="pos-admin.linkly-settlement-detail.table-1"
              rowKey={(record, index) => `${record.cardName}-${index ?? 0}`}
              size="small"
              columns={cardColumns}
              dataSource={detail?.cardTotals ?? []}
              pagination={false}
              scroll={{ x: 1220 }}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('linklySettlements.detail.noCardTotals')} /> }}
            />
          </Card>

          <Card size="small" title={t('linklySettlements.detail.identityAndStatus')} loading={loading}>
            {detail ? (
              <Descriptions bordered size="small" column={{ xs: 1, sm: 1, md: 2, lg: 3 }}>
                <Descriptions.Item label={t('linklySettlements.detail.id')}>{detail.id}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.settlementGuid')}>{detail.settlementGuid}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.businessDate')}>{detail.businessDate}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.store')}>{detail.storeCode}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.device')}>{detail.deviceCode}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.clientRevision')}>{detail.clientRevision}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.providerSessionId')}>{displayValue(detail.providerSessionId)}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.cloudBackendSessionId')}>{displayValue(detail.cloudBackendSessionId)}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.filters.connectionMode')}>
                  {t(`linklySettlements.connectionMode.${detail.connectionMode}`)}
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.filters.environment')}>
                  <Tag color={detail.environment === 'Production' ? 'blue' : 'orange'}>
                    {t(`linklySettlements.environment.${detail.environment}`)}
                  </Tag>
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.filters.status')}>
                  <Tag color={getSettlementStatusColor(detail.status)}>
                    {t(`linklySettlements.status.${detail.status}`)}
                  </Tag>
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.filters.submissionState')}>
                  <Tag color={getProviderSubmissionColor(detail.providerSubmissionState)}>
                    {detail.providerSubmissionState
                      ? t(`linklySettlements.submissionState.${detail.providerSubmissionState}`)
                      : '--'}
                  </Tag>
                </Descriptions.Item>
              </Descriptions>
            ) : null}
          </Card>

          <Card size="small" title={t('linklySettlements.detail.response')} loading={loading}>
            {detail ? (
              <Descriptions bordered size="small" column={{ xs: 1, sm: 1, md: 2 }}>
                <Descriptions.Item label={t('linklySettlements.detail.responseCode')}>
                  <Typography.Text code>{displayValue(detail.responseCode)}</Typography.Text>
                </Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.responseText')}>
                  <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                    {displayValue(detail.responseText)}
                  </Typography.Text>
                </Descriptions.Item>
              </Descriptions>
            ) : null}
          </Card>

          <Card size="small" title={t('linklySettlements.detail.times')} loading={loading}>
            {detail ? (
              <Descriptions bordered size="small" column={{ xs: 1, sm: 1, md: 2, lg: 3 }}>
                <Descriptions.Item label={t('linklySettlements.columns.requestedAt')}>{formatDateTime(detail.requestedAtUtc)}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.completedAt')}>{formatDateTime(detail.completedAtUtc)}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.receivedAt')}>{formatDateTime(detail.receivedAtUtc)}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.updatedAt')}>{formatDateTime(detail.updatedAtUtc)}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.firstPrintedAt')}>{formatDateTime(detail.firstPrintedAtUtc)}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.lastPrintedAt')}>{formatDateTime(detail.lastPrintedAtUtc)}</Descriptions.Item>
              </Descriptions>
            ) : null}
          </Card>

          <Card size="small" title={t('linklySettlements.detail.printing')} loading={loading}>
            {detail ? (
              <Descriptions bordered size="small" column={{ xs: 1, sm: 1, md: 3 }}>
                <Descriptions.Item label={t('linklySettlements.columns.receipts')}>{detail.receiptCount}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.columns.prints')}>{detail.printCount}</Descriptions.Item>
                <Descriptions.Item label={t('linklySettlements.detail.lastPrintError')}>
                  <Typography.Text type={detail.lastPrintError ? 'danger' : undefined}>
                    {displayValue(detail.lastPrintError)}
                  </Typography.Text>
                </Descriptions.Item>
              </Descriptions>
            ) : null}
          </Card>

          <Card size="small" title={t('linklySettlements.detail.receipts')} loading={loading}>
            {detail?.receipts.length || receiptAmountRows.length || detail?.generatedSummary ? (
              <Space direction="vertical" size={8} style={{ width: '100%' }}>
                {(detail?.receipts ?? []).map((receipt, index) => (
                  <Card
                    key={`${index}-${receipt.length}`}
                    size="small"
                    type="inner"
                    title={t('linklySettlements.detail.receiptNumber', { number: index + 1 })}
                  >
                    <pre style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontFamily: 'monospace' }}>
                      {receipt}
                    </pre>
                  </Card>
                ))}
                {detail?.generatedSummary ? (
                  <Card
                    size="small"
                    type="inner"
                    title={t('linklySettlements.detail.generatedSummary')}
                    extra={<Typography.Text type="secondary">{t('linklySettlements.detail.generatedSummaryHint')}</Typography.Text>}
                  >
                    <pre style={{ margin: 0, whiteSpace: 'pre', overflowX: 'auto', fontFamily: 'monospace' }}>
                      {detail.generatedSummary}
                    </pre>
                  </Card>
                ) : null}
                {receiptAmountRows.length ? (
                  <Card size="small" type="inner" title={t('linklySettlements.detail.receiptAmountData')}>
                    <MeasuredTable<LinklySettlementCardTotal> metricId="pos-admin.linkly-settlement-detail.table-2"
                      rowKey={(record, index) => `${record.cardName}-${index ?? 0}`}
                      size="small"
                      columns={receiptAmountColumns}
                      dataSource={receiptAmountRows}
                      pagination={false}
                      scroll={{ x: 'max-content' }}
                    />
                  </Card>
                ) : null}
              </Space>
            ) : (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('linklySettlements.detail.noReceipts')} />
            )}
          </Card>
        </Space>
      )}
    </PageContainer>
  )
}
