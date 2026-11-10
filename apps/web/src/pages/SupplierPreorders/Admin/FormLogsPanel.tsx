import { Input, Segmented } from 'antd'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'

import AuthLogsTable from './AuthLogsTable'
import './registerSpAdminMessages'

type LogView = 'admin' | 'orders'

/**
 * 订单表详情「操作记录」页签：
 * - 管理操作：授权、开关分店、发布 / 截止、修订版、提醒（原来的内容）；
 * - 订货数量：各分店每次订货箱数变化（谁、哪家店、哪个商品、旧 → 新）和提交订单。量大，单独一个视图，可按分店 / 货号 / 账号搜索。
 */
export default function FormLogsPanel({ formGuid, refreshKey }: { formGuid: string; refreshKey: number }) {
  const { t } = useTranslation()
  const [view, setView] = useState<LogView>('admin')
  const [keyword, setKeyword] = useState('')

  return (
    <div className="sp-admin-card">
      <div className="sp-admin-toolbar">
        <Segmented<LogView>
          value={view}
          onChange={setView}
          options={[
            { label: t('spAdmin.logs.viewAdmin'), value: 'admin' },
            { label: t('spAdmin.logs.viewOrders'), value: 'orders' },
          ]}
        />
        <span className="sp-admin-toolbar-spacer" />
        {view === 'orders' ? (
          <Input.Search
            className="sp-admin-search"
            allowClear
            placeholder={t('spAdmin.logs.searchPlaceholder')}
            onSearch={(value) => setKeyword(value.trim())}
          />
        ) : null}
      </div>
      <AuthLogsTable
        formGuid={formGuid}
        scope={view === 'orders' ? 'Order' : undefined}
        keyword={view === 'orders' ? keyword : undefined}
        refreshKey={refreshKey}
      />
    </div>
  )
}
