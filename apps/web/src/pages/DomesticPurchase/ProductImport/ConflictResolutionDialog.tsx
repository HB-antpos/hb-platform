import { SendOutlined } from '@ant-design/icons'
import { Button, Modal, Segmented, Space, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { computePiecesAfterSend } from './importGridLogic'
import type { ConflictResolution, ConflictSelection, ContainerConflictRow } from './importGridLogic'

interface ConflictResolutionDialogProps {
  open: boolean
  /** 目标货柜编号，显示在标题旁。 */
  containerCode?: string
  /** 已在货柜里的冲突行（含已有 / 本次件数）。 */
  conflicts: ContainerConflictRow[]
  /** 本次一共要发送多少个货号（含无冲突的），用于底部摘要。 */
  totalSendCount: number
  /** 确认发送进行中：按钮 loading，且不允许关闭，防止重复提交。 */
  confirming: boolean
  onClose: () => void
  onConfirm: (selection: ConflictSelection) => void
}

type StrategyMode = ConflictResolution | 'perItem'

/** 货柜冲突处理：展示 已有 / 本次 / 发送后 件数，支持全部增加、全部覆盖、逐项选择。 */
export function ConflictResolutionDialog({ open, containerCode, conflicts, totalSendCount, confirming, onClose, onConfirm }: ConflictResolutionDialogProps) {
  const { t } = useTranslation()
  // 默认「全部增加」：与旧版一致，且不会在用户没留意时覆盖货柜里已有的件数
  const [mode, setMode] = useState<StrategyMode>('increase')
  const [perItem, setPerItem] = useState<Record<string, ConflictResolution>>({})

  // 每次重新打开都从默认值开始，避免带着上一次的逐项选择
  useEffect(() => {
    if (open) {
      setMode('increase')
      setPerItem({})
    }
  }, [open])

  const resolutionOf = (row: ContainerConflictRow): ConflictResolution => (mode === 'perItem' ? perItem[row.productCode] ?? 'increase' : mode)

  const columns: ColumnsType<ContainerConflictRow> = useMemo(() => [
    {
      title: t('productImport.conflictColItemNo', '货号'),
      dataIndex: 'hbProductNo',
      key: 'hbProductNo',
      width: 130,
      render: (value: string) => <span className="pi-mono">{value}</span>,
    },
    { title: t('productImport.conflictColName', '名称'), dataIndex: 'productName', key: 'productName', ellipsis: true },
    {
      title: t('productImport.conflictColExisting', '已有件数'),
      dataIndex: 'existingPieces',
      key: 'existingPieces',
      width: 92,
      align: 'right',
      render: (value?: number) => value ?? '—',
    },
    { title: t('productImport.conflictColIncoming', '本次件数'), dataIndex: 'incomingPieces', key: 'incomingPieces', width: 92, align: 'right' },
    {
      title: t('productImport.conflictColResolution', '处理方式'),
      key: 'resolution',
      width: 150,
      render: (_: unknown, row: ContainerConflictRow) => (
        <Segmented<ConflictResolution>
          size="small"
          value={resolutionOf(row)}
          // 只有「逐项选择」模式下才能改单行；统一模式下只是展示该策略的效果
          disabled={mode !== 'perItem' || confirming}
          options={[
            { label: t('productImport.conflictIncrease', '增加'), value: 'increase' },
            { label: t('productImport.conflictOverride', '覆盖'), value: 'override' },
          ]}
          onChange={(value) => setPerItem((prev) => ({ ...prev, [row.productCode]: value }))}
        />
      ),
    },
    {
      title: t('productImport.conflictColAfter', '发送后件数'),
      key: 'after',
      width: 130,
      align: 'right',
      render: (_: unknown, row: ContainerConflictRow) => {
        const resolution = resolutionOf(row)
        return (
          <span>
            <b>{computePiecesAfterSend(row, resolution)}</b>
            {resolution === 'override' ? <span className="pi-sub">{t('productImport.conflictWas', '（原 {{count}}）', { count: row.existingPieces ?? 0 })}</span> : null}
          </span>
        )
      },
    },
    // resolutionOf 依赖 mode / perItem，列定义随它们重建
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [t, mode, perItem, confirming])

  const handleConfirm = () => {
    onConfirm(mode === 'perItem' ? { perItem } : { global: mode })
  }

  return (
    <Modal
      open={open}
      width={760}
      maskClosable={false}
      keyboard={!confirming}
      closable={!confirming}
      onCancel={() => { if (!confirming) onClose() }}
      title={(
        <Space size={10}>
          <span>{t('productImport.conflictTitle', '货柜冲突：{{count}} 个货号已在货柜里', { count: conflicts.length })}</span>
          {containerCode ? <Tag bordered={false}>{containerCode}</Tag> : null}
        </Space>
      )}
      footer={(
        <div className="pi-conflict-footer">
          <span className="pi-sub">{t('productImport.conflictFooter', '共发送 {{total}} 个货号，其中 {{conflicts}} 个有冲突', { total: totalSendCount, conflicts: conflicts.length })}</span>
          <Space>
            <Button onClick={onClose} disabled={confirming}>{t('common.cancel', '取消')}</Button>
            <Button type="primary" icon={<SendOutlined />} loading={confirming} onClick={handleConfirm}>{t('productImport.conflictConfirm', '确认发送')}</Button>
          </Space>
        </div>
      )}
    >
      <div data-testid="product-import-conflict-dialog">
        <div className="pi-conflict-mode">
          <span className="pi-sub">{t('productImport.conflictModeLabel', '统一处理方式')}</span>
          <Segmented<StrategyMode>
            value={mode}
            disabled={confirming}
            options={[
              { label: t('productImport.conflictAllIncrease', '全部增加'), value: 'increase' },
              { label: t('productImport.conflictAllOverride', '全部覆盖'), value: 'override' },
              { label: t('productImport.conflictPerItem', '逐项选择'), value: 'perItem' },
            ]}
            onChange={setMode}
          />
          <span className="pi-sub pi-conflict-hint">{t('productImport.conflictHint', '增加 = 在已有件数上累加；覆盖 = 以本次件数为准')}</span>
        </div>
        <MeasuredTable
          metricId="domestic-purchase.product-import.conflict-resolution-dialog.table-1"
          columns={columns}
          dataSource={conflicts}
          rowKey="productCode"
          size="small"
          pagination={false}
          scroll={{ x: 640, y: 320 }}
        />
      </div>
    </Modal>
  )
}
