import { Alert, Button, Modal, Segmented, Space, Spin, Tag, Typography, message, theme } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { fetchUnwelcomeReferenceImage, fetchUnwelcomeTaskImage, reviewUnwelcomeTask } from '../../../services/faceMatchService'
import type { FaceReviewVerdict, FaceUnwelcomeTask } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import {
  CAPTURE_SLOT,
  REVIEW_IMAGE_ERROR_KEYS,
  createReviewImageSession,
  referenceSlot,
  type ReviewImageSession,
  type ReviewImageState,
  type ReviewImageStates,
} from './imageLogic'
import {
  BAND_COLORS,
  BAND_LABEL_KEYS,
  REVIEW_STEP_HINT_KEYS,
  describeFaceMatchError,
  formatSimilarityPercent,
  getReviewStep,
  getVerdictBlockReasonKey,
  referenceIndexes,
} from './logic'

interface ReviewTaskModalProps {
  /** 要复核的任务快照。父组件只在点击「复核」后才挂载本组件，并以 taskId 作 key：切换任务即重新挂载。 */
  task: FaceUnwelcomeTask
  /** 功能未开启（只读）时不允许提交判断。 */
  writeBlocked: boolean
  onReviewed: () => void
  onClose: () => void
}

interface ReviewImageViewProps {
  state: ReviewImageState | undefined
  alt: string
  onBroken: () => void
  broken: boolean
}

/** 单张复核图片：<img src> 永远是 state 里的 objectURL，不是后端地址。 */
function ReviewImageView({ state, alt, onBroken, broken }: ReviewImageViewProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  if (!state || state.status === 'loading') {
    return (
      <div style={{ minHeight: 240, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <Spin tip={t('faceMatch.review.loadingImage')}>
          <div style={{ padding: 24 }} />
        </Spin>
      </div>
    )
  }
  if (state.status === 'error' || broken) {
    const key = state.status === 'error' ? REVIEW_IMAGE_ERROR_KEYS[state.kind] : REVIEW_IMAGE_ERROR_KEYS.failed
    return <Alert type="error" showIcon message={t(key)} />
  }
  return (
    <div style={{ background: token.colorFillQuaternary, borderRadius: token.borderRadius, lineHeight: 0 }}>
      <img
        src={state.url}
        alt={alt}
        draggable={false}
        style={{ display: 'block', width: '100%', maxHeight: '44vh', objectFit: 'contain' }}
        onError={onBroken}
      />
    </div>
  )
}

/**
 * 复核弹框（敏感生物识别信息）：并排显示现场抓拍与条目参考照片，由一位有复核权限的人判断一次即结束。
 * 约定（由 sourceContract / imageLogic 测试守住）：
 * 1. 挂载（用户点击「复核」）后才请求图片：先现场抓拍与第一张参考照片，其余参考照片切换时才取；
 * 2. 图片只以 Blob → objectURL 的形式存在于 state 里；关闭 / 切换 / 卸载时 effect 清理函数调用 session.dispose()；
 * 3. 不显示姓名与事件细节；相似度只写「相似度 NN%（不等于是同一个人的概率）」，档位用后端 band 字段，页面不自己算阈值；
 * 4. 按钮是否可点只看后端 canReview（外加图片还在、任务未结束），页面不自己推算「是不是复核过的人」；
 *    nextStep 为 first 或旧版遗留的 second 都显示「待复核」。
 */
export default function ReviewTaskModal({ task, writeBlocked, onReviewed, onClose }: ReviewTaskModalProps) {
  const { t } = useTranslation()
  const [modal, modalContextHolder] = Modal.useModal()
  const [states, setStates] = useState<ReviewImageStates>(() => new Map())
  // 字节已拿到但浏览器解码失败（内容损坏）的图片槽位。
  const [broken, setBroken] = useState<ReadonlySet<string>>(() => new Set())
  const [referenceIndex, setReferenceIndex] = useState(0)
  const [submitting, setSubmitting] = useState(false)
  const [errorText, setErrorText] = useState<string | null>(null)
  const sessionRef = useRef<ReviewImageSession | null>(null)

  const taskId = task.taskId
  const referenceCount = task.referencePhotoCount
  const imageAvailable = task.imageAvailable

  useEffect(() => {
    // 命中图已按保留期删除时没有可看的图，不发任何请求。
    if (!imageAvailable) return
    const session = createReviewImageSession({
      referenceCount,
      fetchCapture: (signal) => fetchUnwelcomeTaskImage(taskId, signal),
      fetchReference: (index, signal) => fetchUnwelcomeReferenceImage(taskId, index, signal),
      onChange: setStates,
    })
    sessionRef.current = session
    void session.start()
    // 关闭 / 切换任务 / 卸载（含 React 严格模式的重复挂载）：中止在途请求并 revoke 全部 objectURL。
    return () => {
      session.dispose()
      sessionRef.current = null
    }
  }, [taskId, referenceCount, imageAvailable])

  const markBroken = (slot: string) => setBroken((current) => new Set(current).add(slot))

  const selectReference = (index: number) => {
    setReferenceIndex(index)
    void sessionRef.current?.loadReference(index)
  }

  const percent = formatSimilarityPercent(task)
  const step = getReviewStep(task)
  const blockedKey = getVerdictBlockReasonKey(task)
  const verdictDisabled = blockedKey !== null || writeBlocked || submitting

  const submit = async (verdict: FaceReviewVerdict) => {
    setSubmitting(true)
    setErrorText(null)
    try {
      await reviewUnwelcomeTask(taskId, verdict)
      void message.success(verdict === 'match' ? t('faceMatch.review.submittedMatch') : t('faceMatch.review.submittedNotMatch'))
      onReviewed()
      onClose()
    } catch (error) {
      setErrorText(describeFaceMatchError(t, error))
      throw error
    } finally {
      setSubmitting(false)
    }
  }

  const confirmVerdict = (verdict: FaceReviewVerdict) => {
    modal.confirm({
      title: verdict === 'match' ? t('faceMatch.review.confirmMatch.title') : t('faceMatch.review.confirmNotMatch.title'),
      content: verdict === 'match' ? t('faceMatch.review.confirmMatch.content', { percent }) : t('faceMatch.review.confirmNotMatch.content'),
      okText: t('faceMatch.review.confirmOk'),
      cancelText: t('faceMatch.common.cancel'),
      onOk: () => submit(verdict).catch(() => undefined),
    })
  }

  const indexes = useMemo(() => referenceIndexes(task), [task])

  return (
    <Modal
      open
      width="min(1100px, 96vw)"
      title={t('faceMatch.review.modalTitle')}
      destroyOnHidden
      maskClosable={false}
      keyboard={!submitting}
      closable={!submitting}
      onCancel={() => {
        if (!submitting) onClose()
      }}
      styles={{ body: { maxHeight: '74vh', overflowY: 'auto' } }}
      footer={
        <Space wrap>
          <Button disabled={submitting} onClick={onClose}>
            {t('faceMatch.review.close')}
          </Button>
          <Button disabled={verdictDisabled} onClick={() => confirmVerdict('notMatch')}>
            {t('faceMatch.review.verdict.notMatch')}
          </Button>
          <Button type="primary" disabled={verdictDisabled} loading={submitting} onClick={() => confirmVerdict('match')}>
            {t('faceMatch.review.verdict.match')}
          </Button>
        </Space>
      }
    >
      {modalContextHolder}
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Alert type="warning" showIcon message={t('faceMatch.review.suspectedNotice', { percent })} />
        <Alert type="info" showIcon message={t('faceMatch.review.sensitiveNotice')} />
        <Space wrap size={8}>
          <Typography.Text strong>{t('faceMatch.review.similarity', { percent })}</Typography.Text>
          <Tag color={BAND_COLORS[task.band]}>{t(BAND_LABEL_KEYS[task.band])}</Tag>
          <Typography.Text type="secondary">{t('faceMatch.review.notProbability')}</Typography.Text>
        </Space>

        {imageAvailable ? (
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: 16 }}>
            <section>
              <Typography.Title level={5} style={{ marginTop: 0 }}>
                {t('faceMatch.review.captureTitle')}
              </Typography.Title>
              <ReviewImageView
                state={states.get(CAPTURE_SLOT)}
                alt={t('faceMatch.review.captureTitle')}
                broken={broken.has(CAPTURE_SLOT)}
                onBroken={() => markBroken(CAPTURE_SLOT)}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('faceMatch.review.captureMeta', {
                  store: `${task.storeCode}${task.storeName ? ` · ${task.storeName}` : ''}`,
                  time: formatDateTime(task.capturedAtUtc),
                })}
              </Typography.Text>
            </section>
            <section>
              <Space style={{ width: '100%', justifyContent: 'space-between' }} wrap>
                <Typography.Title level={5} style={{ marginTop: 0 }}>
                  {t('faceMatch.review.referenceTitle')}
                </Typography.Title>
                {indexes.length > 1 ? (
                  <Segmented
                    size="small"
                    value={referenceIndex}
                    options={indexes.map((index) => ({ value: index, label: t('faceMatch.review.referenceIndex', { index: index + 1 }) }))}
                    onChange={(value) => selectReference(Number(value))}
                  />
                ) : null}
              </Space>
              {indexes.length === 0 ? (
                <Alert type="warning" showIcon message={t('faceMatch.review.noReference')} />
              ) : (
                <ReviewImageView
                  // 切换参考照片时换 key：旧图卸载，不残留上一张的解码失败状态。
                  key={referenceSlot(referenceIndex)}
                  state={states.get(referenceSlot(referenceIndex))}
                  alt={t('faceMatch.review.referenceTitle')}
                  broken={broken.has(referenceSlot(referenceIndex))}
                  onBroken={() => markBroken(referenceSlot(referenceIndex))}
                />
              )}
            </section>
          </div>
        ) : (
          <Alert type="warning" showIcon message={t('faceMatch.review.imageGone')} />
        )}

        <Alert type={step === 'done' ? 'success' : 'info'} showIcon message={t(REVIEW_STEP_HINT_KEYS[step])} />
        {blockedKey && step !== 'done' ? <Typography.Text type="warning">{t(blockedKey)}</Typography.Text> : null}
        {errorText ? <Alert type="error" showIcon message={errorText} /> : null}
      </Space>
    </Modal>
  )
}
