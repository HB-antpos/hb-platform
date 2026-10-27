import { CheckCircleFilled, CloseCircleFilled, CloseOutlined, InfoCircleFilled } from '@ant-design/icons'
import { Button, Drawer, Empty, Tag } from 'antd'
import { useTranslation } from 'react-i18next'

import type { MyobDayGridRow, MyobPreviewRow, MyobTimesheetPreview } from '../../../types/myob'
import {
  buildDayHeader,
  buildDrawerSubtitle,
  buildExplanation,
  formatHours,
  formatHoursCell,
  getKindLabelKey,
  getPushedLabelKey,
  stripCheckPrefix,
  type DayHeader,
} from './myobTimesheetLogic'

interface PushDetailDrawerProps {
  /** 要看明细的员工行；null = 抽屉关闭。 */
  row: MyobPreviewRow | null
  /** 当前预览（取公司名 / 周期用）。 */
  preview: MyobTimesheetPreview | null
  onClose: () => void
}

/** 「按日明细」抽屉：本公司实际工时 → 将推送的转账工时 → 不推送的现金工时 → 校验。 */
export default function PushDetailDrawer({ row, preview, onClose }: PushDetailDrawerProps) {
  const { t } = useTranslation()
  const open = row !== null && preview !== null

  const days = row ? row.detail.days.map(buildDayHeader) : []
  const subtitle = row && preview ? buildDrawerSubtitle(row, preview) : null
  const subtitleText = subtitle
    ? [subtitle.company, subtitle.period, subtitle.payModeKey ? t(subtitle.payModeKey, subtitle.payModeValues) : null]
        .filter(Boolean)
        .join(' · ')
    : ''
  const explanation = row ? buildExplanation(row) : null
  const checkMessage = row ? stripCheckPrefix(row.detail.checkMessage) : ''
  const checkLabel = row ? (row.detail.checkOk ? t('myob.push.drawer.check.ok') : t('myob.push.drawer.check.bad')) : ''

  return (
    <Drawer
      open={open}
      width={760}
      rootClassName="myob-drawer"
      destroyOnClose
      onClose={onClose}
      // 关闭按钮放在标题栏右侧（antd 默认在左侧）
      closable={false}
      extra={<Button type="text" icon={<CloseOutlined />} aria-label={t('myob.push.drawer.close')} onClick={onClose} />}
      title={
        row ? (
          <div>
            <div>{t('myob.push.drawer.title', { name: row.userName })}</div>
            <span className="myob-sub" style={{ fontWeight: 400 }}>
              {subtitleText}
            </span>
          </div>
        ) : null
      }
      footer={
        <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
          <Button onClick={onClose}>{t('myob.push.drawer.closeButton')}</Button>
        </div>
      }
    >
      {row && explanation ? (
        <div className="myob-drawer-body">
          {/* 怎么分的 */}
          <div className="myob-push-explain" role="note">
            <b>{t('myob.push.drawer.explain.label')}</b>
            {explanation.kind === 'cross' ? (
              <>
                {t('myob.push.drawer.explain.crossIntro', {
                  pairs: explanation.pairs ?? '',
                  ratio: explanation.ratioPart ?? '',
                  allocated: explanation.allocated ?? '',
                })}
                <b>{t('myob.push.drawer.explain.orderChain')}</b>
                {t('myob.push.drawer.explain.crossOutro')}
              </>
            ) : explanation.kind === 'transfer' ? (
              t('myob.push.drawer.explain.allTransfer')
            ) : explanation.kind === 'cash' ? (
              t('myob.push.drawer.explain.allCash')
            ) : (
              <>
                {t('myob.push.drawer.explain.singleIntro')}
                <b>{t('myob.push.drawer.explain.orderChain')}</b>
                {t('myob.push.drawer.explain.singleOutro')}
              </>
            )}
          </div>

          {days.length === 0 ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.push.drawer.noDays')} />
          ) : (
            <>
              <section>
                <h3 className="myob-section-title">{t('myob.push.drawer.actualTitle', { stores: row.storeLabels.join('、') || '—' })}</h3>
                <DayGrid
                  days={days}
                  rows={row.detail.actual}
                  rowLabel={(grid) => resolveLabel(t, getKindLabelKey(grid.key), grid.key)}
                  headLabel=""
                  emptyText={t('myob.push.drawer.actualEmpty')}
                  variant="actual"
                />
              </section>

              <section>
                <h3 className="myob-section-title">
                  {t('myob.push.drawer.pushedTitle')}
                  <Tag color="processing" className="myob-push-api-tag">
                    {t('myob.push.drawer.pushedApi')}
                  </Tag>
                </h3>
                <DayGrid
                  days={days}
                  rows={row.detail.pushed}
                  rowLabel={(grid) => resolveLabel(t, getPushedLabelKey(grid.key), grid.key)}
                  rowSub={(grid) => grid.label}
                  headLabel={t('myob.push.drawer.categoryHead')}
                  emptyText={t('myob.push.drawer.pushedEmpty')}
                  variant="pushed"
                />
                {row.detail.pushed.length > 0 ? <span className="myob-sub myob-push-grid-note">{t('myob.push.drawer.pushedNote')}</span> : null}
              </section>

              <section>
                <h3 className="myob-section-title">{t('myob.push.drawer.cashTitle')}</h3>
                <DayGrid
                  days={days}
                  rows={row.detail.cash}
                  rowLabel={(grid) => t('myob.push.drawer.cashRow', { name: resolveLabel(t, getKindLabelKey(grid.key), grid.key) })}
                  headLabel={t('myob.push.drawer.categoryHead')}
                  emptyText={t('myob.push.drawer.cashEmpty')}
                  variant="cash"
                />
              </section>
            </>
          )}

          {/* 校验：颜色之外有 ✓ / ✕ 图标与文字 */}
          <div className={`myob-push-check ${row.detail.checkOk ? 'myob-push-check-ok' : 'myob-push-check-bad'}`} role="status">
            {row.detail.checkOk ? <CheckCircleFilled aria-hidden="true" /> : <CloseCircleFilled aria-hidden="true" />}
            <div>
              {checkMessage ? (
                <>
                  <b>{checkLabel}</b>
                  {t('myob.push.drawer.check.sep')}
                  {checkMessage}
                </>
              ) : (
                <b>{checkLabel}</b>
              )}
            </div>
          </div>

          {row.detail.annualCashOutTransferHours > 0 ? (
            <div className="myob-push-check myob-push-check-warn" role="note">
              <InfoCircleFilled aria-hidden="true" />
              <div>{t('myob.push.drawer.annualCashOut', { hours: formatHours(row.detail.annualCashOutTransferHours) })}</div>
            </div>
          ) : null}
        </div>
      ) : null}
    </Drawer>
  )
}

/** 行名：有文案键翻译，没有就显示原始键（比空白强，也方便排查后端新增了未知种类）。 */
function resolveLabel(t: (key: string) => string, key: string | null, fallback: string): string {
  return key ? t(key) : fallback
}

interface DayGridProps {
  days: DayHeader[]
  rows: MyobDayGridRow[]
  rowLabel: (row: MyobDayGridRow) => string
  /** 行名下方的灰色小字（如后端给的 MYOB 类别名）；没有返回 null。 */
  rowSub?: (row: MyobDayGridRow) => string | null
  /** 左上角表头（实际工时表没有）。 */
  headLabel: string
  emptyText: string
  variant: 'actual' | 'pushed' | 'cash'
}

/** 日网格：行 = 类别 / 种类，列 = 周期内每一天 + 合计；0 显示「—」。 */
function DayGrid({ days, rows, rowLabel, rowSub, headLabel, emptyText, variant }: DayGridProps) {
  const { t } = useTranslation()
  if (rows.length === 0) {
    return <div className="myob-push-grid-empty">{emptyText}</div>
  }
  return (
    <div className="myob-daygrid-scroll">
      <table className={`myob-daygrid myob-daygrid-${variant}`}>
        <thead>
          <tr>
            <th scope="col" className="myob-daygrid-name">
              {headLabel}
            </th>
            {days.map((day) => (
              <th scope="col" key={day.day} className="myob-daygrid-num" title={day.day}>
                {day.weekdayKey ? `${t(day.weekdayKey)} ${day.dayOfMonth}` : day.day}
              </th>
            ))}
            <th scope="col" className="myob-daygrid-num myob-daygrid-total">
              {t('myob.push.drawer.total')}
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((grid) => {
            const sub = rowSub?.(grid) ?? null
            return (
              <tr key={grid.key}>
                <th scope="row" className="myob-daygrid-name">
                  {rowLabel(grid)}
                  {sub ? <span className="myob-sub">{sub}</span> : null}
                </th>
                {grid.hours.map((hours, index) => (
                  <td key={days[index]?.day ?? index} className={`myob-daygrid-num${Math.abs(hours) >= 0.005 ? ' myob-daygrid-hit' : ' myob-daygrid-zero'}`}>
                    {formatHoursCell(hours)}
                  </td>
                ))}
                <td className={`myob-daygrid-num myob-daygrid-total${Math.abs(grid.total) >= 0.005 ? ' myob-daygrid-hit' : ' myob-daygrid-zero'}`}>
                  {formatHoursCell(grid.total)}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
