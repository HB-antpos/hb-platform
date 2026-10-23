import { ArrowLeftOutlined, CheckOutlined, InboxOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, App, Button, Form, Input, Radio, Segmented, Select, Spin, Steps, Tag, Tooltip, Upload } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import {
  createSupplierPreorderForm,
  getSupplierPreorderImportRefCandidates,
  getSupplierPreorderSuppliers,
  parseSupplierPreorderFile,
  parseSupplierPreorderPasted,
  previewSupplierPreorderImport,
} from '../../../services/supplierPreorderService'
import type {
  SupplierPreorderColumnMapping,
  SupplierPreorderHolidayType,
  SupplierPreorderExportQtyUnit,
  SupplierPreorderParseResult,
  SupplierPreorderPreviewItem,
  SupplierPreorderPreviewResult,
  SupplierPreorderSupplierOption,
} from '../../../types/supplierPreorder'

import FormHeaderFields from './FormHeaderFields'
import { buildHeaderValuesFromParse, toHeaderPayload, type FormHeaderValues } from './formHeaderValues'
import {
  buildRefOverrides,
  collectMatchedProductCodes,
  getRefDisplayCode,
  importChoiceToItemRef,
  resolveImportRefContext,
  toImportRefChoice,
  type ImportRefChoice,
} from './refPicker.logic'
import RefPickerModal, { type RefCandidatesLoader, type RefPickerResult, type RefPickerTarget } from './RefPickerModal'
import './registerSpAdminMessages'
import { ItemThumb, PreviewStatusTag } from './SpAdminParts'
import {
  DEFAULT_PAGE_SIZE,
  EMPTY_MAPPING,
  OPTIONAL_MAPPING_KEYS,
  PAGE_SIZE_OPTIONS,
  REQUIRED_MAPPING_KEYS,
  filterPreviewItems,
  findDuplicateMappedColumns,
  formatCbm,
  formatMoney,
  formatPriceChangeRate,
  getColumnLetter,
  getErrorText,
  getImportFileKind,
  getMissingRequiredMappings,
  isAbortError,
  isPriceChanged,
  normalizeMapping,
  setMappingColumn,
  splitPastedSheet,
  type MappingKey,
  type PreviewFilter,
} from './supplierPreorderAdmin.logic'
import './styles.css'

type ImportSource = { kind: 'file'; file: File } | { kind: 'paste'; rows: string[][] }

/** 映射改动后等用户停手再请求校对预览 */
const PREVIEW_DEBOUNCE_MS = 450

/** 导入供应商订单表向导（/supplier-preorders/import）：上传或粘贴 → 识别列与校对 → 订单表信息。 */
export default function SupplierPreorderImportPage() {
  const { t } = useTranslation()
  const { message, modal } = App.useApp()
  const navigate = useNavigate()
  const [form] = Form.useForm<FormHeaderValues>()

  const [step, setStep] = useState(0)
  const [suppliers, setSuppliers] = useState<SupplierPreorderSupplierOption[]>([])
  const [suppliersLoading, setSuppliersLoading] = useState(false)
  const [supplierCode, setSupplierCode] = useState<string | undefined>()
  const [pasteText, setPasteText] = useState('')
  const [parsing, setParsing] = useState(false)
  const [parseResult, setParseResult] = useState<SupplierPreorderParseResult | null>(null)
  /** 解析时选的供应商：之后换供应商要重新识别才会套用该供应商记住的列映射 */
  const [parsedWithSupplier, setParsedWithSupplier] = useState<string | undefined>()
  const [sourceLabel, setSourceLabel] = useState<string | null>(null)
  const lastSourceRef = useRef<ImportSource | null>(null)
  const [mapping, setMapping] = useState<SupplierPreorderColumnMapping>(EMPTY_MAPPING)
  const [exportQtyUnit, setExportQtyUnit] = useState<SupplierPreorderExportQtyUnit>('Cartons')
  const [preview, setPreview] = useState<SupplierPreorderPreviewResult | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [previewRetry, setPreviewRetry] = useState(0)
  const [previewFilter, setPreviewFilter] = useState<PreviewFilter>('all')
  const [previewPage, setPreviewPage] = useState(1)
  const [previewPageSize, setPreviewPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [submitting, setSubmitting] = useState(false)
  /** 校对页人工指定的参考商品（按原表行号）；建表时作为 refOverrides 提交 */
  const [refChoices, setRefChoices] = useState<Record<number, ImportRefChoice>>({})
  const [refPickingRow, setRefPickingRow] = useState<SupplierPreorderPreviewItem | null>(null)
  // 第 3 步的表单解析后就已回填（表单一直挂载），第 2 步直接读；preserve 保证字段没渲染时也能取到
  const watchedHoliday = Form.useWatch<SupplierPreorderHolidayType | undefined>('holidayType', { form, preserve: true })
  const watchedYear = Form.useWatch<number | undefined>('seasonYear', { form, preserve: true })
  const watchedHistory = Form.useWatch<FormHeaderValues['historyRange']>('historyRange', { form, preserve: true })

  useEffect(() => {
    setSuppliersLoading(true)
    getSupplierPreorderSuppliers()
      .then(setSuppliers)
      .catch((error) => message.error(getErrorText(error, t('spAdmin.import.suppliersLoadFailed'))))
      .finally(() => setSuppliersLoading(false))
  }, [message, t])

  const supplierName = useMemo(() => suppliers.find((item) => item.code === supplierCode)?.name ?? null, [supplierCode, suppliers])
  const missingMappings = useMemo(() => getMissingRequiredMappings(mapping), [mapping])
  const duplicateColumns = useMemo(() => findDuplicateMappedColumns(mapping), [mapping])

  // 关键逻辑：映射或供应商变化后防抖请求校对预览；新请求发出前取消旧请求，旧响应不会覆盖新结果。
  useEffect(() => {
    if (!parseResult || !supplierCode || missingMappings.length > 0) {
      setPreview(null)
      setPreviewError(null)
      setPreviewLoading(false)
      return undefined
    }
    const controller = new AbortController()
    setPreviewLoading(true)
    const timer = window.setTimeout(() => {
      setPreviewError(null)
      previewSupplierPreorderImport(supplierCode, mapping, parseResult.rows, controller.signal)
        .then((result) => {
          if (controller.signal.aborted) return
          setPreview(result)
          setPreviewPage(1)
        })
        .catch((error) => {
          if (controller.signal.aborted || isAbortError(error)) return
          setPreviewError(getErrorText(error, t('spAdmin.import.previewFailed')))
        })
        .finally(() => {
          if (!controller.signal.aborted) setPreviewLoading(false)
        })
    }, PREVIEW_DEBOUNCE_MS)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [mapping, missingMappings.length, parseResult, previewRetry, supplierCode, t])

  const runParse = async (source: ImportSource, code: string | undefined, label: string) => {
    setParsing(true)
    try {
      const result =
        source.kind === 'file'
          ? await parseSupplierPreorderFile(source.file, code)
          : await parseSupplierPreorderPasted(source.rows, code)
      lastSourceRef.current = source
      const unit = result.suggestedExportQtyUnit ?? 'Cartons'
      setParseResult(result)
      setParsedWithSupplier(code)
      setSourceLabel(label)
      setMapping(normalizeMapping(result.suggestedMapping))
      setExportQtyUnit(unit)
      setPreview(null)
      setPreviewFilter('all')
      setPreviewPage(1)
      // 换了来源：行号不再对应，之前指定的参考作废
      setRefChoices({})
      const name = suppliers.find((item) => item.code === code)?.name ?? null
      form.setFieldsValue(buildHeaderValuesFromParse(result.headerInfo ?? {}, name, unit, new Date().getFullYear()))
      setStep(1)
      message.success(t('spAdmin.import.parsed', { count: result.rows.length }))
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.import.parseFailed')))
    } finally {
      setParsing(false)
    }
  }

  const handleFile = (file: File) => {
    const kind = getImportFileKind(file.name)
    if (kind === 'xls') {
      message.error(t('spAdmin.import.xlsNotSupported'))
      return
    }
    if (kind === 'unsupported') {
      message.error(t('spAdmin.import.fileNotSupported'))
      return
    }
    void runParse({ kind: 'file', file }, supplierCode, file.name)
  }

  const handleParsePaste = () => {
    const rows = splitPastedSheet(pasteText)
    if (rows.length < 2) {
      message.warning(t('spAdmin.import.pasteTooShort'))
      return
    }
    void runParse({ kind: 'paste', rows }, supplierCode, t('spAdmin.import.pasteSourceLabel', { count: rows.length }))
  }

  const handleReparseWithSupplier = () => {
    const source = lastSourceRef.current
    if (!source || !sourceLabel) return
    void runParse(source, supplierCode, sourceLabel)
  }

  const importableCount = preview ? preview.counts.total - preview.counts.invalid : 0
  const canGoStep3 = Boolean(parseResult && supplierCode && missingMappings.length === 0 && preview && !previewLoading && importableCount > 0)

  const handleSubmit = async () => {
    if (!parseResult || !preview) return
    if (!supplierCode) {
      message.warning(t('spAdmin.import.supplierRequired'))
      return
    }
    try {
      await form.validateFields()
    } catch {
      return
    }
    // getFieldsValue(true) 取整个表单存储：柜型等未渲染字段（选了 40GP 预设时）也要带上
    const values = form.getFieldsValue(true) as FormHeaderValues
    const refOverrides = buildRefOverrides(refChoices, preview.items)
    const create = async () => {
      setSubmitting(true)
      try {
        const detail = await createSupplierPreorderForm({
          ...toHeaderPayload(values),
          exportQtyUnit,
          localSupplierCode: supplierCode,
          sourceFileGuid: parseResult.sourceFileGuid ?? null,
          sheetName: parseResult.sheetName,
          headerRowNo: parseResult.headerRowNo,
          headerSignature: parseResult.headerSignature,
          mapping,
          rows: parseResult.rows,
          refOverrides: refOverrides.length ? refOverrides : null,
        })
        message.success(t('spAdmin.import.created'))
        navigate(`/supplier-preorders/forms/${detail.summary.formGuid}`)
      } catch (error) {
        message.error(getErrorText(error, t('spAdmin.import.createFailed')))
      } finally {
        setSubmitting(false)
      }
    }
    if (preview.counts.invalid > 0) {
      modal.confirm({
        title: t('spAdmin.import.invalidConfirmTitle'),
        content: t('spAdmin.import.invalidConfirmContent', { invalid: preview.counts.invalid, count: importableCount }),
        okText: t('spAdmin.import.createConfirm'),
        cancelText: t('spAdmin.common.cancel'),
        onOk: create,
      })
      return
    }
    await create()
  }

  // ---------- 列映射下拉 ----------

  const columnOptions = useMemo(
    () =>
      (parseResult?.columns ?? []).map((column) => {
        const letter = column.letter || getColumnLetter(column.index)
        const header = column.header?.trim() || t('spAdmin.import.noHeader')
        return {
          value: column.index,
          label: `${letter} · ${header}`,
          samples: column.sampleValues.filter((value) => value?.trim()).slice(0, 3),
        }
      }),
    [parseResult, t],
  )

  const renderMappingField = (key: MappingKey, required: boolean) => {
    const missing = required && mapping[key] === null
    const duplicated = duplicateColumns.some((entry) => entry.keys.includes(key))
    return (
      <div key={key} className={`sp-admin-mapping-field${missing ? ' is-missing' : ''}`}>
        <div className="sp-admin-mapping-label">
          {t(`spAdmin.mapping.${key}`)}
          {required ? <span className="sp-admin-required">*</span> : null}
        </div>
        <Select<number>
          value={mapping[key] ?? undefined}
          allowClear
          showSearch
          optionFilterProp="label"
          status={missing ? 'error' : duplicated ? 'warning' : undefined}
          placeholder={required ? t('spAdmin.import.mappingRequiredPlaceholder') : t('spAdmin.import.mappingOptionalPlaceholder')}
          options={columnOptions}
          popupMatchSelectWidth={320}
          optionRender={(option) => (
            <div className="sp-admin-mapping-option">
              <div>{option.data.label}</div>
              {option.data.samples.length ? (
                <div className="sp-admin-muted sp-admin-small sp-admin-ellipsis">{option.data.samples.join(' / ')}</div>
              ) : null}
            </div>
          )}
          onChange={(value) => setMapping((current) => setMappingColumn(current, key, value))}
          style={{ width: '100%' }}
        />
      </div>
    )
  }

  // ---------- 参考商品（人工指定） ----------

  const refContext = useMemo(
    () =>
      resolveImportRefContext({
        supplierCode,
        holidayType: watchedHoliday,
        seasonYear: watchedYear,
        historyFrom: watchedHistory?.[0]?.format('YYYY-MM-DD') ?? null,
        historyTo: watchedHistory?.[1]?.format('YYYY-MM-DD') ?? null,
        headerInfo: parseResult?.headerInfo,
      }),
    [parseResult, supplierCode, watchedHistory, watchedHoliday, watchedYear],
  )
  const excludeProductCodes = useMemo(() => collectMatchedProductCodes(preview?.items ?? []), [preview])
  const refChoiceCount = Object.keys(refChoices).length

  const refPickerTarget = useMemo<RefPickerTarget | null>(
    () =>
      refPickingRow
        ? {
            key: String(refPickingRow.rowNo),
            name: refPickingRow.name,
            supplierItemCode: refPickingRow.supplierItemCode,
            price: refPickingRow.price,
            currentRef: importChoiceToItemRef(refChoices[refPickingRow.rowNo]),
          }
        : null,
    [refChoices, refPickingRow],
  )

  const refLoader = useCallback<RefCandidatesLoader>(
    (keyword, signal) => {
      if (!refPickingRow || !refContext.ok) return Promise.reject(new Error(t('spAdmin.ref.importContextMissing')))
      return getSupplierPreorderImportRefCandidates(
        {
          ...refContext.context,
          name: refPickingRow.name,
          price: refPickingRow.price,
          keyword: keyword ?? null,
          excludeProductCodes,
        },
        signal,
      )
    },
    [excludeProductCodes, refContext, refPickingRow, t],
  )

  // 导入页只记本地：选定 → 记下；清除 → 记「不要参考」；交回系统推荐 → 删掉这行的人工记录
  const handleRefPicked = useCallback(
    (result: RefPickerResult) => {
      if (!refPickingRow) return
      const rowNo = refPickingRow.rowNo
      setRefChoices((current) => {
        const next = { ...current }
        if (result.mode === 'Manual') next[rowNo] = toImportRefChoice(result.candidate)
        else if (result.mode === 'Clear') next[rowNo] = { kind: 'cleared' }
        else delete next[rowNo]
        return next
      })
      setRefPickingRow(null)
    },
    [refPickingRow],
  )

  // ---------- 校对预览表 ----------

  const filteredPreviewItems = useMemo(() => filterPreviewItems(preview?.items ?? [], previewFilter), [preview, previewFilter])

  const previewColumns = useMemo<ColumnsType<SupplierPreorderPreviewItem>>(
    () => [
      { title: t('spAdmin.import.colSeq'), dataIndex: 'seq', width: 64, className: 'sp-admin-number' },
      { title: t('spAdmin.import.colImage'), dataIndex: 'imageUrl', width: 56, render: (value?: string | null) => <ItemThumb url={value} /> },
      { title: t('spAdmin.import.colItemCode'), dataIndex: 'supplierItemCode', width: 120, className: 'sp-admin-mono' },
      { title: t('spAdmin.import.colBarcode'), dataIndex: 'barcode', width: 130, className: 'sp-admin-mono', render: (value?: string | null) => value || '--' },
      { title: t('spAdmin.import.colName'), dataIndex: 'name', width: 260 },
      { title: t('spAdmin.import.colUnit'), dataIndex: 'unit', width: 64, render: (value?: string | null) => value || '--' },
      { title: t('spAdmin.import.colCartonQty'), dataIndex: 'cartonQty', width: 72, align: 'right', className: 'sp-admin-number' },
      { title: t('spAdmin.import.colCbm'), dataIndex: 'cartonCbm', width: 80, align: 'right', className: 'sp-admin-number', render: formatCbm },
      {
        title: t('spAdmin.import.colPrice'),
        dataIndex: 'price',
        width: 90,
        align: 'right',
        className: 'sp-admin-number sp-admin-strong',
        render: formatMoney,
      },
      {
        title: t('spAdmin.import.colMatch'),
        key: 'match',
        width: 140,
        render: (_, row) => (
          <PreviewStatusTag
            status={row.status}
            suffix={row.status === 'Matched' || row.status === 'Warning' ? t(`spAdmin.common.matchMethod.${row.matchMethod}`) : null}
          />
        ),
      },
      {
        title: t('spAdmin.import.colNotes'),
        key: 'notes',
        width: 280,
        render: (_, row) => {
          const lines: string[] = [...row.issues]
          if (isPriceChanged(row) && typeof row.priceChangeRate === 'number') {
            lines.push(
              t('spAdmin.import.priceChange', {
                rate: formatPriceChangeRate(row.priceChangeRate),
                price: formatMoney(row.lastPurchasePrice ?? null),
              }),
            )
          }
          // 只在「有提示」的行列出对应商品，已匹配的正常行不重复显示，避免说明列太吵
          if (row.matchedProductName && row.status === 'Warning') {
            lines.push(t('spAdmin.import.matchedProduct', { name: row.matchedProductName, code: row.matchedItemNumber ?? row.matchedProductCode ?? '' }))
          }
          if (!lines.length) return <span className="sp-admin-muted">--</span>
          return (
            <div className={`sp-admin-cell-stack sp-admin-small${row.status === 'Invalid' ? ' sp-admin-danger' : ''}`}>
              {lines.map((line, index) => (
                <span key={index}>{line}</span>
              ))}
            </div>
          )
        },
      },
      {
        title: t('spAdmin.ref.colRef'),
        key: 'ref',
        width: 260,
        render: (_, row) => {
          if (row.status === 'Invalid') return <span className="sp-admin-faint">—</span>
          const choice = refChoices[row.rowNo]
          const disabledReason = refContext.ok
            ? null
            : refContext.reason === 'supplier'
              ? t('spAdmin.ref.importNeedSupplier')
              : t('spAdmin.ref.importNeedSeason')
          let content
          if (choice?.kind === 'manual') {
            content = (
              <div className="sp-admin-cell-stack">
                <span className="sp-admin-ref-line">
                  <Tag color="purple" className="sp-admin-tag sp-admin-mono">
                    {t('spAdmin.ref.tag', { code: getRefDisplayCode(choice) })}
                  </Tag>
                  <span className="sp-admin-ellipsis sp-admin-small" title={choice.name}>
                    {choice.name}
                  </span>
                </span>
                <span className="sp-admin-muted sp-admin-small">{t('spAdmin.ref.importManual')}</span>
              </div>
            )
          } else if (choice?.kind === 'cleared') {
            content = <span className="sp-admin-muted sp-admin-small">{t('spAdmin.ref.importCleared')}</span>
          } else {
            content = (
              <span className="sp-admin-faint sp-admin-small">{row.status === 'New' ? t('spAdmin.ref.importAutoLater') : '—'}</span>
            )
          }
          return (
            <div className="sp-admin-ref-import-cell">
              {content}
              <Tooltip title={disabledReason}>
                <Button size="small" type="link" disabled={Boolean(disabledReason)} onClick={() => setRefPickingRow(row)}>
                  {t('spAdmin.ref.assign')}
                </Button>
              </Tooltip>
            </div>
          )
        },
      },
    ],
    [refChoices, refContext, t],
  )

  const previewCounts = preview?.counts
  const previewFilterOptions: { value: PreviewFilter; label: string }[] = [
    { value: 'all', label: t('spAdmin.import.filterAll', { count: previewCounts?.total ?? 0 }) },
    { value: 'matched', label: t('spAdmin.import.filterMatched', { count: previewCounts?.matched ?? 0 }) },
    { value: 'new', label: t('spAdmin.import.filterNew', { count: previewCounts?.new ?? 0 }) },
    { value: 'warning', label: t('spAdmin.import.filterWarning', { count: previewCounts?.warning ?? 0 }) },
    { value: 'invalid', label: t('spAdmin.import.filterInvalid', { count: previewCounts?.invalid ?? 0 }) },
    { value: 'priceChanged', label: t('spAdmin.import.filterPriceChanged', { count: previewCounts?.priceChanged ?? 0 }) },
  ]

  const supplierSelect = (
    <Select<string>
      value={supplierCode}
      allowClear
      showSearch
      loading={suppliersLoading}
      optionFilterProp="label"
      placeholder={t('spAdmin.import.supplierPlaceholder')}
      options={suppliers.map((item) => ({ value: item.code, label: `${item.code} · ${item.name}` }))}
      onChange={(value) => setSupplierCode(value)}
      style={{ width: '100%' }}
    />
  )

  const supplierChangedAfterParse = Boolean(parseResult && supplierCode && supplierCode !== parsedWithSupplier)

  // ---------- 渲染 ----------

  const stepItems = [
    { title: t('spAdmin.import.step1') },
    { title: t('spAdmin.import.step2') },
    { title: t('spAdmin.import.step3') },
  ]

  return (
    <PageContainer
      title={t('spAdmin.import.title')}
      extra={
        <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/supplier-preorders')}>
          {t('spAdmin.import.backToList')}
        </Button>
      }
    >
      <div className="sp-admin-card sp-admin-steps-card">
        <Steps
          size="small"
          current={step}
          items={stepItems.map((item, index) => ({ ...item, disabled: !parseResult && index > 0 }))}
          onChange={(next) => {
            // 只能回到前面的步骤；前进必须走「下一步」按钮的校验
            if (next < step) setStep(next)
          }}
        />
      </div>

      {/* 第 1 步：上传或粘贴 */}
      <div style={{ display: step === 0 ? undefined : 'none' }}>
        <Spin spinning={parsing} tip={t('spAdmin.import.parsing')}>
          <div className="sp-admin-card sp-admin-card-pad sp-admin-source-card">
            <h3 className="sp-admin-section-title">{t('spAdmin.import.sourceTitle')}</h3>
            <div className="sp-admin-field">
              <div className="sp-admin-field-label">{t('spAdmin.import.supplier')}</div>
              {supplierSelect}
              <div className="sp-admin-muted sp-admin-small">{t('spAdmin.import.supplierHint')}</div>
            </div>
            <Upload.Dragger
              accept=".xlsx,.xlsm,.xls"
              multiple={false}
              showUploadList={false}
              disabled={parsing}
              beforeUpload={(file) => {
                handleFile(file)
                // 不走 antd 自动上传，由 handleFile 调接口
                return Upload.LIST_IGNORE
              }}
            >
              <p className="ant-upload-drag-icon">
                <InboxOutlined />
              </p>
              <p className="ant-upload-text">{t('spAdmin.import.dropTitle')}</p>
              <p className="ant-upload-hint">{t('spAdmin.import.dropHint')}</p>
            </Upload.Dragger>
            {sourceLabel ? (
              <div className="sp-admin-source-chip">
                <CheckOutlined /> {sourceLabel}
              </div>
            ) : null}
            <div className="sp-admin-paste-head">{t('spAdmin.import.pasteTitle')}</div>
            <Input.TextArea
              className="sp-admin-paste"
              rows={8}
              value={pasteText}
              placeholder={t('spAdmin.import.pastePlaceholder')}
              onChange={(event) => setPasteText(event.target.value)}
            />
            <div className="sp-admin-footer-actions">
              <Button type="primary" disabled={!pasteText.trim() || parsing} onClick={handleParsePaste}>
                {t('spAdmin.import.parsePaste')}
              </Button>
              {parseResult ? <Button onClick={() => setStep(1)}>{t('spAdmin.import.continueStep2')}</Button> : null}
            </div>
          </div>
        </Spin>
      </div>

      {/* 第 2 步：识别列与校对 */}
      {parseResult ? (
        <div className="sp-admin-step" style={{ display: step === 1 ? undefined : 'none' }}>
          <div className="sp-admin-import-grid">
            <div className="sp-admin-card sp-admin-card-pad">
              <h3 className="sp-admin-section-title">{t('spAdmin.import.resultTitle')}</h3>
              <dl className="sp-admin-kv">
                <dt>{t('spAdmin.import.resultSource')}</dt>
                <dd className="sp-admin-ellipsis" title={sourceLabel ?? ''}>{sourceLabel ?? '--'}</dd>
                <dt>{t('spAdmin.import.resultSheet')}</dt>
                <dd>{parseResult.sheetName || '--'}</dd>
                <dt>{t('spAdmin.import.resultHeaderRow')}</dt>
                <dd>{t('spAdmin.import.resultHeaderRowValue', { row: parseResult.headerRowNo })}</dd>
                <dt>{t('spAdmin.import.resultRows')}</dt>
                <dd className="sp-admin-number">{parseResult.rows.length}</dd>
                <dt>{t('spAdmin.import.resultSkipped')}</dt>
                <dd>
                  <span className="sp-admin-number">{parseResult.skippedRowCount}</span>
                  {parseResult.skippedSamples.length ? (
                    <span className="sp-admin-muted sp-admin-small">
                      {' '}
                      {t('spAdmin.import.skippedSamples', { samples: parseResult.skippedSamples.slice(0, 4).join(' / ') })}
                    </span>
                  ) : null}
                </dd>
                <dt>{t('spAdmin.import.resultMapping')}</dt>
                <dd>{parseResult.mappingFromProfile ? t('spAdmin.import.mappingFromProfile') : t('spAdmin.import.mappingGuessed')}</dd>
              </dl>
              <div className="sp-admin-field">
                <div className="sp-admin-field-label">{t('spAdmin.import.supplier')}</div>
                {supplierSelect}
              </div>
              {!supplierCode ? <Alert type="warning" showIcon message={t('spAdmin.import.supplierNeededForPreview')} /> : null}
              {supplierChangedAfterParse ? (
                <Alert
                  type="info"
                  showIcon
                  message={t('spAdmin.import.supplierChanged')}
                  action={
                    <Button size="small" icon={<ReloadOutlined />} loading={parsing} onClick={handleReparseWithSupplier}>
                      {t('spAdmin.import.reparse')}
                    </Button>
                  }
                />
              ) : null}
              {parseResult.warnings.length ? (
                <Alert
                  type="warning"
                  showIcon
                  message={t('spAdmin.import.warningsTitle')}
                  description={
                    <ul className="sp-admin-plain-list">
                      {parseResult.warnings.map((warning, index) => (
                        <li key={index}>{warning}</li>
                      ))}
                    </ul>
                  }
                />
              ) : null}
            </div>

            <div className="sp-admin-card sp-admin-card-pad">
              <div className="sp-admin-section-head">
                <h3 className="sp-admin-section-title">{t('spAdmin.import.mappingTitle')}</h3>
                <span className="sp-admin-muted sp-admin-small">{t('spAdmin.import.mappingHint')}</span>
              </div>
              <div className="sp-admin-mapping-grid">
                {REQUIRED_MAPPING_KEYS.map((key) => renderMappingField(key, true))}
                {OPTIONAL_MAPPING_KEYS.map((key) => renderMappingField(key, false))}
              </div>
              {duplicateColumns.length ? (
                <Alert
                  className="sp-admin-gap-top"
                  type="warning"
                  showIcon
                  message={t('spAdmin.import.duplicateColumns', {
                    detail: duplicateColumns
                      .map((entry) =>
                        t('spAdmin.import.duplicateEntry', {
                          column: getColumnLetter(entry.column),
                          fields: entry.keys.map((key) => t(`spAdmin.mapping.${key}`)).join(t('spAdmin.common.listSep')),
                        }),
                      )
                      .join(t('spAdmin.common.groupSep')),
                  })}
                />
              ) : null}
              <div className="sp-admin-export-unit">
                <span className="sp-admin-field-label">{t('spAdmin.fields.exportQtyUnit')}</span>
                <Radio.Group
                  optionType="button"
                  size="small"
                  value={exportQtyUnit}
                  onChange={(event) => setExportQtyUnit(event.target.value as SupplierPreorderExportQtyUnit)}
                  options={[
                    { value: 'Cartons', label: t('spAdmin.import.exportQtyCartons') },
                    { value: 'Pieces', label: t('spAdmin.import.exportQtyPieces') },
                  ]}
                />
                <span className="sp-admin-muted sp-admin-small">{t('spAdmin.import.exportQtyUnitHint')}</span>
              </div>
            </div>
          </div>

          <div className="sp-admin-card">
            <div className="sp-admin-toolbar">
              <h3 className="sp-admin-section-title">{t('spAdmin.import.previewTitle')}</h3>
              <Segmented<PreviewFilter>
                value={previewFilter}
                options={previewFilterOptions}
                disabled={!preview}
                onChange={(value) => {
                  setPreviewFilter(value)
                  setPreviewPage(1)
                }}
              />
              <span className="sp-admin-toolbar-spacer" />
              <span className="sp-admin-muted sp-admin-small">{t('spAdmin.import.matchOrder')}</span>
            </div>
            {preview ? (
              <div className="sp-admin-ref-hint sp-admin-small">
                <span className="sp-admin-muted">{t('spAdmin.ref.importHint')}</span>
                {refChoiceCount ? <span className="sp-admin-number">{t('spAdmin.ref.importChosenCount', { count: refChoiceCount })}</span> : null}
              </div>
            ) : null}
            {missingMappings.length ? (
              <Alert
                className="sp-admin-inline-alert"
                type="warning"
                showIcon
                message={t('spAdmin.import.mappingMissing', {
                  fields: missingMappings.map((key) => t(`spAdmin.mapping.${key}`)).join(t('spAdmin.common.listSep')),
                })}
              />
            ) : null}
            {previewError ? (
              <Alert
                className="sp-admin-inline-alert"
                type="error"
                showIcon
                message={previewError}
                action={
                  <Button size="small" onClick={() => setPreviewRetry((value) => value + 1)}>
                    {t('spAdmin.common.retry')}
                  </Button>
                }
              />
            ) : null}
            <MeasuredTable<SupplierPreorderPreviewItem>
              metricId="supplier-preorder-import-preview"
              className="sp-admin-table"
              rowKey="rowNo"
              size="small"
              loading={previewLoading}
              columns={previewColumns}
              dataSource={filteredPreviewItems}
              rowClassName={(row) => (row.status === 'Invalid' ? 'sp-admin-row-invalid' : '')}
              scroll={{ x: 1660 }}
              locale={{ emptyText: preview ? t('spAdmin.import.previewEmpty') : t('spAdmin.import.previewWaiting') }}
              pagination={{
                current: previewPage,
                pageSize: previewPageSize,
                showSizeChanger: true,
                pageSizeOptions: PAGE_SIZE_OPTIONS,
                showTotal: (count) => t('spAdmin.import.previewTotal', { count }),
                onChange: (nextPage, nextPageSize) => {
                  if (nextPageSize !== previewPageSize) {
                    setPreviewPageSize(nextPageSize)
                    setPreviewPage(1)
                  } else {
                    setPreviewPage(nextPage)
                  }
                },
              }}
            />
            <RefPickerModal target={refPickerTarget} loader={refLoader} onSubmit={handleRefPicked} onClose={() => setRefPickingRow(null)} />
          </div>

          <div className="sp-admin-card sp-admin-card-pad sp-admin-footer-bar">
            <span className="sp-admin-muted sp-admin-small">
              {preview
                ? t('spAdmin.import.step2Summary', { count: importableCount, invalid: preview.counts.invalid })
                : t('spAdmin.import.step2Waiting')}
            </span>
            <span className="sp-admin-toolbar-spacer" />
            <Button onClick={() => setStep(0)}>{t('spAdmin.import.prev')}</Button>
            <Button type="primary" disabled={!canGoStep3} onClick={() => setStep(2)}>
              {t('spAdmin.import.nextToInfo')}
            </Button>
          </div>
        </div>
      ) : null}

      {/* 第 3 步：订单表信息（表单始终挂载，便于解析后直接回填） */}
      <div style={{ display: step === 2 ? undefined : 'none' }}>
        <div className="sp-admin-card sp-admin-card-pad">
          <div className="sp-admin-section-head">
            <h3 className="sp-admin-section-title">{t('spAdmin.import.infoTitle')}</h3>
            <span className="sp-admin-muted sp-admin-small">{t('spAdmin.import.infoHint')}</span>
          </div>
          <Form<FormHeaderValues> form={form} layout="vertical" requiredMark className="sp-admin-header-form">
            <FormHeaderFields
              form={form}
              supplierName={supplierName}
              leading={
                <Form.Item
                  label={t('spAdmin.import.supplier')}
                  required
                  validateStatus={supplierCode ? undefined : 'error'}
                  help={supplierCode ? undefined : t('spAdmin.import.supplierRequired')}
                >
                  {supplierSelect}
                </Form.Item>
              }
            />
          </Form>
          <div className="sp-admin-footer-bar sp-admin-footer-inner">
            <span className="sp-admin-muted sp-admin-small">
              {preview ? t('spAdmin.import.step3Summary', { count: importableCount, invalid: preview.counts.invalid }) : null}
            </span>
            <span className="sp-admin-toolbar-spacer" />
            <Button onClick={() => setStep(1)}>{t('spAdmin.import.prev')}</Button>
            <Button type="primary" loading={submitting} disabled={!canGoStep3} onClick={() => void handleSubmit()}>
              {t('spAdmin.import.create')}
            </Button>
          </div>
        </div>
      </div>
    </PageContainer>
  )
}
