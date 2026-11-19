import { Button, Tag, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import type { MyobPreviewRow, MyobTimesheetPreview } from '../../../types/myob'

import { getPushableUserGuids, getPushStateView, isPushableRow } from './myobPushLogic'
import { buildReadinessCards, formatHours, getRowStatusView } from './myobTimesheetLogic'
import { HOUR_COLUMNS, HoursCell, PushHints, ReadinessItem } from './PushReadiness'

interface PushSelectionPanelProps {
  preview: MyobTimesheetPreview
  /** 勾选的员工（只会包含可推送的人）。 */
  selected: ReadonlySet<string>
  onSelectedChange: (selected: Set<string>) => void
  acknowledged: boolean
  onAcknowledgedChange: (value: boolean) => void
  /** 有员工绑定权限才显示「去绑定」；点击由上层决定去哪。 */
  onGoBind?: () => void
  /** 有连接管理权限才显示「去映射」。 */
  onGoMapping?: () => void
}

/**
 * 推送弹窗里「挑人」的那一块：就绪卡 + 提示项 + 带勾选的员工表。
 * 被拦截 / 没有转账工时 / MYOB 里已发薪的行不能勾选，原因写在状态列里（不悄悄跳过）。
 */
export default function PushSelectionPanel({ preview, selected, onSelectedChange, acknowledged, onAcknowledgedChange, onGoBind, onGoMapping }: PushSelectionPanelProps) {
  const { t } = useTranslation()
  const cards = useMemo(() => buildReadinessCards(preview), [preview])
  const pushable = useMemo(() => getPushableUserGuids(preview), [preview])

  const columns: ColumnsType<MyobPreviewRow> = [
    {
      title: t('myob.push.columns.employee'),
      key: 'employee',
      width: 170,
      render: (_, row) => <b className="myob-person-name">{row.userName}</b>,
    },
    ...HOUR_COLUMNS.map(({ key, titleKey }) => ({
      title: t(titleKey),
      key,
      width: 64,
      align: 'right' as const,
      render: (_: unknown, row: MyobPreviewRow) => <HoursCell value={row.pushed[key]} />,
    })),
    {
      title: t('myob.push.columns.pushedTotal'),
      key: 'pushedTotal',
      width: 84,
      align: 'right' as const,
      render: (_, row) => <b className={`myob-push-hours${Math.abs(row.pushedTotal) < 0.005 ? ' myob-muted' : ''}`}>{formatHours(row.pushedTotal)}</b>,
    },
    {
      title: t('myob.push.columns.status'),
      key: 'status',
      render: (_, row) => {
        const view = getRowStatusView(row)
        const pushState = row.push.state === 'NotPushed' && row.push.lastStatus === null ? null : getPushStateView(row.push)
        const tag = (
          <Tag color={view.tone} style={{ marginInlineEnd: 0 }} tabIndex={view.detail ? 0 : undefined}>
            {t(view.labelKey)}
          </Tag>
        )
        return (
          <div className="myob-push-status">
            {view.detail ? <Tooltip title={<span style={{ whiteSpace: 'pre-line' }}>{view.detail}</span>}>{tag}</Tooltip> : tag}
            {view.extraBlockers > 0 ? <span className="myob-sub myob-push-extra">{t('myob.push.moreBlockers', { count: view.extraBlockers })}</span> : null}
            {view.blockerCode === 'NOT_BOUND' && onGoBind ? (
              <Button type="link" size="small" aria-label={t('myob.push.goBindAria', { name: row.userName })} onClick={onGoBind}>
                {t('myob.push.goBind')}
              </Button>
            ) : null}
            {pushState ? (
              <Tag color={pushState.tone} style={{ marginInlineEnd: 0 }}>
                {t(pushState.labelKey)}
              </Tag>
            ) : null}
          </div>
        )
      },
    },
  ]

  return (
    <div className="myob-push-selection">
      <ul className="myob-push-steps" aria-label={t('myob.push.readiness.aria')}>
        {cards.map((card) => (
          <ReadinessItem key={card.id} card={card} canGoMapping={Boolean(onGoMapping)} onGoMapping={() => onGoMapping?.()} />
        ))}
      </ul>

      <PushHints hints={preview.hints} acknowledged={acknowledged} onAcknowledgedChange={onAcknowledgedChange} />

      <MeasuredTable<MyobPreviewRow>
        metricId="attendance-payroll.myob-push-selection"
        rowKey="userGuid"
        size="small"
        columns={columns}
        dataSource={preview.rows}
        pagination={false}
        scroll={{ x: 'max-content', y: 320 }}
        rowSelection={{
          selectedRowKeys: [...selected],
          onChange: (keys) => onSelectedChange(new Set(keys.map(String))),
          // 被拦截 / 没有转账工时 / MYOB 里已发薪的行不能选
          getCheckboxProps: (row) => ({ disabled: !isPushableRow(row), 'aria-label': t('myob.push.selectAria', { name: row.userName }) }),
          preserveSelectedRowKeys: true,
        }}
      />

      <div className="myob-push-selection-foot">
        <Button type="link" size="small" onClick={() => onSelectedChange(new Set(pushable))}>
          {t('myob.push.selectAll', { count: pushable.length })}
        </Button>
        <Button type="link" size="small" disabled={selected.size === 0} onClick={() => onSelectedChange(new Set())}>
          {t('myob.push.selectNone')}
        </Button>
      </div>
    </div>
  )
}
