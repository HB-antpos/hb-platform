import { Alert, Input, Modal, Tag, message } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { importPlan, listPlans } from '../../../../services/planogramService'
import type { PlanogramDetailDto, PlanogramExportDto, PlanogramImportUnresolvedItem } from '../../../../types/planogram'
import { extractImportUnresolved, planogramErrorText, resolvePlanogramError, SEARCH_DEBOUNCE_MS } from '../planogramLogic'
import { groupUnresolved, isDuplicatePlanName, isSupportedFormatVersion, suggestImportName } from '../transferLogic'
import { useDebouncedValue, usePlanogramRequest } from '../usePlanogramRequest'

interface ImportPlanModalProps {
  /** 解析好的导出文件内容。 */
  file: PlanogramExportDto
  /** 用户选的文件名（只用于展示）。 */
  fileName: string
  onClose: () => void
  onImported: (detail: PlanogramDetailDto) => void
}

/** 每组最多列出多少个缺失编码（其余折叠成「等 N 个」）。 */
const MAX_LISTED_CODES = 30

/**
 * 导入方案 JSON：展示文件摘要，方案名称可改；与已有方案重名时要求改名（后端不限制重名，这里避免列表里出现两个一模一样的方案）。
 * 失败按错误码提示：PLANOGRAM_IMPORT_UNRESOLVED 列出缺失的货架 / 挂钩 / 商品编码；PLANOGRAM_VALIDATION 列出字段问题。
 */
export default function ImportPlanModal({ file, fileName, onClose, onImported }: ImportPlanModalProps) {
  const { t, i18n } = useTranslation()
  const [name, setName] = useState(file.plan.name)
  const [touched, setTouched] = useState(false)
  const [busy, setBusy] = useState(false)
  const [unresolved, setUnresolved] = useState<PlanogramImportUnresolvedItem[] | null>(null)
  const [problems, setProblems] = useState<string[]>([])
  const supported = isSupportedFormatVersion(file)

  // 重名检查：按名称关键字查列表（关键字匹配方案名与明细商品，所以再按名称精确比对）
  const trimmed = name.trim()
  const debounced = useDebouncedValue(trimmed, SEARCH_DEBOUNCE_MS)
  const check = usePlanogramRequest(debounced ? `import-name:${debounced}` : null, (signal) => listPlans({ keyword: debounced, page: 1, pageSize: 100 }, signal))
  const existingNames = useMemo(() => (check.data?.items ?? []).map((plan) => plan.name), [check.data])
  const checking = trimmed !== debounced || check.loading
  const duplicate = !checking && isDuplicatePlanName(trimmed, existingNames)

  // 默认名字重名且用户还没动过：给一个不重名的建议（原名 + 「 (导入)」）
  const suggestedRef = useRef(false)
  useEffect(() => {
    if (!duplicate || touched || suggestedRef.current) return
    suggestedRef.current = true
    setName(suggestImportName(file.plan.name, existingNames, t('planogram.transfer.nameSuffix')))
  }, [duplicate, existingNames, file.plan.name, t, touched])

  const submit = async () => {
    if (busy || !trimmed || duplicate || !supported) return
    setBusy(true)
    setUnresolved(null)
    setProblems([])
    try {
      const detail = await importPlan({ ...file, name: trimmed })
      message.success(t('planogram.transfer.imported', { name: detail.header.name }))
      onImported(detail)
    } catch (error) {
      const resolved = resolvePlanogramError(error)
      if (resolved.kind === 'abort') return
      if (resolved.code === 'PLANOGRAM_IMPORT_UNRESOLVED') {
        setUnresolved(extractImportUnresolved(resolved.details))
      } else if (resolved.code === 'PLANOGRAM_VALIDATION' && resolved.fieldErrors.length > 0) {
        setProblems(resolved.fieldErrors.map((entry) => (entry.field ? `${entry.field}: ${entry.message}` : entry.message)))
      } else {
        message.error(planogramErrorText(resolved, i18n.language, (key) => t(key)))
      }
    } finally {
      setBusy(false)
    }
  }

  const groups = unresolved ? groupUnresolved(unresolved) : null
  const kindLabels = [
    { kind: 'shelf' as const, label: t('planogram.transfer.kindShelf') },
    { kind: 'hook' as const, label: t('planogram.transfer.kindHook') },
    { kind: 'product' as const, label: t('planogram.transfer.kindProduct') },
  ]

  return (
    <Modal
      open
      title={t('planogram.transfer.importTitle')}
      okText={t('planogram.transfer.importOk')}
      cancelText={t('planogram.common.cancel')}
      confirmLoading={busy}
      okButtonProps={{ disabled: !trimmed || duplicate || !supported || checking }}
      onOk={() => void submit()}
      onCancel={busy ? undefined : onClose}
      maskClosable={false}
      destroyOnHidden
    >
      {!supported ? <Alert className="planogram-modal-alert" type="error" showIcon message={t('planogram.transfer.formatUnsupported', { version: file.formatVersion })} /> : null}
      <dl className="planogram-import-summary">
        <div><dt>{t('planogram.transfer.summaryFile')}</dt><dd>{fileName}</dd></div>
        <div><dt>{t('planogram.transfer.summaryShelf')}</dt><dd className="planogram-mono">{file.shelfModelCode || '—'}</dd></div>
        <div><dt>{t('planogram.transfer.summaryLayout')}</dt><dd>{t('planogram.transfer.summaryLayoutValue', { bays: file.plan.bayCount, items: file.items.length, gx: file.plan.gapXMm, gy: file.plan.gapYMm })}</dd></div>
      </dl>
      <label className="planogram-import-name-label" htmlFor="planogram-import-name">{t('planogram.plans.newModal.name')}</label>
      <Input
        id="planogram-import-name"
        autoFocus
        value={name}
        maxLength={100}
        status={duplicate ? 'error' : undefined}
        onChange={(event) => {
          setTouched(true)
          setName(event.target.value)
        }}
        onPressEnter={() => void submit()}
      />
      <div className={duplicate ? 'planogram-import-name-note is-error' : 'planogram-import-name-note'} role={duplicate ? 'alert' : undefined}>
        {duplicate ? t('planogram.transfer.nameDuplicate') : t('planogram.transfer.nameHint')}
      </div>
      {groups ? (
        <Alert
          className="planogram-import-unresolved"
          type="error"
          showIcon
          message={t('planogram.transfer.unresolvedTitle', { count: unresolved?.length ?? 0 })}
          description={
            <div>
              {kindLabels.map(({ kind, label }) =>
                groups[kind].length > 0 ? (
                  <div key={kind} className="planogram-import-unresolved-group">
                    <div className="planogram-import-unresolved-label">{`${label} (${groups[kind].length})`}</div>
                    <div className="planogram-import-unresolved-codes">
                      {groups[kind].slice(0, MAX_LISTED_CODES).map((code) => <Tag key={code} className="planogram-mono">{code}</Tag>)}
                      {groups[kind].length > MAX_LISTED_CODES ? <span className="planogram-muted-text planogram-small">{t('planogram.transfer.unresolvedMore', { count: groups[kind].length - MAX_LISTED_CODES })}</span> : null}
                    </div>
                  </div>
                ) : null,
              )}
              <div className="planogram-muted-text planogram-small">{t('planogram.transfer.unresolvedHint')}</div>
            </div>
          }
        />
      ) : null}
      {problems.length > 0 ? (
        <Alert
          className="planogram-import-unresolved"
          type="error"
          showIcon
          message={t('planogram.transfer.validationTitle')}
          description={<ul className="planogram-import-problems">{problems.slice(0, 8).map((problem, index) => <li key={index}>{problem}</li>)}</ul>}
        />
      ) : null}
    </Modal>
  )
}
