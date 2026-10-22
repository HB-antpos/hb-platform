import { Alert, Button, InputNumber, Table, Typography } from 'antd'
import { FIXED_PREVIEW_COSTS, FIXED_RATE_MAX, FIXED_RATE_MIN, fixedRateError, fixedRetailPrice } from './pricingCurve'

const PRESETS = [1.5, 2, 2.5, 3, 3.5, 5]

interface Props { value?: number | null; onChange?: (value: number | null) => void }

/** 固定成率面板：只填一个倍数，右侧按同一公式实时试算，便于保存前核对。 */
export default function FixedRatePanel({ value, onChange }: Props) {
  const rate = typeof value === 'number' && Number.isFinite(value) ? value : null
  const error = fixedRateError(rate)
  const rows = FIXED_PREVIEW_COSTS.map(cost => {
    const price = error === null && rate !== null ? fixedRetailPrice(cost, rate) : null
    return { cost, price, actual: price === null ? null : price / cost }
  })
  return <div style={{ marginBottom: 16 }}>
    <Alert type="info" showIcon style={{ marginBottom: 16 }} message="所有进货价使用同一个成率"
      description="零售价 = 进货价 × 成率，直接四舍五入到分，不再调整为 .50 / .99 尾数。适用于进货价 0.10 以上的全部区间。" />
    <div style={{ display: 'grid', gridTemplateColumns: 'minmax(260px, 330px) 1fr', gap: 24, alignItems: 'start' }}>
      <div style={{ border: '1px solid #f0f0f0', borderRadius: 8, padding: 16 }}>
        <div style={{ marginBottom: 4 }}><span style={{ color: '#ff4d4f', marginRight: 4 }}>*</span>成率（倍）</div>
        <InputNumber aria-label="固定成率" size="large" min={FIXED_RATE_MIN} max={FIXED_RATE_MAX} step={0.1} precision={4}
          status={error ? 'error' : undefined} value={rate} onChange={v => onChange?.(v)}
          style={{ width: 160, fontSize: 24, fontWeight: 600 }} addonAfter="倍" />
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 12 }}>
          {PRESETS.map(p => <Button key={p} size="small" shape="round" type={rate === p ? 'primary' : 'default'} ghost={rate === p} onClick={() => onChange?.(p)}>{p}</Button>)}
        </div>
        <div style={{ marginTop: 14, padding: '10px 12px', background: '#fafafa', borderRadius: 6, color: '#595959', fontSize: 13 }}>
          零售价 = 进货价 × <b style={{ color: '#1f1f1f' }}>{rate === null ? '—' : rate.toFixed(4)}</b>
        </div>
        {error && <Typography.Text type="danger" style={{ display: 'block', marginTop: 8 }}>{error}</Typography.Text>}
        <ul style={{ margin: '10px 0 0', paddingLeft: 16, color: '#8c8c8c', fontSize: 12, lineHeight: 1.7 }}>
          <li>成率范围 {FIXED_RATE_MIN} ～ {FIXED_RATE_MAX}，保存时校验</li>
          <li>最多 4 位小数</li>
          <li>保存后可用「策略测试」输入进货价核对</li>
        </ul>
      </div>
      <div>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', marginBottom: 8 }}>
          <b>实时预览</b><Typography.Text type="secondary" style={{ fontSize: 12 }}>按当前成率试算，与后端计算一致</Typography.Text>
        </div>
        <Table size="small" pagination={false} rowKey="cost" dataSource={rows} columns={[
          { title: '进货价', dataIndex: 'cost', render: (v: number) => v.toFixed(2) },
          { title: '零售价', dataIndex: 'price', align: 'right', render: (v: number | null) => v === null ? '—' : <b style={{ color: '#1677ff' }}>{v.toFixed(2)}</b> },
          { title: '实际成率', dataIndex: 'actual', align: 'right', render: (v: number | null) => v === null ? '—' : <span style={{ color: '#8c8c8c' }}>{v.toFixed(4)}</span> },
        ]} />
        <Typography.Text type="secondary" style={{ display: 'block', marginTop: 8, fontSize: 12 }}>实际成率与设定值的微小差异，仅来自「四舍五入到分」。</Typography.Text>
      </div>
    </div>
  </div>
}
