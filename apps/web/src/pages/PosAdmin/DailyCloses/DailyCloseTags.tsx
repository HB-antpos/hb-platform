import { useTranslation } from 'react-i18next'

import type { DailyCloseDifferenceKind } from '../../../types/dailyClose'

import { DIFFERENCE_GLYPHS } from './logic'

// 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键，才能发现孤立键与缺失键。
const DIFFERENCE_LABEL_KEYS: Record<DailyCloseDifferenceKind, string> = {
  short: 'dailyCloses.status.short',
  over: 'dailyCloses.status.over',
  even: 'dailyCloses.status.even',
  none: 'dailyCloses.status.none',
}

const SOURCE_LABEL_KEYS: Record<string, string> = {
  Wpf: 'dailyCloses.source.Wpf',
  Handheld: 'dailyCloses.source.Handheld',
  Ipad: 'dailyCloses.source.Ipad',
}

const SOURCE_LONG_LABEL_KEYS: Record<string, string> = {
  Wpf: 'dailyCloses.sourceLong.Wpf',
  Handheld: 'dailyCloses.sourceLong.Handheld',
  Ipad: 'dailyCloses.sourceLong.Ipad',
}

const DATA_SOURCE_LABEL_KEYS: Record<string, string> = {
  ClientUpload: 'dailyCloses.dataSource.ClientUpload',
  AuditBackfill: 'dailyCloses.dataSource.AuditBackfill',
}

/** 终端类型简称（WPF / 手持 / iPad）；未知类型原样显示。 */
export function useSourceLabel() {
  const { t } = useTranslation()
  return (clientKind: string) => (SOURCE_LABEL_KEYS[clientKind] ? t(SOURCE_LABEL_KEYS[clientKind]) : clientKind || '—')
}

/** 终端类型全称（WPF 收银端 / 手持收银端 / iPad 收银端）。 */
export function useSourceLongLabel() {
  const { t } = useTranslation()
  return (clientKind: string) =>
    SOURCE_LONG_LABEL_KEYS[clientKind] ? t(SOURCE_LONG_LABEL_KEYS[clientKind]) : clientKind || '—'
}

export function useDataSourceLabel() {
  const { t } = useTranslation()
  return (dataSource: string) =>
    DATA_SOURCE_LABEL_KEYS[dataSource] ? t(DATA_SOURCE_LABEL_KEYS[dataSource]) : dataSource || '—'
}

export function useDifferenceLabel() {
  const { t } = useTranslation()
  return (kind: DailyCloseDifferenceKind) => t(DIFFERENCE_LABEL_KEYS[kind])
}

/** 差额状态标签：图标 + 文字，不只靠颜色（▼ 短款 / ▲ 长款 / ✓ 已平 / – 无金额）。 */
export function DifferenceTag({ kind }: { kind: DailyCloseDifferenceKind }) {
  const label = useDifferenceLabel()
  return (
    <span className={`daily-closes-tag is-${kind}`}>
      <span aria-hidden="true">{DIFFERENCE_GLYPHS[kind]}</span>
      {label(kind)}
    </span>
  )
}

/** 同日多次保存的「第 N 次」标记。 */
export function SaveSequenceChip({ sequence }: { sequence: number }) {
  const { t } = useTranslation()
  return <span className="daily-closes-chip">{t('dailyCloses.nth', { n: sequence })}</span>
}

/** 历史补录标记。 */
export function BackfillChip() {
  const { t } = useTranslation()
  return <span className="daily-closes-chip is-backfill">{t('dailyCloses.backfill')}</span>
}
