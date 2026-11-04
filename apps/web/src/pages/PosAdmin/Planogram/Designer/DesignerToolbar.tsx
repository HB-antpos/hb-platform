import { CheckCircleOutlined, ExpandOutlined, MinusOutlined, PlusOutlined, WarningOutlined } from '@ant-design/icons'
import { Button, Checkbox, Segmented, Select } from 'antd'
import { useTranslation } from 'react-i18next'
import type { ShelfModelDto } from '../../../../types/planogram'
import type { CanvasSnapMode } from './BoardCanvas'
import { MAX_BAYS, MAX_ZOOM, MIN_BAYS, MIN_ZOOM } from './designerLogic'

/** 数字步进器：减号 / 当前值 / 加号，到头时对应按钮禁用。 */
function Stepper({ value, label, display, min, max, disabled, onChange, step = 1 }: {
  value: number
  label: string
  display: string
  min: number
  max: number
  disabled?: boolean
  onChange: (value: number) => void
  step?: number
}) {
  const { t } = useTranslation()
  return (
    <span className="planogram-designer-stepper" role="group" aria-label={label}>
      <button type="button" disabled={disabled || value <= min} aria-label={t('planogram.designer.decrease', { name: label })} onClick={() => onChange(Math.max(min, value - step))}><MinusOutlined aria-hidden="true" /></button>
      <output aria-live="polite">{display}</output>
      <button type="button" disabled={disabled || value >= max} aria-label={t('planogram.designer.increase', { name: label })} onClick={() => onChange(Math.min(max, value + step))}><PlusOutlined aria-hidden="true" /></button>
    </span>
  )
}

interface DesignerToolbarProps {
  shelfModels: readonly ShelfModelDto[]
  shelfModelGuid: string
  bayCount: number
  zoom: number
  showHoles: boolean
  showRulers: boolean
  showTags: boolean
  /** 商品方块里显示商品图（每次打开设计器都是开，不记忆）。 */
  showImages: boolean
  snapMode: CanvasSnapMode
  conflictCount: number
  readOnly: boolean
  onShelfChange: (guid: string) => void
  onBayCountChange: (count: number) => void
  onZoomChange: (zoom: number) => void
  onFit: () => void
  onToggle: (key: 'holes' | 'rulers' | 'tags' | 'images', value: boolean) => void
  onSnapChange: (mode: CanvasSnapMode) => void
  onLocate: () => void
}

/** 工具栏：货架型号 / 节数 / 缩放 / 显示开关 / 吸附方式 / 冲突定位。切换货架或减少节数不会自动挪动商品，只会重新校验并标红。 */
export default function DesignerToolbar(props: DesignerToolbarProps) {
  const { shelfModels, shelfModelGuid, bayCount, zoom, showHoles, showRulers, showTags, showImages, snapMode, conflictCount, readOnly } = props
  const { t } = useTranslation()
  return (
    <div className="planogram-designer-toolbar" role="toolbar" aria-label={t('planogram.designer.toolbar.aria')}>
      <label className="planogram-designer-toolbar-label" htmlFor="planogram-designer-shelf">{t('planogram.designer.toolbar.shelf')}</label>
      <Select
        id="planogram-designer-shelf"
        className="planogram-designer-shelf-select"
        value={shelfModelGuid}
        disabled={readOnly}
        onChange={props.onShelfChange}
        options={shelfModels.map((model) => ({ value: model.guid, label: model.isActive ? model.name : t('planogram.designer.toolbar.shelfInactive', { name: model.name }) }))}
      />
      <span className="planogram-designer-toolbar-label">{t('planogram.designer.toolbar.bays')}</span>
      <Stepper value={bayCount} label={t('planogram.designer.toolbar.bays')} display={String(bayCount)} min={MIN_BAYS} max={MAX_BAYS} disabled={readOnly} onChange={props.onBayCountChange} />
      <span className="planogram-designer-vsep" aria-hidden="true" />
      <span className="planogram-designer-toolbar-label">{t('planogram.designer.toolbar.zoom')}</span>
      <Stepper value={zoom} label={t('planogram.designer.toolbar.zoom')} display={`${zoom}%`} min={MIN_ZOOM} max={MAX_ZOOM} step={5} onChange={props.onZoomChange} />
      <Button size="small" type="text" icon={<ExpandOutlined />} onClick={props.onFit}>{t('planogram.designer.toolbar.fit')}</Button>
      <span className="planogram-designer-vsep" aria-hidden="true" />
      <span className="planogram-designer-toolbar-label">{t('planogram.designer.toolbar.show')}</span>
      <Checkbox checked={showHoles} onChange={(event) => props.onToggle('holes', event.target.checked)}>{t('planogram.designer.toolbar.holes')}</Checkbox>
      <Checkbox checked={showRulers} onChange={(event) => props.onToggle('rulers', event.target.checked)}>{t('planogram.designer.toolbar.rulers')}</Checkbox>
      <Checkbox checked={showTags} onChange={(event) => props.onToggle('tags', event.target.checked)}>{t('planogram.designer.toolbar.tags')}</Checkbox>
      <Checkbox checked={showImages} onChange={(event) => props.onToggle('images', event.target.checked)}>{t('planogram.designer.toolbar.images')}</Checkbox>
      <span className="planogram-designer-vsep" aria-hidden="true" />
      <span className="planogram-designer-toolbar-label">{t('planogram.designer.toolbar.snap')}</span>
      <Segmented<CanvasSnapMode>
        size="small"
        value={snapMode}
        onChange={props.onSnapChange}
        aria-label={t('planogram.designer.toolbar.snap')}
        options={[
          { label: t('planogram.designer.toolbar.snapHole'), value: 'hole' },
          { label: t('planogram.designer.toolbar.snapFree'), value: 'free' },
        ]}
      />
      <span className="planogram-designer-toolbar-end">
        {conflictCount > 0 ? (
          <Button size="small" danger icon={<WarningOutlined />} onClick={props.onLocate}>
            {t('planogram.designer.toolbar.conflictLocate', { count: conflictCount })}
          </Button>
        ) : (
          <span className="planogram-designer-ok-tag"><CheckCircleOutlined aria-hidden="true" /> {t('planogram.designer.toolbar.noConflict')}</span>
        )}
      </span>
    </div>
  )
}
