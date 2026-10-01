import { AlertOutlined, ToolOutlined, WarningOutlined } from '@ant-design/icons'
import { Alert, Button, Descriptions, Space, Spin, Tag, Timeline, Typography, theme } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useMemo, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { reviewOperationAudit } from '../../../services/operationAuditService'
import type { LegacyEmployeeLogReview } from '../../../types/legacyEmployeeLog'
import type {
  OperationAuditContext,
  OperationAuditDetail,
  OperationAuditDetailItem,
  OperationAuditListItem,
  OperationAuditOutcome,
} from '../../../types/operationAudit'
import RiskReviewSection from '../LegacyEmployeeLogs/RiskReviewSection'

import { buildSystemLogLink, formatMoney, formatSignedMoney, summarizeProducts } from './operationLogsLogic'
import PosProductSummary from './PosProductSummary'

interface PosLogDetailPanelProps {
  /** 列表行（立即可显示）；详情加载完成后用 detail 补齐商品明细与安全属性。 */
  record: OperationAuditListItem
  detail: OperationAuditDetail | null
  detailLoading: boolean
  context: OperationAuditContext | null
  contextLoading: boolean
  contextError: boolean
  storeLabel: string
  canReview: boolean
  canViewSystemLogs: boolean
  currentUserName: string
  operationLabel: (operationType: string) => string
  outcomeTag: (outcome: OperationAuditOutcome) => ReactNode
  deviceSystemLabel: (deviceSystem?: string | null) => string
  onShowCashierDay: (record: OperationAuditListItem) => void
  onReviewChanged: (record: OperationAuditListItem, review: LegacyEmployeeLogReview | null) => void
}

function formatValue(value: unknown) {
  return value === null || value === undefined || value === '' ? '-' : String(value)
}

function formatSafeProperties(value?: string) {
  if (!value) return '-'
  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}

/** 新收银一条审计事件的详情：异常依据与核查、基本信息、商品明细、同设备前后操作。 */
export default function PosLogDetailPanel(props: PosLogDetailPanelProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const { record, detail } = props
  const currency = record.currencyCode || 'AUD'
  const flags = record.flags ?? []
  const danger = Boolean(record.isDanger)

  const itemColumns = useMemo<ColumnsType<OperationAuditDetailItem>>(
    () => [
      {
        title: t('operationLogs.detail.product'),
        key: 'product',
        width: 220,
        render: (_, item) => (
          <Space direction="vertical" size={0}>
            <Typography.Text>{item.displayName || item.productCode || '-'}</Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {[
                item.productCode,
                item.itemNumber ? `${t('operationLogs.detail.itemNumber')}: ${item.itemNumber}` : null,
                item.lookupCode,
                item.lineKind ? `${t('operationLogs.detail.lineKind')}: ${item.lineKind}` : null,
              ].filter(Boolean).join(' / ') || '-'}
            </Typography.Text>
          </Space>
        ),
      },
      {
        title: t('operationLogs.detail.quantity'),
        key: 'quantity',
        width: 150,
        render: (_, item) => `${formatValue(item.beforeQuantity)} → ${formatValue(item.afterQuantity)}`,
      },
      {
        title: t('operationLogs.detail.unitPrice'),
        key: 'unitPrice',
        width: 170,
        render: (_, item) => `${formatMoney(item.beforeUnitPrice, currency)} → ${formatMoney(item.afterUnitPrice, currency)}`,
      },
      {
        title: t('operationLogs.detail.discount'),
        key: 'discount',
        width: 170,
        render: (_, item) => `${formatMoney(item.beforeDiscountAmount, currency)} → ${formatMoney(item.afterDiscountAmount, currency)}`,
      },
      {
        title: t('operationLogs.detail.actual'),
        key: 'actual',
        width: 190,
        render: (_, item) => `${formatMoney(item.beforeActualAmount, currency)} → ${formatMoney(item.afterActualAmount, currency)} (${formatSignedMoney(item.actualAmountDelta, currency)})`,
      },
    ],
    [currency, t],
  )

  const systemLogLink = buildSystemLogLink({
    deviceCode: record.deviceCode,
    deviceSystem: record.deviceSystem,
    traceId: record.traceId,
    occurredAtUtc: record.occurredAtUtc,
  })

  return (
    <Space direction="vertical" size={18} style={{ width: '100%' }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <Space size={6} wrap>
          <Tag style={{ marginInlineEnd: 0 }}>{props.operationLabel(record.operationType)}</Tag>
          {props.outcomeTag(record.outcome)}
          {danger ? <Tag color="error" icon={<WarningOutlined />} style={{ marginInlineEnd: 0 }}>{t('legacyEmployeeLogs.badges.danger')}</Tag> : null}
          {flags.map((flag) => (
            <Tag key={flag.ruleCode} color="warning" icon={<AlertOutlined />} style={{ marginInlineEnd: 0 }}>{t(`legacyEmployeeLogs.rules.${flag.ruleCode}.label`)}</Tag>
          ))}
        </Space>
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 12 }}>
          {record.productCount > 0 ? (
            <PosProductSummary
              record={record}
              fallbackName={t('operationLogs.detail.productFallback')}
              imageSize={48}
              nameStyle={{ fontSize: 17, fontWeight: 600 }}
            />
          ) : (
            <Typography.Text strong style={{ fontSize: 17 }}>
              {props.operationLabel(record.operationType)}
            </Typography.Text>
          )}
          {record.amountImpact ? (
            <span style={{ color: token.colorError, fontWeight: 600, fontSize: 17, fontVariantNumeric: 'tabular-nums' }}>
              −{record.amountImpact.toFixed(2)}
            </span>
          ) : null}
        </div>
        <Typography.Text type="secondary" style={{ fontSize: 13 }}>
          {[record.cashierName || record.cashierId || '-', props.storeLabel, record.deviceCode || '-', dayjs(record.occurredAtUtc).format('HH:mm:ss')].join(' · ')}
        </Typography.Text>
      </div>

      {flags.length > 0 ? (
        <RiskReviewSection
          recordKey={record.eventId}
          flags={flags}
          review={record.review}
          canReview={props.canReview}
          currentUserName={props.currentUserName}
          ruleDescription={(code) => t(`legacyEmployeeLogs.rulesPos.${code}`)}
          submit={(result, note, expectedVersion) => reviewOperationAudit({ eventId: record.eventId, result, note, expectedVersion })}
          onReviewChanged={(review) => props.onReviewChanged(record, review)}
        />
      ) : danger ? (
        <Alert type="error" showIcon={false} message={t('legacyEmployeeLogs.pos.dangerOnly')} />
      ) : null}

      <Descriptions bordered size="small" column={1} labelStyle={{ width: 120 }}>
        <Descriptions.Item label={t('operationLogs.columns.time')}>
          {dayjs(record.occurredAtUtc).format('YYYY-MM-DD HH:mm:ss')}
          <Typography.Text type="secondary">{` · ${t('operationLogs.detail.receivedAt')} ${dayjs(record.receivedAtUtc).format('HH:mm:ss')}`}</Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('legacyEmployeeLogs.detail.storeDevice')}>
          {props.storeLabel} · <Typography.Text code>{record.deviceCode || '-'}</Typography.Text>
          <Typography.Text type="secondary">{` · ${props.deviceSystemLabel(record.deviceSystem)}`}</Typography.Text>
        </Descriptions.Item>
        <Descriptions.Item label={t('operationLogs.columns.employee')}>
          {record.cashierName || '-'}
          {record.cashierId ? (
            <Typography.Text type="secondary" copyable={{ text: record.cashierId }} style={{ fontSize: 11, marginInlineStart: 6 }}>
              {record.cashierId}
            </Typography.Text>
          ) : null}
        </Descriptions.Item>
        {record.isOfflineCached || record.isEmergencyOverride ? (
          <Descriptions.Item label={t('operationLogs.detail.sessionFlags')}>
            <Space wrap size={[4, 4]}>
              {record.isOfflineCached ? <Tag>{t('operationLogs.detail.offlineCached')}</Tag> : null}
              {record.isEmergencyOverride ? <Tag color="warning">{t('operationLogs.detail.emergencyOverride')}</Tag> : null}
            </Space>
          </Descriptions.Item>
        ) : null}
        <Descriptions.Item label={t('operationLogs.detail.reason')}>{record.reasonCode || '-'}</Descriptions.Item>
        {record.paymentMethod || (record.paymentAmount !== undefined && record.paymentAmount !== null) ? (
          <Descriptions.Item label={t('operationLogs.detail.paymentAmount')}>
            {`${record.paymentMethod || '-'} · ${formatMoney(record.paymentAmount, currency)}`}
          </Descriptions.Item>
        ) : null}
        {record.beforeActual !== undefined && record.beforeActual !== null ? (
          <Descriptions.Item label={t('operationLogs.detail.beforeAfterActual')}>
            {`${formatMoney(record.beforeActual, currency)} → ${formatMoney(record.afterActual, currency)}（${formatSignedMoney(record.amountDelta, currency)}）`}
          </Descriptions.Item>
        ) : null}
        {record.orderGuid ? (
          <Descriptions.Item label={t('operationLogs.detail.orderGuid')}>
            <Typography.Text code copyable style={{ fontSize: 12 }}>{record.orderGuid}</Typography.Text>
          </Descriptions.Item>
        ) : null}
        {record.safeMessage ? (
          <Descriptions.Item label={t('operationLogs.detail.safeMessage')}>
            <Typography.Paragraph style={{ marginBottom: 0, whiteSpace: 'pre-wrap' }}>{record.safeMessage}</Typography.Paragraph>
          </Descriptions.Item>
        ) : null}
        <Descriptions.Item label={t('operationLogs.detail.traceId')}>
          <Space wrap>
            <Typography.Text copyable={Boolean(record.traceId)} style={{ fontSize: 12 }}>{record.traceId || '-'}</Typography.Text>
            {props.canViewSystemLogs && systemLogLink ? (
              <Link to={systemLogLink}>
                <ToolOutlined /> {t('operationLogs.detail.openSystemLogs')}
              </Link>
            ) : null}
          </Space>
        </Descriptions.Item>
        <Descriptions.Item label={t('operationLogs.detail.appVersion')}>{record.appVersion || '-'}</Descriptions.Item>
      </Descriptions>

      {props.detailLoading ? <Spin /> : null}
      {detail?.items?.length ? (
        <MeasuredTable<OperationAuditDetailItem>
          metricId="pos-admin.operation-logs.table-2"
          rowKey={(item) => `${item.eventId}-${item.lineIndex}`}
          size="small"
          columns={itemColumns}
          dataSource={detail.items}
          pagination={false}
          scroll={{ x: 900 }}
        />
      ) : null}
      {detail?.propertiesJson ? (
        <div>
          <Typography.Text strong>{t('operationLogs.detail.safeProperties')}</Typography.Text>
          <Typography.Paragraph code style={{ marginTop: 8, marginBottom: 0, whiteSpace: 'pre-wrap' }}>
            {formatSafeProperties(detail.propertiesJson)}
          </Typography.Paragraph>
        </div>
      ) : null}

      <div>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 12, marginBottom: 10 }}>
          <Typography.Text strong>{t('legacyEmployeeLogs.pos.contextTitle')}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {`${record.deviceCode || '-'} · ${t('legacyEmployeeLogs.pos.contextHint', { minutes: props.context?.windowMinutes ?? 5 })}`}
          </Typography.Text>
        </div>
        {props.contextLoading ? (
          <Spin />
        ) : props.contextError ? (
          <Alert type="warning" showIcon message={t('legacyEmployeeLogs.pos.contextFailed')} />
        ) : props.context && props.context.neighbors.length > 1 ? (
          <>
            {props.context.truncated ? (
              <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('legacyEmployeeLogs.pos.contextTruncated', { count: props.context.neighbors.length })} />
            ) : null}
            <Timeline
              items={props.context.neighbors.map((item) => {
                const isCurrent = item.eventId === record.eventId
                return {
                  key: item.eventId,
                  color: isCurrent ? 'orange' : item.isDanger ? 'red' : 'gray',
                  children: (
                    <div style={{ fontWeight: isCurrent ? 500 : undefined, fontSize: 13 }}>
                      <Typography.Text type="secondary" style={{ fontSize: 12, marginInlineEnd: 8, fontVariantNumeric: 'tabular-nums' }}>
                        {dayjs(item.occurredAtUtc).format('HH:mm:ss')}
                      </Typography.Text>
                      {props.operationLabel(item.operationType)}
                      {item.outcome !== 'Succeeded' ? <span style={{ marginInlineStart: 6 }}>{props.outcomeTag(item.outcome)}</span> : null}
                      {item.cashierName && item.cashierName !== record.cashierName ? (
                        <Typography.Text type="secondary" style={{ marginInlineStart: 6 }}>{item.cashierName}</Typography.Text>
                      ) : null}
                      <Typography.Text type="secondary" style={{ marginInlineStart: 6, fontVariantNumeric: 'tabular-nums' }}>
                        {[
                          summarizeProducts(item, t('operationLogs.detail.productFallback')),
                          item.paymentAmount !== undefined && item.paymentAmount !== null ? formatMoney(item.paymentAmount, currency) : null,
                        ].filter((part) => part && part !== '-').join(' · ')}
                      </Typography.Text>
                    </div>
                  ),
                }
              })}
            />
          </>
        ) : (
          <Typography.Text type="secondary">{t('legacyEmployeeLogs.pos.contextEmpty')}</Typography.Text>
        )}
        {record.cashierId ? (
          <Button size="small" style={{ marginTop: 8 }} onClick={() => props.onShowCashierDay(record)}>
            {t('legacyEmployeeLogs.pos.onlyCashierDay')} →
          </Button>
        ) : null}
      </div>
    </Space>
  )
}
