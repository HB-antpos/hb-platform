import { SaveOutlined } from '@ant-design/icons'
import { Button } from 'antd'
import type { ReactNode } from 'react'

interface UserDrawerTabFooterProps {
  /** 左侧状态说明；没有未保存修改时可传 null。 */
  status: ReactNode
  discardLabel: string
  saveLabel: string
  saving: boolean
  dirty: boolean
  onDiscard: () => void
  onSave: () => void
}

/**
 * 用户抽屉页签的底部保存栏：放在 users-ws-tab-body 之后，贴抽屉底部（sticky），
 * 左右与正文同为 24px 边距。功能权限与移动端菜单共用同一份直接授权草稿，所以两处都用它。
 */
export default function UserDrawerTabFooter({
  status,
  discardLabel,
  saveLabel,
  saving,
  dirty,
  onDiscard,
  onSave,
}: UserDrawerTabFooterProps) {
  return (
    <div className="users-ws-tab-footer">
      <span className="users-ws-tab-footer-status">{status}</span>
      <Button disabled={!dirty || saving} onClick={onDiscard}>
        {discardLabel}
      </Button>
      <Button type="primary" icon={<SaveOutlined />} loading={saving} disabled={!dirty} onClick={onSave}>
        {saveLabel}
      </Button>
    </div>
  )
}
