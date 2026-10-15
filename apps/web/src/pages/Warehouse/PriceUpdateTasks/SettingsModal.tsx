import { Alert, Button, InputNumber, Modal, Space, Spin, Typography, message } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import {
  getStorePriceUpdateTaskSettings,
  updateStorePriceUpdateTaskSettings,
  type StorePriceUpdateTaskSettings,
} from '../../../services/storePriceUpdateTaskService'

import { formatLocalDateTime, isOverdueDaysChanged, resolveInitiatorName, validateOverdueDays } from './logic'

const I18N = 'warehouse.priceUpdateTasks.settings'

interface SettingsModalProps {
  open: boolean
  /** 保存成功后回调：页面据此重新取数，让「超期」统计立刻按新天数重算。 */
  onSaved: () => void
  onClose: () => void
}

function getErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback
}

/** 价格变更任务设置弹窗：目前只能修改「任务超期天数」。 */
export default function SettingsModal({ open, onSaved, onClose }: SettingsModalProps) {
  const { t } = useTranslation()
  const [settings, setSettings] = useState<StorePriceUpdateTaskSettings | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<unknown>(null)
  const [inputValue, setInputValue] = useState<number | null>(null)
  const [saving, setSaving] = useState(false)
  const loadControllerRef = useRef<AbortController | null>(null)

  const load = useCallback(async () => {
    // 重试或关闭弹窗时终止上一次请求，旧请求的结果不得覆盖新状态。
    loadControllerRef.current?.abort()
    const controller = new AbortController()
    loadControllerRef.current = controller
    setLoading(true)
    setLoadError(null)
    try {
      const next = await getStorePriceUpdateTaskSettings({ signal: controller.signal })
      if (controller.signal.aborted) return
      setSettings(next)
      setInputValue(next.overdueDays)
    } catch (error) {
      if (controller.signal.aborted) return
      setLoadError(error)
    } finally {
      // 只有最新一次请求负责收尾 loading，被重试顶替的旧请求不能把它提前关掉。
      if (loadControllerRef.current === controller) setLoading(false)
    }
  }, [])

  // 每次打开都重新读取当前设置，并清掉上一次的输入，避免看到过期的值。
  useEffect(() => {
    if (!open) return undefined
    setSettings(null)
    setInputValue(null)
    void load()
    return () => loadControllerRef.current?.abort()
  }, [load, open])

  const validation = settings
    ? validateOverdueDays(inputValue, { min: settings.minOverdueDays, max: settings.maxOverdueDays })
    : null
  // 只有输入合法且与当前生效值不同才允许保存。
  const canSave = settings !== null && validation?.ok === true && isOverdueDaysChanged(validation.value, settings.overdueDays)

  const handleSave = async () => {
    if (!settings || validation?.ok !== true || !canSave) return
    setSaving(true)
    try {
      await updateStorePriceUpdateTaskSettings(validation.value)
      message.success(t(`${I18N}.saved`))
      onSaved()
      onClose()
    } catch (saveError) {
      // 后端的范围校验等错误信息直接展示给用户，弹窗保持打开以便修改后重试。
      message.error(getErrorMessage(saveError, t(`${I18N}.saveFailed`)))
    } finally {
      setSaving(false)
    }
  }

  const renderBody = () => {
    if (loading) {
      return <div style={{ padding: '32px 0', textAlign: 'center' }}><Spin /></div>
    }
    if (loadError) {
      return (
        <Alert
          type="error"
          showIcon
          message={getErrorMessage(loadError, t(`${I18N}.loadFailed`))}
          action={<Button size="small" onClick={() => void load()}>{t('common.retry', '重试')}</Button>}
        />
      )
    }
    if (!settings) return null
    return (
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>{t(`${I18N}.description`)}</Typography.Paragraph>
        <div>
          <Typography.Text strong>{t(`${I18N}.overdueDaysLabel`)}</Typography.Text>
          <div style={{ marginTop: 8 }}>
            {/* 不给 InputNumber 设 min/max：它对越界的键入值不会触发 onChange，校验与界面显示会对不上；
                范围统一交给 validateOverdueDays 判断并在下方提示。 */}
            <InputNumber
              style={{ width: 160 }}
              step={1}
              value={inputValue}
              status={validation?.ok === false ? 'error' : undefined}
              suffix={t(`${I18N}.daysSuffix`)}
              disabled={saving}
              onChange={(value) => setInputValue(typeof value === 'number' ? value : null)}
              onPressEnter={() => void handleSave()}
            />
          </div>
          {validation?.ok === false ? (
            <Typography.Text type="danger" style={{ display: 'block', marginTop: 4 }}>
              {t(`${I18N}.error.${validation.reason}`, { min: settings.minOverdueDays, max: settings.maxOverdueDays })}
            </Typography.Text>
          ) : null}
        </div>
        <div>
          <Typography.Text type="secondary" style={{ display: 'block' }}>
            {t(`${I18N}.defaultHint`, { days: settings.defaultOverdueDays, min: settings.minOverdueDays, max: settings.maxOverdueDays })}
          </Typography.Text>
          {settings.isCustomized ? (
            <Typography.Text type="secondary" style={{ display: 'block' }}>
              {t(`${I18N}.lastModified`, { name: resolveInitiatorName(settings.updatedBy, t), time: formatLocalDateTime(settings.updatedAtUtc) })}
            </Typography.Text>
          ) : null}
        </div>
      </Space>
    )
  }

  return (
    <Modal
      open={open}
      title={t(`${I18N}.title`)}
      okText={t('common.save', '保存')}
      okButtonProps={{ disabled: !canSave, loading: saving }}
      cancelText={t('common.cancel', '取消')}
      cancelButtonProps={{ disabled: saving }}
      closable={!saving}
      maskClosable={false}
      keyboard={!saving}
      onOk={() => void handleSave()}
      onCancel={onClose}
    >
      {renderBody()}
    </Modal>
  )
}
