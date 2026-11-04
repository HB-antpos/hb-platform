import { Button, Drawer, Form, Input, Segmented, Space, Switch, message, type FormInstance } from 'antd'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useIsMobile } from '../../../../hooks/useIsMobile'
import { createShelfModel, updateShelfModel } from '../../../../services/planogramService'
import type { ShelfModelDto } from '../../../../types/planogram'
import { autoHoleGrid, boardSize, holeCountMismatch, type ShelfGeometryLike } from '../engine/holeGrid'
import { applyPlanogramFormError, AutoCell, NumberField, SectionTitle, useOpenKey } from '../formParts'
import {
  defaultShelfFormValues,
  formatNumber,
  formatUpdatedAt,
  shelfCopyFormValues,
  shelfFormToInput,
  shelfToFormValues,
  type ModelDrawerTarget,
  type ShelfFormValues,
} from '../planogramLogic'
import ShelfFrontPreview from '../preview/ShelfFrontPreview'

interface ShelfModelDrawerProps {
  /** null = 关闭。 */
  target: ModelDrawerTarget<ShelfModelDto> | null
  /** 没有型号维护权限（Planogram.Manage）时只读查看。 */
  readOnly: boolean
  onClose: () => void
  /** 保存成功：由父级关闭抽屉并刷新列表。 */
  onSaved: () => void
}

/** 表单字段名，用来把后端 details 里的字段错误对回表单。 */
const FIELD_NAMES = [
  'name', 'code', 'sideType', 'color', 'source', 'bayWidthMm', 'totalHeightMm', 'baseDepthMm', 'baseTopHeightMm',
  'uprightWidthMm', 'holePitchXMm', 'holePitchYMm', 'holeDiameterMm', 'firstHoleLeftMm', 'firstHoleTopMm',
  'holeCols', 'holeRows', 'rowNumberingFrom', 'isActive',
] as const

function initialValuesFor(target: ModelDrawerTarget<ShelfModelDto>, copySuffix: string): ShelfFormValues {
  if (target.mode === 'edit') return shelfToFormValues(target.model)
  if (target.mode === 'copy') return shelfCopyFormValues(target.model, copySuffix)
  return defaultShelfFormValues()
}

function finite(value: number | null | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : Number.NaN
}

/** 表单值 → 几何输入；缺失 / 非法的数字变成 NaN，下游换算与预览都按「不可算」处理。 */
function geometryOf(values: Partial<ShelfFormValues>): ShelfGeometryLike {
  return {
    bayWidthMm: finite(values.bayWidthMm),
    totalHeightMm: finite(values.totalHeightMm),
    baseTopHeightMm: finite(values.baseTopHeightMm),
    uprightWidthMm: finite(values.uprightWidthMm),
    holePitchXMm: finite(values.holePitchXMm),
    holePitchYMm: finite(values.holePitchYMm),
    firstHoleLeftMm: finite(values.firstHoleLeftMm),
    firstHoleTopMm: finite(values.firstHoleTopMm),
    holeCols: finite(values.holeCols),
    holeRows: finite(values.holeRows),
  }
}

/**
 * 货架型号抽屉：新建 / 编辑 / 复制为新型号；没有型号维护权限时只读。
 * 每次「打开」（target 对象换了）都用新的 key 重建整个抽屉：表单实例、输入内容、保存状态都是全新的。
 * 关闭动画期间（约 0.3 秒）立刻再打开另一个目标时，Drawer 的 destroyOnHidden 还没销毁内容，共用的表单实例会把上一次的输入
 * 盖在新目标的初值上，提交时却带着新目标的 guid，等于把旧数据写到别的记录上。
 */
export default function ShelfModelDrawer(props: ShelfModelDrawerProps) {
  const openKey = useOpenKey(props.target)
  return <ShelfModelDrawerInner key={openKey} {...props} />
}

function ShelfModelDrawerInner({ target, readOnly, onClose, onSaved }: ShelfModelDrawerProps) {
  const { t, i18n } = useTranslation()
  const isMobile = useIsMobile()
  const [form] = Form.useForm<ShelfFormValues>()
  const [saving, setSaving] = useState(false)
  const initialValues = useMemo(
    () => (target ? initialValuesFor(target, t('planogram.common.copySuffix')) : null),
    [target, t],
  )

  const title = !target
    ? ''
    : readOnly
      ? t('planogram.shelf.drawer.view')
      : target.mode === 'edit'
        ? t('planogram.shelf.drawer.edit')
        : target.mode === 'copy'
          ? t('planogram.shelf.drawer.copy')
          : t('planogram.shelf.drawer.create')

  const submit = async (values: ShelfFormValues) => {
    if (!target || readOnly) return
    setSaving(true)
    try {
      const input = shelfFormToInput(values)
      if (target.mode === 'edit') await updateShelfModel(target.model.guid, input)
      else await createShelfModel(input)
      message.success(t('planogram.common.saved'))
      onSaved()
    } catch (error) {
      const toast = applyPlanogramFormError(form, error, FIELD_NAMES, i18n.language, (key) => t(key))
      if (toast) message.error(toast)
    } finally {
      setSaving(false)
    }
  }

  const editing = target?.mode === 'edit' ? target.model : null

  return (
    <Drawer
      open={target !== null}
      width={isMobile ? '100%' : 600}
      title={title}
      closable={{ placement: 'end' }}
      destroyOnHidden
      maskClosable={false}
      onClose={onClose}
      footer={(
        <div className="planogram-drawer-footer">
          {editing ? (
            <span className="planogram-drawer-updated">
              {t('planogram.common.lastUpdated', { time: formatUpdatedAt(editing.updatedAt, true), by: editing.updatedBy ?? '—' })}
            </span>
          ) : <span />}
          <Space>
            <Button onClick={onClose}>{readOnly ? t('planogram.common.close') : t('planogram.common.cancel')}</Button>
            {readOnly ? null : (
              <Button type="primary" loading={saving} onClick={() => form.submit()}>
                {t('planogram.common.save')}
              </Button>
            )}
          </Space>
        </div>
      )}
    >
      {initialValues ? (
        <Form<ShelfFormValues>
          form={form}
          layout="vertical"
          className="planogram-form"
          initialValues={initialValues}
          disabled={readOnly}
          onFinish={submit}
          scrollToFirstError
        >
          <ShelfFormBody form={form} initialValues={initialValues} autoFocusCode={target?.mode !== 'edit'} readOnly={readOnly} />
        </Form>
      ) : null}
    </Drawer>
  )
}

interface ShelfFormBodyProps {
  form: FormInstance<ShelfFormValues>
  initialValues: ShelfFormValues
  autoFocusCode: boolean
  /** antd 的 Segmented 不读取 Form 的 disabled，只读时要显式传给它。 */
  readOnly: boolean
}

function ShelfFormBody({ form, initialValues, autoFocusCode, readOnly }: ShelfFormBodyProps) {
  const { t } = useTranslation()
  const watched = Form.useWatch([], form) as Partial<ShelfFormValues> | undefined
  const values: ShelfFormValues = { ...initialValues, ...watched }

  const geometry = geometryOf(values)
  const board = boardSize(geometry)
  const auto = autoHoleGrid(geometry)

  // 孔数：没手改过就跟着尺寸自动换算；手改过（或已存的值与换算值不一致）就以手填为准，只提示差异。
  const [colsManual, setColsManual] = useState(
    () => holeCountMismatch(initialValues.holeCols, autoHoleGrid(geometryOf(initialValues)).cols) !== null,
  )
  const [rowsManual, setRowsManual] = useState(
    () => holeCountMismatch(initialValues.holeRows, autoHoleGrid(geometryOf(initialValues)).rows) !== null,
  )

  useEffect(() => {
    if (!colsManual && auto.cols >= 1 && form.getFieldValue('holeCols') !== auto.cols) form.setFieldValue('holeCols', auto.cols)
  }, [auto.cols, colsManual, form])
  useEffect(() => {
    if (!rowsManual && auto.rows >= 1 && form.getFieldValue('holeRows') !== auto.rows) form.setFieldValue('holeRows', auto.rows)
  }, [auto.rows, rowsManual, form])

  const colsDiff = colsManual ? holeCountMismatch(values.holeCols, auto.cols) : null
  const rowsDiff = rowsManual ? holeCountMismatch(values.holeRows, auto.rows) : null

  const boardText = Number.isFinite(board.widthMm) && Number.isFinite(board.heightMm)
    ? `${formatNumber(board.widthMm)} × ${formatNumber(board.heightMm)}`
    : '—'

  return (
    <>
      <SectionTitle title={t('planogram.shelf.section.basic')} />
      <div className="planogram-grid is-2">
        <Form.Item name="name" label={t('planogram.shelf.field.name')} rules={[{ required: true, whitespace: true, message: t('planogram.validation.required') }]}>
          <Input maxLength={100} />
        </Form.Item>
        <Form.Item name="code" label={t('planogram.shelf.field.code')} rules={[{ required: true, whitespace: true, message: t('planogram.validation.required') }]}>
          <Input className="planogram-mono" maxLength={50} autoFocus={autoFocusCode} />
        </Form.Item>
      </div>
      {/* 类型 / 颜色 / 来源并成一行（设计图里颜色与来源是同一格），让孔阵预览更靠上 */}
      <div className="planogram-grid is-3">
        <Form.Item name="sideType" label={t('planogram.shelf.field.sideType')}>
          <Segmented
            block
            disabled={readOnly}
            options={[
              { label: t('planogram.shelf.sideType.single'), value: 1 },
              { label: t('planogram.shelf.sideType.double'), value: 2 },
            ]}
          />
        </Form.Item>
        <Form.Item name="color" label={t('planogram.shelf.field.color')}>
          <Input maxLength={50} />
        </Form.Item>
        <Form.Item name="source" label={t('planogram.shelf.field.source')}>
          <Input maxLength={100} />
        </Form.Item>
      </div>

      <SectionTitle title={t('planogram.shelf.section.dimensions')} hint={t('planogram.shelf.section.dimensionsHint')} />
      <div className="planogram-grid is-3">
        {/* 外形尺寸后端是整数列（int）：只能填整数；孔距 / 孔径 / 首孔边距才允许小数（25.4 等） */}
        <NumberField
          name="bayWidthMm"
          label={t('planogram.shelf.field.bayWidth')}
          precision={0}
          dependencies={['uprightWidthMm']}
          extraRules={[({ getFieldValue }) => ({
            // 板面宽 = 节宽 − 立柱宽，必须为正，否则放不下任何孔
            validator: (_rule, value: unknown) => (
              typeof value === 'number' && value <= (Number(getFieldValue('uprightWidthMm')) || 0)
                ? Promise.reject(new Error(t('planogram.validation.bayWiderThanUpright')))
                : Promise.resolve()
            ),
          })]}
        />
        <NumberField
          name="totalHeightMm"
          label={t('planogram.shelf.field.totalHeight')}
          precision={0}
          dependencies={['baseTopHeightMm']}
          extraRules={[({ getFieldValue }) => ({
            validator: (_rule, value: unknown) => (
              typeof value === 'number' && value <= (Number(getFieldValue('baseTopHeightMm')) || 0)
                ? Promise.reject(new Error(t('planogram.validation.heightTallerThanBase')))
                : Promise.resolve()
            ),
          })]}
        />
        <NumberField name="baseDepthMm" label={t('planogram.shelf.field.baseDepth')} precision={0} />
        <NumberField name="baseTopHeightMm" label={t('planogram.shelf.field.baseTopHeight')} precision={0} />
        <NumberField name="uprightWidthMm" label={t('planogram.shelf.field.uprightWidth')} precision={0} />
        <AutoCell label={t('planogram.shelf.field.boardSize')} value={boardText} note={t('planogram.common.auto')} />
      </div>

      <SectionTitle title={t('planogram.shelf.section.holes')} hint={t('planogram.shelf.section.holesHint')} />
      <div className="planogram-grid is-3">
        <NumberField name="holePitchXMm" label={t('planogram.shelf.field.pitchX')} min={0.5} max={200} />
        <NumberField name="holePitchYMm" label={t('planogram.shelf.field.pitchY')} min={0.5} max={200} />
        <NumberField name="holeDiameterMm" label={t('planogram.shelf.field.holeDiameter')} />
        <NumberField name="firstHoleLeftMm" label={t('planogram.shelf.field.firstLeft')} />
        <NumberField name="firstHoleTopMm" label={t('planogram.shelf.field.firstTop')} />
        <Form.Item label={t('planogram.shelf.field.holeCount')} className="planogram-pair-field">
          <div className="planogram-pair">
            <NumberField
              name="holeCols"
              label=""
              required={false}
              min={1}
              precision={0}
              unit=""
              inline
              className="planogram-pair-item"
              onChange={(value) => setColsManual(value !== null && value !== auto.cols)}
            />
            <span className="planogram-pair-sep" aria-hidden="true">×</span>
            <NumberField
              name="holeRows"
              label=""
              required={false}
              min={1}
              precision={0}
              unit=""
              inline
              className="planogram-pair-item"
              onChange={(value) => setRowsManual(value !== null && value !== auto.rows)}
            />
          </div>
          <div className="planogram-field-note">
            {colsDiff || rowsDiff ? (
              <span className="planogram-warn-text">
                {[
                  colsDiff ? t('planogram.shelf.holeMismatch.cols', colsDiff) : '',
                  rowsDiff ? t('planogram.shelf.holeMismatch.rows', rowsDiff) : '',
                ].filter(Boolean).join(' · ')}
                {' '}
                <Button
                  type="link"
                  size="small"
                  className="planogram-inline-link"
                  onClick={() => {
                    setColsManual(false)
                    setRowsManual(false)
                    form.setFieldsValue({ holeCols: auto.cols, holeRows: auto.rows })
                  }}
                >
                  {t('planogram.shelf.holeMismatch.reset')}
                </Button>
              </span>
            ) : t('planogram.shelf.holeAutoNote', { cols: auto.cols, rows: auto.rows })}
          </div>
        </Form.Item>
      </div>
      <div className="planogram-grid is-2 planogram-grid-gap-top">
        <Form.Item name="rowNumberingFrom" label={t('planogram.shelf.field.rowNumbering')}>
          <Segmented
            block
            disabled={readOnly}
            options={[
              { label: t('planogram.shelf.rowNumbering.top'), value: 1 },
              { label: t('planogram.shelf.rowNumbering.base'), value: 2 },
            ]}
          />
        </Form.Item>
        <Form.Item label={t('planogram.common.enabled')}>
          <div className="planogram-switch-row">
            <Form.Item name="isActive" valuePropName="checked" noStyle>
              <Switch />
            </Form.Item>
            <span className="planogram-muted-text">{t('planogram.shelf.enabledHint')}</span>
          </div>
        </Form.Item>
      </div>

      <SectionTitle title={t('planogram.shelf.section.preview')} hint={t('planogram.shelf.section.previewHint')} />
      <div className="planogram-preview-box is-column">
        <div className="planogram-preview-row">
          <ShelfFrontPreview
            shelf={geometry}
            ariaLabel={t('planogram.shelf.preview.aria')}
            labels={{
              bayWidth: t('planogram.shelf.preview.bayWidth', { value: formatNumber(geometry.bayWidthMm) }),
              totalHeight: { name: t('planogram.shelf.preview.totalHeight'), value: formatNumber(geometry.totalHeightMm) },
              board: { name: t('planogram.shelf.preview.board'), value: formatNumber(board.heightMm) },
              base: t('planogram.shelf.preview.base', {
                depth: formatNumber(finite(values.baseDepthMm)),
                face: formatNumber(geometry.baseTopHeightMm),
              }),
            }}
          />
          <div className="planogram-preview-note">
            {t('planogram.shelf.preview.formula', {
              boardW: formatNumber(board.widthMm),
              boardH: formatNumber(board.heightMm),
              firstLeft: formatNumber(geometry.firstHoleLeftMm),
              firstTop: formatNumber(geometry.firstHoleTopMm),
              pitchX: formatNumber(geometry.holePitchXMm),
              pitchY: formatNumber(geometry.holePitchYMm),
              cols: auto.cols,
              rows: auto.rows,
            })}
          </div>
        </div>
        <div className="planogram-preview-caption">
          {t('planogram.shelf.preview.summary', {
            pitchX: formatNumber(geometry.holePitchXMm),
            pitchY: formatNumber(geometry.holePitchYMm),
            firstLeft: formatNumber(geometry.firstHoleLeftMm),
            firstTop: formatNumber(geometry.firstHoleTopMm),
            cols: formatNumber(geometry.holeCols),
            rows: formatNumber(geometry.holeRows),
          })}
        </div>
      </div>
    </>
  )
}
