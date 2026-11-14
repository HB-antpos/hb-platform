import { PlusOutlined, PrinterOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Form, Input, Modal, Popconfirm, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import { listPackLocations, savePackLocation } from '../../services/packingService'
import type { PackLocation } from '../../types/packing'

import { PACK_LOCATION_NAME_MAX, normalizeLocationName } from './packingLogic'
import { LocationLabelPreview, showPackingError, usePackingPage } from './packingShared'
import { buildLocationLabelFields, printLocationLabels } from './packLabel'

interface LocationEditing {
  location: PackLocation | null
}

/**
 * 存放位置：每家分店自己维护位置名称（只有店长能新增 / 改名 / 停用），每个位置有条码，可打印位置标签贴在货架上。
 * 选中总仓时展示现有货位（只读），维护去「货位管理」。
 */
export default function LocationsTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { owner, ownerCode, isWarehouse, isManager, reloadLocations } = usePackingPage()
  const [form] = Form.useForm<{ name: string }>()
  const [locations, setLocations] = useState<PackLocation[]>([])
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [editing, setEditing] = useState<LocationEditing | null>(null)
  const [saving, setSaving] = useState(false)
  const [selectedKeys, setSelectedKeys] = useState<string[]>([])
  const [previewing, setPreviewing] = useState<PackLocation | null>(null)

  const load = useCallback(() => {
    setLoading(true)
    listPackLocations(ownerCode, true)
      .then(setLocations)
      .catch(showPackingError)
      .finally(() => setLoading(false))
  }, [ownerCode])

  useEffect(() => {
    if (active) {
      load()
    }
  }, [active, load])

  const save = useCallback(
    async (location: PackLocation | null, name: string, isActive: boolean) => {
      setSaving(true)
      try {
        await savePackLocation(location?.locationGuid ?? null, { ownerCode, name, isActive })
        void message.success(t('packingUi.message.locationItemSaved'))
        setEditing(null)
        load()
        // 去向 / 签收里的位置下拉同步刷新。
        reloadLocations()
      } catch (error) {
        showPackingError(error)
      } finally {
        setSaving(false)
      }
    },
    [load, ownerCode, reloadLocations, t],
  )

  const openEditor = (location: PackLocation | null) => {
    form.setFieldsValue({ name: location?.name ?? '' })
    setEditing({ location })
  }

  const submit = async () => {
    if (!editing) {
      return
    }
    const values = await form.validateFields().catch(() => null)
    const name = normalizeLocationName(values?.name)
    if (!name) {
      return
    }
    await save(editing.location, name, editing.location?.isActive ?? true)
  }

  const visible = useMemo(() => {
    const text = keyword.trim().toLowerCase()
    return text ? locations.filter((item) => `${item.name} ${item.barcode}`.toLowerCase().includes(text)) : locations
  }, [keyword, locations])

  const printSelected = (items: PackLocation[]) => {
    printLocationLabels(items.map((item) => buildLocationLabelFields(item, owner.name)))
  }

  const columns = useMemo<ColumnsType<PackLocation>>(
    () => [
      { title: t('packingUi.location.name'), dataIndex: 'name', ellipsis: true },
      {
        title: t('packingUi.location.barcode'),
        dataIndex: 'barcode',
        width: 180,
        render: (value: string) => <span className="wh-packing-mono">{value || '—'}</span>,
      },
      {
        title: t('common.status'),
        dataIndex: 'isActive',
        width: 100,
        render: (value: boolean) =>
          value ? <Tag color="green">{t('common.enabled')}</Tag> : <Tag>{t('packingUi.location.inactive')}</Tag>,
      },
      {
        title: t('packingUi.columns.actions'),
        key: 'actions',
        width: isWarehouse ? 90 : 260,
        render: (_value, record) => (
          <>
            {isWarehouse ? null : (
              <Button type="link" size="small" icon={<PrinterOutlined />} onClick={() => printSelected([record])}>
                {t('packingUi.location.printLabel')}
              </Button>
            )}
            <Button type="link" size="small" onClick={() => setPreviewing(record)}>
              {t('packingUi.location.preview')}
            </Button>
            {!isWarehouse && isManager ? (
              <>
                <Button type="link" size="small" onClick={() => openEditor(record)}>
                  {t('common.edit')}
                </Button>
                {record.isActive ? (
                  <Popconfirm
                    title={t('packingUi.confirm.deactivateLocation', { name: record.name })}
                    okText={t('common.confirm')}
                    cancelText={t('common.cancel')}
                    onConfirm={() => save(record, record.name, false)}
                  >
                    <Button type="link" size="small" danger disabled={saving}>
                      {t('packingUi.location.deactivate')}
                    </Button>
                  </Popconfirm>
                ) : (
                  <Button type="link" size="small" disabled={saving} onClick={() => void save(record, record.name, true)}>
                    {t('packingUi.location.activate')}
                  </Button>
                )}
              </>
            ) : null}
          </>
        ),
      },
    ],
    // openEditor / printSelected 只用到稳定的 form 实例和当前地点名称。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [isManager, isWarehouse, owner.name, save, saving, t],
  )

  const selectedItems = locations.filter((item) => selectedKeys.includes(item.locationGuid))

  return (
    <div className="wh-packing-panel wh-packing-list">
      {isWarehouse ? (
        <Alert className="wh-packing-receive-alert" type="info" showIcon message={t('packingUi.location.warehouseHint')} />
      ) : !isManager ? (
        <Alert className="wh-packing-receive-alert" type="info" showIcon message={t('packingUi.location.readOnlyHint')} />
      ) : null}
      <div className="wh-packing-filters">
        <Input.Search
          className="wh-packing-keyword"
          allowClear
          placeholder={t('packingUi.location.search')}
          onSearch={(value) => setKeyword(value)}
        />
        <span className="wh-packing-filters-spacer" />
        <Typography.Text type="secondary">{t('packingUi.location.total', { count: visible.length })}</Typography.Text>
        <Button icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
        {!isWarehouse ? (
          <Button
            icon={<PrinterOutlined />}
            disabled={selectedItems.length === 0}
            onClick={() => printSelected(selectedItems)}
          >
            {t('packingUi.location.printSelected', { count: selectedItems.length })}
          </Button>
        ) : null}
        {!isWarehouse && isManager ? (
          <Button type="primary" icon={<PlusOutlined />} onClick={() => openEditor(null)}>
            {t('packingUi.location.add')}
          </Button>
        ) : null}
      </div>
      <MeasuredTable<PackLocation>
        metricId="packing.locations"
        size="middle"
        rowKey="locationGuid"
        loading={loading}
        columns={columns}
        dataSource={visible}
        pagination={{ defaultPageSize: 50, showSizeChanger: true, hideOnSinglePage: true }}
        rowSelection={
          isWarehouse
            ? undefined
            : { selectedRowKeys: selectedKeys, onChange: (keys) => setSelectedKeys(keys.map(String)) }
        }
      />
      <Modal
        open={editing !== null}
        title={editing?.location ? t('packingUi.location.edit') : t('packingUi.location.add')}
        width={420}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={saving}
        onOk={() => void submit()}
        onCancel={() => setEditing(null)}
        forceRender
      >
        <Form form={form} layout="vertical">
          <Form.Item
            name="name"
            label={t('packingUi.location.name')}
            extra={t('packingUi.location.nameHint')}
            rules={[
              { required: true, whitespace: true, message: t('packingUi.location.nameRequired') },
              { max: PACK_LOCATION_NAME_MAX, message: t('packingUi.location.nameRequired') },
            ]}
          >
            <Input maxLength={PACK_LOCATION_NAME_MAX} autoComplete="off" />
          </Form.Item>
        </Form>
      </Modal>
      <Modal
        open={previewing !== null}
        title={t('packingUi.location.preview')}
        footer={
          previewing && !isWarehouse ? (
            <Button type="primary" icon={<PrinterOutlined />} onClick={() => printSelected([previewing])}>
              {t('packingUi.location.printLabel')}
            </Button>
          ) : null
        }
        width={560}
        onCancel={() => setPreviewing(null)}
        destroyOnClose
      >
        {previewing ? <LocationLabelPreview fields={buildLocationLabelFields(previewing, owner.name)} scale={0.9} /> : null}
      </Modal>
    </div>
  )
}
