import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Input, Space, Tag, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getFaceStaff } from '../../../services/faceMatchService'
import type { FaceStaffCandidate } from '../../../types/faceMatch'
import { describeFaceMatchError } from './logic'
import { StoreSelect, type FaceMatchTabContext } from './shared'
import StaffDetailDrawer from './StaffDetailDrawer'
import { useRemote } from './useRemote'

/** 员工同意与登记页签（Manage）：选门店 → 员工列表 → 抽屉里管理该员工的同意与登记。 */
export default function StaffTab({ context }: { context: FaceMatchTabContext }) {
  const { t } = useTranslation()
  const { storeOptions, storeOptionsFailed, config } = context
  const [storeCode, setStoreCode] = useState<string | null>(null)
  const [keyword, setKeyword] = useState('')
  const [selected, setSelected] = useState<FaceStaffCandidate | null>(null)

  const loader = useMemo(
    () => (storeCode ? (signal: AbortSignal) => getFaceStaff(storeCode, keyword, signal) : null),
    [storeCode, keyword],
  )
  const staff = useRemote<FaceStaffCandidate[]>(loader)

  const columns = useMemo<ColumnsType<FaceStaffCandidate>>(
    () => [
      { title: t('faceMatch.staff.name'), dataIndex: 'displayName', width: 200, ellipsis: true },
      { title: t('faceMatch.staff.email'), dataIndex: 'email', width: 240, ellipsis: true, render: (value: string | null) => value ?? '-' },
      {
        title: t('faceMatch.staff.enrollmentState'),
        dataIndex: 'enrollmentState',
        width: 120,
        render: (value: string) => (value === 'active' ? <Tag color="green">{t('faceMatch.staff.stateActive')}</Tag> : <Tag>{t('faceMatch.staff.stateNone')}</Tag>),
      },
      {
        title: t('faceMatch.staff.nsw'),
        dataIndex: 'requiresNswNotice',
        width: 160,
        render: (value: boolean) => (value ? <Tag color="gold">{t('faceMatch.staff.nswRequired')}</Tag> : '-'),
      },
      {
        title: t('faceMatch.common.actions'),
        key: 'actions',
        width: 140,
        fixed: 'right',
        render: (_: unknown, record) => (
          <Button type="link" size="small" onClick={() => setSelected(record)}>
            {t('faceMatch.staff.manage')}
          </Button>
        ),
      },
    ],
    [t],
  )

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Alert type="info" showIcon message={t('faceMatch.staff.intro')} />
      {!config.objectStoreConfigured ? <Alert type="warning" showIcon message={t('faceMatch.staff.objectStoreMissing')} /> : null}
      <Space wrap>
        <Typography.Text>{t('faceMatch.common.store')}</Typography.Text>
        <StoreSelect
          options={storeOptions}
          failed={storeOptionsFailed}
          value={storeCode}
          onChange={setStoreCode}
          allowClear={false}
          placeholder={t('faceMatch.staff.pickStore')}
          style={{ width: 280 }}
        />
        <Input.Search
          allowClear
          style={{ width: 240 }}
          placeholder={t('faceMatch.staff.searchPlaceholder')}
          maxLength={50}
          onSearch={(value) => setKeyword(value.trim())}
        />
        <Button icon={<ReloadOutlined />} disabled={!storeCode} loading={staff.loading} onClick={staff.reload}>
          {t('faceMatch.common.refresh')}
        </Button>
      </Space>
      {staff.error ? (
        <Alert type="error" showIcon message={describeFaceMatchError(t, staff.error)} action={<Button size="small" onClick={staff.reload}>{t('faceMatch.common.retry')}</Button>} />
      ) : null}
      <MeasuredTable<FaceStaffCandidate>
        metricId="system.face-match.staff-table-1"
        rowKey="userGuid"
        size="small"
        loading={staff.loading && !staff.data}
        dataSource={staff.data ?? []}
        columns={columns}
        pagination={false}
        scroll={{ x: 860 }}
        locale={{ emptyText: storeCode ? t('faceMatch.staff.empty') : t('faceMatch.staff.pickStoreHint') }}
      />
      {selected ? (
        <StaffDetailDrawer
          key={selected.userGuid}
          context={context}
          staff={selected}
          onChanged={staff.reload}
          onClose={() => setSelected(null)}
        />
      ) : null}
    </Space>
  )
}
