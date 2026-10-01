import { AlertOutlined, UnorderedListOutlined, WarningOutlined } from '@ant-design/icons'
import { Button, Segmented, Space, Tag, Typography, theme } from 'antd'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import type {
  LegacyEmployeeLogOperationCount,
  LegacyEmployeeLogRiskSummary,
  LegacyReviewStatus,
  LegacyRiskLens,
} from '../../../types/legacyEmployeeLog'

import {
  DANGER_GROUP_OPERATIONS,
  LEGACY_REVIEW_STATUSES,
  LEGACY_RULE_CODES,
  QUICK_FILTER_OPERATIONS,
  sameOperations,
  sumOperationCounts,
  type LegacyDangerGroupKey,
  type LegacyQuickFilterKey,
  type LegacyRiskFilter,
} from './legacyEmployeeLogsLogic'

const CATEGORY_KEYS = Object.keys(QUICK_FILTER_OPERATIONS) as LegacyQuickFilterKey[]
const DANGER_KEYS = Object.keys(DANGER_GROUP_OPERATIONS) as LegacyDangerGroupKey[]

interface RiskLensBarProps {
  risk: LegacyRiskFilter
  /** 当前所选操作类型（「全部 / 危险」入口的细分用它表示）。 */
  operations: string[] | undefined
  total: number
  counts: LegacyEmployeeLogOperationCount[]
  summary?: LegacyEmployeeLogRiskSummary
  people: number
  devices: number
  onLensChange: (lens: LegacyRiskLens) => void
  onOperationsChange: (operations: string[] | undefined) => void
  onRuleChange: (ruleCode: string | null) => void
  onReviewStatusChange: (status: LegacyReviewStatus) => void
}

/**
 * 三个风险入口（全部 / 危险 / 异常）与各自的细分。入口块是按钮（aria-pressed），计数与按操作类型计数同口径，
 * 不随所选细分变化，便于横向比较；异常入口额外给出待核查数与核查状态切换。
 */
export default function RiskLensBar(props: RiskLensBarProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const { risk, operations, counts, summary } = props
  const allTotal = counts.reduce((sum, row) => sum + row.count, 0)
  const dangerTotal = summary?.dangerTotal ?? 0
  const sharePercent = allTotal > 0 ? ((dangerTotal / allTotal) * 100).toFixed(1) : '0'

  const tile = (
    lens: LegacyRiskLens,
    color: string,
    background: string,
    icon: ReactNode,
    value: number,
    extra: ReactNode,
  ) => {
    const active = risk.riskLens === lens
    return (
      <button
        key={lens}
        type="button"
        aria-pressed={active}
        onClick={() => props.onLensChange(lens)}
        style={{
          flex: '1 1 260px',
          minWidth: 0,
          textAlign: 'start',
          cursor: 'pointer',
          font: 'inherit',
          color: token.colorText,
          background: active ? background : token.colorBgContainer,
          border: `1px solid ${active ? color : token.colorBorderSecondary}`,
          boxShadow: active ? `inset 0 0 0 1px ${color}` : undefined,
          borderRadius: token.borderRadiusLG,
          padding: '14px 18px',
          display: 'flex',
          flexDirection: 'column',
          gap: 6,
        }}
      >
        <span style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, color: lens === 'all' ? token.colorTextSecondary : color, fontWeight: 500 }}>
          {icon}
          {t(`legacyEmployeeLogs.lens.${lens}.title`)}
        </span>
        <span style={{ display: 'flex', alignItems: 'baseline', gap: 6, flexWrap: 'wrap' }}>
          <span style={{ fontSize: 26, fontWeight: 600, color: lens === 'all' ? token.colorText : color, fontVariantNumeric: 'tabular-nums' }}>
            {value.toLocaleString('en-US')}
          </span>
          <Typography.Text type="secondary" style={{ fontSize: 13 }}>{t('legacyEmployeeLogs.lens.unit')}</Typography.Text>
          {extra}
        </span>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t(`legacyEmployeeLogs.lens.${lens}.hint`)}</Typography.Text>
      </button>
    )
  }

  const chip = (key: string, label: string, count: number, active: boolean, onClick: () => void) => (
    <Tag.CheckableTag
      key={key}
      checked={active}
      onChange={onClick}
      style={{
        border: `1px solid ${active ? token.colorText : token.colorBorder}`,
        background: active ? token.colorText : undefined,
        color: active ? token.colorBgContainer : undefined,
        borderRadius: 14,
        paddingInline: 12,
        lineHeight: '26px',
        marginInlineEnd: 0,
      }}
    >
      {label}
      <span style={{ marginInlineStart: 6, opacity: 0.75, fontVariantNumeric: 'tabular-nums' }}>{count.toLocaleString('en-US')}</span>
    </Tag.CheckableTag>
  )

  let subLabel: string
  let chips: ReactNode[]
  let hasSub: boolean
  if (risk.riskLens === 'abnormal') {
    subLabel = t('legacyEmployeeLogs.lens.byRule')
    const byRule = new Map((summary?.abnormalByRule ?? []).map((row) => [row.ruleCode, row.count]))
    chips = LEGACY_RULE_CODES.map((code) => {
      const active = risk.ruleCodes.length === 1 && risk.ruleCodes[0] === code
      return chip(code, t(`legacyEmployeeLogs.rules.${code}.label`), byRule.get(code) ?? 0, active, () => props.onRuleChange(active ? null : code))
    })
    hasSub = risk.ruleCodes.length > 0
  } else if (risk.riskLens === 'danger') {
    subLabel = t('legacyEmployeeLogs.lens.byDangerType')
    chips = DANGER_KEYS.map((key) => {
      const expected = DANGER_GROUP_OPERATIONS[key]
      const active = sameOperations(expected, operations)
      return chip(key, t(`legacyEmployeeLogs.dangerGroups.${key}`), sumOperationCounts(counts, expected), active, () =>
        props.onOperationsChange(active ? undefined : [...expected]))
    })
    hasSub = Boolean(operations?.length)
  } else {
    subLabel = t('legacyEmployeeLogs.lens.byCategory')
    chips = CATEGORY_KEYS.map((key) => {
      const expected = QUICK_FILTER_OPERATIONS[key]
      const active = sameOperations(expected, operations)
      return chip(key, t(`legacyEmployeeLogs.categories.${key}`), sumOperationCounts(counts, expected), active, () =>
        props.onOperationsChange(active ? undefined : [...expected]))
    })
    hasSub = Boolean(operations?.length)
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 12 }}>
        {tile('all', token.colorPrimary, token.colorPrimaryBg, <UnorderedListOutlined style={{ color: token.colorPrimary }} />, allTotal, (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            · {t('legacyEmployeeLogs.lens.all.scope', { people: props.people, devices: props.devices })}
          </Typography.Text>
        ))}
        {tile('danger', token.colorError, token.colorErrorBg, <WarningOutlined />, dangerTotal, (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>· {t('legacyEmployeeLogs.lens.danger.share', { percent: sharePercent })}</Typography.Text>
        ))}
        {tile('abnormal', token.colorWarningText, token.colorWarningBg, <AlertOutlined />, summary?.abnormalTotal ?? 0, (
          <>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>· {t('legacyEmployeeLogs.lens.abnormal.people', { count: summary?.abnormalEmployees ?? 0 })}</Typography.Text>
            {summary?.pendingReview ? (
              <Tag color="warning" style={{ marginInlineStart: 'auto', marginInlineEnd: 0 }}>
                {t('legacyEmployeeLogs.lens.abnormal.pending', { count: summary.pendingReview })}
              </Tag>
            ) : null}
          </>
        ))}
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8 }}>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>{subLabel}</Typography.Text>
        {chips}
        {hasSub ? (
          <Button
            type="link"
            size="small"
            onClick={() => (risk.riskLens === 'abnormal' ? props.onRuleChange(null) : props.onOperationsChange(undefined))}
          >
            {t('legacyEmployeeLogs.lens.clearSub')}
          </Button>
        ) : null}
        {risk.riskLens === 'abnormal' ? (
          <Space size={8} style={{ marginInlineStart: 'auto' }}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t('legacyEmployeeLogs.lens.reviewStatus')}</Typography.Text>
            <Segmented<LegacyReviewStatus>
              size="small"
              value={risk.reviewStatus}
              options={LEGACY_REVIEW_STATUSES.map((status) => ({ value: status, label: t(`legacyEmployeeLogs.reviewStatuses.${status}`) }))}
              onChange={props.onReviewStatusChange}
            />
          </Space>
        ) : null}
      </div>
    </Space>
  )
}
