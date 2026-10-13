import { Alert, Form, Modal, Select } from 'antd'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { createFaceEnrollment } from '../../../services/faceMatchService'
import type { FaceConsent, FaceStaffCandidate } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import PhotoPicker, { type PhotoItem } from './PhotoPicker'
import {
  ENROLLMENT_PHOTO_MAX,
  ENROLLMENT_PHOTO_MIN,
  describeFaceMatchError,
  validatePhotoSet,
} from './logic'

interface EnrollmentModalProps {
  staff: FaceStaffCandidate
  /** 可用的同意（未撤回，新的在前），非空。 */
  consents: FaceConsent[]
  onCreated: () => void
  onClose: () => void
}

/**
 * 登记员工人脸：先选一份有效同意，再上传 3–5 张登记照（前端先校验张数 / 类型 / 大小）。
 * 登记照预览只用 objectURL，关闭弹框即撤销；照片不预览之外的任何用途、不写浏览器存储。
 */
export default function EnrollmentModal({ staff, consents, onCreated, onClose }: EnrollmentModalProps) {
  const { t } = useTranslation()
  const [consentId, setConsentId] = useState(consents[0]?.consentId ?? '')
  const [photos, setPhotos] = useState<PhotoItem[]>([])
  const [showErrors, setShowErrors] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [errorText, setErrorText] = useState<string | null>(null)

  const photoIssue = useMemo(
    () => validatePhotoSet(photos.map((item) => item.file), { min: ENROLLMENT_PHOTO_MIN, max: ENROLLMENT_PHOTO_MAX }),
    [photos],
  )

  const submit = async () => {
    setShowErrors(true)
    if (!consentId || photoIssue) return
    setSubmitting(true)
    setErrorText(null)
    try {
      await createFaceEnrollment({ userGuid: staff.userGuid, consentId, photos: photos.map((item) => item.file) })
      onCreated()
      onClose()
    } catch (error) {
      setErrorText(describeFaceMatchError(t, error))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal
      open
      title={t('faceMatch.enrollment.title', { name: staff.displayName })}
      okText={t('faceMatch.enrollment.submit')}
      cancelText={t('faceMatch.common.cancel')}
      confirmLoading={submitting}
      cancelButtonProps={{ disabled: submitting }}
      maskClosable={false}
      keyboard={!submitting}
      closable={!submitting}
      destroyOnHidden
      width={620}
      onOk={() => void submit()}
      onCancel={() => {
        if (!submitting) onClose()
      }}
    >
      <Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('faceMatch.enrollment.purposeNotice')} />
      <Form layout="vertical" autoComplete="off" requiredMark="optional">
        <Form.Item label={t('faceMatch.enrollment.consent')} required>
          <Select
            value={consentId || undefined}
            onChange={setConsentId}
            options={consents.map((consent) => ({
              value: consent.consentId,
              label: t('faceMatch.enrollment.consentOption', {
                version: consent.consentVersion,
                time: formatDateTime(consent.signedAtUtc),
              }),
            }))}
          />
        </Form.Item>
        <Form.Item label={t('faceMatch.enrollment.photos')} required extra={t('faceMatch.enrollment.photosHint', { min: ENROLLMENT_PHOTO_MIN, max: ENROLLMENT_PHOTO_MAX })}>
          <PhotoPicker items={photos} onChange={setPhotos} max={ENROLLMENT_PHOTO_MAX} disabled={submitting} issue={showErrors ? photoIssue : null} />
        </Form.Item>
      </Form>
      {errorText ? <Alert type="error" showIcon message={errorText} /> : null}
    </Modal>
  )
}
