import { Alert, App, Button, Checkbox, Modal, Select, Spin, Switch, Tag } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { saveSupplierPreorderOrderer, searchSupplierPreorderUserCandidates } from '../../../services/supplierPreorderService'
import type { SupplierPreorderOrderer, SupplierPreorderStoreRef, SupplierPreorderUserCandidate } from '../../../types/supplierPreorder'
import { getErrorText, isAbortError } from '../Admin/supplierPreorderAdmin.logic'

import './registerSpOrderersMessages'
import { formatCandidateLabel, getOrdererDisplayName, mergeStoreSelection, normalizeStoreSelection } from './orderers.logic'

const SEARCH_DEBOUNCE_MS = 300

interface OrdererEditModalProps {
  open: boolean
  /** 编辑某人；null = 添加 */
  editing: SupplierPreorderOrderer | null
  orderers: SupplierPreorderOrderer[]
  stores: SupplierPreorderStoreRef[]
  storesLoading: boolean
  onClose: () => void
  onSaved: (orderer: SupplierPreorderOrderer) => void
}

interface SelectedUser {
  userGuid: string
  label: string
}

/** 添加 / 编辑下单人员：选人（防抖搜索）、勾选可替哪些分店下单、可查看其他分店开关。 */
export default function OrdererEditModal({ open, editing, orderers, stores, storesLoading, onClose, onSaved }: OrdererEditModalProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const [user, setUser] = useState<SelectedUser | null>(null)
  /** 选中的人已在名单：按编辑处理（回填现有授权） */
  const [existing, setExisting] = useState<SupplierPreorderOrderer | null>(null)
  const [storeGuids, setStoreGuids] = useState<string[]>([])
  const [canView, setCanView] = useState(false)
  const [candidates, setCandidates] = useState<SupplierPreorderUserCandidate[]>([])
  const [searching, setSearching] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [saving, setSaving] = useState(false)
  const [switchedToEdit, setSwitchedToEdit] = useState(false)
  const searchAbortRef = useRef<AbortController | null>(null)

  // 每次打开按传入的 editing 重置表单
  useEffect(() => {
    if (!open) return
    setCandidates([])
    setKeyword('')
    setSwitchedToEdit(false)
    if (editing) {
      setUser({ userGuid: editing.userGuid, label: `${getOrdererDisplayName(editing)} (${editing.userName})` })
      setExisting(editing)
      setStoreGuids(editing.stores.map((store) => store.storeGuid))
      setCanView(editing.canViewOtherStores)
    } else {
      setUser(null)
      setExisting(null)
      setStoreGuids([])
      setCanView(false)
    }
  }, [editing, open])

  // 关键逻辑：人员搜索防抖；新关键字发出前取消上一次请求，避免旧结果覆盖
  useEffect(() => {
    if (!open || editing) return undefined
    const text = keyword.trim()
    if (!text) {
      setCandidates([])
      setSearching(false)
      return undefined
    }
    setSearching(true)
    const timer = window.setTimeout(() => {
      searchAbortRef.current?.abort()
      const controller = new AbortController()
      searchAbortRef.current = controller
      searchSupplierPreorderUserCandidates(text, controller.signal)
        .then((result) => {
          if (!controller.signal.aborted) setCandidates(result)
        })
        .catch((error) => {
          if (controller.signal.aborted || isAbortError(error)) return
          message.error(getErrorText(error, t('spOrderers.modal.searchFailed')))
        })
        .finally(() => {
          if (!controller.signal.aborted) setSearching(false)
        })
    }, SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [editing, keyword, message, open, t])

  useEffect(() => () => searchAbortRef.current?.abort(), [])

  const handleSelectUser = (userGuid: string) => {
    const candidate = candidates.find((item) => item.userGuid === userGuid)
    if (!candidate) return
    setUser({ userGuid, label: formatCandidateLabel(candidate) })
    const current = orderers.find((person) => person.userGuid === userGuid) ?? null
    if (candidate.alreadyOrderer || current) {
      // 已在名单：改为编辑，回填现有授权
      setExisting(current)
      setStoreGuids(current ? current.stores.map((store) => store.storeGuid) : [])
      setCanView(current?.canViewOtherStores ?? false)
      setSwitchedToEdit(true)
    } else {
      setExisting(null)
      setSwitchedToEdit(false)
    }
  }

  const selectedActiveCount = normalizeStoreSelection(storeGuids, stores).length
  const allSelected = stores.length > 0 && selectedActiveCount === stores.length

  const handleSave = async () => {
    if (!user) {
      message.warning(t('spOrderers.modal.userRequired'))
      return
    }
    if (selectedActiveCount === 0) {
      message.warning(t('spOrderers.modal.storeRequired'))
      return
    }
    setSaving(true)
    try {
      const saved = await saveSupplierPreorderOrderer(user.userGuid, {
        storeGuids: mergeStoreSelection(storeGuids, stores, existing?.stores.map((store) => store.storeGuid) ?? []),
        canViewOtherStores: canView,
      })
      message.success(t('spOrderers.modal.saved'))
      onSaved(saved)
    } catch (error) {
      message.error(getErrorText(error, t('spOrderers.modal.saveFailed')))
    } finally {
      setSaving(false)
    }
  }

  const candidateOptions = useMemo(
    () =>
      candidates.map((candidate) => ({
        value: candidate.userGuid,
        label: formatCandidateLabel(candidate),
        alreadyOrderer: candidate.alreadyOrderer,
      })),
    [candidates],
  )

  const isEditMode = Boolean(editing || existing)
  const title = isEditMode
    ? t('spOrderers.modal.editTitle', { name: existing ? getOrdererDisplayName(existing) : user?.label ?? '' })
    : t('spOrderers.modal.addTitle')

  return (
    <Modal
      open={open}
      title={title}
      width={560}
      onCancel={onClose}
      destroyOnHidden
      footer={
        <div className="sp-orderers-modal-foot">
          <span className="sp-admin-muted sp-admin-small">{t('spOrderers.modal.footHint')}</span>
          <span className="sp-admin-toolbar-spacer" />
          <Button onClick={onClose}>{t('spOrderers.common.cancel')}</Button>
          <Button type="primary" loading={saving} onClick={() => void handleSave()}>
            {t('spOrderers.common.save')}
          </Button>
        </div>
      }
    >
      <div className="sp-orderers-modal">
        <div className="sp-admin-field">
          <div className="sp-admin-field-label">{t('spOrderers.modal.user')}</div>
          {editing ? (
            <div className="sp-orderers-user-fixed">{user?.label}</div>
          ) : (
            <Select<string>
              showSearch
              filterOption={false}
              value={user?.userGuid}
              placeholder={t('spOrderers.modal.userPlaceholder')}
              notFoundContent={searching ? <Spin size="small" /> : keyword.trim() ? t('spOrderers.modal.noCandidate') : t('spOrderers.modal.typeToSearch')}
              onSearch={setKeyword}
              onChange={handleSelectUser}
              options={candidateOptions}
              labelRender={() => user?.label ?? ''}
              optionRender={(option) => (
                <div className="sp-orderers-candidate">
                  <span className="sp-admin-ellipsis">{option.data.label}</span>
                  {option.data.alreadyOrderer ? (
                    <Tag color="blue" bordered={false}>
                      {t('spOrderers.modal.alreadyOrderer')}
                    </Tag>
                  ) : null}
                </div>
              )}
              style={{ width: '100%' }}
            />
          )}
          {switchedToEdit ? <Alert className="sp-admin-gap-top" type="info" showIcon message={t('spOrderers.modal.switchedToEdit')} /> : null}
        </div>

        <div className="sp-admin-field">
          <div className="sp-orderers-store-head">
            <span className="sp-admin-field-label">
              {t('spOrderers.modal.stores')}
              <span className="sp-admin-muted">{t('spOrderers.modal.selectedCount', { count: selectedActiveCount })}</span>
            </span>
            <span className="sp-admin-toolbar-spacer" />
            <Button size="small" type="link" disabled={!stores.length || allSelected} onClick={() => setStoreGuids(stores.map((store) => store.storeGuid))}>
              {t('spOrderers.modal.selectAll')}
            </Button>
            <Button size="small" type="link" disabled={!selectedActiveCount} onClick={() => setStoreGuids([])}>
              {t('spOrderers.modal.clear')}
            </Button>
          </div>
          <Spin spinning={storesLoading}>
            <Checkbox.Group className="sp-orderers-store-grid" value={storeGuids} onChange={(values) => setStoreGuids(values as string[])}>
              {stores.map((store) => (
                <Checkbox key={store.storeGuid} value={store.storeGuid}>
                  {store.storeName}
                </Checkbox>
              ))}
            </Checkbox.Group>
          </Spin>
        </div>

        <label className="sp-orderers-view-row">
          <span>
            {t('spOrderers.modal.canView')}
            <span className="sp-admin-muted sp-admin-small sp-orderers-view-hint">{t('spOrderers.modal.canViewHint')}</span>
          </span>
          <Switch checked={canView} onChange={setCanView} />
        </label>
      </div>
    </Modal>
  )
}
