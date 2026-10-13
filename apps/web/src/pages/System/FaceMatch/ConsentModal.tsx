import { DatePicker, Form, Input, Modal, Radio, Space, Typography, Alert, Button } from 'antd'
import dayjs from 'dayjs'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { createFaceConsent } from '../../../services/faceMatchService'
import type { FaceConsentMethod, FaceStaffCandidate } from '../../../types/faceMatch'
import {
  CONSENT_METHOD_OPTIONS,
  CONSENT_SCAN_RULE,
  CONSENT_VERSION_MAX_LENGTH,
  DATE_FORMAT,
  NSW_NOTICE_MIN_DAYS,
  buildConsentInput,
  describeFaceMatchError,
  formatFileSize,
  validateConsentForm,
  validateFile,
  type FaceConsentFormValues,
  type ValidationIssue,
} from './logic'

interface ConsentModalProps {
  staff: FaceStaffCandidate
  onCreated: () => void
  onClose: () => void
}

/**
 * 记录员工同意：同意书扫描件 + 同意版本 + 签署时间 + 方式；NSW 门店员工另需书面通知日期且须满 14 天。
 * 父组件按需挂载（挂载即打开，关闭即卸载）；选中的扫描件只在本组件内存里，不预览、不写任何存储。
 */
export default function ConsentModal({ staff, onCreated, onClose }: ConsentModalProps) {
  const { t } = useTranslation()
  const inputRef = useRef<HTMLInputElement>(null)
  const [values, setValues] = useState<FaceConsentFormValues>(() => ({
    consentVersion: '',
    signedAtUtc: new Date().toISOString(),
    method: 'paper',
    nswNoticeDate: '',
    scan: null,
  }))
  const [showErrors, setShowErrors] = useState(false)
  const [scanIssue, setScanIssue] = useState<ValidationIssue | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [errorText, setErrorText] = useState<string | null>(null)

  const errors = validateConsentForm(values, staff)
  const show = (field: keyof FaceConsentFormValues) => {
    const issue = showErrors ? errors[field] : undefined
    return issue ? { validateStatus: 'error' as const, help: t(issue.key, issue.params) } : {}
  }

  const patch = (next: Partial<FaceConsentFormValues>) => setValues((current) => ({ ...current, ...next }))

  const pickScan = (list: FileList | null) => {
    const file = list && list.length > 0 ? list[0] : null
    if (inputRef.current) inputRef.current.value = ''
    if (!file) return
    const issue = validateFile(file, CONSENT_SCAN_RULE)
    setScanIssue(issue)
    if (!issue) patch({ scan: file })
  }

  const submit = async () => {
    setShowErrors(true)
    if (Object.keys(errors).length > 0) return
    setSubmitting(true)
    setErrorText(null)
    try {
      await createFaceConsent(buildConsentInput(staff.userGuid, values, staff))
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
      title={t('faceMatch.consent.title', { name: staff.displayName })}
      okText={t('faceMatch.consent.submit')}
      cancelText={t('faceMatch.common.cancel')}
      confirmLoading={submitting}
      cancelButtonProps={{ disabled: submitting }}
      maskClosable={false}
      keyboard={!submitting}
      closable={!submitting}
      destroyOnHidden
      width={560}
      onOk={() => void submit()}
      onCancel={() => {
        if (!submitting) onClose()
      }}
    >
      <Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('faceMatch.consent.purposeNotice')} />
      <Form layout="vertical" autoComplete="off" requiredMark="optional">
        <Form.Item label={t('faceMatch.consent.version')} required extra={t('faceMatch.consent.versionHint')} {...show('consentVersion')}>
          <Input
            value={values.consentVersion}
            maxLength={CONSENT_VERSION_MAX_LENGTH}
            onChange={(event) => patch({ consentVersion: event.target.value })}
          />
        </Form.Item>
        <Form.Item label={t('faceMatch.consent.signedAt')} required {...show('signedAtUtc')}>
          <DatePicker
            showTime
            allowClear={false}
            style={{ width: '100%' }}
            format="YYYY-MM-DD HH:mm"
            value={values.signedAtUtc ? dayjs(values.signedAtUtc) : null}
            disabledDate={(date) => date.isAfter(dayjs(), 'day')}
            onChange={(date) => patch({ signedAtUtc: date ? date.toDate().toISOString() : '' })}
          />
        </Form.Item>
        <Form.Item label={t('faceMatch.consent.methodLabel')} required {...show('method')}>
          <Radio.Group
            value={values.method}
            onChange={(event) => patch({ method: event.target.value as FaceConsentMethod })}
          >
            {CONSENT_METHOD_OPTIONS.map((option) => (
              <Radio key={option.value} value={option.value}>
                {t(option.labelKey)}
              </Radio>
            ))}
          </Radio.Group>
        </Form.Item>
        {staff.requiresNswNotice ? (
          <Form.Item
            label={t('faceMatch.consent.nswNoticeDate')}
            required
            extra={t('faceMatch.consent.nswNoticeHint', { days: NSW_NOTICE_MIN_DAYS })}
            {...show('nswNoticeDate')}
          >
            <DatePicker
              style={{ width: '100%' }}
              format={DATE_FORMAT}
              value={values.nswNoticeDate ? dayjs(values.nswNoticeDate, DATE_FORMAT) : null}
              disabledDate={(date) => date.isAfter(dayjs(), 'day')}
              onChange={(date) => patch({ nswNoticeDate: date ? date.format(DATE_FORMAT) : '' })}
            />
          </Form.Item>
        ) : null}
        <Form.Item
          label={t('faceMatch.consent.scan')}
          required={values.method === 'paper'}
          extra={t('faceMatch.consent.scanHint')}
          {...(scanIssue ? { validateStatus: 'error' as const, help: t(scanIssue.key, scanIssue.params) } : show('scan'))}
        >
          <Space>
            <Button onClick={() => inputRef.current?.click()}>{t('faceMatch.consent.chooseScan')}</Button>
            {values.scan ? (
              <>
                <Typography.Text>{values.scan.name}</Typography.Text>
                <Typography.Text type="secondary">{formatFileSize(values.scan.size)}</Typography.Text>
                <Button type="link" size="small" danger onClick={() => patch({ scan: null })}>
                  {t('faceMatch.photos.remove')}
                </Button>
              </>
            ) : null}
          </Space>
          <input
            ref={inputRef}
            type="file"
            accept="application/pdf,image/jpeg,image/png"
            hidden
            onChange={(event) => pickScan(event.target.files)}
          />
        </Form.Item>
      </Form>
      {errorText ? <Alert type="error" showIcon message={errorText} /> : null}
    </Modal>
  )
}
