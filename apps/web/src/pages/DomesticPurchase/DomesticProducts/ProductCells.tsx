import { CopyOutlined } from '@ant-design/icons'
import { Button, Tag, Tooltip } from 'antd'
import { useTranslation } from 'react-i18next'
import { ProductType } from '../../../types/domesticProduct'
import { copyTextToClipboard } from '../../../utils/clipboard'
import './domesticProducts.css'

/** 类型标签：普通（灰）/ 套装（紫）/ 多码（琥珀），与设计稿一致；文案是短词，不带「商品」二字。 */
export function ProductTypeTag({ type }: { type: ProductType }) {
  const { t } = useTranslation()
  if (type === ProductType.SET) {
    return <Tag className="dp-type-tag dp-type-set">{t('domesticProducts.typeShortSet')}</Tag>
  }
  if (type === ProductType.MULTICODE) {
    return <Tag className="dp-type-tag dp-type-multi">{t('domesticProducts.typeShortMulti')}</Tag>
  }
  return <Tag className="dp-type-tag dp-type-normal">{t('domesticProducts.typeShortNormal')}</Tag>
}

/** 状态：圆点 + 文字（启用绿、停用灰），不用整块彩色标签，表格里更安静。 */
export function ProductStatusText({ active }: { active: boolean }) {
  const { t } = useTranslation()
  return (
    <span className={active ? 'dp-status dp-status-on' : 'dp-status'}>
      {active ? t('common.enable', '启用') : t('common.disable', '停用')}
    </span>
  )
}

/** 复制按钮：行内 / 抽屉里共用，点击不能冒泡成「整行点击开详情」。 */
export function CopyButton({ value, label }: { value: string; label: string }) {
  const { t } = useTranslation()
  return (
    <Tooltip title={t('common.copy', '复制')}>
      <Button
        type="text"
        size="small"
        className="dp-copy-btn"
        icon={<CopyOutlined />}
        aria-label={t('common.copyValue', { value: label })}
        onClick={(event) => {
          event.stopPropagation()
          void copyTextToClipboard(value)
        }}
      />
    </Tooltip>
  )
}

/** 等宽可复制文本（货号 / 条码）。文字过长时省略，复制图标始终可见。 */
export function CopyableText({ value, label }: { value?: string; label: string }) {
  if (!value) {
    return <span className="dp-faint">--</span>
  }
  return (
    <span className="dp-copyable">
      <span className="dp-mono" title={value}>
        {value}
      </span>
      <CopyButton value={value} label={label} />
    </span>
  )
}
