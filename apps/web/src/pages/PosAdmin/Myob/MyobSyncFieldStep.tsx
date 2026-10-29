import { LockOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Radio, Skeleton, Tag } from 'antd'
import { useTranslation } from 'react-i18next'

import type { MyobSyncField, MyobSyncFieldStat, MyobSyncFieldStats, MyobSyncMode } from '../../../types/myob'

import {
  groupFieldStats,
  isFieldUnavailable,
  isSensitiveField,
  selectAllFields,
  selectDiffFields,
  summarizeNames,
  syncFieldLabelKey,
  syncFieldSourceKey,
  type SyncScopeKind,
} from './myobSyncLogic'

/** 范围区的展示信息（由容器根据列表勾选 / 单人入口 / 重新预览算好）。 */
export interface SyncScopeView {
  kind: SyncScopeKind
  /** 可选的范围；单人 / 重新预览时只有一项且不可切换。 */
  options: SyncScopeKind[]
  /** 单人入口 / 重新预览时的员工姓名；非空表示范围固定为这 1 人。 */
  singleName: string | null
  /** 重新预览（沿用失败字段）而非首次单人入口：说明文案不同。 */
  rescoped: boolean
  selectedNames: string[]
  selectedCount: number
  boundCount: number
}

interface MyobSyncFieldStepProps {
  stats: MyobSyncFieldStats | null
  loading: boolean
  error: string | null
  onRetry: () => void
  selected: ReadonlySet<MyobSyncField>
  onSelectedChange: (next: Set<MyobSyncField>) => void
  mode: MyobSyncMode
  onModeChange: (mode: MyobSyncMode) => void
  scope: SyncScopeView
  onScopeChange: (kind: SyncScopeKind) => void
}

const MODES: readonly MyobSyncMode[] = ['FillEmpty', 'Overwrite']

/** 第 1 步：选字段 + 写入方式 + 范围。 */
export default function MyobSyncFieldStep({
  stats,
  loading,
  error,
  onRetry,
  selected,
  onSelectedChange,
  mode,
  onModeChange,
  scope,
  onScopeChange,
}: MyobSyncFieldStepProps) {
  const { t } = useTranslation()

  if (error) {
    return (
      <Alert
        type="error"
        showIcon
        message={t('myob.sync.step1.loadFailed')}
        description={error}
        action={
          <Button size="small" onClick={onRetry}>
            {t('myob.sync.step1.retry')}
          </Button>
        }
      />
    )
  }
  if (!stats) {
    return loading ? <Skeleton active paragraph={{ rows: 8 }} /> : null
  }
  if (!stats.canWrite) {
    return <Alert type="warning" showIcon message={t('myob.sync.step1.noPermission')} />
  }

  const unavailable = stats.unavailableFields
  const groups = groupFieldStats(stats.fields)
  const toggle = (field: MyobSyncField, on: boolean) => {
    const next = new Set(selected)
    if (on) next.add(field)
    else next.delete(field)
    onSelectedChange(next)
  }

  return (
    <div className="myob-sync-step" aria-busy={loading}>
      <div className="myob-sync-section-head">
        <div>
          <h3 className="myob-section-title">{t('myob.sync.step1.fieldsTitle')}</h3>
          <span className="myob-sub">{t('myob.sync.step1.fieldsHint')}</span>
        </div>
        <div className="myob-sync-links">
          <Button type="link" size="small" onClick={() => onSelectedChange(new Set(selectAllFields(stats.fields, unavailable)))}>
            {t('myob.sync.step1.selectAll')}
          </Button>
          <Button type="link" size="small" onClick={() => onSelectedChange(new Set(selectDiffFields(stats.fields, unavailable)))}>
            {t('myob.sync.step1.selectDiff')}
          </Button>
          <Button type="link" size="small" onClick={() => onSelectedChange(new Set())}>
            {t('myob.sync.step1.clear')}
          </Button>
        </div>
      </div>

      {groups.map((group) => (
        <div key={group.key} className="myob-sync-group">
          <div className="myob-sync-group-head">
            <span>{t(`myob.sync.step1.groups.${group.key}`)}</span>
            <span>{t('myob.sync.step1.groupSource')}</span>
          </div>
          {group.stats.map((stat) => (
            <FieldRow
              key={stat.field}
              stat={stat}
              checked={selected.has(stat.field) && !isFieldUnavailable(stat.field, unavailable)}
              unavailable={isFieldUnavailable(stat.field, unavailable)}
              onChange={(on) => toggle(stat.field, on)}
            />
          ))}
        </div>
      ))}
      <p className="myob-sub myob-sync-note">
        <LockOutlined aria-hidden="true" /> {t('myob.sync.step1.sensitiveNote')}
      </p>

      <h3 className="myob-section-title">{t('myob.sync.step1.modeTitle')}</h3>
      <Radio.Group className="myob-sync-cards" value={mode} onChange={(event) => onModeChange(event.target.value as MyobSyncMode)}>
        {MODES.map((item) => (
          <Radio key={item} value={item} className="myob-sync-card">
            <span className="myob-sync-card-title">{t(`myob.sync.step1.modes.${item}.title`)}</span>
            <span className="myob-sub">{t(`myob.sync.step1.modes.${item}.desc`)}</span>
          </Radio>
        ))}
      </Radio.Group>

      <h3 className="myob-section-title">{t('myob.sync.step1.scopeTitle')}</h3>
      <ScopeCards scope={scope} stats={stats} onScopeChange={onScopeChange} />
    </div>
  )
}

interface FieldRowProps {
  stat: MyobSyncFieldStat
  checked: boolean
  unavailable: boolean
  onChange: (on: boolean) => void
}

function FieldRow({ stat, checked, unavailable, onChange }: FieldRowProps) {
  const { t } = useTranslation()
  const sensitive = stat.sensitive || isSensitiveField(stat.field)
  return (
    <div className={`myob-sync-field${unavailable ? ' myob-sync-field-off' : ''}`}>
      <Checkbox checked={checked} disabled={unavailable} onChange={(event) => onChange(event.target.checked)}>
        <span className="myob-sync-field-name">{t(syncFieldLabelKey(stat.field), { defaultValue: stat.field })}</span>
        {sensitive ? (
          <Tag color="orange" className="myob-sync-lock" icon={<LockOutlined />}>
            {t('myob.sync.step1.sensitive')}
          </Tag>
        ) : null}
      </Checkbox>
      <span className="myob-sync-source">{t(syncFieldSourceKey(stat.field), { defaultValue: '' })}</span>
      <span className="myob-sync-stats">
        {unavailable ? (
          <Tag>{t('myob.sync.step1.unavailable')}</Tag>
        ) : (
          <>
            <Tag color={stat.fillable > 0 ? 'success' : 'default'}>{t('myob.sync.step1.fillable', { count: stat.fillable })}</Tag>
            <Tag color={stat.differs > 0 ? 'warning' : 'default'}>{t('myob.sync.step1.differs', { count: stat.differs })}</Tag>
          </>
        )}
      </span>
    </div>
  )
}

interface ScopeCardsProps {
  scope: SyncScopeView
  stats: MyobSyncFieldStats
  onScopeChange: (kind: SyncScopeKind) => void
}

function ScopeCards({ scope, stats, onScopeChange }: ScopeCardsProps) {
  const { t } = useTranslation()
  // 单人入口 / 重新预览：范围固定，只显示一张选中的卡片
  if (scope.singleName !== null) {
    return (
      <div className="myob-sync-cards">
        <div className="myob-sync-card myob-sync-card-fixed">
          <span className="myob-sync-card-title">{t('myob.sync.step1.scope.single', { name: scope.singleName })}</span>
          <span className="myob-sub">{t(scope.rescoped ? 'myob.sync.step1.scope.rescope' : 'myob.sync.step1.scope.singleDesc', { name: scope.singleName })}</span>
        </div>
      </div>
    )
  }
  const names = summarizeNames(scope.selectedNames)
  return (
    <Radio.Group className="myob-sync-cards" value={scope.kind} onChange={(event) => onScopeChange(event.target.value as SyncScopeKind)}>
      {scope.options.map((kind) =>
        kind === 'selected' ? (
          <Radio key={kind} value={kind} className="myob-sync-card">
            <span className="myob-sync-card-title">{t('myob.sync.step1.scope.selected', { count: scope.selectedCount })}</span>
            <span className="myob-sub">
              {names.more > 0
                ? t('myob.sync.step1.scope.selectedNamesMore', { names: names.shown.join('、'), count: scope.selectedCount })
                : t('myob.sync.step1.scope.selectedNames', { names: names.shown.join('、') })}
            </span>
          </Radio>
        ) : (
          <Radio key={kind} value={kind} className="myob-sync-card">
            <span className="myob-sync-card-title">{t('myob.sync.step1.scope.all', { count: scope.boundCount })}</span>
            <span className="myob-sub">
              {kind === scope.kind && stats.missingCardCount > 0
                ? t('myob.sync.step1.scope.allSkip', { count: stats.missingCardCount })
                : t('myob.sync.step1.scope.allNoSkip')}
            </span>
          </Radio>
        ),
      )}
    </Radio.Group>
  )
}
