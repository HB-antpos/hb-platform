import { useTranslation } from 'react-i18next'
import { Alert, Button, Card, Descriptions, Space, Tag, Typography, theme } from 'antd'
import { EditOutlined } from '@ant-design/icons'
import {
  buildDecisionLadder,
  type DecisionSegment,
  type ReleaseLaneAttention,
  type ReleaseLaneKey,
  type ReleaseLaneScope,
  type ReleaseLaneSummary,
} from './releaseCenterLogic'
import { formatAppDownloadLocalDateTime } from './time'
import type { ReleaseLaneState } from './useReleaseLaneSummaries'

interface LanePolicyCardProps {
  laneKey: ReleaseLaneKey
  state: ReleaseLaneState
  /** 有管理权限时显示「调整策略」，点击后定位到下方的策略表单。 */
  onAdjust?: () => void
}

const MOBILE_OTA_KEYS: ReleaseLaneKey[] = ['mobile-ios-ota', 'mobile-android-ota']

/** 轨道顶部的「当前生效策略」：一眼看清目标、门槛、范围，以及各版本客户端会收到什么。 */
export default function LanePolicyCard({ laneKey, state, onAdjust }: LanePolicyCardProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const summary = state.state === 'ready' ? state.summary : null

  const formatScope = (scope: ReleaseLaneScope | null) =>
    scope ? t(`system.releaseCenter.scope.${scope.kind}`, { count: scope.count }) : '--'

  const describeAttention = (attention: ReleaseLaneAttention) => {
    switch (attention.kind) {
      case 'pending-release':
        return t('system.releaseCenter.attention.pendingRelease', {
          count: attention.count,
          latest: attention.latest,
        })
      case 'binding-invalid':
        return `${t('system.releaseCenter.attention.bindingInvalid')}${attention.reason ? `：${attention.reason}` : ''}`
      case 'partial-rollout':
        return t('system.releaseCenter.attention.partialRollout', { scope: formatScope(attention.scope) })
    }
  }

  // 每段的配色：强制 = 警示色，可选 = 信息色，已最新 = 成功色，其余（不覆盖 / 不受影响 / 回退 / 不下发）= 中性色。
  const segmentColors = (segment: DecisionSegment) => {
    switch (segment.kind) {
      case 'force':
        return { bg: token.colorWarningBg, border: token.colorWarningBorder, text: token.colorWarningText }
      case 'optional':
        return { bg: token.colorInfoBg, border: token.colorInfoBorder, text: token.colorInfoText }
      case 'latest':
        return { bg: token.colorSuccessBg, border: token.colorSuccessBorder, text: token.colorSuccessText }
      default:
        return { bg: token.colorFillQuaternary, border: token.colorBorderSecondary, text: token.colorText }
    }
  }

  const segmentRange = (segment: DecisionSegment, laneSummary: ReleaseLaneSummary) => {
    const isOta = laneSummary.key.endsWith('-ota')
    switch (segment.kind) {
      case 'force':
        return segment.variant === 'ota-required'
          ? t('system.releaseCenter.ladder.range.otaRequired')
          : t('system.releaseCenter.ladder.range.below', { bound: segment.bound })
      case 'optional':
        if (segment.variant === 'ota-optional') {
          return t('system.releaseCenter.ladder.range.otaOptional')
        }
        return segment.variant === 'between'
          ? t('system.releaseCenter.ladder.range.between', { bound: segment.bound })
          : t('system.releaseCenter.ladder.range.below', { bound: segment.bound })
      case 'latest':
        return isOta
          ? t('system.releaseCenter.ladder.range.latestOta')
          : t('system.releaseCenter.ladder.range.equal', { bound: segment.bound })
      case 'unaffected':
        return t('system.releaseCenter.ladder.range.unaffected', { bound: segment.bound })
      case 'not-covered':
        return t('system.releaseCenter.ladder.range.notCovered')
      case 'rollback':
        return t('system.releaseCenter.ladder.range.rollback', { bound: segment.bound })
      case 'off':
        return t('system.releaseCenter.ladder.range.off')
    }
  }

  const statusTag = state.state === 'failed'
    ? <Tag color="error">{t('system.releaseCenter.loadFailed')}</Tag>
    : summary
      ? summary.status === 'active'
        ? <Tag color="success">{t('system.releaseCenter.card.active')}</Tag>
        : <Tag>{t('system.releaseCenter.notEnabled')}</Tag>
      : null

  return (
    <Card
      size="small"
      loading={state.state === 'loading'}
      title={(
        <Space size={8} wrap>
          {t('system.releaseCenter.card.title')}
          {statusTag}
          {MOBILE_OTA_KEYS.includes(laneKey) ? <Tag>production</Tag> : null}
        </Space>
      )}
      extra={onAdjust ? (
        <Button type="primary" icon={<EditOutlined />} onClick={onAdjust}>
          {t('system.releaseCenter.card.adjust')}
        </Button>
      ) : null}
    >
      {state.state === 'failed' ? (
        <Typography.Text type="secondary">{t('system.releaseCenter.card.failedHint')}</Typography.Text>
      ) : summary ? (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          {summary.attentions.map((attention, index) => (
            <Alert key={index} type="warning" showIcon message={describeAttention(attention)} />
          ))}

          <Descriptions size="small" bordered column={{ xs: 1, sm: 2, lg: 3 }}>
            <Descriptions.Item label={t('system.releaseCenter.card.target')}>
              <Space direction="vertical" size={0}>
                <Typography.Text strong>
                  {summary.status === 'disabled' ? '--' : summary.target ?? t('system.releaseCenter.noTarget')}
                </Typography.Text>
                {summary.status === 'active' && summary.detail ? (
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>{summary.detail}</Typography.Text>
                ) : null}
              </Space>
            </Descriptions.Item>
            <Descriptions.Item label={t('system.releaseCenter.card.minimum')}>
              {summary.status === 'active' && summary.minimum ? summary.minimum : '--'}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.releaseCenter.card.mode')}>
              {summary.mode ? t(`system.releaseCenter.modes.${summary.mode}`) : '--'}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.releaseCenter.card.scope')}>
              {summary.status === 'active' ? formatScope(summary.scope) : '--'}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.releaseCenter.card.updated')} span={2}>
              {summary.updatedAt
                ? `${formatAppDownloadLocalDateTime(summary.updatedAt)} · ${summary.updatedBy ?? '--'}`
                : '--'}
            </Descriptions.Item>
          </Descriptions>

          <div>
            <Space size={8} wrap style={{ marginBottom: 8 }}>
              <Typography.Text strong>{t('system.releaseCenter.ladder.title')}</Typography.Text>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('system.releaseCenter.ladder.hint')}
              </Typography.Text>
            </Space>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
              {buildDecisionLadder(summary).map((segment, index) => {
                const colors = segmentColors(segment)
                return (
                  <div
                    key={`${segment.kind}-${index}`}
                    style={{
                      flex: '1 1 160px',
                      minWidth: 0,
                      padding: '8px 12px',
                      borderRadius: token.borderRadius,
                      background: colors.bg,
                      border: `1px solid ${colors.border}`,
                    }}
                  >
                    <div style={{ fontWeight: 600, color: colors.text }}>
                      {t(`system.releaseCenter.ladder.kind.${segment.kind}`)}
                    </div>
                    <div style={{ fontSize: 12, color: token.colorTextSecondary }}>
                      {segmentRange(segment, summary)}
                    </div>
                  </div>
                )
              })}
            </div>
            {summary.status === 'active' && summary.scope && summary.scope.kind !== 'all' ? (
              <Typography.Text type="secondary" style={{ display: 'block', marginTop: 8, fontSize: 12 }}>
                {t('system.releaseCenter.ladder.outOfScope')}
              </Typography.Text>
            ) : null}
          </div>
        </Space>
      ) : null}
    </Card>
  )
}
