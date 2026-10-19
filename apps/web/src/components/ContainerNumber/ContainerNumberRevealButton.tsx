import { EyeInvisibleOutlined, EyeOutlined } from '@ant-design/icons'
import { Button, Tooltip } from 'antd'
import type { MouseEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useContainerNumberDisplay } from './useContainerNumberDisplay'

interface ContainerNumberRevealButtonProps {
  className?: string
}

/**
 * 货柜编号旁的眼睛按钮：只有「查看完整货柜编号」权限（仓库管理、管理员）才渲染。
 * 点击切换全站货柜编号的完整 / 遮罩显示；阻止冒泡，放在表头里点击不会触发排序。
 */
export default function ContainerNumberRevealButton({ className }: ContainerNumberRevealButtonProps) {
  const { t } = useTranslation()
  const { canReveal, showFull, toggle } = useContainerNumberDisplay()
  if (!canReveal) return null

  const label = showFull ? t('common.hideFullContainerNumber') : t('common.showFullContainerNumber')
  const handleClick = (event: MouseEvent) => {
    event.stopPropagation()
    toggle()
  }

  return (
    <Tooltip title={label}>
      <Button
        type="text"
        size="small"
        className={className}
        aria-label={label}
        aria-pressed={showFull}
        icon={showFull ? <EyeInvisibleOutlined /> : <EyeOutlined />}
        onClick={handleClick}
      />
    </Tooltip>
  )
}
