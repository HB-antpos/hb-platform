import { ArrowLeftOutlined, InfoCircleOutlined } from '@ant-design/icons'
import { Button, Modal, Select, Typography, message } from 'antd'
import { useCallback, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  createBatch,
  getActivePrefixes,
  getSetProductTemplate,
  getSetProductTemplates,
} from '../../../services/domesticProductCreationService'
import type { BatchInfo, SetProductTemplateSummary } from '../../../types/domesticProductCreation'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import BatchGrid from './BatchGrid'
import type { BatchGridHandle } from './BatchGrid'
import PrefixCodeManageModal from './PrefixCodeManageModal'
import PreviewCodesModal from './PreviewCodesModal'
import SaveSetTemplateModal from './SaveSetTemplateModal'
import SetTemplateDrawer from './SetTemplateDrawer'
import SummaryPanel from './SummaryPanel'
import SupplierSelect from './SupplierSelect'
import type { SupplierOption, SupplierSelectRef } from './SupplierSelect'
import { applySetTemplateDraft, createSetDraftFromTemplate, validateSetTemplateProduct } from './setTemplateRules'
import { getSetTemplateValidationMessage } from './setTemplateUi'
import { getBatchDetailErrorMessage } from './batchDetailErrorMessage'
import {
  buildCreatedBatchInfo,
  buildSubmitRequest,
  createInitialProducts,
  isBatchDraftDirty,
  summarizeDraft,
  validateDraft,
} from './batchWorkspaceLogic'
import type { DraftIssue } from './batchWorkspaceLogic'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { DraftProductItem } from './batchCreateRules'

interface PrefixOption {
  prefixCode: string
  prefixName: string
  prefixDescription?: string
}

interface BatchWorkspaceProps {
  suppliers: SupplierOption[]
  suppliersLoading: boolean
  /** 返回批次记录（有未提交内容时已在内部确认过）。 */
  onExit: () => void
  /** 创建成功；batch 为空表示后端没返回批次号，调用方只需回到列表刷新。 */
  onCreated: (batch?: BatchInfo) => void
}

/**
 * 「创建批次」工作台：左设置 / 中网格 / 右汇总与校验，取代原来的 3 步弹窗。
 * 草稿只存在于本组件状态里，提交成功才会生成货号与条码。
 */
export default function BatchWorkspace({ suppliers, suppliersLoading, onExit, onCreated }: BatchWorkspaceProps) {
  const { t } = useTranslation()
  const gridRef = useRef<BatchGridHandle>(null)
  const supplierSelectRef = useRef<SupplierSelectRef>(null)
  const prefixGuardRef = useRef(createLatestRequestGuard())
  const templateGuardRef = useRef(createLatestRequestGuard())
  const submittingRef = useRef(false)

  const [supplierCode, setSupplierCode] = useState<string>()
  const [prefixCode, setPrefixCode] = useState<string>()
  const [prefixes, setPrefixes] = useState<PrefixOption[]>([])
  const [prefixLoading, setPrefixLoading] = useState(false)
  const [templates, setTemplates] = useState<SetProductTemplateSummary[]>([])
  const [templateLoading, setTemplateLoading] = useState(false)
  // 套用模板的下拉是「动作选择器」：选完不应停留在已选状态，靠换 key 重置内部状态。
  const [templateSelectKey, setTemplateSelectKey] = useState(0)
  const [products, setProducts] = useState<DraftProductItem[]>(createInitialProducts)
  // 记录系统自动占位行，套用模板时只清理它，避免误删用户手动添加的空白行。
  const automaticPlaceholderKeyRef = useRef<string | undefined>(products[0]?.key)
  const [expandedKeys, setExpandedKeys] = useState<string[]>([])
  const [prefixManageOpen, setPrefixManageOpen] = useState(false)
  const [templateDrawerOpen, setTemplateDrawerOpen] = useState(false)
  const [saveTemplateProduct, setSaveTemplateProduct] = useState<DraftProductItem | null>(null)
  const [previewOpen, setPreviewOpen] = useState(false)
  const [submitting, setSubmitting] = useState(false)

  const supplierName = useMemo(
    () => suppliers.find((supplier) => supplier.supplierCode === supplierCode)?.supplierName,
    [supplierCode, suppliers],
  )
  const summary = useMemo(() => summarizeDraft(products), [products])
  const issues = useMemo(() => validateDraft({ supplierCode, products }), [products, supplierCode])
  const invalidRowKeys = useMemo(
    () => new Set(issues.flatMap((issue) => (issue.kind === 'set_without_sub_item' ? [issue.rowKey] : []))),
    [issues],
  )

  // ---------- 供应商相关数据 ----------
  const loadPrefixes = useCallback(async (code: string) => {
    await runLatestGuardedRequest(prefixGuardRef.current, () => getActivePrefixes(code), {
      onStart: () => setPrefixLoading(true),
      onSuccess: (response) => {
        if (response.success) setPrefixes(response.data || [])
      },
      onError: () => message.error(t('productCreation.loadPrefixFailed')),
      onSettled: () => setPrefixLoading(false),
    })
  }, [t])

  const loadTemplates = useCallback(async (code: string) => {
    await runLatestGuardedRequest(templateGuardRef.current, () => getSetProductTemplates(code, false), {
      onStart: () => setTemplateLoading(true),
      onSuccess: (response) => {
        if (!response.success) {
          message.error(response.message || t('productCreation.loadSetTemplatesFailed'))
          return
        }
        setTemplates((response.data || []).filter((template) => template.isEnabled))
      },
      onError: (error) => message.error(getBatchDetailErrorMessage(error, t('productCreation.loadSetTemplatesFailed'))),
      onSettled: () => setTemplateLoading(false),
    })
  }, [t])

  const handleSupplierChange = (value?: string) => {
    // 前缀与套装模板都按供应商隔离：换供应商就必须清掉旧的，并让还在路上的旧请求作废。
    prefixGuardRef.current.invalidate()
    templateGuardRef.current.invalidate()
    setSupplierCode(value || undefined)
    setPrefixCode(undefined)
    setPrefixes([])
    setTemplates([])
    setPrefixLoading(false)
    setTemplateLoading(false)
    if (value) {
      void loadPrefixes(value)
      void loadTemplates(value)
    }
  }

  // ---------- 套装模板 ----------
  const handleApplyTemplate = async (templateId: string) => {
    if (!supplierCode) return
    const requestedSupplier = supplierCode
    setTemplateLoading(true)
    try {
      const response = await getSetProductTemplate(templateId, requestedSupplier)
      if (!response.success || !response.data) {
        message.error(response.message || t('productCreation.loadSetTemplateFailed'))
        return
      }
      if (!response.data.isEnabled || response.data.supplierCode !== requestedSupplier) {
        message.error(t('productCreation.setTemplateUnavailable'))
        return
      }
      const draft = createSetDraftFromTemplate(response.data, products.length)
      setProducts((current) => applySetTemplateDraft(current, draft, automaticPlaceholderKeyRef.current))
      setExpandedKeys((keys) => [...keys, draft.key])
      gridRef.current?.locateRow(draft.key)
    } catch (error) {
      message.error(getBatchDetailErrorMessage(error, t('productCreation.loadSetTemplateFailed')))
    } finally {
      setTemplateLoading(false)
    }
  }

  const handleSaveAsTemplate = (setKey: string) => {
    if (!supplierCode) {
      message.warning(t('domesticProducts.selectSupplier'))
      return
    }
    const product = products.find((item) => item.key === setKey)
    if (!product || product.productType !== ProductCreationType.SET) return
    const validationError = validateSetTemplateProduct(product)
    if (validationError) {
      message.error(getSetTemplateValidationMessage(validationError, t))
      return
    }
    setSaveTemplateProduct(product)
  }

  // ---------- 返回 / 提交 ----------
  const handleBack = () => {
    if (submitting) return
    if (!isBatchDraftDirty({ supplierCode, prefixCode, products })) {
      onExit()
      return
    }
    Modal.confirm({
      title: t('productCreation.leaveConfirmTitle'),
      content: t('productCreation.leaveConfirmContent'),
      okText: t('productCreation.leaveConfirmOk'),
      cancelText: t('productCreation.keepEditing'),
      okButtonProps: { danger: true },
      maskClosable: false,
      onOk: onExit,
    })
  }

  const handleLocateIssue = (issue: DraftIssue) => {
    if (issue.kind === 'missing_supplier') supplierSelectRef.current?.focus()
    else if (issue.kind === 'set_without_sub_item') gridRef.current?.locateRow(issue.rowKey)
  }

  const doSubmit = async () => {
    // 返回的 Promise 会让确认弹窗的「提交创建」按钮保持 loading；ref 锁则挡住极快的重复点击。
    if (!supplierCode || submittingRef.current) return
    submittingRef.current = true
    setSubmitting(true)
    try {
      const response = await createBatch(buildSubmitRequest({ supplierCode, prefixCode, products }))
      if (!response.success) {
        message.error(response.message || t('productCreation.createFailed'))
        return
      }
      message.success(t('productCreation.createSuccess'))
      if (!response.data?.batchNumber) {
        message.warning(t('productCreation.createSuccessNoBatchNumber'))
        onCreated()
        return
      }
      onCreated(buildCreatedBatchInfo({ response: response.data, supplierCode, supplierName, prefixCode }))
    } catch (error) {
      message.error(getBatchDetailErrorMessage(error, t('productCreation.createFailed')))
    } finally {
      submittingRef.current = false
      setSubmitting(false)
    }
  }

  const handleSubmit = () => {
    if (issues.length > 0 || submittingRef.current) return
    // 货号与条码一经创建就被占用，提交前再确认一次数量，取代原来的「预览确认」步骤。
    Modal.confirm({
      title: t('productCreation.confirmSubmitTitle', { count: summary.expectedItems }),
      content: t('productCreation.confirmSubmitContent', {
        normal: summary.normalCount,
        set: summary.setCount,
        supplier: supplierName || supplierCode,
      }),
      okText: t('productCreation.submitCreate'),
      cancelText: t('common.cancel'),
      maskClosable: false,
      onOk: doSubmit,
    })
  }

  return (
    <div className="page-container pc-workspace" data-testid="product-creation-workspace">
      <div className="page-header page-header-compact pc-workspace-header">
        <Button type="link" icon={<ArrowLeftOutlined />} className="pc-back" disabled={submitting} onClick={handleBack}>
          {t('productCreation.backToList')}
        </Button>
        <span className="pc-faint">/</span>
        <Typography.Title level={4} style={{ margin: 0 }}>{t('productCreation.createBatch')}</Typography.Title>
        <span className="pc-header-spacer" />
        <span className="pc-sub">{t('productCreation.draftHint')}</span>
      </div>

      <div className="pc-workgrid">
        <section className="pc-panel pc-settings" data-testid="product-creation-settings" aria-label={t('productCreation.settingsTitle')}>
          <h3 className="pc-panel-title">{t('productCreation.settingsTitle')}</h3>

          <div className="pc-field">
            <div className="pc-field-label"><span>{t('domesticProducts.supplier')}<em>*</em></span></div>
            <SupplierSelect
              ref={supplierSelectRef}
              suppliers={suppliers}
              value={supplierCode}
              loading={suppliersLoading}
              placeholder={t('domesticProducts.selectSupplier')}
              onChange={(value) => handleSupplierChange(value)}
              aria-label={t('domesticProducts.supplier')}
              data-testid="product-creation-supplier-select"
            />
          </div>

          <div className="pc-field">
            <div className="pc-field-label">
              <span>{t('productCreation.prefixColumn')}</span>
              <Button type="link" size="small" className="pc-link" disabled={!supplierCode} onClick={() => setPrefixManageOpen(true)}>
                {t('productCreation.managePrefix')}
              </Button>
            </div>
            <Select<string>
              allowClear
              showSearch
              value={prefixCode}
              loading={prefixLoading}
              disabled={!supplierCode}
              placeholder={supplierCode ? t('productCreation.prefixPlaceholder') : t('productCreation.selectSupplierFirst')}
              aria-label={t('productCreation.prefixColumn')}
              onChange={(value) => setPrefixCode(value || undefined)}
              filterOption={(input, option) => `${option?.searchText ?? ''}`.toLowerCase().includes(input.trim().toLowerCase())}
              options={prefixes.map((prefix) => ({
                value: prefix.prefixName,
                searchText: `${prefix.prefixName} ${prefix.prefixDescription ?? ''}`,
                label: (
                  <span className="pc-prefix-option">
                    <span className="pc-prefix-tag">{prefix.prefixName}</span>
                    {prefix.prefixDescription ? <span className="pc-prefix-desc">{prefix.prefixDescription}</span> : null}
                  </span>
                ),
              }))}
              data-testid="product-creation-prefix-select"
            />
            <span className="pc-help">{t('productCreation.prefixHelp')}</span>
          </div>

          <div className="pc-field">
            <div className="pc-field-label">
              <span>{t('productCreation.setTemplateLabel')}</span>
              <Button type="link" size="small" className="pc-link" disabled={!supplierCode} onClick={() => setTemplateDrawerOpen(true)}>
                {t('productCreation.manageSetTemplates')}
              </Button>
            </div>
            <Select<string>
              key={templateSelectKey}
              showSearch
              optionFilterProp="label"
              loading={templateLoading}
              disabled={!supplierCode}
              placeholder={supplierCode ? t('productCreation.selectSetTemplate') : t('productCreation.selectSupplierFirst')}
              notFoundContent={t('productCreation.noSetTemplates')}
              aria-label={t('productCreation.setTemplateLabel')}
              onSelect={(value) => {
                setTemplateSelectKey((key) => key + 1)
                void handleApplyTemplate(value)
              }}
              options={templates.map((template) => ({
                value: template.templateId,
                label: `${template.templateName} · ${template.setProductName} (${template.setQuantity})`,
              }))}
              data-testid="product-creation-template-select"
            />
          </div>

          <div className="pc-notice pc-notice-warn pc-settings-notice">
            <InfoCircleOutlined />
            <span>
              {t('productCreation.previewNoticeLead')}
              <b>{t('productCreation.previewNoticeStrong')}</b>
              {t('productCreation.previewNoticeTail')}
            </span>
          </div>
        </section>

        <BatchGrid
          ref={gridRef}
          products={products}
          onProductsChange={(updater) => setProducts(updater)}
          expandedKeys={expandedKeys}
          onExpandedKeysChange={(updater) => setExpandedKeys(updater)}
          invalidRowKeys={invalidRowKeys}
          canSaveTemplate={Boolean(supplierCode)}
          onSaveAsTemplate={handleSaveAsTemplate}
        />

        <SummaryPanel
          summary={summary}
          issues={issues}
          submitting={submitting}
          onLocateIssue={handleLocateIssue}
          onPreview={() => setPreviewOpen(true)}
          onSubmit={handleSubmit}
        />
      </div>

      <PrefixCodeManageModal
        visible={prefixManageOpen}
        supplierCode={supplierCode || ''}
        supplierName={supplierName || ''}
        onClose={() => setPrefixManageOpen(false)}
        onSuccess={() => {
          setPrefixManageOpen(false)
          if (supplierCode) void loadPrefixes(supplierCode)
        }}
      />

      <SetTemplateDrawer
        open={templateDrawerOpen}
        supplierCode={supplierCode}
        supplierName={supplierName}
        onClose={() => setTemplateDrawerOpen(false)}
        onChanged={() => {
          if (supplierCode) void loadTemplates(supplierCode)
        }}
      />

      <SaveSetTemplateModal
        product={saveTemplateProduct}
        supplierCode={supplierCode}
        onClose={() => setSaveTemplateProduct(null)}
        onSaved={() => {
          setSaveTemplateProduct(null)
          if (supplierCode) void loadTemplates(supplierCode)
        }}
      />

      <PreviewCodesModal
        open={previewOpen}
        products={products}
        prefixCode={prefixCode}
        supplierName={supplierName}
        onClose={() => setPreviewOpen(false)}
      />
    </div>
  )
}
