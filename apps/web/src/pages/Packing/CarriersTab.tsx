import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Button, Form, Input, Modal, Popconfirm, Switch, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import { listPackCarriers, savePackCarrier } from '../../services/packingService'
import type { PackCarrier, PackCarrierSaveRequest } from '../../types/packing'

import { showPackingError, usePackingPage } from './packingShared'

/** 正在编辑的运输公司；carrierGuid 为空表示新增。 */
interface CarrierEditing {
  carrierGuid: string | null
  initial: PackCarrierSaveRequest
}

/** 运输公司主数据：列表、新增、编辑、停用 / 启用（修改和停用后端只允许仓库经理，403 时照实提示）。 */
export default function CarriersTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { isAdmin, reloadCarriers } = usePackingPage()
  const [form] = Form.useForm<PackCarrierSaveRequest>()
  const [carriers, setCarriers] = useState<PackCarrier[]>([])
  const [loading, setLoading] = useState(false)
  const [editing, setEditing] = useState<CarrierEditing | null>(null)
  const [saving, setSaving] = useState(false)

  const load = useCallback(() => {
    setLoading(true)
    listPackCarriers(true)
      .then(setCarriers)
      .catch(showPackingError)
      .finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    if (active) {
      load()
    }
  }, [active, load])

  const save = useCallback(
    async (carrierGuid: string | null, payload: PackCarrierSaveRequest) => {
      setSaving(true)
      try {
        await savePackCarrier(carrierGuid, payload)
        void message.success(t('packingUi.message.carrierSaved'))
        setEditing(null)
        load()
        // 去向选择里的运输公司列表同步刷新。
        reloadCarriers()
      } catch (error) {
        showPackingError(error)
      } finally {
        setSaving(false)
      }
    },
    [load, reloadCarriers, t],
  )

  const openEditor = (carrier?: PackCarrier) => {
    const initial: PackCarrierSaveRequest = {
      name: carrier?.name ?? '',
      remark: carrier?.remark ?? '',
      isActive: carrier?.isActive ?? true,
    }
    form.setFieldsValue(initial)
    setEditing({ carrierGuid: carrier?.carrierGuid ?? null, initial })
  }

  const submit = async () => {
    if (!editing) {
      return
    }
    const values = await form.validateFields().catch(() => null)
    if (!values) {
      return
    }
    await save(editing.carrierGuid, {
      name: values.name.trim(),
      remark: values.remark?.trim() || null,
      isActive: values.isActive,
    })
  }

  const columns = useMemo<ColumnsType<PackCarrier>>(
    () => [
      { title: t('packingUi.carriers.name'), dataIndex: 'name', width: 260, ellipsis: true },
      {
        title: t('packingUi.columns.remark'),
        dataIndex: 'remark',
        ellipsis: true,
        render: (value: string | null | undefined) => value || '—',
      },
      {
        title: t('common.status'),
        dataIndex: 'isActive',
        width: 100,
        render: (value: boolean) =>
          value ? <Tag color="green">{t('common.enabled')}</Tag> : <Tag>{t('packingUi.carriers.inactive')}</Tag>,
      },
      {
        title: t('packingUi.columns.actions'),
        key: 'actions',
        width: 150,
        render: (_value, record) =>
          isAdmin ? (
          <>
            <Button type="link" size="small" onClick={() => openEditor(record)}>
              {t('common.edit')}
            </Button>
            {record.isActive ? (
              <Popconfirm
                title={t('packingUi.confirm.deactivateCarrier', { name: record.name })}
                okText={t('common.confirm')}
                cancelText={t('common.cancel')}
                onConfirm={() =>
                  save(record.carrierGuid, { name: record.name, remark: record.remark ?? null, isActive: false })
                }
              >
                <Button type="link" size="small" danger disabled={saving}>
                  {t('packingUi.carriers.deactivate')}
                </Button>
              </Popconfirm>
            ) : (
              <Button
                type="link"
                size="small"
                disabled={saving}
                onClick={() => void save(record.carrierGuid, { name: record.name, remark: record.remark ?? null, isActive: true })}
              >
                {t('packingUi.carriers.activate')}
              </Button>
            )}
          </>
          ) : null,
      },
    ],
    // openEditor 只用到稳定的 form 实例。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [isAdmin, save, saving, t],
  )

  return (
    <div className="wh-packing-panel wh-packing-list">
      <div className="wh-packing-filters">
        <Typography.Text type="secondary">{t('packingUi.carriers.hint')}</Typography.Text>
        <span className="wh-packing-filters-spacer" />
        <Button icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
        <Button type="primary" icon={<PlusOutlined />} onClick={() => openEditor()}>
          {t('packingUi.carriers.add')}
        </Button>
      </div>
      <MeasuredTable<PackCarrier>
        metricId="packing.carriers"
        size="middle"
        rowKey="carrierGuid"
        loading={loading}
        // 编辑 / 停用只有管理员可用：非管理员不显示操作列。
        columns={isAdmin ? columns : columns.filter((column) => column.key !== 'actions')}
        dataSource={carriers}
        pagination={false}
      />
      <Modal
        open={editing !== null}
        title={editing?.carrierGuid ? t('packingUi.carriers.edit') : t('packingUi.carriers.add')}
        width={440}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={saving}
        onOk={() => void submit()}
        onCancel={() => setEditing(null)}
        forceRender
      >
        <Form form={form} layout="vertical" initialValues={{ isActive: true }}>
          <Form.Item
            name="name"
            label={t('packingUi.carriers.name')}
            rules={[{ required: true, whitespace: true, message: t('packingUi.carriers.nameRequired') }]}
          >
            <Input maxLength={100} autoComplete="off" />
          </Form.Item>
          <Form.Item name="remark" label={t('packingUi.columns.remark')}>
            <Input maxLength={200} autoComplete="off" />
          </Form.Item>
          <Form.Item name="isActive" label={t('common.enabled')} valuePropName="checked">
            <Switch />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
