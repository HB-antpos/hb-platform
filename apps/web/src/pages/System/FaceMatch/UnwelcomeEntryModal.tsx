import { Alert, DatePicker, Form, Input, Modal, Select } from 'antd'
import dayjs from 'dayjs'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'

import { createUnwelcomeEntry } from '../../../services/faceMatchService'
import PhotoPicker, { type PhotoItem } from './PhotoPicker'
import {
  CATEGORY_NOTE_MAX_LENGTH,
  CATEGORY_OTHER,
  DATE_FORMAT,
  EVIDENCE_REF_MAX_LENGTH,
  INCIDENT_CATEGORY_OPTIONS,
  UNWELCOME_MAX_EXPIRY_MONTHS,
  UNWELCOME_PHOTO_MAX,
  UNWELCOME_PHOTO_MIN,
  buildUnwelcomeEntryInput,
  describeFaceMatchError,
  maxExpiryDay,
  minExpiryDay,
  validateUnwelcomeEntryForm,
  type FaceUnwelcomeFormValues,
} from './logic'
import { StoreMultiSelect, type FaceMatchTabContext } from './shared'

interface UnwelcomeEntryModalProps {
  context: FaceMatchTabContext
  onCreated: () => void
  onClose: () => void
}

/**
 * 提交不受欢迎人员名单条目。硬条件（前端先校验，后端强制）：
 * 事件类别是枚举（没有「一般偷窃」）；Other 必须说明且需两位不同批准人；证据编号必填；至少一家生效门店；
 * 到期日必填且不超过 12 个月；参考照片 1–5 张（只用于复核时并排比对）。
 */
export default function UnwelcomeEntryModal({ context, onCreated, onClose }: UnwelcomeEntryModalProps) {
  const { t } = useTranslation()
  const { storeOptions, storeOptionsFailed } = context
  const [values, setValues] = useState<Omit<FaceUnwelcomeFormValues, 'photos'>>({
    incidentCategory: null,
    categoryNote: '',
    evidenceRef: '',
    storeScope: [],
    expiresOn: '',
  })
  const [photos, setPhotos] = useState<PhotoItem[]>([])
  const [showErrors, setShowErrors] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [errorText, setErrorText] = useState<string | null>(null)

  const full: FaceUnwelcomeFormValues = { ...values, photos: photos.map((item) => item.file) }
  const errors = validateUnwelcomeEntryForm(full)
  const patch = (next: Partial<typeof values>) => setValues((current) => ({ ...current, ...next }))
  const show = (field: keyof FaceUnwelcomeFormValues) => {
    const issue = showErrors ? errors[field] : undefined
    return issue ? { validateStatus: 'error' as const, help: t(issue.key, issue.params) } : {}
  }

  const submit = async () => {
    setShowErrors(true)
    if (Object.keys(errors).length > 0) return
    setSubmitting(true)
    setErrorText(null)
    try {
      await createUnwelcomeEntry(buildUnwelcomeEntryInput(full))
      onCreated()
      onClose()
    } catch (error) {
      setErrorText(describeFaceMatchError(t, error))
    } finally {
      setSubmitting(false)
    }
  }

  const earliest = minExpiryDay()
  const latest = maxExpiryDay()

  return (
    <Modal
      open
      title={t('faceMatch.unwelcome.form.title')}
      okText={t('faceMatch.unwelcome.form.submit')}
      cancelText={t('faceMatch.common.cancel')}
      confirmLoading={submitting}
      cancelButtonProps={{ disabled: submitting }}
      maskClosable={false}
      keyboard={!submitting}
      closable={!submitting}
      destroyOnHidden
      width={640}
      onOk={() => void submit()}
      onCancel={() => {
        if (!submitting) onClose()
      }}
    >
      <Alert type="warning" showIcon style={{ marginBottom: 16 }} message={t('faceMatch.unwelcome.form.scopeNotice')} />
      <Form layout="vertical" autoComplete="off" requiredMark="optional">
        <Form.Item label={t('faceMatch.unwelcome.form.category')} required {...show('incidentCategory')}>
          <Select
            value={values.incidentCategory ?? undefined}
            placeholder={t('faceMatch.unwelcome.form.categoryPlaceholder')}
            options={INCIDENT_CATEGORY_OPTIONS.map((option) => ({ value: option.value, label: t(option.labelKey) }))}
            onChange={(value: number) => patch({ incidentCategory: value })}
          />
        </Form.Item>
        {values.incidentCategory === CATEGORY_OTHER ? (
          <>
            <Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('faceMatch.unwelcome.form.otherNeedsTwo')} />
            <Form.Item label={t('faceMatch.unwelcome.form.categoryNote')} required {...show('categoryNote')}>
              <Input.TextArea
                rows={2}
                value={values.categoryNote}
                maxLength={CATEGORY_NOTE_MAX_LENGTH}
                showCount
                onChange={(event) => patch({ categoryNote: event.target.value })}
              />
            </Form.Item>
          </>
        ) : null}
        <Form.Item label={t('faceMatch.unwelcome.form.evidenceRef')} required extra={t('faceMatch.unwelcome.form.evidenceRefHint')} {...show('evidenceRef')}>
          <Input value={values.evidenceRef} maxLength={EVIDENCE_REF_MAX_LENGTH} onChange={(event) => patch({ evidenceRef: event.target.value })} />
        </Form.Item>
        <Form.Item label={t('faceMatch.unwelcome.form.storeScope')} required extra={t('faceMatch.unwelcome.form.storeScopeHint')} {...show('storeScope')}>
          <StoreMultiSelect
            options={storeOptions}
            failed={storeOptionsFailed}
            value={values.storeScope}
            onChange={(next) => patch({ storeScope: next })}
            placeholder={t('faceMatch.unwelcome.form.storeScopePlaceholder')}
          />
        </Form.Item>
        <Form.Item
          label={t('faceMatch.unwelcome.form.expiresOn')}
          required
          extra={t('faceMatch.unwelcome.form.expiresHint', { months: UNWELCOME_MAX_EXPIRY_MONTHS, latest: latest.format(DATE_FORMAT) })}
          {...show('expiresOn')}
        >
          <DatePicker
            style={{ width: '100%' }}
            format={DATE_FORMAT}
            value={values.expiresOn ? dayjs(values.expiresOn, DATE_FORMAT) : null}
            defaultPickerValue={earliest}
            disabledDate={(date) => date.isBefore(earliest) || date.isAfter(latest)}
            onChange={(date) => patch({ expiresOn: date ? date.format(DATE_FORMAT) : '' })}
          />
        </Form.Item>
        <Form.Item
          label={t('faceMatch.unwelcome.form.photos')}
          required
          extra={t('faceMatch.unwelcome.form.photosHint', { min: UNWELCOME_PHOTO_MIN, max: UNWELCOME_PHOTO_MAX })}
        >
          <PhotoPicker items={photos} onChange={setPhotos} max={UNWELCOME_PHOTO_MAX} disabled={submitting} issue={showErrors ? errors.photos : null} />
        </Form.Item>
      </Form>
      {errorText ? <Alert type="error" showIcon message={errorText} /> : null}
    </Modal>
  )
}
