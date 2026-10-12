import { Alert, Button, Modal, Space, Spin, Tag, Typography, theme } from 'antd'
import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { fetchFaceCaptureImage } from '../../../services/faceCameraService'
import type { FaceCapture, FaceCaptureImageMeta } from '../../../types/faceCamera'
import {
  FACE_IMAGE_ERROR_MESSAGE_KEYS,
  collectFaceBoxes,
  createImageSession,
  groupImages,
  pickReferenceSize,
  shortCaptureId,
  type ImageLoadState,
  type ImageLoadStates,
  type ImageSize,
} from './imageLogic'

interface FaceImageViewerModalProps {
  cameraId: string
  /** 要查看的抓拍。父组件只在点击「查看图片」后才挂载本组件，并以 captureId 作 key：切换抓拍即重新挂载。 */
  capture: FaceCapture
  onClose: () => void
}

/**
 * 抓拍图片查看弹框（敏感生物识别信息）。
 * 约定（由 sourceContract / imageLogic 测试守住）：
 * 1. 挂载（用户点击「查看图片」）后才逐张请求图片，没有预加载、没有缩略图列表；
 * 2. 图片只以 Blob → objectURL 的形式存在于 state 里，<img src> 用的永远是 objectURL，不是后端地址；
 * 3. 关闭 / 切换行 / 卸载时 effect 清理函数调用 session.dispose()：中止在途请求、revoke 全部 objectURL、清空引用；
 * 4. 不写任何浏览器存储、地址栏、控制台。
 */
export default function FaceImageViewerModal({ cameraId, capture, onClose }: FaceImageViewerModalProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const [states, setStates] = useState<ImageLoadStates>(() => new Map())
  // 图片字节已拿到但浏览器解码失败（内容损坏）的图片编号。
  const [broken, setBroken] = useState<ReadonlySet<string>>(() => new Set())
  // 浏览器报告的图片自然尺寸：后端没有记录背景图宽高、人脸框又是像素坐标时，用它做换算分母。
  const [naturalSizes, setNaturalSizes] = useState<Readonly<Record<string, ImageSize>>>({})
  const groups = useMemo(() => groupImages(capture.images), [capture.images])

  useEffect(() => {
    const session = createImageSession({
      images: capture.images,
      fetchImage: (image, signal) => fetchFaceCaptureImage(cameraId, capture.captureId, image.imageId, signal),
      onChange: setStates,
    })
    void session.start()
    // 关闭 / 切换行 / 卸载：中止在途请求并 revoke 全部 objectURL（React 严格模式下的重复挂载同样走这里）。
    return () => session.dispose()
  }, [cameraId, capture])

  function renderState(image: FaceCaptureImageMeta, state: ImageLoadState | undefined, label: string, overlay?: ReactNode) {
    if (!state || state.status === 'loading') {
      return (
        <div style={{ minHeight: 120, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <Spin tip={t('faceCameras.images.loading')}>
            <div style={{ padding: 24 }} />
          </Spin>
        </div>
      )
    }
    if (state.status === 'local') {
      return (
        <Space direction="vertical" size={4}>
          <Tag>{t('faceCameras.images.local')}</Tag>
          <Typography.Text type="secondary">{t('faceCameras.images.localTip')}</Typography.Text>
        </Space>
      )
    }
    if (state.status === 'error' || broken.has(image.imageId)) {
      const kind = state.status === 'error' ? state.kind : 'failed'
      return <Alert type={kind === 'local' ? 'info' : 'error'} showIcon message={t(FACE_IMAGE_ERROR_MESSAGE_KEYS[kind])} />
    }
    return (
      <div style={{ position: 'relative', lineHeight: 0 }}>
        <img
          src={state.url}
          alt={label}
          draggable={false}
          style={{ display: 'block', width: '100%', height: 'auto' }}
          onLoad={(event) => {
            const { naturalWidth, naturalHeight } = event.currentTarget
            setNaturalSizes((current) => ({ ...current, [image.imageId]: { width: naturalWidth, height: naturalHeight } }))
          }}
          onError={() => setBroken((current) => new Set(current).add(image.imageId))}
        />
        {overlay}
      </div>
    )
  }

  // 人脸框用 CSS 绝对定位 + 百分比画在背景图上（不用 canvas，不读取像素）。
  function renderBoxes(background: FaceCaptureImageMeta) {
    const reference = pickReferenceSize(background, naturalSizes[background.imageId])
    return collectFaceBoxes(capture.images, reference).map((box) => (
      <div
        key={box.key}
        style={{
          position: 'absolute',
          ...box.style,
          boxSizing: 'border-box',
          border: `2px solid ${token.colorSuccess}`,
          borderRadius: 2,
          pointerEvents: 'none',
        }}
      >
        <span
          style={{
            position: 'absolute',
            left: -2,
            top: -2,
            padding: '0 4px',
            fontSize: 12,
            lineHeight: '16px',
            color: token.colorTextLightSolid,
            background: token.colorSuccess,
          }}
        >
          {box.label}
        </span>
      </div>
    ))
  }

  return (
    <Modal
      open
      width="min(1000px, 96vw)"
      title={t('faceCameras.images.title', { captureId: shortCaptureId(capture.captureId) })}
      destroyOnHidden
      onCancel={onClose}
      styles={{ body: { maxHeight: '72vh', overflowY: 'auto' } }}
      footer={<Button onClick={onClose}>{t('faceCameras.images.close')}</Button>}
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Alert type="warning" showIcon message={t('faceCameras.images.sensitiveNotice')} />
        {groups.backgrounds.length > 0 ? (
          <section>
            <Typography.Title level={5} style={{ marginTop: 0 }}>
              {t('faceCameras.images.background')}
            </Typography.Title>
            <Space direction="vertical" size={12} style={{ width: '100%' }}>
              {groups.backgrounds.map((image) => (
                <div key={image.imageId}>
                  {renderState(image, states.get(image.imageId), t('faceCameras.images.background'), renderBoxes(image))}
                </div>
              ))}
            </Space>
          </section>
        ) : null}
        {groups.faces.length > 0 ? (
          <section>
            <Typography.Title level={5} style={{ marginTop: 0 }}>
              {t('faceCameras.images.faces')}
            </Typography.Title>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))', gap: 12 }}>
              {groups.faces.map((image) => (
                <div key={image.imageId}>
                  {renderState(image, states.get(image.imageId), t('faceCameras.images.faces'))}
                  <Space size={4} wrap style={{ marginTop: 4 }}>
                    <Typography.Text strong>{t('faceCameras.images.faceTitle', { index: (image.faceIndex ?? 0) + 1 })}</Typography.Text>
                    {image.faceScore !== null ? (
                      <Typography.Text type="secondary">{t('faceCameras.images.score', { score: image.faceScore.toFixed(2) })}</Typography.Text>
                    ) : null}
                  </Space>
                </div>
              ))}
            </div>
          </section>
        ) : null}
        {groups.others.length > 0 ? (
          <section>
            <Typography.Title level={5} style={{ marginTop: 0 }}>
              {t('faceCameras.images.others')}
            </Typography.Title>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))', gap: 12 }}>
              {groups.others.map((image) => (
                <div key={image.imageId}>{renderState(image, states.get(image.imageId), t('faceCameras.images.others'))}</div>
              ))}
            </div>
          </section>
        ) : null}
      </Space>
    </Modal>
  )
}
