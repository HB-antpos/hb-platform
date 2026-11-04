import { WarningOutlined } from '@ant-design/icons'
import { Alert, Button, Checkbox, Drawer, Form, Input, InputNumber, Popconfirm, Segmented, Select, Space, Tag, message, type FormInstance } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import ProductListImage from '../../../../components/ProductListImage'
import { useIsMobile } from '../../../../hooks/useIsMobile'
import { deleteProductSpec, saveProductSpec } from '../../../../services/planogramService'
import type { HookModelDto, ProductSpecListItemDto } from '../../../../types/planogram'
import { checkHookLoad, formatKgFromGrams, formatLimitKg, resolveHookQty } from '../engine/hookQty'
import { applyPlanogramFormError, NumberField, SectionTitle, useOpenKey } from '../formParts'
import {
  DEFAULT_TAG,
  EMPTY_CELL,
  formatNumber,
  formatUpdatedAt,
  specFormToDto,
  specTableHooks,
  specToFormValues,
  type SpecFormValues,
} from '../planogramLogic'
import ProductDimPreview from '../preview/ProductDimPreview'

interface ProductSpecDrawerProps {
  /** null = 关闭；否则是列表行（含已有参数，没设过参数时 spec 为空）。 */
  target: ProductSpecListItemDto | null
  /** 全部挂钩型号（含停用）。 */
  hooks: HookModelDto[]
  /** 挂钩型号目录加载成功才允许保存：没加载好时保存会把覆盖 / 默认挂钩当成「不存在」而清掉。 */
  hooksReady: boolean
  /** 目录加载失败（用于显示重试提示；加载中不算失败）。 */
  hooksFailed: boolean
  onReloadHooks: () => void
  /** Planogram.Edit：能改参数；否则只读。 */
  canEdit: boolean
  /** Planogram.Manage：能清除参数。 */
  canManage: boolean
  onClose: () => void
  /** 保存 / 清除成功：由父级关闭抽屉并刷新列表。 */
  onSaved: () => void
}

const FIELD_NAMES = [
  'widthMm', 'heightMm', 'depthMm', 'hangHoleOffsetMm', 'hangHoleType', 'unitWeightG', 'source', 'defaultHookGuid',
] as const

/**
 * 商品陈列参数抽屉：包装尺寸、吊孔、单件重、尺寸来源，以及按挂钩型号列出的每钩挂数表。
 * 每次「打开」（target 对象换了）都用新的 key 重建整个抽屉：表单实例、输入内容、保存状态都是全新的。
 * 关闭动画期间（约 0.3 秒）立刻再打开另一个目标时，Drawer 的 destroyOnHidden 还没销毁内容，共用的表单实例会把上一次的输入
 * 盖在新目标的初值上，提交时却带着新目标的 guid，等于把旧数据写到别的记录上。
 */
export default function ProductSpecDrawer(props: ProductSpecDrawerProps) {
  const openKey = useOpenKey(props.target)
  return <ProductSpecDrawerInner key={openKey} {...props} />
}

function ProductSpecDrawerInner({
  target, hooks, hooksReady, hooksFailed, onReloadHooks, canEdit, canManage, onClose, onSaved,
}: ProductSpecDrawerProps) {
  const { t, i18n } = useTranslation()
  const isMobile = useIsMobile()
  const [form] = Form.useForm<SpecFormValues>()
  const [saving, setSaving] = useState(false)
  const [clearing, setClearing] = useState(false)
  const initialValues = useMemo(() => (target ? specToFormValues(target.spec) : null), [target])

  const submit = async (values: SpecFormValues) => {
    if (!target || !canEdit) return
    setSaving(true)
    try {
      await saveProductSpec(target.productCode, specFormToDto(target.productCode, values, hooks, target.spec))
      message.success(t('planogram.common.saved'))
      onSaved()
    } catch (error) {
      const toast = applyPlanogramFormError(form, error, FIELD_NAMES, i18n.language, (key) => t(key))
      if (toast) message.error(toast)
    } finally {
      setSaving(false)
    }
  }

  const clear = async () => {
    if (!target) return
    setClearing(true)
    try {
      await deleteProductSpec(target.productCode)
      message.success(t('planogram.spec.cleared'))
      onSaved()
    } catch (error) {
      const toast = applyPlanogramFormError(form, error, FIELD_NAMES, i18n.language, (key) => t(key))
      if (toast) message.error(toast)
    } finally {
      setClearing(false)
    }
  }

  return (
    <Drawer
      open={target !== null}
      width={isMobile ? '100%' : 640}
      title={canEdit ? t('planogram.spec.drawer.edit') : t('planogram.spec.drawer.view')}
      closable={{ placement: 'end' }}
      destroyOnHidden
      maskClosable={false}
      onClose={onClose}
      footer={(
        <div className="planogram-drawer-footer">
          {target?.hasSpec ? (
            <span className="planogram-drawer-updated">
              {t('planogram.common.lastUpdated', { time: formatUpdatedAt(target.updatedAt, true), by: target.updatedBy ?? EMPTY_CELL })}
            </span>
          ) : <span />}
          <Space>
            {canManage && target?.hasSpec ? (
              <Popconfirm
                title={t('planogram.spec.clearConfirm')}
                okText={t('planogram.spec.clear')}
                okButtonProps={{ danger: true }}
                cancelText={t('planogram.common.cancel')}
                onConfirm={clear}
              >
                <Button danger loading={clearing}>{t('planogram.spec.clear')}</Button>
              </Popconfirm>
            ) : null}
            <Button onClick={onClose}>{canEdit ? t('planogram.common.cancel') : t('planogram.common.close')}</Button>
            {canEdit ? (
              <Button type="primary" loading={saving} disabled={!hooksReady} onClick={() => form.submit()}>
                {t('planogram.common.save')}
              </Button>
            ) : null}
          </Space>
        </div>
      )}
    >
      {target && initialValues ? (
        <Form<SpecFormValues>
          form={form}
          layout="vertical"
          className="planogram-form"
          initialValues={initialValues}
          disabled={!canEdit}
          onFinish={submit}
          scrollToFirstError
        >
          <SpecFormBody
            form={form}
            item={target}
            hooks={hooks}
            hooksFailed={hooksFailed}
            canEdit={canEdit}
            onReloadHooks={onReloadHooks}
          />
        </Form>
      ) : null}
    </Drawer>
  )
}

interface SpecFormBodyProps {
  form: FormInstance<SpecFormValues>
  item: ProductSpecListItemDto
  hooks: HookModelDto[]
  hooksFailed: boolean
  canEdit: boolean
  onReloadHooks: () => void
}

function numberOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function SpecFormBody({ form, item, hooks, hooksFailed, canEdit, onReloadHooks }: SpecFormBodyProps) {
  const { t } = useTranslation()
  const watched = Form.useWatch([], form) as Partial<SpecFormValues> | undefined
  const initial = useMemo(() => specToFormValues(item.spec), [item.spec])
  const values: SpecFormValues = { ...initial, ...watched }
  const depth = numberOrNull(values.depthMm) ?? 0
  const unitWeight = numberOrNull(values.unitWeightG)
  const overrides = values.overrides ?? {}
  const defaultGuid = values.defaultHookGuid ?? null

  const tableHooks = useMemo(
    () => specTableHooks(hooks, { defaultHookGuid: defaultGuid, hookQtyOverrides: item.spec?.hookQtyOverrides ?? {} }),
    [defaultGuid, hooks, item.spec?.hookQtyOverrides],
  )
  const defaultHook = tableHooks.find((hook) => hook.guid === defaultGuid)
  const footprintTag = defaultHook?.hasPriceTagArm && defaultHook.tagWidthMm > 0 ? defaultHook.tagWidthMm : DEFAULT_TAG.widthMm

  const columns = useMemo<ColumnsType<HookModelDto>>(() => [
    {
      title: t('planogram.spec.hookTable.hook'),
      key: 'hook',
      render: (_: unknown, hook) => (
        <span>
          {hook.name}
          {hook.isActive ? null : <Tag className="planogram-mini-tag">{t('planogram.common.statusInactive')}</Tag>}
        </span>
      ),
    },
    {
      title: t('planogram.spec.hookTable.arm'),
      key: 'arm',
      render: (_: unknown, hook) => `${formatNumber(hook.armLengthMm)} / ${formatNumber(hook.tipReserveMm)}`,
    },
    {
      title: t('planogram.spec.hookTable.auto'),
      key: 'auto',
      render: (_: unknown, hook) => {
        const auto = resolveHookQty(hook, depth, overrides).auto
        return auto > 0 ? auto : EMPTY_CELL
      },
    },
    {
      title: t('planogram.spec.hookTable.manual'),
      key: 'manual',
      render: (_: unknown, hook) => (
        <Form.Item name={['overrides', hook.guid]} noStyle>
          <InputNumber
            className="planogram-manual-qty"
            size="small"
            controls={false}
            min={1}
            precision={0}
            placeholder={EMPTY_CELL}
            aria-label={t('planogram.spec.hookTable.manualAria', { name: hook.name })}
          />
        </Form.Item>
      ),
    },
    {
      title: t('planogram.spec.hookTable.effective'),
      key: 'effective',
      render: (_: unknown, hook) => {
        const result = resolveHookQty(hook, depth, overrides)
        return (
          <span>
            <strong>{result.effective > 0 ? result.effective : EMPTY_CELL}</strong>
            {result.source === 'override' ? <Tag color="gold" className="planogram-mini-tag">{t('planogram.spec.manualTag')}</Tag> : null}
          </span>
        )
      },
    },
    {
      title: t('planogram.spec.hookTable.load'),
      key: 'load',
      render: (_: unknown, hook) => {
        const result = resolveHookQty(hook, depth, overrides)
        const check = checkHookLoad(result.effective, unitWeight, hook.loadKg)
        if (check.status === 'noWeight' || check.totalG === null) return <span className="planogram-muted-text">{EMPTY_CELL}</span>
        const total = formatKgFromGrams(check.totalG)
        if (check.status === 'noLimit' || check.limitG === null) return <Tag className="planogram-mini-tag">{`${total} kg`}</Tag>
        const text = `${total} / ${formatLimitKg(check.limitG)} kg`
        // 超承重：除了颜色，还带警告图标和「超重」文字，不只靠颜色区分
        return check.status === 'over'
          ? <Tag color="warning" className="planogram-mini-tag" icon={<WarningOutlined />}>{`${text} · ${t('planogram.spec.overLoad')}`}</Tag>
          : <Tag color="success" className="planogram-mini-tag">{text}</Tag>
      },
    },
    {
      title: t('planogram.spec.hookTable.default'),
      key: 'default',
      render: (_: unknown, hook) => (
        <Checkbox
          checked={defaultGuid === hook.guid}
          disabled={!canEdit}
          aria-label={t('planogram.spec.hookTable.defaultAria', { name: hook.name })}
          onChange={(event) => form.setFieldValue('defaultHookGuid', event.target.checked ? hook.guid : null)}
        />
      ),
    },
  ], [canEdit, defaultGuid, depth, form, overrides, t, unitWeight])

  return (
    <>
      <div className="planogram-product-head">
        {item.productImage ? (
          <ProductListImage src={item.productImage} size={52} radius={6} />
        ) : (
          <div className="planogram-thumb is-large">{t('planogram.spec.noImage')}</div>
        )}
        <div className="planogram-product-head-text">
          <div className="planogram-product-name">{item.productName}</div>
          <div className="planogram-mono planogram-muted-text">
            {[
              item.productCode,
              item.barcode,
              t('planogram.spec.retail', { price: item.retailPrice.toFixed(2) }),
              item.categoryName,
            ].filter(Boolean).join(' · ')}
          </div>
        </div>
      </div>

      <div className="planogram-spec-top">
        <div className="planogram-spec-top-form">
          <SectionTitle title={t('planogram.spec.section.size')} hint={t('planogram.spec.section.sizeHint')} />
          <div className="planogram-grid is-3">
            <NumberField name="widthMm" label={t('planogram.spec.field.width')} min={1} precision={0} />
            <NumberField name="heightMm" label={t('planogram.spec.field.height')} min={1} precision={0} />
            <NumberField name="depthMm" label={t('planogram.spec.field.depth')} min={1} precision={0} />
            <NumberField name="hangHoleOffsetMm" label={t('planogram.spec.field.hangOffset')} allowZero min={0} precision={0} />
            <Form.Item name="hangHoleType" label={t('planogram.spec.field.hangType')}>
              <Select
                options={[
                  { value: 2, label: t('planogram.spec.hangType.euro') },
                  { value: 1, label: t('planogram.spec.hangType.round') },
                  { value: 0, label: t('planogram.spec.hangType.none') },
                ]}
              />
            </Form.Item>
            <NumberField name="unitWeightG" label={t('planogram.spec.field.weight')} required={false} unit="g" min={1} precision={0} />
          </div>
          <SectionTitle title={t('planogram.spec.section.source')} />
          <Form.Item name="source">
            {/* antd 的 Segmented 不读取 Form 的 disabled，没有编辑权限时要显式禁用 */}
            <Segmented
              disabled={!canEdit}
              options={[
                { label: t('planogram.spec.source.manual'), value: 1 },
                { label: t('planogram.spec.source.supplier'), value: 2 },
                { label: t('planogram.spec.source.set'), value: 3 },
              ]}
            />
          </Form.Item>
        </div>
        <div className="planogram-preview-box is-narrow">
          <ProductDimPreview
            widthMm={numberOrNull(values.widthMm)}
            heightMm={numberOrNull(values.heightMm)}
            depthMm={numberOrNull(values.depthMm)}
            hangHoleOffsetMm={numberOrNull(values.hangHoleOffsetMm)}
            hangHoleType={(values.hangHoleType ?? 2) as 0 | 1 | 2}
            ariaLabel={t('planogram.spec.preview.aria')}
            labels={{
              front: t('planogram.spec.preview.front'),
              side: t('planogram.spec.preview.side'),
              width: t('planogram.spec.preview.width', { value: formatNumber(numberOrNull(values.widthMm)) }),
              height: { name: 'H', value: formatNumber(numberOrNull(values.heightMm)) },
              depth: t('planogram.spec.preview.depth', { value: formatNumber(numberOrNull(values.depthMm)) }),
              hangHole: t('planogram.spec.preview.hangHole', { value: formatNumber(numberOrNull(values.hangHoleOffsetMm)) }),
              footprint: t('planogram.spec.preview.footprint', { tag: formatNumber(footprintTag) }),
            }}
          />
        </div>
      </div>

      <SectionTitle title={t('planogram.spec.section.qty')} hint={t('planogram.spec.section.qtyHint')} />
      {hooksFailed ? (
        <Alert
          type="warning"
          showIcon
          className="planogram-hooks-alert"
          message={t('planogram.spec.hooksFailed')}
          action={<Button size="small" onClick={onReloadHooks}>{t('planogram.common.retry')}</Button>}
        />
      ) : null}
      {/* 默认挂钩只通过表格里的勾选框改，这里只是让该字段登记进表单（useWatch / 提交都要带上它） */}
      <Form.Item name="defaultHookGuid" hidden>
        <Input />
      </Form.Item>
      <MeasuredTable<HookModelDto>
        metricId="pos-admin.planogram.spec-hooks"
        className="planogram-hook-table"
        rowKey="guid"
        size="small"
        columns={columns}
        dataSource={tableHooks}
        pagination={false}
        scroll={{ x: 'max-content' }}
        rowClassName={(hook) => (hook.guid === defaultGuid ? 'planogram-row-default' : '')}
        locale={{ emptyText: t('planogram.spec.hookTable.empty') }}
      />
      {item.referencedPlanCount > 0 ? (
        <div className="planogram-muted-text planogram-spec-tip">
          {t('planogram.spec.referencedTip', { count: item.referencedPlanCount })}
        </div>
      ) : null}
    </>
  )
}
