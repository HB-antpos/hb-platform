import { Tabs } from 'antd'
import { useTranslation } from 'react-i18next'
import type { ModelView } from '../planogramLogic'
import HookModels from './HookModels'
import ShelfModels from './ShelfModels'

interface ModelsTabProps {
  view: ModelView
  onViewChange: (view: ModelView) => void
  /** Planogram.Manage：型号新建 / 编辑 / 复制 / 启停 / 删除。 */
  canManage: boolean
  /** 挂钩型号变化后通知页面刷新挂钩目录。 */
  onHooksChanged: () => void
}

/** 货架与挂钩型号：陈列图的基础资料，页内再分「货架型号 / 挂钩型号」两个子页签。 */
export default function ModelsTab({ view, onViewChange, canManage, onHooksChanged }: ModelsTabProps) {
  const { t } = useTranslation()
  return (
    <div className="planogram-models">
      <p className="planogram-intro">{t('planogram.models.intro')}</p>
      <Tabs
        className="planogram-subtabs"
        activeKey={view}
        onChange={(key) => onViewChange(key as ModelView)}
        items={[
          { key: 'shelf', label: t('planogram.models.shelfTab'), children: <ShelfModels canManage={canManage} /> },
          { key: 'hook', label: t('planogram.models.hookTab'), children: <HookModels canManage={canManage} onChanged={onHooksChanged} /> },
        ]}
      />
    </div>
  )
}
