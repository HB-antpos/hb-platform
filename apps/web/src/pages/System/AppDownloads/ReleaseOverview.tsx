import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { useTranslation } from 'react-i18next'
import { Alert, Card, Empty, Space, Tag, Typography, theme } from 'antd'
import { RightOutlined } from '@ant-design/icons'
import { appUpdatePolicyService } from '../../../services/appUpdatePolicyService'
import { mobileOtaPolicyService } from '../../../services/mobileOtaPolicyService'
import { posHandheldUpdatePolicyService } from '../../../services/posHandheldUpdatePolicyService'
import { getWpfAppReleases } from '../../../services/wpfVersionService'
import {
  RELEASE_LANE_DEFINITIONS,
  RELEASE_MATRIX_COLUMNS,
  RELEASE_TERMINALS,
  buildRecentLaneChanges,
  getReleaseLaneDefinition,
  summarizeHandheldLane,
  summarizeIpadOtaLane,
  summarizeMobileAndroidNativeLane,
  summarizeMobileOtaLane,
  summarizeNativeLane,
  summarizeWpfLane,
  type ReleaseLaneAttention,
  type ReleaseLaneKey,
  type ReleaseLaneScope,
  type ReleaseLaneSummary,
  type ReleaseTerminal,
} from './releaseCenterLogic'
import { formatAppDownloadLocalDateTime } from './time'

type LaneState =
  | { state: 'loading' }
  | { state: 'failed' }
  | { state: 'ready'; summary: ReleaseLaneSummary }

interface ReleaseOverviewProps {
  refreshVersion: number
  onOpenLane: (terminal: ReleaseTerminal, lane: string) => void
}

const HANDHELD_KEYS: ReleaseLaneKey[] = [
  'handheld-ios-native',
  'handheld-android-native',
  'handheld-ios-ota',
  'handheld-android-ota',
]

function initialLaneStates(): Record<ReleaseLaneKey, LaneState> {
  return Object.fromEntries(
    RELEASE_LANE_DEFINITIONS.map((definition) => [definition.key, { state: 'loading' }]),
  ) as Record<ReleaseLaneKey, LaneState>
}

/** 投放总览：并行读取各轨道当前策略；单条轨道失败只影响自己的格子。 */
export default function ReleaseOverview({ refreshVersion, onOpenLane }: ReleaseOverviewProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const [lanes, setLanes] = useState<Record<ReleaseLaneKey, LaneState>>(initialLaneStates)
  const requestIdRef = useRef(0)

  useEffect(() => {
    const requestId = requestIdRef.current + 1
    requestIdRef.current = requestId
    const controller = new AbortController()
    const { signal } = controller
    setLanes(initialLaneStates())

    // 只回写本次刷新的结果：刷新或卸载后，旧请求晚到也不会覆盖界面。
    const commit = (keys: ReleaseLaneKey[], task: Promise<ReleaseLaneSummary[]>) => {
      task.then(
        (summaries) => {
          if (requestIdRef.current !== requestId) {
            return
          }
          setLanes((current) => {
            const next = { ...current }
            for (const key of keys) {
              const summary = summaries.find((item) => item.key === key)
              next[key] = summary ? { state: 'ready', summary } : { state: 'failed' }
            }
            return next
          })
        },
        (error: unknown) => {
          if (requestIdRef.current !== requestId) {
            return
          }
          console.error('Failed to load release lanes', keys, error)
          setLanes((current) => {
            const next = { ...current }
            for (const key of keys) {
              next[key] = { state: 'failed' }
            }
            return next
          })
        },
      )
    }

    commit(
      ['mobile-ios-native'],
      Promise.all([
        appUpdatePolicyService.getMobileIosNativePolicy(signal),
        appUpdatePolicyService.getIosAppStoreReleases('mobile-ios', signal),
      ]).then(([policy, releases]) => [summarizeNativeLane('mobile-ios-native', policy, releases)]),
    )
    commit(
      ['mobile-android-native'],
      appUpdatePolicyService.getMobileAndroidNativePolicy(signal)
        .then((policy) => [summarizeMobileAndroidNativeLane(policy)]),
    )
    commit(
      ['mobile-android-ota'],
      mobileOtaPolicyService.getPolicy('production', 'android', signal)
        .then((policy) => [summarizeMobileOtaLane('mobile-android-ota', policy)]),
    )
    commit(
      ['mobile-ios-ota'],
      mobileOtaPolicyService.getPolicy('production', 'ios', signal)
        .then((policy) => [summarizeMobileOtaLane('mobile-ios-ota', policy)]),
    )
    commit(
      ['ipad-ios-native'],
      Promise.all([
        appUpdatePolicyService.getPosIpadNativePolicy(signal),
        appUpdatePolicyService.getIosAppStoreReleases('pos-ipad', signal),
      ]).then(([policy, releases]) => [summarizeNativeLane('ipad-ios-native', policy, releases)]),
    )
    commit(
      ['ipad-ios-ota'],
      appUpdatePolicyService.getPosIpadOtaRollout(signal)
        .then((rollout) => [summarizeIpadOtaLane(rollout)]),
    )
    commit(
      HANDHELD_KEYS,
      posHandheldUpdatePolicyService.getPolicies(signal)
        .then((policies) => policies.map(summarizeHandheldLane)),
    )
    commit(
      ['wpf-windows'],
      getWpfAppReleases({ channel: 'production', page: 1, pageSize: 50 })
        .then((result) => [summarizeWpfLane(result.items)]),
    )

    return () => {
      controller.abort()
      requestIdRef.current += 1
    }
  }, [refreshVersion])

  const readySummaries = useMemo(
    () => Object.values(lanes)
      .filter((lane): lane is Extract<LaneState, { state: 'ready' }> => lane.state === 'ready')
      .map((lane) => lane.summary),
    [lanes],
  )
  const anyFailed = Object.values(lanes).some((lane) => lane.state === 'failed')
  const attentionItems = readySummaries.flatMap((summary) =>
    summary.attentions.map((attention, index) => ({ summary, attention, id: `${summary.key}-${index}` })),
  )
  const recent = buildRecentLaneChanges(readySummaries)

  const terminalName = (terminal: ReleaseTerminal) => t(`system.releaseCenter.nav.${terminal}`)
  const formatScope = (scope: ReleaseLaneScope | null) => {
    if (!scope) {
      return null
    }
    return t(`system.releaseCenter.scope.${scope.kind}`, { count: scope.count })
  }
  const openLane = (key: ReleaseLaneKey) => {
    const definition = getReleaseLaneDefinition(key)
    onOpenLane(definition.terminal, definition.lane)
  }

  const describeAttention = (attention: ReleaseLaneAttention) => {
    switch (attention.kind) {
      case 'pending-release':
        return {
          title: t('system.releaseCenter.attention.pendingRelease', {
            count: attention.count,
            latest: attention.latest,
          }),
          desc: t('system.releaseCenter.attention.pendingReleaseDesc'),
        }
      case 'binding-invalid':
        return {
          title: t('system.releaseCenter.attention.bindingInvalid'),
          desc: attention.reason || t('system.releaseCenter.attention.bindingInvalidDesc'),
        }
      case 'partial-rollout':
        return {
          title: t('system.releaseCenter.attention.partialRollout', {
            scope: formatScope(attention.scope),
          }),
          desc: t('system.releaseCenter.attention.partialRolloutDesc'),
        }
    }
  }

  const dot = (color: string): CSSProperties => ({
    display: 'inline-block',
    width: 8,
    height: 8,
    borderRadius: 4,
    background: color,
    flex: 'none',
  })
  const dotColor = (summary: ReleaseLaneSummary) => {
    if (summary.status === 'disabled') {
      return token.colorTextQuaternary
    }
    return summary.attentions.length ? token.colorWarning : token.colorSuccess
  }
  const modeColor = { optional: 'blue', minimum: 'gold', required: 'orange' } as const

  const gridColumns = '150px repeat(5, minmax(128px, 1fr))'
  const cellBorder = `1px solid ${token.colorBorderSecondary}`

  const renderCell = (terminal: ReleaseTerminal, column: (typeof RELEASE_MATRIX_COLUMNS)[number]) => {
    const definition = RELEASE_LANE_DEFINITIONS.find(
      (item) => item.terminal === terminal && item.column === column,
    )
    const baseStyle: CSSProperties = {
      borderLeft: cellBorder,
      minHeight: 104,
      minWidth: 0,
    }
    if (!definition) {
      return (
        <div
          key={column}
          style={{
            ...baseStyle,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            background: token.colorFillQuaternary,
          }}
        >
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('system.releaseCenter.notApplicable')}
          </Typography.Text>
        </div>
      )
    }

    const lane = lanes[definition.key]
    const summary = lane.state === 'ready' ? lane.summary : null
    return (
      <button
        key={column}
        type="button"
        className="release-center-cell"
        aria-label={t('system.releaseCenter.enterLaneAria', {
          lane: t(`system.releaseCenter.laneNames.${definition.key}`),
        })}
        onClick={() => openLane(definition.key)}
        style={{
          ...baseStyle,
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'flex-start',
          gap: 6,
          padding: '12px 14px',
          border: 'none',
          borderLeft: cellBorder,
          background: token.colorBgContainer,
          textAlign: 'left',
          cursor: 'pointer',
          fontFamily: 'inherit',
          fontSize: 'inherit',
          color: 'inherit',
        }}
      >
        {lane.state === 'loading' ? (
          <Typography.Text type="secondary">…</Typography.Text>
        ) : lane.state === 'failed' ? (
          <Typography.Text type="danger" style={{ fontSize: 12 }}>
            {t('system.releaseCenter.loadFailed')}
          </Typography.Text>
        ) : summary ? (
          <>
            <span style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0, maxWidth: '100%' }}>
              <span style={dot(dotColor(summary))} />
              <Typography.Text strong ellipsis style={{ fontSize: 15 }}>
                {summary.status === 'disabled'
                  ? t('system.releaseCenter.notEnabled')
                  : summary.target ?? t('system.releaseCenter.noTarget')}
              </Typography.Text>
            </span>
            {summary.status === 'active' && (summary.detail || summary.minimum) ? (
              <Typography.Text type="secondary" ellipsis style={{ fontSize: 12, maxWidth: '100%' }}>
                {[
                  summary.detail,
                  summary.minimum ? t('system.releaseCenter.minimum', { value: summary.minimum }) : null,
                ].filter(Boolean).join(' · ')}
              </Typography.Text>
            ) : null}
            {summary.status === 'active' ? (
              <span style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                {summary.mode ? (
                  <Tag color={modeColor[summary.mode]} style={{ marginInlineEnd: 0 }}>
                    {t(`system.releaseCenter.modes.${summary.mode}`)}
                  </Tag>
                ) : null}
                {summary.scope ? (
                  <Tag style={{ marginInlineEnd: 0 }}>{formatScope(summary.scope)}</Tag>
                ) : null}
              </span>
            ) : null}
          </>
        ) : null}
      </button>
    )
  }

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      {/* 格子悬停与键盘焦点高亮：内联样式无法表达伪类。 */}
      <style>{`
        .release-center-cell:hover { background: ${token.colorPrimaryBg} !important; }
        .release-center-cell:focus-visible { outline: 2px solid ${token.colorPrimary}; outline-offset: -2px; }
        .release-center-attention:hover { border-color: ${token.colorPrimary} !important; }
        .release-center-attention:focus-visible { outline: 2px solid ${token.colorPrimary}; outline-offset: 2px; }
      `}</style>

      {anyFailed ? (
        <Alert type="warning" showIcon message={t('system.releaseCenter.loadFailedHint')} />
      ) : null}

      <Card title={t('system.releaseCenter.attentionTitle')}>
        <Typography.Paragraph type="secondary" style={{ marginTop: -4 }}>
          {t('system.releaseCenter.attentionHint')}
        </Typography.Paragraph>
        {attentionItems.length ? (
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))',
              gap: 12,
            }}
          >
            {attentionItems.map(({ summary, attention, id }) => {
              const text = describeAttention(attention)
              return (
                <button
                  key={id}
                  type="button"
                  className="release-center-attention"
                  onClick={() => openLane(summary.key)}
                  style={{
                    display: 'flex',
                    flexDirection: 'column',
                    alignItems: 'flex-start',
                    gap: 4,
                    padding: 14,
                    border: cellBorder,
                    borderRadius: token.borderRadiusLG,
                    background: token.colorBgContainer,
                    textAlign: 'left',
                    cursor: 'pointer',
                    fontFamily: 'inherit',
                    fontSize: 'inherit',
                    color: 'inherit',
                  }}
                >
                  <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                    <span style={dot(token.colorWarning)} />
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {t(`system.releaseCenter.laneNames.${summary.key}`)}
                    </Typography.Text>
                  </span>
                  <Typography.Text strong>{text.title}</Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>{text.desc}</Typography.Text>
                  {/* 整张卡片已是按钮，这里只做视觉提示，不能再嵌一层链接。 */}
                  <span style={{ fontSize: 13, color: token.colorPrimary }}>
                    {t('system.releaseCenter.attention.go')} <RightOutlined style={{ fontSize: 10 }} />
                  </span>
                </button>
              )
            })}
          </div>
        ) : (
          <Typography.Text type="secondary">{t('system.releaseCenter.attentionEmpty')}</Typography.Text>
        )}
      </Card>

      <Card title={t('system.releaseCenter.matrixTitle')}>
        <div
          style={{
            display: 'flex',
            flexWrap: 'wrap',
            justifyContent: 'space-between',
            gap: '8px 16px',
            marginTop: -4,
            marginBottom: 12,
          }}
        >
          <Typography.Text type="secondary">{t('system.releaseCenter.matrixHint')}</Typography.Text>
          <Space size={12} wrap>
            <Space size={6}><span style={dot(token.colorSuccess)} />{t('system.releaseCenter.legendActive')}</Space>
            <Space size={6}><span style={dot(token.colorWarning)} />{t('system.releaseCenter.legendAttention')}</Space>
            <Space size={6}><span style={dot(token.colorTextQuaternary)} />{t('system.releaseCenter.legendDisabled')}</Space>
          </Space>
        </div>
        <div style={{ overflowX: 'auto' }}>
          <div
            style={{
              minWidth: 800,
              border: cellBorder,
              borderRadius: token.borderRadiusLG,
              overflow: 'hidden',
            }}
          >
            <div
              style={{
                display: 'grid',
                gridTemplateColumns: gridColumns,
                background: token.colorFillAlter,
                fontSize: 12,
                color: token.colorTextSecondary,
              }}
            >
              <div />
              <div style={{ gridColumn: 'span 3', padding: '8px 14px', borderLeft: cellBorder, fontWeight: 600 }}>
                {t('system.releaseCenter.columns.nativeGroup')}
              </div>
              <div style={{ gridColumn: 'span 2', padding: '8px 14px', borderLeft: cellBorder, fontWeight: 600 }}>
                {t('system.releaseCenter.columns.otaGroup')}
              </div>
            </div>
            <div
              style={{
                display: 'grid',
                gridTemplateColumns: gridColumns,
                background: token.colorFillAlter,
                borderTop: cellBorder,
                fontWeight: 600,
              }}
            >
              <div style={{ padding: '10px 14px' }}>{t('system.releaseCenter.columns.terminal')}</div>
              {RELEASE_MATRIX_COLUMNS.map((column) => (
                <div key={column} style={{ padding: '10px 14px', borderLeft: cellBorder }}>
                  {t(`system.releaseCenter.columns.${column}`)}
                </div>
              ))}
            </div>
            {RELEASE_TERMINALS.map((terminal) => (
              <div
                key={terminal}
                style={{ display: 'grid', gridTemplateColumns: gridColumns, borderTop: cellBorder }}
              >
                <div style={{ padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: 2 }}>
                  <Typography.Text strong>{terminalName(terminal)}</Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {t(`system.releaseCenter.nav.${terminal}Sub`)}
                  </Typography.Text>
                </div>
                {RELEASE_MATRIX_COLUMNS.map((column) => renderCell(terminal, column))}
              </div>
            ))}
          </div>
        </div>
      </Card>

      <Card title={t('system.releaseCenter.recentTitle')}>
        <Typography.Paragraph type="secondary" style={{ marginTop: -4 }}>
          {t('system.releaseCenter.recentHint')}
        </Typography.Paragraph>
        {recent.length ? (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {recent.map((summary, index) => (
              <div
                key={summary.key}
                style={{
                  display: 'flex',
                  flexWrap: 'wrap',
                  alignItems: 'center',
                  gap: 12,
                  padding: '10px 0',
                  borderTop: index === 0 ? 'none' : cellBorder,
                }}
              >
                <Typography.Text type="secondary" style={{ width: 150, fontVariantNumeric: 'tabular-nums' }}>
                  {formatAppDownloadLocalDateTime(summary.updatedAt)}
                </Typography.Text>
                <Typography.Link onClick={() => openLane(summary.key)} style={{ flex: '1 1 220px' }}>
                  {t(`system.releaseCenter.laneNames.${summary.key}`)}
                </Typography.Link>
                <Typography.Text style={{ flex: '1 1 200px' }}>
                  {summary.status === 'disabled'
                    ? t('system.releaseCenter.notEnabled')
                    : [
                      summary.target,
                      summary.mode ? t(`system.releaseCenter.modes.${summary.mode}`) : null,
                      formatScope(summary.scope),
                    ].filter(Boolean).join(' · ')}
                </Typography.Text>
                <Typography.Text type="secondary">{summary.updatedBy ?? '--'}</Typography.Text>
              </div>
            ))}
          </div>
        ) : (
          <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.releaseCenter.recentEmpty')} />
        )}
      </Card>
    </Space>
  )
}
