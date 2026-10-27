import { CheckOutlined, CloseOutlined, EditOutlined } from '@ant-design/icons'
import { Button, Input, Tooltip, message } from 'antd'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'

import { updateMyobConnectionAbn } from '../../../services/myobService'
import type { MyobConnection } from '../../../types/myob'
import { extractMyobError, formatAbnDisplay, isValidAbnInput, normalizeAbnInput } from './myobLogic'

interface AbnCellProps {
  connection: MyobConnection
  /** 已断开的连接只读。 */
  readOnly?: boolean
  onSaved: (connection: MyobConnection) => void
}

/** ABN 内联编辑：允许输入空格，提交前去空格并校验 11 位数字；空串清除；失败展示后端 message。 */
export default function AbnCell({ connection, readOnly = false, onSaved }: AbnCellProps) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState('')
  const [saving, setSaving] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)

  const startEdit = () => {
    setDraft(formatAbnDisplay(connection.abn))
    setServerError(null)
    setEditing(true)
  }

  const valid = isValidAbnInput(draft)

  const save = async () => {
    if (!valid || saving) {
      return
    }
    const normalized = normalizeAbnInput(draft)
    // 没改动就不请求
    if (normalized === (connection.abn ?? '')) {
      setEditing(false)
      return
    }
    setSaving(true)
    setServerError(null)
    try {
      const updated = await updateMyobConnectionAbn(connection.connectionGuid, normalized)
      onSaved(updated)
      setEditing(false)
      message.success(normalized ? t('myob.abn.saved') : t('myob.abn.cleared'))
    } catch (error) {
      // 失败显示后端 message，留在编辑态方便修改重试
      setServerError(extractMyobError(error, t('myob.abn.saveFailed')).message)
    } finally {
      setSaving(false)
    }
  }

  if (!editing) {
    return (
      <span className="myob-abn-edit">
        {connection.abn ? (
          <span className="myob-mono">{formatAbnDisplay(connection.abn)}</span>
        ) : (
          <span className="myob-muted">{t('myob.abn.empty')}</span>
        )}
        {readOnly ? null : (
          <Tooltip title={t('myob.abn.edit')}>
            <Button
              type="text"
              size="small"
              icon={<EditOutlined />}
              aria-label={t('myob.abn.edit')}
              onClick={startEdit}
            />
          </Tooltip>
        )}
      </span>
    )
  }

  return (
    <div>
      <span className="myob-abn-edit">
        <Input
          autoFocus
          size="small"
          className="myob-mono"
          style={{ width: 168 }}
          value={draft}
          status={valid && !serverError ? undefined : 'error'}
          placeholder={t('myob.abn.placeholder')}
          disabled={saving}
          onChange={(event) => {
            setDraft(event.target.value)
            setServerError(null)
          }}
          onPressEnter={() => void save()}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              setEditing(false)
            }
          }}
        />
        <Button
          type="primary"
          size="small"
          icon={<CheckOutlined />}
          aria-label={t('myob.abn.save')}
          loading={saving}
          disabled={!valid}
          onClick={() => void save()}
        />
        <Button
          size="small"
          icon={<CloseOutlined />}
          aria-label={t('myob.abn.cancel')}
          disabled={saving}
          onClick={() => setEditing(false)}
        />
      </span>
      {!valid ? <span className="myob-abn-error">{t('myob.abn.invalid')}</span> : null}
      {valid && serverError ? <span className="myob-abn-error">{serverError}</span> : null}
    </div>
  )
}
