import { AutoComplete, Form, Input, Modal, message } from 'antd'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { StoreOption } from '../../../services/storeService'
import type { CreateFaceCameraRequest, FaceCamera, UpdateFaceCameraRequest } from '../../../types/faceCamera'
import {
  CAMERA_ID_MAX_LENGTH,
  DEVICE_MODEL_MAX_LENGTH,
  MAC_ADDRESS_MAX_LENGTH,
  NOTE_MAX_LENGTH,
  STORE_CODE_MAX_LENGTH,
  buildCreateRequest,
  buildUpdateRequest,
  getFaceCameraErrorCode,
  toFormValues,
  validateCameraId,
  validateDeviceModel,
  validateMacAddress,
  validateNote,
  validateStoreCode,
  type FaceCameraFormValues,
} from './logic'

interface CameraFormModalProps {
  /** null 表示新增；否则是正在编辑的摄像机（cameraId 只读）。 */
  camera: FaceCamera | null
  open: boolean
  storeOptions: StoreOption[]
  /** 分店列表加载失败（例如账号没有分店查看权限）：降级成手填分店编码。 */
  storeOptionsFailed: boolean
  /** 提交请求并处理结果；失败时抛出，由本组件把错误显示在表单上。表单不接触 token。 */
  onCreate: (request: CreateFaceCameraRequest) => Promise<void>
  onUpdate: (cameraId: string, request: UpdateFaceCameraRequest) => Promise<void>
  describeError: (error: unknown) => string
  onClose: () => void
}

// 校验函数返回文案键，这里统一翻成当前语言；通过时 resolve。
function toRule(validate: (value: string | null | undefined) => string | null, translate: (key: string) => string) {
  return {
    validator: (_: unknown, value: string | undefined) => {
      const key = validate(value)
      return key ? Promise.reject(new Error(translate(key))) : Promise.resolve()
    },
  }
}

export default function CameraFormModal({
  camera,
  open,
  storeOptions,
  storeOptionsFailed,
  onCreate,
  onUpdate,
  describeError,
  onClose,
}: CameraFormModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<FaceCameraFormValues>()
  const [submitting, setSubmitting] = useState(false)
  const isEdit = camera !== null

  // 每次打开都重置：编辑回填当前值，新增清空。
  useEffect(() => {
    if (!open) return
    form.resetFields()
    if (camera) form.setFieldsValue(toFormValues(camera))
  }, [open, camera, form])

  const autoCompleteOptions = useMemo(
    () =>
      storeOptions.map((option) => ({
        value: option.value,
        label: option.label && option.label !== option.value ? `${option.value} · ${option.label}` : option.value,
      })),
    [storeOptions],
  )

  const handleSubmit = async () => {
    let values: FaceCameraFormValues
    try {
      values = await form.validateFields()
    } catch {
      return
    }
    setSubmitting(true)
    try {
      if (camera) {
        await onUpdate(camera.cameraId, buildUpdateRequest(values, camera.enabled))
      } else {
        await onCreate(buildCreateRequest(values))
      }
      onClose()
    } catch (error) {
      // 后端的字段级错误挂回对应输入框，其余用全局提示；不记录错误对象。
      const code = getFaceCameraErrorCode(error)
      if (code === 'FACE_CAMERA_ID_EXISTS' || code === 'FACE_CAMERA_ID_INVALID') {
        form.setFields([{ name: 'cameraId', errors: [describeError(error)] }])
      } else if (code === 'FACE_CAMERA_STORE_NOT_FOUND' || code === 'FACE_CAMERA_STORE_CODE_INVALID') {
        form.setFields([{ name: 'storeCode', errors: [describeError(error)] }])
      } else {
        void message.error(describeError(error))
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal
      open={open}
      title={isEdit ? t('faceCameras.form.titleEdit') : t('faceCameras.form.titleCreate')}
      okText={isEdit ? t('faceCameras.form.submitSave') : t('faceCameras.form.submitCreate')}
      cancelText={t('faceCameras.form.cancel')}
      confirmLoading={submitting}
      cancelButtonProps={{ disabled: submitting }}
      maskClosable={false}
      keyboard={!submitting}
      closable={!submitting}
      destroyOnHidden
      onOk={() => void handleSubmit()}
      onCancel={() => {
        if (!submitting) onClose()
      }}
    >
      <Form form={form} layout="vertical" requiredMark="optional" autoComplete="off" style={{ marginTop: 16 }}>
        <Form.Item
          name="cameraId"
          label={t('faceCameras.form.cameraId')}
          extra={isEdit ? t('faceCameras.form.cameraIdReadonly') : t('faceCameras.form.cameraIdHint')}
          style={{ marginBottom: 24 }}
          required
          rules={isEdit ? [] : [toRule(validateCameraId, t)]}
        >
          <Input
            readOnly={isEdit}
            disabled={isEdit}
            maxLength={CAMERA_ID_MAX_LENGTH}
            placeholder={t('faceCameras.form.cameraIdPlaceholder')}
          />
        </Form.Item>
        <Form.Item
          name="storeCode"
          label={t('faceCameras.form.storeCode')}
          extra={storeOptionsFailed ? t('faceCameras.form.storeCodeManualHint') : t('faceCameras.form.storeCodeHint')}
          style={{ marginBottom: 24 }}
          required
          rules={[toRule(validateStoreCode, t)]}
        >
          <AutoComplete
            options={autoCompleteOptions}
            maxLength={STORE_CODE_MAX_LENGTH}
            placeholder={t('faceCameras.form.storeCodePlaceholder')}
            filterOption={(input, option) =>
              String(option?.label ?? '').toLowerCase().includes(input.trim().toLowerCase())
            }
          />
        </Form.Item>
        <Form.Item
          name="deviceModel"
          label={t('faceCameras.form.deviceModel')}
          rules={[toRule(validateDeviceModel, t)]}
        >
          <Input maxLength={DEVICE_MODEL_MAX_LENGTH} />
        </Form.Item>
        <Form.Item
          name="macAddress"
          label={t('faceCameras.form.macAddress')}
          rules={[toRule(validateMacAddress, t)]}
        >
          <Input maxLength={MAC_ADDRESS_MAX_LENGTH} />
        </Form.Item>
        <Form.Item name="note" label={t('faceCameras.form.note')} rules={[toRule(validateNote, t)]}>
          <Input.TextArea rows={2} maxLength={NOTE_MAX_LENGTH} showCount />
        </Form.Item>
      </Form>
    </Modal>
  )
}
