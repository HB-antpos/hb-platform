import { ColumnWidthOutlined, CopyOutlined, DeleteOutlined, QuestionCircleOutlined } from '@ant-design/icons'
import { Alert, Button, Input, InputNumber, Select, Tooltip } from 'antd'
import { useTranslation } from 'react-i18next'
import ProductListImage from '../../../../components/ProductListImage'
import type { HookModelDto, ProductSpecListItemDto } from '../../../../types/planogram'
import { boardSize } from '../engine/holeGrid'
import { resolveHookQty } from '../engine/hookQty'
import { formatNumber } from '../planogramLogic'
import { conflictText, type ConflictTextEnv } from './conflictText'
import { MAX_BAYS, MAX_GAP_MM, productColor, type DesignerItem, type Slot } from './designerLogic'
import type { DesignerLayout } from './useDesignerLayout'
import { useDesignerStore } from './useDesignerStore'

interface InspectorPanelProps {
  layout: DesignerLayout
  readOnly: boolean
  language: string
  onCopyRight: () => void
  onDistribute: () => void
  onRemove: () => void
  onLocate: (guid: string) => void
}

function productName(info: ProductSpecListItemDto | undefined, code: string, language: string): string {
  if (!info) return code
  return language.toLowerCase().startsWith('en') && info.englishName ? info.englishName : info.productName || code
}

/** 一格统计数字。 */
function Stat({ value, label, tone }: { value: string; label: string; tone?: 'danger' }) {
  return (
    <div className="planogram-designer-stat">
      <b className={tone === 'danger' ? 'is-danger' : undefined}>{value}</b>
      <span>{label}</span>
    </div>
  )
}

/** 右栏：选中项属性 / 方案信息 / 统计与冲突清单。所有数字输入都是受控的，改动走 store（一次操作一个撤销快照）。 */
export default function InspectorPanel({ layout, readOnly, language, onCopyRight, onDistribute, onRemove, onLocate }: InspectorPanelProps) {
  const { t } = useTranslation()
  const specs = useDesignerStore((state) => state.specs)
  const hooks = useDesignerStore((state) => state.hooks)
  const selectedGuid = useDesignerStore((state) => state.selectedGuid)
  const setHeader = useDesignerStore((state) => state.setHeader)
  const patchItem = useDesignerStore((state) => state.patchItem)
  const moveItem = useDesignerStore((state) => state.moveItem)
  const { doc, shelf, ctx, stats, conflicts, conflictsByGuid } = layout

  const item: DesignerItem | undefined = selectedGuid ? doc.items.find((candidate) => candidate.guid === selectedGuid) : undefined
  const info = item ? specs[item.productCode] : undefined
  const hook: HookModelDto | undefined = item ? hooks.find((candidate) => candidate.guid === item.hookModelGuid) : undefined
  const entry = item ? layout.placed.find((candidate) => candidate.item.guid === item.guid) : undefined
  const env: ConflictTextEnv = {
    bayCount: doc.bayCount,
    gapXMm: doc.gapXMm,
    gapYMm: doc.gapYMm,
    holeCols: shelf?.holeCols ?? 0,
    holeRows: shelf?.holeRows ?? 0,
  }

  const move = (slot: Partial<Slot>) => {
    if (!item) return
    moveItem(item.guid, { bayIndex: slot.bayIndex ?? item.bayIndex, holeCol: slot.holeCol ?? item.holeCol, holeRow: slot.holeRow ?? item.holeRow }, { coalesce: true })
  }

  // ---------- 选中项 ----------
  const renderSelected = () => {
    if (!item) {
      return <p className="planogram-designer-empty-note">{t('planogram.designer.inspector.emptyHint')}</p>
    }
    const spec = info?.spec
    const resolved = hook && spec ? resolveHookQty(hook, spec.depthMm, spec.hookQtyOverrides) : null
    const itemConflicts = conflictsByGuid.get(item.guid) ?? []
    const geometry = entry?.geometry
    const board = shelf ? boardSize(shelf) : null
    const hookOptions = hooks
      .filter((candidate) => candidate.isActive || candidate.guid === item.hookModelGuid)
      .map((candidate) => ({
        value: candidate.guid,
        label: candidate.isActive
          ? t('planogram.designer.hookOption', { name: candidate.name, arm: formatNumber(candidate.armLengthMm) })
          : t('planogram.designer.hookOptionInactive', { name: candidate.name }),
      }))
    const qtyAuto = resolved?.effective ?? 0
    return (
      <>
        <div className="planogram-designer-selected-head">
          {info?.productImage ? (
            <ProductListImage src={info.productImage} size={38} radius={6} />
          ) : (
            <div className="planogram-designer-thumb" style={{ width: 38, height: 38, background: productColor(item.productCode) }}>
              {productName(info, item.productCode, language).slice(0, 2)}
            </div>
          )}
          <div className="planogram-designer-selected-text">
            <div className="planogram-designer-selected-name">{productName(info, item.productCode, language)}</div>
            <div className="planogram-muted-text planogram-small planogram-mono">
              {info?.itemNumber || item.productCode}{info ? ` · $${info.retailPrice.toFixed(2)}` : ''}
            </div>
            {spec ? (
              <div className="planogram-muted-text planogram-small">
                {t('planogram.designer.inspector.package', { w: formatNumber(spec.widthMm), h: formatNumber(spec.heightMm), t: formatNumber(spec.depthMm) })}
              </div>
            ) : null}
          </div>
        </div>
        {item.specChanged ? <Alert className="planogram-designer-mini-alert" type="warning" showIcon message={t('planogram.designer.inspector.specChanged')} /> : null}
        <div className="planogram-designer-field">
          <span className="planogram-designer-field-label">{t('planogram.designer.inspector.hook')}</span>
          <Select
            size="small"
            className="planogram-designer-field-control is-wide"
            value={item.hookModelGuid}
            disabled={readOnly}
            aria-label={t('planogram.designer.inspector.hook')}
            options={hookOptions.length > 0 ? hookOptions : [{ value: item.hookModelGuid, label: item.hookModelGuid }]}
            onChange={(value) => patchItem(item.guid, { hookModelGuid: value })}
          />
        </div>
        <div className="planogram-designer-field is-block" role="group" aria-label={t('planogram.designer.inspector.position')}>
          <span className="planogram-designer-field-grid is-3">
            <InputNumber size="small" min={1} max={MAX_BAYS} precision={0} disabled={readOnly} value={item.bayIndex} prefix={<span className="planogram-unit">{t('planogram.designer.inspector.bay')}</span>} aria-label={t('planogram.designer.inspector.bay')} onChange={(value) => value !== null && move({ bayIndex: value })} />
            <InputNumber size="small" min={1} max={Math.max(1, shelf?.holeCols ?? 1)} precision={0} disabled={readOnly} value={item.holeCol} prefix={<span className="planogram-unit">{t('planogram.designer.inspector.col')}</span>} aria-label={t('planogram.designer.inspector.col')} onChange={(value) => value !== null && move({ holeCol: value })} />
            <InputNumber size="small" min={1} max={Math.max(1, shelf?.holeRows ?? 1)} precision={0} disabled={readOnly} value={item.holeRow} prefix={<span className="planogram-unit">{t('planogram.designer.inspector.row')}</span>} aria-label={t('planogram.designer.inspector.row')} onChange={(value) => value !== null && move({ holeRow: value })} />
          </span>
        </div>
        <div className="planogram-designer-field">
          <span className="planogram-designer-field-label">{t('planogram.designer.inspector.perHook')}</span>
          <InputNumber
            size="small"
            className="planogram-designer-field-control"
            min={1}
            max={999}
            precision={0}
            disabled={readOnly}
            value={item.perHookQty}
            placeholder={qtyAuto > 0 ? t('planogram.designer.inspector.perHookAuto', { count: qtyAuto }) : undefined}
            aria-label={t('planogram.designer.inspector.perHook')}
            onChange={(value) => patchItem(item.guid, { perHookQty: value === null ? null : Math.max(1, Math.round(value)) })}
          />
        </div>
        <div className="planogram-designer-hint">
          {hook && spec && resolved ? (
            <>
              {t('planogram.designer.inspector.suggest', {
                count: resolved.auto,
                arm: formatNumber(hook.armLengthMm),
                reserve: formatNumber(hook.tipReserveMm),
                depth: formatNumber(spec.depthMm),
              })}{' '}
              {t('planogram.designer.inspector.hookTotal', { count: item.perHookQty ?? resolved.effective })}
            </>
          ) : (
            t('planogram.designer.inspector.noSpecHint')
          )}
          {geometry && board ? (
            <div>
              {t('planogram.designer.inspector.clearance', {
                left: formatNumber(geometry.left),
                right: formatNumber(board.widthMm - geometry.right),
                top: formatNumber(geometry.top),
                bottom: formatNumber(board.heightMm - geometry.bottom),
                gapX: formatNumber(doc.gapXMm),
                gapY: formatNumber(doc.gapYMm),
              })}
            </div>
          ) : null}
        </div>
        {itemConflicts.length > 0 ? (
          <ul className="planogram-designer-conflict-list is-inline">
            {itemConflicts.map((conflict, index) => (
              <li key={`${conflict.code}-${conflict.otherItemGuid ?? ''}-${index}`} className={conflict.severity === 'error' ? 'is-error' : 'is-warn'}>
                {conflictText(t, conflict, env)}
              </li>
            ))}
          </ul>
        ) : null}
        {readOnly ? null : (
          <div className="planogram-designer-actions">
            <Button size="small" icon={<CopyOutlined />} onClick={onCopyRight}>{t('planogram.designer.float.copyRight')}</Button>
            <Button size="small" icon={<ColumnWidthOutlined />} onClick={onDistribute}>{t('planogram.designer.float.distribute')}</Button>
            <Button size="small" danger icon={<DeleteOutlined />} onClick={onRemove} className="planogram-designer-actions-end">{t('planogram.designer.float.remove')}</Button>
          </div>
        )}
      </>
    )
  }

  // ---------- 冲突清单（统计卡片里，最多列 8 个条目，点击定位） ----------
  const errorGuids = layout.locateList
  const conflictRows = errorGuids.slice(0, 8).map((guid) => {
    const target = doc.items.find((candidate) => candidate.guid === guid)
    const list = conflictsByGuid.get(guid) ?? []
    const first = list.find((candidate) => candidate.severity === 'error') ?? list[0]
    return target && first ? { guid, name: productName(specs[target.productCode], target.productCode, language), text: conflictText(t, first, env), at: `${target.bayIndex}·${target.holeCol}·${target.holeRow}` } : null
  }).filter((row): row is { guid: string; name: string; text: string; at: string } => row !== null)

  return (
    <div className="planogram-designer-right">
      <section className="planogram-designer-card-box" aria-label={t('planogram.designer.inspector.selected')}>
        <h3>
          {t('planogram.designer.inspector.selected')}
          {item ? <span className="planogram-muted-text planogram-small">{t('planogram.designer.inspector.at', { bay: item.bayIndex, col: item.holeCol, row: item.holeRow })}</span> : null}
        </h3>
        {renderSelected()}
      </section>

      <section className="planogram-designer-card-box" aria-label={t('planogram.designer.inspector.plan')}>
        <h3>{t('planogram.designer.inspector.plan')}</h3>
        <div className="planogram-designer-field">
          <span className="planogram-designer-field-label">{t('planogram.designer.inspector.name')}</span>
          <Input
            size="small"
            className="planogram-designer-field-control is-wide"
            value={doc.name}
            maxLength={100}
            disabled={readOnly}
            status={doc.name.trim() ? undefined : 'error'}
            aria-label={t('planogram.designer.inspector.name')}
            onChange={(event) => setHeader({ name: event.target.value })}
          />
        </div>
        <div className="planogram-designer-field">
          <span className="planogram-designer-field-label">{t('planogram.designer.inspector.notes')}</span>
          <Input
            size="small"
            className="planogram-designer-field-control is-wide"
            value={doc.notes}
            maxLength={500}
            disabled={readOnly}
            aria-label={t('planogram.designer.inspector.notes')}
            onChange={(event) => setHeader({ notes: event.target.value })}
          />
        </div>
        <div className="planogram-designer-field">
          <span className="planogram-designer-field-label">{t('planogram.designer.inspector.shelfBays')}</span>
          <span className="planogram-designer-field-text" title={shelf ? shelf.name : undefined}>
            {shelf ? `${shelf.name} · ${t('planogram.designer.bays', { count: doc.bayCount })}` : t('planogram.designer.inspector.noShelf')}
          </span>
        </div>
        <div className="planogram-designer-field is-block">
          <span className="planogram-designer-field-label">
            {t('planogram.designer.inspector.gap')}{' '}
            <Tooltip title={t('planogram.designer.inspector.gapNote')}>
              <QuestionCircleOutlined className="planogram-muted-text" aria-label={t('planogram.designer.inspector.gapNote')} tabIndex={0} />
            </Tooltip>
          </span>
          <span className="planogram-designer-field-grid is-2">
            <InputNumber size="small" min={0} max={MAX_GAP_MM} precision={0} disabled={readOnly} value={doc.gapXMm} prefix={<span className="planogram-unit">{t('planogram.designer.inspector.gapX')}</span>} suffix={<span className="planogram-unit">mm</span>} aria-label={t('planogram.designer.inspector.gapX')} onChange={(value) => value !== null && setHeader({ gapXMm: value })} />
            <InputNumber size="small" min={0} max={MAX_GAP_MM} precision={0} disabled={readOnly} value={doc.gapYMm} prefix={<span className="planogram-unit">{t('planogram.designer.inspector.gapY')}</span>} suffix={<span className="planogram-unit">mm</span>} aria-label={t('planogram.designer.inspector.gapY')} onChange={(value) => value !== null && setHeader({ gapYMm: value })} />
          </span>
        </div>
      </section>

      <section className="planogram-designer-card-box" aria-label={t('planogram.designer.stats.title')}>
        <h3>{t('planogram.designer.stats.title')}</h3>
        <div className="planogram-designer-stats">
          <Stat value={String(stats.skuCount)} label={t('planogram.designer.stats.sku')} />
          <Stat value={String(stats.itemCount)} label={t('planogram.designer.stats.hooks')} />
          <Stat value={String(stats.unitCount)} label={t('planogram.designer.stats.units')} />
          <Stat value={`${stats.fillPercent}%`} label={t('planogram.designer.stats.fill')} />
          <Stat value={String(stats.conflictCount)} label={t('planogram.designer.stats.conflicts')} tone={stats.conflictCount > 0 ? 'danger' : undefined} />
          <Stat value={`$${formatNumber(stats.retailValue)}`} label={t('planogram.designer.stats.value')} />
        </div>
        {ctx && conflicts.some((entry) => entry.severity === 'warn') ? (
          <div className="planogram-field-note planogram-warn-text">{t('planogram.designer.stats.warnCount', { count: new Set(conflicts.filter((entry) => entry.severity === 'warn').map((entry) => entry.itemGuid)).size })}</div>
        ) : null}
        {conflictRows.length > 0 ? (
          <ul className="planogram-designer-conflict-list" aria-label={t('planogram.designer.stats.conflictList')}>
            {conflictRows.map((row) => (
              <li key={row.guid}>
                <button type="button" onClick={() => onLocate(row.guid)}>
                  <span className="planogram-designer-conflict-name">{row.name}</span>
                  <span className="planogram-muted-text planogram-small">{t('planogram.designer.stats.conflictAt', { at: row.at })} · {row.text}</span>
                </button>
              </li>
            ))}
            {errorGuids.length > conflictRows.length ? (
              <li className="planogram-muted-text planogram-small">{t('planogram.designer.stats.conflictMore', { count: errorGuids.length - conflictRows.length })}</li>
            ) : null}
          </ul>
        ) : null}
      </section>
    </div>
  )
}
