import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, Space, Typography, theme } from 'antd'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { createPreviewManager, nextPreviewUid, type PreviewManager } from './imageLogic'
import { formatFileSize, pickAcceptableFiles, type ValidationIssue } from './logic'

export interface PhotoItem {
  uid: string
  file: File
}

interface PhotoPickerProps {
  items: PhotoItem[]
  onChange: (items: PhotoItem[]) => void
  max: number
  disabled?: boolean
  /** 提交时的校验问题（来自 logic 的校验结果）。 */
  issue?: ValidationIssue | null
}

/**
 * 待上传照片选择器（登记照 / 名单参考照片，敏感生物识别信息）。
 * 1. 选择时先在前端校验张数、类型（jpeg/png）与大小（≤5MB），不合格的当场提示、不加入；
 * 2. 预览只用 URL.createObjectURL（经登记表）：移除一张立即撤销对应 URL，组件卸载（关闭弹框 / 离开页面）撤销全部；
 * 3. 不转 dataURL、不读取像素、不写任何浏览器存储。
 */
export default function PhotoPicker({ items, onChange, max, disabled, issue }: PhotoPickerProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const inputRef = useRef<HTMLInputElement>(null)
  const managerRef = useRef<PreviewManager | null>(null)
  const itemsRef = useRef(items)
  const [urls, setUrls] = useState<ReadonlyMap<string, string>>(() => new Map())
  const [pickIssue, setPickIssue] = useState<ValidationIssue | null>(null)

  useEffect(() => {
    itemsRef.current = items
  }, [items])

  // 挂载时建立登记表，卸载（含 React 严格模式的重复挂载）时撤销全部 objectURL。
  useEffect(() => {
    const manager = createPreviewManager()
    managerRef.current = manager
    setUrls(manager.sync(itemsRef.current))
    return () => {
      manager.dispose()
      managerRef.current = null
    }
  }, [])

  // 列表变化：新文件创建 URL，移除的文件立即撤销。
  useEffect(() => {
    if (managerRef.current) setUrls(managerRef.current.sync(items))
  }, [items])

  const handleFiles = (list: FileList | null) => {
    const files = list ? Array.from(list) : []
    // 允许下次再选同一个文件。
    if (inputRef.current) inputRef.current.value = ''
    if (files.length === 0) return
    const { accepted, issue: problem } = pickAcceptableFiles(files, items.length, max)
    setPickIssue(problem)
    if (accepted.length > 0) onChange([...items, ...accepted.map((file) => ({ uid: nextPreviewUid(), file }))])
  }

  const shownIssue = pickIssue ?? issue ?? null

  return (
    <Space direction="vertical" size={8} style={{ width: '100%' }}>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 12 }}>
        {items.map((item, index) => {
          const url = urls.get(item.uid)
          return (
            <div key={item.uid} style={{ width: 112 }}>
              <div
                style={{
                  width: 112,
                  height: 112,
                  border: `1px solid ${token.colorBorder}`,
                  borderRadius: token.borderRadius,
                  overflow: 'hidden',
                  background: token.colorFillQuaternary,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                {url ? (
                  <img
                    src={url}
                    alt={t('faceMatch.photos.previewAlt', { index: index + 1 })}
                    draggable={false}
                    style={{ width: '100%', height: '100%', objectFit: 'cover' }}
                  />
                ) : null}
              </div>
              <Space size={4} style={{ width: '100%', justifyContent: 'space-between' }}>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {formatFileSize(item.file.size)}
                </Typography.Text>
                <Button
                  type="text"
                  size="small"
                  danger
                  disabled={disabled}
                  icon={<DeleteOutlined />}
                  aria-label={t('faceMatch.photos.remove')}
                  onClick={() => {
                    setPickIssue(null)
                    onChange(items.filter((candidate) => candidate.uid !== item.uid))
                  }}
                />
              </Space>
            </div>
          )
        })}
        {items.length < max ? (
          <Button
            style={{ width: 112, height: 112, display: 'flex', flexDirection: 'column', gap: 4 }}
            disabled={disabled}
            onClick={() => inputRef.current?.click()}
          >
            <PlusOutlined />
            <span>{t('faceMatch.photos.add')}</span>
          </Button>
        ) : null}
      </div>
      <input
        ref={inputRef}
        type="file"
        accept="image/jpeg,image/png"
        multiple
        hidden
        onChange={(event) => handleFiles(event.target.files)}
      />
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {t('faceMatch.photos.count', { count: items.length, max })}
      </Typography.Text>
      {shownIssue ? <Typography.Text type="danger">{t(shownIssue.key, shownIssue.params)}</Typography.Text> : null}
    </Space>
  )
}
