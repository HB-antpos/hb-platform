import { CopyOutlined } from '@ant-design/icons'
import { Button } from 'antd'
import { useTranslation } from 'react-i18next'
import { copyTextToClipboard } from '../../../utils/clipboard'

interface CopyValueButtonProps {
  value?: string
  /** 无障碍名称里带上被复制的内容，读屏时能区分是复制哪一个。 */
  label?: string
}

/** 单元格里的小复制图标：货号、条码、批次号共用。 */
export default function CopyValueButton({ value, label }: CopyValueButtonProps) {
  const { t } = useTranslation()
  if (!value) return null

  return (
    <Button
      type="text"
      size="small"
      className="pc-copy"
      icon={<CopyOutlined />}
      aria-label={label ? `${t('common.copy')} ${label}` : t('common.copy')}
      title={t('common.copy')}
      onClick={(event) => {
        // 行内按钮不应触发整行的点击行为。
        event.stopPropagation()
        void copyTextToClipboard(value, {
          successMessage: t('productCreation.copySuccess', { value }),
          failureMessage: t('productCreation.copyFailed'),
        })
      }}
    />
  )
}
