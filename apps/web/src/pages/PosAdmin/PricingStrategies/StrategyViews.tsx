import { Typography } from 'antd'
import { MeasuredTable } from '../../../components/MeasuredTable'
import type { PricingStrategyIntervalDto, PricingStrategyListDto, PricingStrategyNodeDto } from '../../../types/pricingStrategy'
import { fmtRate } from './pricingCurve'

const muted = { color: '#8c8c8c', fontSize: 12 } as const
const GOLD = '#d48806', BLUE = '#1677ff'
/** 与保存时一致：上限为该值表示「不限」。 */
const UNBOUNDED = 99999

const costRange = (i: PricingStrategyIntervalDto) => `${i.minPrice.toFixed(2)} ~ ${i.maxPrice >= UNBOUNDED ? '不限' : i.maxPrice.toFixed(2)}`

/** 图表横轴的右端：最后一个区间不限上限时，只画到「最后一个交界的两倍」（至少 100），避免被 99999 压扁。 */
function domainEnd(intervals: PricingStrategyIntervalDto[]): number {
  const first = intervals[0].minPrice, last = intervals[intervals.length - 1]
  if (last.maxPrice < UNBOUNDED) return last.maxPrice
  const boundary = intervals.length > 1 ? intervals[intervals.length - 2].maxPrice : first
  return Math.max(boundary * 2, first * 10, 100)
}

/** 区间在 cost 处的理论价：固定 = 成本 × 成率，曲线按端点线性插值（缩略图不需要弧度精度）。 */
function priceAt(i: PricingStrategyIntervalDto, cost: number): number {
  if (i.type === 'Fixed' && typeof i.rate === 'number') return cost * i.rate
  const span = i.maxPrice - i.minPrice
  return span > 0 ? i.startPrice + (i.endPrice - i.startPrice) * (cost - i.minPrice) / span : i.startPrice
}

/** 每个区间在可见范围内的线段（起止成本、起止价格），超出右端的区间被裁掉。 */
function segments(intervals: PricingStrategyIntervalDto[], end: number) {
  return intervals.filter(i => i.minPrice < end).map(i => {
    const hi = Math.min(i.maxPrice, end)
    return { i, x0: i.minPrice, x1: hi, y0: priceAt(i, i.minPrice), y1: priceAt(i, hi) }
  })
}

/** 列表里的迷你曲线：横轴成本，纵轴零售价；固定区间金色、曲线区间蓝色，交界处的价差表现为断开。 */
function Sparkline({ intervals, nodes }: { intervals?: PricingStrategyIntervalDto[]; nodes: PricingStrategyNodeDto[] }) {
  const w = 88, h = 30
  if (intervals?.length) {
    const end = domainEnd(intervals), first = intervals[0].minPrice, segs = segments(intervals, end)
    const top = Math.max(...segs.map(s => Math.max(s.y0, s.y1)))
    if (!(end > first) || !(top > 0)) return null
    const X = (c: number) => 4 + (c - first) / (end - first) * (w - 8), Y = (p: number) => h - 3 - p / top * (h - 8)
    return <svg width={w} height={h} aria-hidden="true">
      {segs.map((s, k) => <line key={k} x1={X(s.x0)} y1={Y(s.y0)} x2={X(s.x1)} y2={Y(s.y1)} stroke={s.i.type === 'Fixed' ? GOLD : BLUE} strokeWidth="2" strokeLinecap="round" />)}
    </svg>
  }
  // 后端较旧、没有区间摘要时退回到节点折线
  if (nodes.length < 2) return null
  const x0 = nodes[0].cost, x1 = nodes[nodes.length - 1].cost, y1 = Math.max(...nodes.map(n => n.price))
  if (!(x1 > x0) || !(y1 > 0)) return null
  const points = nodes.map(n => `${(4 + (n.cost - x0) / (x1 - x0) * (w - 8)).toFixed(1)},${(h - 3 - n.price / y1 * (h - 8)).toFixed(1)}`).join(' ')
  return <svg width={w} height={h} aria-hidden="true"><polyline points={points} fill="none" stroke={BLUE} strokeWidth="2" strokeLinejoin="round" /></svg>
}

/** 「定价方式」列：单个固定成率显示倍数；曲线 / 分段 / 旧版规则显示缩略图与成率区间。 */
export function PricingCell({ record }: { record: PricingStrategyListDto }) {
  const mode = record.pricingMode
  // 后端尚未返回摘要字段（先部署 Web、后部署后端的窗口期）时退回到规则数，避免整列空白。
  if (!mode) return <span style={muted}>{record.detailsCount} 条规则</span>
  if (mode === 'Empty') return <span style={muted}>未配置区间</span>
  if (mode === 'Fixed' && typeof record.fixedRate === 'number') {
    return <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
      <span style={{ fontSize: 18, fontWeight: 600, color: GOLD, fontVariantNumeric: 'tabular-nums' }}>{fmtRate(record.fixedRate)}×</span>
      <div style={{ lineHeight: 1.4 }}><b>固定成率</b><br /><span style={muted}>售价 = 进货价 × {fmtRate(record.fixedRate)}</span></div>
    </div>
  }
  const nodes = record.nodes ?? [], intervals = record.intervals ?? []
  const range = typeof record.startRate === 'number' && typeof record.endRate === 'number' ? `成率 ${fmtRate(record.startRate)} → ${fmtRate(record.endRate)}` : ''
  const count = intervals.length || Math.max(nodes.length - 1, 0)
  const fixedCount = intervals.filter(i => i.type === 'Fixed').length
  const title = mode === 'Mixed' ? '分段' : mode === 'Curve' ? '曲线' : '区间规则'
  const detail = mode === 'Mixed' ? `固定 ${fixedCount} + 曲线 ${count - fixedCount}`
    : mode === 'Curve' && !intervals.length ? `${nodes.length} 节点` : `${count} 个区间`
  return <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
    <Sparkline intervals={intervals} nodes={nodes} />
    <div style={{ lineHeight: 1.4, fontVariantNumeric: 'tabular-nums' }}>
      <b>{title}</b> · {detail}
      {range && <><br /><span style={muted}>{range}</span></>}
    </div>
  </div>
}

/** 展开行里的「成率随成本变化」：纵轴固定为 1.5～5 倍可行带；固定区间是水平线，交界处的价差表现为台阶。 */
function RateChart({ intervals }: { intervals: PricingStrategyIntervalDto[] }) {
  const W = 420, H = 132, L = 10, T = 12, plotW = 400, plotH = 100
  const first = intervals[0].minPrice, end = domainEnd(intervals)
  if (!(end > first)) return null
  const X = (c: number) => L + (c - first) / (end - first) * plotW
  const rate = (p: number, c: number) => (c > 0 ? Math.min(5, Math.max(1.5, p / c)) : 1.5)
  const Y = (r: number) => T + (5 - r) / 3.5 * plotH
  const segs = segments(intervals, end)
  return <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" aria-label="成率随成本变化">
    <rect x="0" y="0" width={W} height={H} fill="#fff" stroke="#f0f0f0" rx="6" />
    <rect x={L} y={T} width={plotW} height={plotH} fill="#f6ffed" />
    <text x={L + plotW - 4} y={T + 12} textAnchor="end" fontSize="10" fill="#8c8c8c">5×</text>
    <text x={L + plotW - 4} y={T + plotH - 4} textAnchor="end" fontSize="10" fill="#8c8c8c">1.5×</text>
    {segs.map((s, k) => {
      const r0 = rate(s.y0, s.x0), r1 = rate(s.y1, s.x1), color = s.i.type === 'Fixed' ? GOLD : BLUE
      return <g key={k}>
        <line x1={X(s.x0)} y1={Y(r0)} x2={X(s.x1)} y2={Y(r1)} stroke={color} strokeWidth="2.5" strokeLinecap="round" />
        {k > 0 && <line x1={X(s.x0)} y1={Y(rate(segs[k - 1].y1, segs[k - 1].x1))} x2={X(s.x0)} y2={Y(r0)} stroke="#bfbfbf" strokeDasharray="3 3" />}
        <text x={(X(s.x0) + X(s.x1)) / 2} y={Y((r0 + r1) / 2) - 7} textAnchor="middle" fontSize="10" fontWeight="600" fill={color}>{s.i.type === 'Fixed' ? `${fmtRate(s.i.rate ?? r0)}×` : ''}</text>
      </g>
    })}
    <text x="12" y={H - 6} fontSize="10" fill="#8c8c8c">成本 {Number(first.toFixed(2))}</text>
    <text x={W - 12} y={H - 6} textAnchor="end" fontSize="10" fill="#8c8c8c">{Number(end.toFixed(2))}</text>
  </svg>
}

/** 展开行：区间表 + 成率曲线；单个固定成率显示公式。 */
export function StrategyExpanded({ record }: { record: PricingStrategyListDto }) {
  if (record.pricingMode === 'Fixed' && typeof record.fixedRate === 'number') {
    return <div style={{ padding: '4px 0 4px 24px' }}>
      <div style={{ display: 'inline-block', padding: '10px 14px', background: '#fff', border: '1px solid #f0f0f0', borderRadius: 6 }}>
        零售价 = 进货价 × <b>{fmtRate(record.fixedRate)}</b>
      </div>
      <div style={{ ...muted, marginTop: 8 }}>再取 .50 / .99 尾数（整数取 .99，1、2 除外）；适用于进货价 0.10 以上的全部区间。</div>
    </div>
  }
  const intervals = record.intervals ?? []
  const nodes = record.nodes ?? []
  if (!intervals.length) {
    // 后端较旧：只有节点，显示节点表
    if (!nodes.length) return <span style={muted}>没有可展示的定价节点</span>
    return <MeasuredTable metricId="pos-admin.pricing-strategies.expanded-nodes" size="small" pagination={false} rowKey={(_, i) => String(i)} dataSource={nodes} columns={[
      { title: '#', width: 48, render: (_: unknown, __: PricingStrategyNodeDto, i: number) => i + 1 },
      { title: '成本', dataIndex: 'cost', render: (v: number) => v.toFixed(2) },
      { title: '零售价', dataIndex: 'price', align: 'right', render: (v: number) => v.toFixed(2) },
    ]} />
  }
  const legacy = record.pricingMode === 'Legacy'
  return <div style={{ display: 'flex', gap: 40, flexWrap: 'wrap', alignItems: 'flex-start', padding: '4px 0 4px 24px' }}>
    <div style={{ flex: '0 1 560px', minWidth: 360 }}>
      <Typography.Text strong style={{ display: 'block', marginBottom: 8 }}>价格区间</Typography.Text>
      <MeasuredTable metricId="pos-admin.pricing-strategies.expanded-intervals" size="small" pagination={false} rowKey={(_, i) => String(i)} dataSource={intervals} columns={[
        { title: '#', width: 48, render: (_: unknown, __: PricingStrategyIntervalDto, i: number) => i + 1 },
        { title: '进货价（> 下限，≤ 上限）', render: (_: unknown, i: PricingStrategyIntervalDto) => costRange(i) },
        { title: '方式', width: 90, render: (_: unknown, i: PricingStrategyIntervalDto) => i.type === 'Fixed' ? <span style={{ color: GOLD }}>固定成率</span> : i.type === 'Curve' ? <span style={{ color: BLUE }}>曲线</span> : '旧版规则' },
        { title: '成率 / 售价', align: 'right', render: (_: unknown, i: PricingStrategyIntervalDto) => i.type === 'Fixed' && typeof i.rate === 'number'
          ? <b>{fmtRate(i.rate)}×</b>
          : <span style={{ fontVariantNumeric: 'tabular-nums' }}>{i.startPrice.toFixed(2)} → {i.endPrice.toFixed(2)}</span> },
      ]} />
      <div style={{ ...muted, marginTop: 8 }}>
        进货价恰好等于交界值时按上一个区间计算。{legacy && '旧版区间规则；编辑时可转换为曲线。'}
      </div>
    </div>
    <div>
      <Typography.Text strong style={{ display: 'block', marginBottom: 8 }}>成率随成本变化</Typography.Text>
      <RateChart intervals={intervals} />
    </div>
  </div>
}
