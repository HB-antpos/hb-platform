import { CheckCircleFilled, CloseCircleFilled, LoadingOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Empty, Form, Input, Space, Switch, Tag, Typography } from 'antd'
import type { FormInstance } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { getInvoiceGrid } from '../../../services/localSupplierInvoiceService'
import type { LocalSupplierDto } from '../../../types/localSupplier'
import type { LocalSupplierInvoiceListDto } from '../../../types/localSupplierInvoice'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import {
  buildSupplierImageUrl,
  COS_SUPPLIER_IMAGE_TEMPLATE,
  SUPPLIER_IMAGE_TEMPLATE_PRESETS,
} from '../ProductManagement/productImageTemplate'
import {
  buildRecentInvoicesRequest,
  buildSupplierInvoicesLink,
  formatPurchaseDate,
  parsePreviewItemNumbers,
  SUPPLIER_IMAGE_TEMPLATE_MAX_LENGTH,
  validateSupplierImageTemplateInput,
} from './supplierPageLogic'

export interface SupplierFormValues {
  name: string
  status: boolean
  contactPerson?: string
  phone?: string
  email?: string
  remark?: string
  imageBaseUrl?: string
}

interface SupplierDrawerProps {
  open: boolean
  /** 编辑 / 查看的供应商；为空表示新建。 */
  supplier: LocalSupplierDto | null
  form: FormInstance<SupplierFormValues>
  canEdit: boolean
  canViewInvoices: boolean
  saving: boolean
  onClose: () => void
  onSubmit: () => void
}

type PreviewStatus = 'loading' | 'ok' | 'failed'

/**
 * 供应商详情抽屉：新建、编辑共用同一个表单；编辑时下方显示近期进货单。
 * 只有查看权限时表单只读、不显示保存。
 */
export default function SupplierDrawer({
  open,
  supplier,
  form,
  canEdit,
  canViewInvoices,
  saving,
  onClose,
  onSubmit,
}: SupplierDrawerProps) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const isMobile = useIsMobile()
  const template = Form.useWatch('imageBaseUrl', form) ?? ''
  const isCreate = supplier === null
  const supplierCode = supplier?.localSupplierCode ?? ''

  // ---- 图片模板预览：输入货号后按当前模板拼地址，直接用 <img> 试加载 ----
  const [previewInput, setPreviewInput] = useState('')
  const [previewItems, setPreviewItems] = useState<string[]>([])
  const [previewStatus, setPreviewStatus] = useState<Record<string, PreviewStatus>>({})
  const templateIssue = validateSupplierImageTemplateInput(template)
  const templateUsesSupplierCode = template.includes('{supplierCode}')
  const previewBlocked = isCreate && templateUsesSupplierCode
  const previewUrls = useMemo(
    () => previewItems.map((itemNumber) => ({
      itemNumber,
      url: buildSupplierImageUrl(template.trim(), { localSupplierCode: supplierCode, itemNumber }),
    })),
    [previewItems, supplierCode, template],
  )

  // ---- 近期进货单 ----
  const [invoices, setInvoices] = useState<LocalSupplierInvoiceListDto[]>([])
  const [invoicesLoading, setInvoicesLoading] = useState(false)
  const [invoicesFailed, setInvoicesFailed] = useState(false)
  const invoiceGuardRef = useRef(createLatestRequestGuard())

  // 换供应商或关闭时清空预览与进货单，避免短暂显示上一个供应商的数据。
  useEffect(() => {
    setPreviewInput('')
    setPreviewItems([])
    setPreviewStatus({})
    setInvoices([])
    setInvoicesFailed(false)
  }, [open, supplierCode])

  useEffect(() => {
    const guard = invoiceGuardRef.current
    if (!open || isCreate || !canViewInvoices || !supplierCode) {
      guard.invalidate()
      return
    }
    void runLatestGuardedRequest(guard, () => getInvoiceGrid(buildRecentInvoicesRequest(supplierCode)), {
      onStart: () => {
        setInvoicesLoading(true)
        setInvoicesFailed(false)
      },
      onSuccess: (result) => setInvoices(result?.items ?? []),
      onError: () => setInvoicesFailed(true),
      onSettled: () => setInvoicesLoading(false),
    })
    return () => guard.invalidate()
  }, [canViewInvoices, isCreate, open, supplierCode])

  const runPreview = () => {
    const items = parsePreviewItemNumbers(previewInput)
    setPreviewItems(items)
    setPreviewStatus(Object.fromEntries(items.map((item) => [item, 'loading' as PreviewStatus])))
  }

  const setTemplate = (value: string) => {
    form.setFieldValue('imageBaseUrl', value)
    void form.validateFields(['imageBaseUrl']).catch(() => undefined)
  }

  const openInvoices = () => {
    onClose()
    navigate(buildSupplierInvoicesLink(supplierCode))
  }

  const lastPurchaseDate = formatPurchaseDate(supplier?.lastPurchaseDate)

  const title = isCreate ? (
    t('posAdmin.suppliers.createTitle')
  ) : (
    <div className="supplier-drawer-title">
      <Space size={8} wrap>
        <span>{supplier.name}</span>
        <Tag className="supplier-code-tag">{supplier.localSupplierCode}</Tag>
        {supplier.status === 1 ? <Tag color="green">{t('common.active')}</Tag> : <Tag>{t('common.inactive')}</Tag>}
      </Space>
      <Typography.Text type="secondary" className="supplier-drawer-subtitle">
        {lastPurchaseDate
          ? t('posAdmin.supplierPage.drawer.lastPurchase', {
              date: lastPurchaseDate,
              store: supplier.lastPurchaseStoreName || supplier.lastPurchaseStoreCode || '-',
            })
          : t('posAdmin.supplierPage.drawer.noPurchase')}
      </Typography.Text>
    </div>
  )

  return (
    <Drawer
      open={open}
      title={title}
      width={isMobile ? '100%' : 600}
      onClose={onClose}
      destroyOnHidden={false}
      className="supplier-drawer"
      footer={
        <div className="supplier-drawer-footer">
          {canEdit ? (
            <>
              <Button onClick={onClose}>{t('common.cancel')}</Button>
              <Button type="primary" loading={saving} onClick={onSubmit}>
                {isCreate ? t('posAdmin.suppliers.createSupplier') : t('common.save')}
              </Button>
            </>
          ) : (
            <>
              <Typography.Text type="secondary">{t('posAdmin.supplierPage.drawer.readOnly')}</Typography.Text>
              <Button onClick={onClose}>{t('common.close')}</Button>
            </>
          )}
        </div>
      }
    >
      <Form form={form} layout="vertical" disabled={!canEdit} requiredMark>
        <section className="supplier-drawer-section">
          <h4>{t('posAdmin.supplierPage.drawer.sectionBasic')}</h4>
          <div className="supplier-form-grid">
            <Form.Item
              name="name"
              label={t('posAdmin.suppliers.name')}
              rules={[{ required: true, message: t('posAdmin.suppliers.nameRequired') }, { max: 128 }]}
            >
              <Input />
            </Form.Item>
            <Form.Item name="status" label={t('column.status')} valuePropName="checked">
              <Switch checkedChildren={t('common.active')} unCheckedChildren={t('common.inactive')} />
            </Form.Item>
            <Form.Item name="contactPerson" label={t('posAdmin.suppliers.contactPerson')} rules={[{ max: 64 }]}>
              <Input />
            </Form.Item>
            <Form.Item name="phone" label={t('posAdmin.suppliers.phone')} rules={[{ max: 32 }]}>
              <Input />
            </Form.Item>
            <Form.Item name="email" label="Email" rules={[{ type: 'email' }, { max: 128 }]} className="supplier-form-full">
              <Input />
            </Form.Item>
            <Form.Item name="remark" label={t('column.remarks')} rules={[{ max: 256 }]} className="supplier-form-full">
              <Input.TextArea autoSize={{ minRows: 1, maxRows: 4 }} placeholder={t('posAdmin.supplierPage.drawer.remarkPlaceholder')} />
            </Form.Item>
          </div>
        </section>

        <section className="supplier-drawer-section">
          <h4>{t('posAdmin.supplierPage.drawer.sectionImage')}</h4>
          <Form.Item
            name="imageBaseUrl"
            rules={[{
              validator: async (_rule, value: string | undefined) => {
                const issue = validateSupplierImageTemplateInput(value)
                if (issue === 'tooLong') {
                  throw new Error(t('posAdmin.supplierPage.drawer.templateTooLong', { max: SUPPLIER_IMAGE_TEMPLATE_MAX_LENGTH }))
                }
                if (issue === 'missingPlaceholder') {
                  throw new Error(t('posAdmin.supplierPage.drawer.templateMissingPlaceholder'))
                }
              },
            }]}
            extra={
              template.trim() && !templateIssue ? (
                <span className="supplier-template-ok">
                  {t('posAdmin.supplierPage.drawer.templateOk', {
                    length: template.trim().length,
                    max: SUPPLIER_IMAGE_TEMPLATE_MAX_LENGTH,
                  })}
                </span>
              ) : !template.trim() ? (
                t('posAdmin.supplierPage.drawer.templateEmpty')
              ) : null
            }
          >
            <Input className="supplier-mono" placeholder="https://www.example.com/images/{itemNumber}.jpg" allowClear />
          </Form.Item>
          {canEdit ? (
            <div className="supplier-template-presets">
              {Object.entries(SUPPLIER_IMAGE_TEMPLATE_PRESETS).map(([code, value]) => (
                <Tag.CheckableTag key={code} checked={template.trim() === value} onChange={() => setTemplate(value)}>
                  {t('posAdmin.supplierPage.drawer.preset', { name: code.toUpperCase() })}
                </Tag.CheckableTag>
              ))}
              <Tag.CheckableTag
                checked={template.trim() === COS_SUPPLIER_IMAGE_TEMPLATE}
                onChange={() => setTemplate(COS_SUPPLIER_IMAGE_TEMPLATE)}
              >
                {t('posAdmin.supplierPage.drawer.cosPreset')}
              </Tag.CheckableTag>
              <Tag.CheckableTag checked={false} onChange={() => setTemplate('')}>
                {t('posAdmin.supplierPage.drawer.clearTemplate')}
              </Tag.CheckableTag>
            </div>
          ) : null}

          {/* 预览不改数据，只读账号也可以试 */}
          <div className="supplier-preview-bar">
            <span>{t('posAdmin.supplierPage.drawer.previewLabel')}</span>
            <Input
              disabled={false}
              value={previewInput}
              onChange={(event) => setPreviewInput(event.target.value)}
              onPressEnter={runPreview}
              placeholder={t('posAdmin.supplierPage.drawer.previewPlaceholder')}
              style={{ maxWidth: 260 }}
            />
            <Button
              disabled={!template.trim() || Boolean(templateIssue) || previewBlocked || !previewInput.trim()}
              onClick={runPreview}
            >
              {t('posAdmin.supplierPage.drawer.preview')}
            </Button>
          </div>
          {previewBlocked ? (
            <Alert type="info" showIcon message={t('posAdmin.supplierPage.drawer.previewNeedsCode')} />
          ) : previewUrls.length ? (
            <div className="supplier-preview-grid">
              {previewUrls.map(({ itemNumber, url }) => {
                const status = previewStatus[itemNumber] ?? 'loading'
                return (
                  <div key={itemNumber} className="supplier-preview-card">
                    <div className="supplier-preview-image">
                      {status === 'failed' ? (
                        <span>{itemNumber}</span>
                      ) : (
                        <img
                          src={url}
                          alt={itemNumber}
                          referrerPolicy="no-referrer"
                          onLoad={() => setPreviewStatus((current) => ({ ...current, [itemNumber]: 'ok' }))}
                          onError={() => setPreviewStatus((current) => ({ ...current, [itemNumber]: 'failed' }))}
                        />
                      )}
                    </div>
                    <div className={`supplier-preview-status is-${status}`}>
                      {status === 'loading' ? <LoadingOutlined /> : status === 'ok' ? <CheckCircleFilled /> : <CloseCircleFilled />}
                      <span>
                        {status === 'loading'
                          ? t('posAdmin.supplierPage.drawer.previewLoading')
                          : status === 'ok'
                            ? t('posAdmin.supplierPage.drawer.previewOk')
                            : t('posAdmin.supplierPage.drawer.previewFailed')}
                      </span>
                    </div>
                    <div className="supplier-preview-url" title={url}>{url}</div>
                  </div>
                )
              })}
            </div>
          ) : null}
        </section>
      </Form>

      {!isCreate && canViewInvoices ? (
        <section className="supplier-drawer-section">
          <h4>
            {t('posAdmin.supplierPage.drawer.sectionInvoices')}
            <Button type="link" size="small" onClick={openInvoices} className="supplier-section-link">
              {t('posAdmin.supplierPage.drawer.viewAllInvoices')} →
            </Button>
          </h4>
          {invoicesFailed ? (
            <Alert type="warning" showIcon message={t('posAdmin.supplierPage.drawer.invoicesFailed')} />
          ) : (
            <MeasuredTable<LocalSupplierInvoiceListDto>
              metricId="pos-admin.supplier-management.recent-invoices"
              size="small"
              rowKey="invoiceGUID"
              loading={invoicesLoading}
              dataSource={invoices}
              pagination={false}
              locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('posAdmin.supplierPage.drawer.invoicesEmpty')} /> }}
              columns={[
                {
                  title: t('posAdmin.supplierPage.drawer.invoiceNo'),
                  dataIndex: 'invoiceNo',
                  render: (value?: string) => <span className="supplier-mono">{value || '-'}</span>,
                },
                {
                  title: t('column.store'),
                  dataIndex: 'storeName',
                  render: (value: string | undefined, record) => value || record.storeCode || '-',
                },
                {
                  title: t('posAdmin.supplierPage.drawer.orderDate'),
                  dataIndex: 'orderDate',
                  render: (value?: string) => formatPurchaseDate(value) ?? '-',
                },
                {
                  title: t('posAdmin.supplierPage.drawer.amount'),
                  dataIndex: 'totalAmount',
                  align: 'right',
                  render: (value?: number) =>
                    typeof value === 'number'
                      ? value.toLocaleString('en-AU', { style: 'currency', currency: 'AUD' })
                      : '-',
                },
              ]}
            />
          )}
        </section>
      ) : null}
    </Drawer>
  )
}
