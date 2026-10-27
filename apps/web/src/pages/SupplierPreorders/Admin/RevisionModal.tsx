// 上传修订版（供应商发来修订版）：上传或粘贴 → 核对列映射与改动对比 → 二次确认后应用。
// 按货号对齐：已下的箱数保留；修订版里没有的商品标为「已下架」（不删数据）；改价、改箱规的商品高亮。
// 纯逻辑复用导入页的 supplierPreorderAdmin.logic（粘贴拆分、文件类型、列映射）与 revision.logic（计数、筛选、能否应用）。
import { CheckOutlined, DownOutlined, InboxOutlined, RightOutlined } from '@ant-design/icons'
import { Alert, App, Button, Input, Modal, Select, Spin, Steps, Tag, Upload } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  applySupplierPreorderRevision,
  parseSupplierPreorderFile,
  parseSupplierPreorderPasted,
  previewSupplierPreorderRevision,
} from '../../../services/supplierPreorderService'
import type {
  SupplierPreorderColumnMapping,
  SupplierPreorderFormDetail,
  SupplierPreorderParseResult,
  SupplierPreorderRevisionEntry,
  SupplierPreorderRevisionKind,
  SupplierPreorderRevisionPreview,
} from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import {
  buildRevisionCountItems,
  filterRevisionEntries,
  getRevisionApplyBlock,
  OTHER_REVISION_CHANGES,
  truncateList,
  type RevisionEntryFilter,
} from './revision.logic'
import { PriceChange } from './RevisionParts'
import {
  EMPTY_MAPPING,
  OPTIONAL_MAPPING_KEYS,
  REQUIRED_MAPPING_KEYS,
  findDuplicateMappedColumns,
  formatInteger,
  getColumnLetter,
  getErrorText,
  getImportFileKind,
  getMissingRequiredMappings,
  isAbortError,
  normalizeMapping,
  setMappingColumn,
  splitPastedSheet,
  type MappingKey,
} from './supplierPreorderAdmin.logic'

/** 映射改动后等用户停手再请求对比（与导入页一致） */
const PREVIEW_DEBOUNCE_MS = 450
/** 已订分店列最多列几家 */
const ORDERED_STORES_LIMIT = 3
const PREVIEW_PAGE_SIZE = 50

const KIND_COLOR: Record<SupplierPreorderRevisionKind, string> = {
  Added: 'purple',
  Restored: 'purple',
  Removed: 'red',
  Changed: 'orange',
  Unchanged: 'default',
}

interface RevisionModalProps {
  open: boolean
  detail: SupplierPreorderFormDetail
  onClose: () => void
  /** 应用成功：返回新的订单表详情，由页面刷新页头、商品、分店与矩阵 */
  onApplied: (next: SupplierPreorderFormDetail) => void
}

export default function RevisionModal({ open, detail, onClose, onApplied }: RevisionModalProps) {
  const { t } = useTranslation()
  return (
    <Modal
      open={open}
      title={t('spAdmin.revision.title', { title: detail.summary.title })}
      width={1120}
      footer={null}
      destroyOnHidden
      maskClosable={false}
      onCancel={onClose}
    >
      {/* destroyOnHidden：关掉再打开时从第 1 步重新开始 */}
      {open ? <RevisionBody detail={detail} onClose={onClose} onApplied={onApplied} /> : null}
    </Modal>
  )
}

type RevisionSource = { kind: 'file'; file: File } | { kind: 'paste'; rows: string[][] }

function RevisionBody({ detail, onClose, onApplied }: Omit<RevisionModalProps, 'open'>) {
  const { t } = useTranslation()
  const { message, modal } = App.useApp()
  const formGuid = detail.summary.formGuid
  const supplierCode = detail.summary.localSupplierCode

  const [step, setStep] = useState(0)
  const [pasteText, setPasteText] = useState('')
  const [parsing, setParsing] = useState(false)
  const [parseResult, setParseResult] = useState<SupplierPreorderParseResult | null>(null)
  const [sourceLabel, setSourceLabel] = useState<string | null>(null)
  const [mapping, setMapping] = useState<SupplierPreorderColumnMapping>(EMPTY_MAPPING)
  const [showOptional, setShowOptional] = useState(false)
  const [preview, setPreview] = useState<SupplierPreorderRevisionPreview | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [previewRetry, setPreviewRetry] = useState(0)
  const [filter, setFilter] = useState<RevisionEntryFilter>('all')
  const [page, setPage] = useState(1)
  const [applying, setApplying] = useState(false)

  const missingMappings = useMemo(() => getMissingRequiredMappings(mapping), [mapping])
  const duplicateColumns = useMemo(() => findDuplicateMappedColumns(mapping), [mapping])
  const pasted = Boolean(parseResult && !parseResult.sourceFileGuid)

  // 关键逻辑：映射变化后防抖请求对比；新请求发出前取消旧请求，旧响应不会覆盖新结果。必填列没选齐时不请求。
  useEffect(() => {
    if (!parseResult || missingMappings.length > 0) {
      setPreview(null)
      setPreviewError(null)
      setPreviewLoading(false)
      return undefined
    }
    const controller = new AbortController()
    setPreviewLoading(true)
    const timer = window.setTimeout(() => {
      setPreviewError(null)
      previewSupplierPreorderRevision(
        formGuid,
        {
          sourceFileGuid: parseResult.sourceFileGuid ?? null,
          sheetName: parseResult.sheetName,
          headerRowNo: parseResult.headerRowNo,
          headerSignature: parseResult.headerSignature,
          mapping,
          rows: parseResult.rows,
        },
        controller.signal,
      )
        .then((result) => {
          if (controller.signal.aborted) return
          setPreview(result)
          setPage(1)
        })
        .catch((error) => {
          if (controller.signal.aborted || isAbortError(error)) return
          setPreview(null)
          setPreviewError(getErrorText(error, t('spAdmin.revision.previewFailed')))
        })
        .finally(() => {
          if (!controller.signal.aborted) setPreviewLoading(false)
        })
    }, PREVIEW_DEBOUNCE_MS)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [formGuid, mapping, missingMappings.length, parseResult, previewRetry, t])

  const runParse = async (source: RevisionSource, label: string) => {
    setParsing(true)
    try {
      // 用订单表的供应商解析：会套用该供应商记住的列映射
      const result =
        source.kind === 'file' ? await parseSupplierPreorderFile(source.file, supplierCode) : await parseSupplierPreorderPasted(source.rows, supplierCode)
      setParseResult(result)
      setSourceLabel(label)
      const nextMapping = normalizeMapping(result.suggestedMapping)
      setMapping(nextMapping)
      setPreview(null)
      setFilter('all')
      setPage(1)
      // 必填列缺了：直接展开可选列没必要，但要让用户先看到缺的必填列（必填列总是显示）
      setShowOptional(false)
      setStep(1)
      message.success(t('spAdmin.revision.parsed', { count: result.rows.length }))
    } catch (error) {
      message.error(getErrorText(error, t('spAdmin.revision.parseFailed')))
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
    void runParse({ kind: 'file', file }, file.name)
  }

  const handleParsePaste = () => {
    const rows = splitPastedSheet(pasteText)
    if (rows.length < 2) {
      message.warning(t('spAdmin.import.pasteTooShort'))
      return
    }
    void runParse({ kind: 'paste', rows }, t('spAdmin.revision.pasteSourceLabel', { count: rows.length }))
  }

  // ---------- 列映射 ----------

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
  const mappedOptionalCount = OPTIONAL_MAPPING_KEYS.filter((key) => mapping[key] !== null).length

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
          size="small"
          optionFilterProp="label"
          status={missing ? 'error' : duplicated ? 'warning' : undefined}
          placeholder={required ? t('spAdmin.import.mappingRequiredPlaceholder') : t('spAdmin.import.mappingOptionalPlaceholder')}
          options={columnOptions}
          popupMatchSelectWidth={320}
          optionRender={(option) => (
            <div className="sp-admin-mapping-option">
              <div>{option.data.label}</div>
              {option.data.samples.length ? <div className="sp-admin-muted sp-admin-small sp-admin-ellipsis">{option.data.samples.join(' / ')}</div> : null}
            </div>
          )}
          onChange={(value) => setMapping((current) => setMappingColumn(current, key, value))}
          style={{ width: '100%' }}
        />
      </div>
    )
  }

  // ---------- 改动对比表 ----------

  const columns = useMemo<ColumnsType<SupplierPreorderRevisionEntry>>(() => {
    const changeNames = (changes: string[]) => changes.map((change) => t(`spAdmin.revision.change.${change}`)).join(t('spAdmin.common.listSep'))
    return [
      { title: t('spAdmin.import.colItemCode'), dataIndex: 'supplierItemCode', width: 130, className: 'sp-admin-mono' },
      {
        title: t('spAdmin.import.colName'),
        key: 'name',
        width: 280,
        render: (_, row) => <span className="sp-admin-revision-name">{row.name || '--'}</span>,
      },
      {
        title: t('spAdmin.revision.colKind'),
        dataIndex: 'kind',
        width: 104,
        render: (kind: SupplierPreorderRevisionKind) => (
          <Tag color={KIND_COLOR[kind] ?? 'default'} className="sp-admin-tag">
            {t(`spAdmin.revision.kind.${kind}`)}
          </Tag>
        ),
      },
      {
        title: t('spAdmin.revision.colChanges'),
        key: 'changes',
        width: 300,
        render: (_, row) => {
          const lines: ReactNode[] = []
          if (row.changes.includes('Price')) {
            lines.push(
              <span key="price">
                <span className="sp-admin-muted">{t('spAdmin.revision.detailPrice')} </span>
                <PriceChange from={row.oldPrice} to={row.newPrice} />
              </span>,
            )
          }
          if (row.changes.includes('Carton')) {
            lines.push(
              <span key="carton" className="sp-admin-number">
                <span className="sp-admin-muted">{t('spAdmin.revision.detailCarton')} </span>
                {formatInteger(row.oldCartonQty ?? null)} → <span className="sp-admin-strong">{formatInteger(row.newCartonQty ?? null)}</span>
              </span>,
            )
          }
          if (row.changes.includes('Name') && row.oldName) {
            lines.push(
              <span key="name" className="sp-admin-muted sp-admin-ellipsis" title={row.oldName}>
                {t('spAdmin.revision.detailName', { name: row.oldName })}
              </span>,
            )
          }
          const others = OTHER_REVISION_CHANGES.filter((change) => row.changes.includes(change))
          if (others.length) {
            lines.push(
              <span key="other" className="sp-admin-muted">
                {t('spAdmin.revision.detailOther', { fields: changeNames(others) })}
              </span>,
            )
          }
          if (!lines.length) return <span className="sp-admin-faint">—</span>
          return <div className="sp-admin-cell-stack sp-admin-small">{lines}</div>
        },
      },
      {
        title: t('spAdmin.revision.colOrdered'),
        key: 'ordered',
        width: 240,
        render: (_, row) => {
          if (!row.orderedCartons) return <span className="sp-admin-faint">—</span>
          const { shown, rest } = truncateList(row.orderedStores ?? [], ORDERED_STORES_LIMIT)
          // 下架但已有分店下单：红字强调（箱数保留但不计入汇总）
          const danger = row.kind === 'Removed'
          return (
            <div className="sp-admin-cell-stack sp-admin-small">
              <span className={`sp-admin-number ${danger ? 'sp-admin-revision-ordered-danger' : 'sp-admin-strong'}`}>
                {t('spAdmin.revision.orderedCartons', { cartons: formatInteger(row.orderedCartons) })}
              </span>
              {shown.length ? (
                <span className="sp-admin-muted" title={(row.orderedStores ?? []).join(t('spAdmin.common.listSep'))}>
                  {shown.join(t('spAdmin.common.listSep'))}
                  {rest ? ` ${t('spAdmin.revision.orderedMore', { count: rest })}` : ''}
                </span>
              ) : null}
            </div>
          )
        },
      },
    ]
  }, [t])

  const filteredEntries = useMemo(() => filterRevisionEntries(preview?.entries ?? [], filter), [filter, preview])
  const countItems = preview ? buildRevisionCountItems(preview) : []
  const applyBlock = preview ? getRevisionApplyBlock(preview) : null
  const nextRevision = (preview?.currentRevision ?? detail.revision ?? 0) + 1
  const canApply = Boolean(parseResult && preview && !previewLoading && !previewError && missingMappings.length === 0 && applyBlock === null)

  const handleApply = () => {
    if (!parseResult || !preview || !canApply) return
    const { counts } = preview
    modal.confirm({
      title: t('spAdmin.revision.applyConfirmTitle', { next: nextRevision }),
      width: 520,
      content: (
        <div className="sp-admin-cell-stack sp-admin-revision-confirm">
          <span>{t('spAdmin.revision.applyConfirmContent')}</span>
          <span className="sp-admin-number sp-admin-strong">
            {t('spAdmin.revision.applyConfirmCounts', {
              added: counts.added,
              removed: counts.removed,
              restored: counts.restored,
              price: counts.priceChanged,
              carton: counts.cartonChanged,
            })}
          </span>
          {counts.removedWithOrders > 0 ? <span className="sp-admin-danger">{t('spAdmin.revision.applyConfirmRemovedOrdered', { count: counts.removedWithOrders })}</span> : null}
          {pasted ? <span className="sp-admin-urgent">{t('spAdmin.revision.pasteNoFile')}</span> : null}
        </div>
      ),
      okText: t('spAdmin.revision.apply'),
      okButtonProps: counts.removedWithOrders > 0 ? { danger: true } : undefined,
      cancelText: t('spAdmin.common.cancel'),
      onOk: async () => {
        setApplying(true)
        try {
          const next = await applySupplierPreorderRevision(formGuid, {
            sourceFileGuid: parseResult.sourceFileGuid ?? null,
            sheetName: parseResult.sheetName,
            headerRowNo: parseResult.headerRowNo,
            headerSignature: parseResult.headerSignature,
            mapping,
            rows: parseResult.rows,
          })
          message.success(t('spAdmin.revision.applied', { revision: next.revision }))
          onApplied(next)
        } catch (error) {
          message.error(getErrorText(error, t('spAdmin.revision.applyFailed')))
        } finally {
          setApplying(false)
        }
      },
    })
  }

  // ---------- 渲染 ----------

  return (
    <div className="sp-admin-revision">
      <Steps
        size="small"
        current={step}
        items={[{ title: t('spAdmin.revision.step1') }, { title: t('spAdmin.revision.step2'), disabled: !parseResult }]}
        onChange={(next) => {
          if (next === 0 || parseResult) setStep(next)
        }}
      />

      {/* 第 1 步：上传或粘贴 */}
      <div style={{ display: step === 0 ? undefined : 'none' }}>
        <Spin spinning={parsing} tip={t('spAdmin.revision.parsing')}>
          <div className="sp-admin-revision-source">
            <div className="sp-admin-muted sp-admin-small">{t('spAdmin.revision.intro')}</div>
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
              <p className="ant-upload-text">{t('spAdmin.revision.dropTitle')}</p>
              <p className="ant-upload-hint">{t('spAdmin.revision.dropHint')}</p>
            </Upload.Dragger>
            {sourceLabel ? (
              <div className="sp-admin-source-chip">
                <CheckOutlined /> {sourceLabel}
              </div>
            ) : null}
            <div className="sp-admin-paste-head">{t('spAdmin.revision.pasteTitle')}</div>
            <Input.TextArea
              className="sp-admin-paste"
              rows={6}
              value={pasteText}
              placeholder={t('spAdmin.revision.pastePlaceholder')}
              onChange={(event) => setPasteText(event.target.value)}
            />
            <div className="sp-admin-footer-actions">
              <Button type="primary" disabled={!pasteText.trim() || parsing} onClick={handleParsePaste}>
                {t('spAdmin.revision.parsePaste')}
              </Button>
              {parseResult ? <Button onClick={() => setStep(1)}>{t('spAdmin.import.continueStep2')}</Button> : null}
              <span className="sp-admin-toolbar-spacer" />
              <Button onClick={onClose}>{t('spAdmin.common.cancel')}</Button>
            </div>
          </div>
        </Spin>
      </div>

      {/* 第 2 步：列映射 + 改动对比 */}
      {parseResult ? (
        <div className="sp-admin-revision-step" style={{ display: step === 1 ? undefined : 'none' }}>
          <div className="sp-admin-revision-source-line sp-admin-small">
            <span className="sp-admin-source-chip sp-admin-revision-chip-inline">
              <CheckOutlined /> {sourceLabel}
            </span>
            <span className="sp-admin-muted sp-admin-number">
              {t('spAdmin.revision.sourceInfo', { sheet: parseResult.sheetName || '--', row: parseResult.headerRowNo, rows: parseResult.rows.length })}
              {parseResult.skippedRowCount ? ` · ${t('spAdmin.revision.skipped', { count: parseResult.skippedRowCount })}` : ''}
              {' · '}
              {parseResult.mappingFromProfile ? t('spAdmin.import.mappingFromProfile') : t('spAdmin.import.mappingGuessed')}
            </span>
            <Button size="small" type="link" onClick={() => setStep(0)}>
              {t('spAdmin.revision.reselect')}
            </Button>
          </div>

          {pasted ? <Alert type="warning" showIcon message={t('spAdmin.revision.pasteNoFile')} /> : null}
          {parseResult.warnings.length ? <WarningsAlert title={t('spAdmin.import.warningsTitle')} warnings={parseResult.warnings} /> : null}

          <section className="sp-admin-revision-section">
            <div className="sp-admin-section-head">
              <h3 className="sp-admin-section-title">{t('spAdmin.revision.mappingTitle')}</h3>
              <span className="sp-admin-muted sp-admin-small">{t('spAdmin.revision.mappingHint')}</span>
            </div>
            <div className="sp-admin-mapping-grid">{REQUIRED_MAPPING_KEYS.map((key) => renderMappingField(key, true))}</div>
            <Button
              type="link"
              size="small"
              className="sp-admin-revision-optional-toggle"
              icon={showOptional ? <DownOutlined /> : <RightOutlined />}
              aria-expanded={showOptional}
              onClick={() => setShowOptional((value) => !value)}
            >
              {t('spAdmin.revision.optionalToggle', { count: mappedOptionalCount })}
            </Button>
            {showOptional ? <div className="sp-admin-mapping-grid">{OPTIONAL_MAPPING_KEYS.map((key) => renderMappingField(key, false))}</div> : null}
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
            {missingMappings.length ? (
              <Alert
                className="sp-admin-gap-top"
                type="error"
                showIcon
                message={t('spAdmin.import.mappingMissing', {
                  fields: missingMappings.map((key) => t(`spAdmin.mapping.${key}`)).join(t('spAdmin.common.listSep')),
                })}
              />
            ) : null}
          </section>

          <section className="sp-admin-revision-section">
            <div className="sp-admin-section-head">
              <h3 className="sp-admin-section-title">{t('spAdmin.revision.previewTitle')}</h3>
              <span className="sp-admin-muted sp-admin-small sp-admin-number">
                {t('spAdmin.revision.previewVersion', { current: preview?.currentRevision ?? detail.revision ?? 0, next: nextRevision })}
              </span>
            </div>

            {previewError ? (
              <Alert
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

            {preview ? (
              <div className="sp-admin-revision-alerts">
                {preview.counts.removedWithOrders > 0 ? (
                  <Alert
                    type="error"
                    showIcon
                    message={t('spAdmin.revision.removedWithOrders', { count: preview.counts.removedWithOrders })}
                    description={t('spAdmin.revision.removedWithOrdersDesc')}
                    action={
                      <Button
                        size="small"
                        danger
                        onClick={() => {
                          setFilter('removed')
                          setPage(1)
                        }}
                      >
                        {t('spAdmin.revision.filterItem', { label: t('spAdmin.revision.count.removed'), count: preview.counts.removed })}
                      </Button>
                    }
                  />
                ) : null}
                {applyBlock === 'noRows' ? <Alert type="error" showIcon message={t('spAdmin.revision.noRows')} /> : null}
                {applyBlock === 'noChanges' ? <Alert type="info" showIcon message={t('spAdmin.revision.noChanges')} /> : null}
                {preview.counts.invalid > 0 && applyBlock !== 'noRows' ? (
                  <Alert type="warning" showIcon message={t('spAdmin.revision.invalidNote', { count: preview.counts.invalid })} />
                ) : null}
                {preview.warnings.length ? <WarningsAlert title={t('spAdmin.import.warningsTitle')} warnings={preview.warnings} /> : null}
              </div>
            ) : null}

            {/* 计数条：能筛选的项是按钮（再点一次回到全部）；不变 / 不能导入只显示数字 */}
            {preview ? (
              <div className="sp-admin-revision-stats" role="group" aria-label={t('spAdmin.revision.previewTitle')}>
                <button
                  type="button"
                  className={`sp-admin-revision-stat${filter === 'all' ? ' is-active' : ''}`}
                  aria-pressed={filter === 'all'}
                  onClick={() => {
                    setFilter('all')
                    setPage(1)
                  }}
                >
                  <span className="sp-admin-revision-stat-label">{t('spAdmin.revision.statAll')}</span>
                  <span className="sp-admin-revision-stat-value">{formatInteger(preview.entries.length)}</span>
                </button>
                {countItems.map((item) =>
                  item.filter ? (
                    <button
                      key={item.key}
                      type="button"
                      className={`sp-admin-revision-stat is-${item.key}${filter === item.filter ? ' is-active' : ''}${item.count === 0 ? ' is-zero' : ''}`}
                      aria-pressed={filter === item.filter}
                      onClick={() => {
                        const target = item.filter as RevisionEntryFilter
                        setFilter((current) => (current === target ? 'all' : target))
                        setPage(1)
                      }}
                    >
                      <span className="sp-admin-revision-stat-label">{t(`spAdmin.revision.count.${item.key}`)}</span>
                      <span className="sp-admin-revision-stat-value">{formatInteger(item.count)}</span>
                    </button>
                  ) : (
                    <div key={item.key} className={`sp-admin-revision-stat is-static is-${item.key}${item.count === 0 ? ' is-zero' : ''}`}>
                      <span className="sp-admin-revision-stat-label">{t(`spAdmin.revision.count.${item.key}`)}</span>
                      <span className="sp-admin-revision-stat-value">{formatInteger(item.count)}</span>
                    </div>
                  ),
                )}
              </div>
            ) : null}

            <MeasuredTable<SupplierPreorderRevisionEntry>
              metricId="supplier-preorder-revision-preview"
              className="sp-admin-table"
              rowKey={(row) => `${row.kind}|${row.supplierItemCode}|${row.itemGuid ?? ''}|${row.rowNo ?? ''}`}
              size="small"
              loading={previewLoading}
              columns={columns}
              dataSource={filteredEntries}
              rowClassName={(row) => (row.kind === 'Removed' && row.orderedCartons > 0 ? 'sp-admin-row-invalid' : row.kind === 'Removed' ? 'sp-admin-row-removed' : '')}
              scroll={{ x: 1054, y: 360 }}
              locale={{ emptyText: preview ? t('spAdmin.revision.previewEmpty') : t('spAdmin.revision.previewWaiting') }}
              pagination={{
                current: page,
                pageSize: PREVIEW_PAGE_SIZE,
                showSizeChanger: false,
                hideOnSinglePage: true,
                showTotal: (count) => t('spAdmin.revision.previewTotal', { count }),
                onChange: (nextPage) => setPage(nextPage),
              }}
            />
          </section>

          <div className="sp-admin-footer-bar sp-admin-footer-inner">
            <span className="sp-admin-muted sp-admin-small">{t('spAdmin.revision.applyConfirmContent')}</span>
            <span className="sp-admin-toolbar-spacer" />
            <Button onClick={() => setStep(0)}>{t('spAdmin.revision.prev')}</Button>
            <Button onClick={onClose}>{t('spAdmin.common.cancel')}</Button>
            <Button type="primary" loading={applying} disabled={!canApply} onClick={handleApply}>
              {t('spAdmin.revision.apply')}
            </Button>
          </div>
        </div>
      ) : null}
    </div>
  )
}

/** 紧凑的识别 / 对比提示：标题 + 列表放在 message 里（不用 description，避免大图标把表格挤出首屏） */
function WarningsAlert({ title, warnings }: { title: string; warnings: string[] }) {
  return (
    <Alert
      type="warning"
      showIcon
      message={
        <div className="sp-admin-revision-warnings">
          <span className="sp-admin-strong">{title}</span>
          <ul className="sp-admin-plain-list">
            {warnings.map((warning, index) => (
              <li key={index}>{warning}</li>
            ))}
          </ul>
        </div>
      }
    />
  )
}
