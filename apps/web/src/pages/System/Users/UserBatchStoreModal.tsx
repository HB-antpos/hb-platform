import { Alert, Checkbox, Modal, Select, Space, Spin, Typography, message } from 'antd'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { batchManageUserStores } from '../../../services/userService'
import type { BatchUserStoreOperation, BatchUserStoreOperationResult, UserDto } from '../../../types/user'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'

import { countBatchStoreChanges, getServerErrorMessage } from './usersPageLogic'

/** 与后端 BatchUserStoreOperationDto.MaxStoreCount 保持一致。 */
const MAX_STORE_COUNT = 50

interface StoreOption {
  label: string
  value: string
}

interface UserBatchStoreModalProps {
  /** null 表示弹窗关闭 */
  mode: BatchUserStoreOperation | null
  selectedUsers: UserDto[]
  storeOptions: StoreOption[]
  /** 只有管理员可以授予「可管理」关系 */
  canGrantManageable: boolean
  onClose: () => void
  onCompleted: () => void
}

/**
 * 批量添加 / 移除分店：选分店后自动向后端预演（dryRun），按真实数据展示新增、升级、移除与店长身份变化，
 * 确认后再正式提交；后端单事务执行，任一目标不合法整批不写入。
 */
export default function UserBatchStoreModal({
  mode,
  selectedUsers,
  storeOptions,
  canGrantManageable,
  onClose,
  onCompleted,
}: UserBatchStoreModalProps) {
  const { t } = useTranslation()
  const [storeGuids, setStoreGuids] = useState<string[]>([])
  const [asManageable, setAsManageable] = useState(false)
  const [preview, setPreview] = useState<BatchUserStoreOperationResult | null>(null)
  const [previewError, setPreviewError] = useState<string | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  // 用户连续改选分店时，只让最后一次预演结果写入弹窗。
  const previewGuardRef = useRef(createLatestRequestGuard())

  const userGuids = selectedUsers.map((user) => user.userGUID)
  const userKey = userGuids.join(',')
  const storeKey = storeGuids.join(',')

  // 每次打开弹窗都从空白开始，避免沿用上一次的分店与预演。
  useEffect(() => {
    setStoreGuids([])
    setAsManageable(false)
    setPreview(null)
    setPreviewError(null)
    previewGuardRef.current.invalidate()
    setPreviewLoading(false)
  }, [mode])

  useEffect(() => {
    if (!mode || !storeGuids.length) {
      previewGuardRef.current.invalidate()
      setPreview(null)
      setPreviewError(null)
      setPreviewLoading(false)
      return
    }
    // 输入一变就作废旧预演：防抖等待期间不允许拿旧数字提交。
    previewGuardRef.current.invalidate()
    setPreview(null)
    setPreviewError(null)
    // 防抖 300ms：多选时连点几家分店只预演一次。
    const timer = window.setTimeout(() => {
      void runLatestGuardedRequest(
        previewGuardRef.current,
        () => batchManageUserStores({
          userGuids,
          storeGuids,
          operation: mode,
          asManageable: mode === 'add' && asManageable,
          dryRun: true,
        }),
        {
          onStart: () => {
            setPreviewLoading(true)
            setPreviewError(null)
          },
          onSuccess: (result) => setPreview(result),
          onError: (error) => {
            console.error(error)
            setPreview(null)
            setPreviewError(getServerErrorMessage(error, t('system.usersBatchStores.previewFailed', '无法计算影响，请稍后重试')))
          },
          onSettled: () => setPreviewLoading(false),
        },
      )
    }, 300)
    return () => window.clearTimeout(timer)
  }, [mode, storeKey, asManageable, userKey])

  useEffect(() => () => previewGuardRef.current.invalidate(), [])

  const changeCount = countBatchStoreChanges(preview)
  const canSubmit = !!preview && !previewLoading && !previewError && changeCount > 0 && !submitting

  const submit = async () => {
    if (!mode || !canSubmit) return
    setSubmitting(true)
    try {
      const result = await batchManageUserStores({
        userGuids,
        storeGuids,
        operation: mode,
        asManageable: mode === 'add' && asManageable,
      })
      message.success(mode === 'add'
        ? t('system.usersBatchStores.addSuccess', '已新增 {{added}} 条分店关联，升级 {{upgraded}} 条为可管理', {
          added: result.addedCount,
          upgraded: result.upgradedCount,
        })
        : t('system.usersBatchStores.removeSuccess', '已移除 {{count}} 条分店关联', { count: result.removedCount }))
      onCompleted()
    } catch (error) {
      console.error(error)
      // 后端整批回滚：失败时没有任何关联被修改。
      message.error(getServerErrorMessage(error, t('system.usersBatchStores.submitFailed', '批量分店操作失败，所有关联均未修改')))
    } finally {
      setSubmitting(false)
    }
  }

  const renderPreview = () => {
    if (!storeGuids.length) {
      return <Typography.Text type="secondary">{t('system.usersBatchStores.pickStoresFirst', '选择分店后会先预演，显示实际影响')}</Typography.Text>
    }
    if (previewError) {
      return <Alert type="error" showIcon message={previewError} />
    }
    if (!preview) {
      return <Spin size="small" />
    }

    const names = (list: string[]) => list.join(t('system.usersBatch.listSeparator', '、'))
    return (
      <Space direction="vertical" size={8} style={{ width: '100%' }}>
        <Typography.Text>
          {mode === 'add'
            ? t('system.usersBatchStores.addPreview', '将新增 {{added}} 条关联，升级 {{upgraded}} 条为可管理；{{unchanged}} 条已存在，保持不变。', {
              added: preview.addedCount,
              upgraded: preview.upgradedCount,
              unchanged: preview.unchangedCount,
            })
            : t('system.usersBatchStores.removePreview', '将移除 {{removed}} 条关联（其中可管理 {{manageable}} 条）；{{unchanged}} 条本来就没有关联。', {
              removed: preview.removedCount,
              manageable: preview.removedManageableCount,
              unchanged: preview.unchangedCount,
            })}
        </Typography.Text>
        {preview.protectedManageableCount > 0 ? (
          <Alert
            type="info"
            showIcon
            message={t('system.usersBatchStores.protectedHint', '{{count}} 条是可管理关联，只有管理员可以移除，将保持不变。', {
              count: preview.protectedManageableCount,
            })}
          />
        ) : null}
        {preview.usersLosingStoreManagerRole.length ? (
          <Alert
            type="warning"
            showIcon
            message={t('system.usersBatchStores.losingManagerRole', '{{count}} 位将失去全部可管理分店，同时失去店长角色：{{names}}', {
              count: preview.usersLosingStoreManagerRole.length,
              names: names(preview.usersLosingStoreManagerRole),
            })}
          />
        ) : null}
        {preview.usersGainingStoreManagerRole.length ? (
          <Alert
            type="info"
            showIcon
            message={t('system.usersBatchStores.gainingManagerRole', '{{count}} 位将首次获得可管理分店，同时成为店长：{{names}}', {
              count: preview.usersGainingStoreManagerRole.length,
              names: names(preview.usersGainingStoreManagerRole),
            })}
          />
        ) : null}
      </Space>
    )
  }

  return (
    <Modal
      title={mode === 'remove'
        ? t('system.usersBatchStores.removeTitle', '为 {{count}} 个已选账号移除分店', { count: selectedUsers.length })
        : t('system.usersBatchStores.addTitle', '为 {{count}} 个已选账号添加分店', { count: selectedUsers.length })}
      open={mode !== null}
      onCancel={() => { if (!submitting) onClose() }}
      onOk={() => void submit()}
      okText={mode === 'remove'
        ? t('system.usersBatchStores.removeOk', '移除（{{count}}）', { count: changeCount })
        : t('system.usersBatchStores.addOk', '添加（{{count}}）', { count: changeCount })}
      okButtonProps={{ disabled: !canSubmit, danger: mode === 'remove' }}
      cancelText={t('common.cancel', '取消')}
      confirmLoading={submitting}
      maskClosable={!submitting}
      destroyOnHidden
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Select
          mode="multiple"
          allowClear
          showSearch
          optionFilterProp="label"
          maxCount={MAX_STORE_COUNT}
          style={{ width: '100%' }}
          placeholder={t('system.usersBatchStores.selectStores', '选择分店（可多选）')}
          aria-label={t('system.usersBatchStores.selectStores', '选择分店（可多选）')}
          value={storeGuids}
          onChange={setStoreGuids}
          options={storeOptions}
        />
        {mode === 'add' && canGrantManageable ? (
          <Checkbox checked={asManageable} onChange={(event) => setAsManageable(event.target.checked)}>
            {t('system.usersBatchStores.asManageable', '同时设为可管理（店长）；已有普通关联的会升级为可管理')}
          </Checkbox>
        ) : null}
        <div aria-live="polite">{renderPreview()}</div>
      </Space>
    </Modal>
  )
}
