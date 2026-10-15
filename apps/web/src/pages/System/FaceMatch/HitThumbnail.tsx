import { PictureOutlined } from '@ant-design/icons'
import { Spin, theme } from 'antd'
import { useTranslation } from 'react-i18next'

import type { HitThumbnailState } from './imageLogic'

interface HitThumbnailProps {
  /** 来自 useHitThumbnails 的状态；undefined 按加载中处理。 */
  state: HitThumbnailState | undefined
}

/**
 * 列表里的命中图缩略图（40×40）：<img src> 永远是会话里的 objectURL，不是后端地址；读不到 / 已过期时显示「命中图」占位。
 * 只用于有名单命中且命中图还在的行，Capture 行的抓拍照片仍不在列表里加载。
 */
export default function HitThumbnail({ state }: HitThumbnailProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const frame = {
    width: 40,
    height: 40,
    borderRadius: token.borderRadius,
    overflow: 'hidden',
    background: token.colorErrorBg,
    color: token.colorError,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: 11,
    lineHeight: 1.2,
    textAlign: 'center',
  } as const
  if (!state || state.status === 'loading') {
    return (
      <div style={frame}>
        <Spin size="small" />
      </div>
    )
  }
  if (state.status === 'none') {
    return (
      <div style={frame} title={t('faceMatch.results.hitThumbUnavailable')}>
        <PictureOutlined style={{ fontSize: 16 }} />
      </div>
    )
  }
  return (
    <div style={frame}>
      <img
        src={state.url}
        alt={t('faceMatch.results.hitThumbAlt')}
        draggable={false}
        style={{ display: 'block', width: 40, height: 40, objectFit: 'cover' }}
      />
    </div>
  )
}
