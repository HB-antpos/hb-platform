import { Button, Drawer, Form, Input, Segmented, Space, Switch, message, type FormInstance } from 'antd'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useIsMobile } from '../../../../hooks/useIsMobile'
import { createHookModel, updateHookModel } from '../../../../services/planogramService'
import type { HookModelDto } from '../../../../types/planogram'
import { suggestHookQty, tagTopBelowHoleMm } from '../engine/hookQty'
import { applyPlanogramFormError, AutoCell, NumberField, SectionTitle, useOpenKey } from '../formParts'
import {
  defaultHoleSpanX,
  defaultHookFormValues,
  formatNumber,
  formatUpdatedAt,
  hookCopyFormValues,
  hookFormToInput,
  hookToFormValues,
  type HookFormValues,
  type ModelDrawerTarget,
} from '../planogramLogic'
import HookSidePreview, { HOOK_PREVIEW_SAMPLE_DEPTH_MM } from '../preview/HookSidePreview'

interface HookModelDrawerProps {
  target: ModelDrawerTarget<HookModelDto> | null
  readOnly: boolean
  onClose: () => void
  onSaved: () => void
}

const FIELD_NAMES = [
  'name', 'code', 'color', 'prongCount', 'hasPriceTagArm', 'armLengthMm', 'dropMm', 'wireDiameterMm', 'tipReserveMm',
  'tagWidthMm', 'tagHeightMm', 'tagBottomAboveArmMm', 'holeSpanX', 'holeSpanY', 'loadKg', 'isActive',
] as const

function initialValuesFor(target: ModelDrawerTarget<HookModelDto>, copySuffix: string): HookFormValues {
  if (target.mode === 'edit') return hookToFormValues(target.model)
  if (target.mode === 'copy') return hookCopyFormValues(target.model, copySuffix)
  return defaultHookFormValues()
}

function finite(value: number | null | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : Number.NaN
}

/**
 * 挂钩型号抽屉：新建 / 编辑 / 复制为新型号；没有型号维护权限时只读。
 * 每次「打开」（target 对象换了）都用新的 key 重建整个抽屉：表单实例、输入内容、保存状态都是全新的。
 * 关闭动画期间（约 0.3 秒）立刻再打开另一个目标时，Drawer 的 destroyOnHidden 还没销毁内容，共用的表单实例会把上一次的输入
 * 盖在新目标的初值上，提交时却带着新目标的 guid，等于把旧数据写到别的记录上。
 */
export default function HookModelDrawer(props: HookModelDrawerProps) {
  const openKey = useOpenKey(props.target)
  return <HookModelDrawerInner key={openKey} {...props} />
}

function HookModelDrawerInner({ target, readOnly, onClose, onSaved }: HookModelDrawerProps) {
  const { t, i18n } = useTranslation()
  const isMobile = useIsMobile()
  const [form] = Form.useForm<HookFormValues>()
  const [saving, setSaving] = useState(false)
  const initialValues = useMemo(
    () => (target ? initialValuesFor(target, t('planogram.common.copySuffix')) : null),
    [target, t],
  )

  const title = !target
    ? ''
    : readOnly
      ? t('planogram.hook.drawer.view')
      : target.mode === 'edit'
        ? t('planogram.hook.drawer.edit')
        : target.mode === 'copy'
          ? t('planogram.hook.drawer.copy')
          : t('planogram.hook.drawer.create')

  const submit = async (values: HookFormValues) => {
    if (!target || readOnly) return
    setSaving(true)
    try {
      const input = hookFormToInput(values)
      if (target.mode === 'edit') await updateHookModel(target.model.guid, input)
      else await createHookModel(input)
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
        <Form<HookFormValues>
          form={form}
          layout="vertical"
          className="planogram-form"
          initialValues={initialValues}
          disabled={readOnly}
          onFinish={submit}
          scrollToFirstError
        >
          <HookFormBody form={form} initialValues={initialValues} autoFocusCode={target?.mode !== 'edit'} readOnly={readOnly} />
        </Form>
      ) : null}
    </Drawer>
  )
}

interface HookFormBodyProps {
  form: FormInstance<HookFormValues>
  initialValues: HookFormValues
  autoFocusCode: boolean
  /** antd 的 Segmented 不读取 Form 的 disabled，只读时要显式传给它。 */
  readOnly: boolean
}

function HookFormBody({ form, initialValues, autoFocusCode, readOnly }: HookFormBodyProps) {
  const { t } = useTranslation()
  const watched = Form.useWatch([], form) as Partial<HookFormValues> | undefined
  const values: HookFormValues = { ...initialValues, ...watched }
  const hasTag = values.hasPriceTagArm

  const arm = finite(values.armLengthMm)
  const drop = finite(values.dropMm)
  const reserve = finite(values.tipReserveMm)
  const tagW = finite(values.tagWidthMm)
  const tagH = finite(values.tagHeightMm)
  const tagBottom = finite(values.tagBottomAboveArmMm)
  const tagTop = tagTopBelowHoleMm({ dropMm: drop, tagBottomAboveArmMm: tagBottom, tagHeightMm: tagH })
  const sampleQty = suggestHookQty({ armLengthMm: arm, tipReserveMm: reserve }, HOOK_PREVIEW_SAMPLE_DEPTH_MM)

  return (
    <>
      <SectionTitle title={t('planogram.hook.section.basic')} />
      <div className="planogram-grid is-2">
        <Form.Item name="name" label={t('planogram.hook.field.name')} rules={[{ required: true, whitespace: true, message: t('planogram.validation.required') }]}>
          <Input maxLength={100} />
        </Form.Item>
        <Form.Item name="code" label={t('planogram.hook.field.code')} rules={[{ required: true, whitespace: true, message: t('planogram.validation.required') }]}>
          <Input className="planogram-mono" maxLength={50} autoFocus={autoFocusCode} />
        </Form.Item>
      </div>
      <div className="planogram-grid is-3">
        <Form.Item name="prongCount" label={t('planogram.hook.field.prongCount')}>
          <Segmented
            block
            disabled={readOnly}
            options={[
              { label: t('planogram.hook.prong.single'), value: 1 },
              { label: t('planogram.hook.prong.double'), value: 2 },
            ]}
            // 单钩占 1 孔、双钩占 2 孔：改钩型时把横向占孔带到对应默认值（仍可在下面手改）
            onChange={(value) => form.setFieldValue('holeSpanX', defaultHoleSpanX(value as 1 | 2))}
          />
        </Form.Item>
        <Form.Item name="hasPriceTagArm" label={t('planogram.hook.field.priceTagArm')}>
          <Segmented
            block
            disabled={readOnly}
            options={[
              { label: t('planogram.hook.tagArm.yes'), value: true },
              { label: t('planogram.hook.tagArm.no'), value: false },
            ]}
          />
        </Form.Item>
        <Form.Item name="color" label={t('planogram.hook.field.color')}>
          <Input maxLength={50} />
        </Form.Item>
      </div>

      <SectionTitle title={t('planogram.hook.section.dimensions')} hint={t('planogram.hook.section.dimensionsHint')} />
      <div className="planogram-grid is-3">
        <NumberField name="armLengthMm" label={t('planogram.hook.field.armLength')} />
        <NumberField name="dropMm" label={t('planogram.hook.field.drop')} />
        <NumberField name="wireDiameterMm" label={t('planogram.hook.field.wireDiameter')} />
        <Form.Item label={t('planogram.hook.field.tagSize')} className="planogram-pair-field" required={false}>
          <div className="planogram-pair">
            <NumberField name="tagWidthMm" label="" unit="" inline disabled={!hasTag} className="planogram-pair-item" />
            <span className="planogram-pair-sep" aria-hidden="true">×</span>
            <NumberField name="tagHeightMm" label="" inline disabled={!hasTag} className="planogram-pair-item" />
          </div>
        </Form.Item>
        <NumberField name="tipReserveMm" label={t('planogram.hook.field.tipReserve')} allowZero />
        <NumberField name="loadKg" label={t('planogram.hook.field.loadKg')} required={false} unit="kg" />
        <NumberField name="tagBottomAboveArmMm" label={t('planogram.hook.field.tagBottomAboveArm')} disabled={!hasTag} />
        <AutoCell
          label={t('planogram.hook.field.tagTop')}
          value={hasTag && Number.isFinite(tagTop) ? formatNumber(tagTop) : '—'}
          note={t('planogram.hook.field.tagTopNote', { bottom: formatNumber(tagBottom), height: formatNumber(tagH) })}
        />
      </div>

      <SectionTitle title={t('planogram.hook.section.span')} hint={t('planogram.hook.section.spanHint')} />
      <div className="planogram-grid is-3">
        <NumberField name="holeSpanX" label={t('planogram.hook.field.spanX')} min={1} precision={0} unit={t('planogram.hook.holeUnit')} />
        <NumberField name="holeSpanY" label={t('planogram.hook.field.spanY')} min={1} precision={0} unit={t('planogram.hook.holeUnit')} />
        <AutoCell label={t('planogram.hook.field.dropBase')} value={formatNumber(drop)} note={t('planogram.hook.field.dropBaseNote')} />
      </div>
      <div className="planogram-grid is-2 planogram-grid-gap-top">
        <Form.Item label={t('planogram.common.enabled')}>
          <div className="planogram-switch-row">
            <Form.Item name="isActive" valuePropName="checked" noStyle>
              <Switch />
            </Form.Item>
            <span className="planogram-muted-text">{t('planogram.hook.enabledHint')}</span>
          </div>
        </Form.Item>
      </div>

      <SectionTitle title={t('planogram.hook.section.preview')} />
      <div className="planogram-preview-box">
        <HookSidePreview
          ariaLabel={t('planogram.hook.preview.aria')}
          hook={{
            hasPriceTagArm: hasTag,
            armLengthMm: arm,
            dropMm: drop,
            wireDiameterMm: finite(values.wireDiameterMm),
            tipReserveMm: reserve,
            tagWidthMm: tagW,
            tagHeightMm: tagH,
            tagBottomAboveArmMm: tagBottom,
          }}
          labels={{
            tag: t('planogram.hook.preview.tag', { width: formatNumber(tagW), height: formatNumber(tagH) }),
            tagBottom: t('planogram.hook.preview.tagBottom', { value: formatNumber(tagBottom) }),
            armLength: t('planogram.hook.preview.armLength', { value: formatNumber(arm) }),
            drop: t('planogram.hook.preview.drop', { value: formatNumber(drop) }),
            reserve: t('planogram.hook.preview.reserve', { value: formatNumber(reserve) }),
            formula: t('planogram.hook.preview.formula', {
              usable: formatNumber(arm - reserve),
              depth: HOOK_PREVIEW_SAMPLE_DEPTH_MM,
              count: sampleQty,
            }),
          }}
        />
        <div className="planogram-preview-note is-grow">
          <p>{t('planogram.hook.preview.noteHang')}</p>
          <p>{t('planogram.hook.preview.noteTag', { value: formatNumber(tagBottom) })}</p>
          <p>{t('planogram.hook.preview.noteFootprint', { value: formatNumber(tagW) })}</p>
          <p>{t('planogram.hook.preview.noteLoad')}</p>
        </div>
      </div>
    </>
  )
}
