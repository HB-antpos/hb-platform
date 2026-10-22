import { Table, Typography } from 'antd'
import type { PricingStrategyListDto, PricingStrategyNodeDto } from '../../../types/pricingStrategy'
import { fmtRate } from './pricingCurve'


const muted = { color: '#8c8c8c', fontSize: 12 } as const

/** 列表里的迷你曲线：横轴成本，纵轴零售价。 */
function Sparkline({ nodes }: { nodes: PricingStrategyNodeDto[] }) {
  const w = 88, h = 30
  if (nodes.length < 2) return null
  const x0 = nodes[0].cost, x1 = nodes[nodes.length - 1].cost, y1 = Math.max(...nodes.map(n => n.price))
  if (!(x1 > x0) || !(y1 > 0)) return null
  const points = nodes.map(n => `${(4 + (n.cost - x0) / (x1 - x0) * (w - 8)).toFixed(1)},${(h - 3 - n.price / y1 * (h - 8)).toFixed(1)}`).join(' ')
  return <svg width={w} height={h} aria-hidden="true"><polyline points={points} fill="none" stroke="#1677ff" strokeWidth="2" strokeLinejoin="round" /></svg>
}

/** 「定价方式」列：固定成率显示倍数，曲线/旧版规则显示缩略图与成率区间。 */
export function PricingCell({ record }: { record: PricingStrategyListDto }) {
  const mode = record.pricingMode
  // 后端尚未返回摘要字段（先部署 Web、后部署后端的窗口期）时退回到规则数，避免整列空白。
  if (!mode) return <span style={muted}>{record.detailsCount} 条规则</span>
  if (mode === 'Empty') return <span style={muted}>未配置区间</span>
  if (mode === 'Fixed' && typeof record.fixedRate === 'number') {
    return <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
      <span style={{ fontSize: 18, fontWeight: 600, color: '#d48806', fontVariantNumeric: 'tabular-nums' }}>{fmtRate(record.fixedRate)}×</span>
      <div style={{ lineHeight: 1.4 }}><b>固定成率</b><br /><span style={muted}>售价 = 进货价 × {fmtRate(record.fixedRate)}</span></div>
    </div>
  }
  const nodes = record.nodes ?? []
  const range = typeof record.startRate === 'number' && typeof record.endRate === 'number' ? `成率 ${fmtRate(record.startRate)} → ${fmtRate(record.endRate)}` : ''
  return <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
    <Sparkline nodes={nodes} />
    <div style={{ lineHeight: 1.4, fontVariantNumeric: 'tabular-nums' }}>
      <b>{mode === 'Curve' ? '曲线' : '区间规则'}</b> · {mode === 'Curve' ? `${nodes.length} 节点` : `${Math.max(nodes.length - 1, 0)} 个区间`}
      {range && <><br /><span style={muted}>{range}</span></>}
    </div>
  </div>
}

/** 展开行里的「成率随成本变化」：纵轴固定为 1.5～5 倍可行带，一眼看出是否贴边。 */
function RateChart({ nodes }: { nodes: PricingStrategyNodeDto[] }) {
  const pts = nodes.filter(n => n.cost > 0)
  if (pts.length < 2) return null
  const W = 420, H = 132, L = 10, T = 12, plotW = 400, plotH = 100
  const x0 = pts[0].cost, x1 = pts[pts.length - 1].cost
  const P = pts.map(n => [L + (n.cost - x0) / (x1 - x0) * plotW, T + (5 - Math.min(5, Math.max(1.5, n.price / n.cost))) / 3.5 * plotH])
  const rate = (n: PricingStrategyNodeDto) => `${fmtRate(n.price / n.cost)}×`
  return <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" aria-label="成率随成本变化">
    <rect x="0" y="0" width={W} height={H} fill="#fff" stroke="#f0f0f0" rx="6" />
    <rect x={L} y={T} width={plotW} height={plotH} fill="#f6ffed" />
    <text x={L + plotW - 4} y={T + 12} textAnchor="end" fontSize="10" fill="#8c8c8c">5×</text>
    <text x={L + plotW - 4} y={T + plotH - 4} textAnchor="end" fontSize="10" fill="#8c8c8c">1.5×</text>
    <polyline points={P.map(q => `${q[0].toFixed(1)},${q[1].toFixed(1)}`).join(' ')} fill="none" stroke="#1677ff" strokeWidth="2.5" strokeLinejoin="round" />
    {P.map((q, i) => <circle key={i} cx={q[0]} cy={q[1]} r="3.5" fill="#fff" stroke="#1677ff" strokeWidth="2" />)}
    <text x={P[0][0] + 16} y={P[0][1] + 3} fontSize="10" fill="#1677ff">{rate(pts[0])}</text>
    <text x={P[P.length - 1][0] - 6} y={P[P.length - 1][1] - 9} textAnchor="end" fontSize="10" fill="#1677ff">{rate(pts[pts.length - 1])}</text>
    <text x="12" y={H - 6} fontSize="10" fill="#8c8c8c">成本 {Number(x0.toFixed(2))}</text>
    <text x={W - 12} y={H - 6} textAnchor="end" fontSize="10" fill="#8c8c8c">{Number(x1.toFixed(2))}</text>
  </svg>
}

/** 展开行：固定成率显示公式；曲线/旧版规则显示节点表和成率曲线。 */
export function StrategyExpanded({ record }: { record: PricingStrategyListDto }) {
  if (record.pricingMode === 'Fixed' && typeof record.fixedRate === 'number') {
    return <div style={{ padding: '4px 0 4px 24px' }}>
      <div style={{ display: 'inline-block', padding: '10px 14px', background: '#fff', border: '1px solid #f0f0f0', borderRadius: 6 }}>
        零售价 = 进货价 × <b>{fmtRate(record.fixedRate)}</b>
      </div>
      <div style={{ ...muted, marginTop: 8 }}>直接四舍五入到分，不做 .50 / .99 尾数调整；适用于进货价 0.10 以上的全部区间。</div>
    </div>
  }
  const nodes = record.nodes ?? []
  if (!nodes.length) return <span style={muted}>没有可展示的定价节点</span>
  const legacy = record.pricingMode === 'Legacy'
  return <div style={{ display: 'flex', gap: 40, flexWrap: 'wrap', alignItems: 'flex-start', padding: '4px 0 4px 24px' }}>
    <div style={{ flex: '0 1 520px', minWidth: 320 }}>
      <Typography.Text strong style={{ display: 'block', marginBottom: 8 }}>{legacy ? '区间端点' : '定价节点'}</Typography.Text>
      <Table size="small" pagination={false} rowKey={(_, i) => String(i)} dataSource={nodes} columns={[
        { title: '#', width: 48, render: (_: unknown, __: PricingStrategyNodeDto, i: number) => i + 1 },
        { title: '成本', dataIndex: 'cost', render: (v: number) => v.toFixed(2) },
        { title: '零售价', dataIndex: 'price', align: 'right', render: (v: number) => v.toFixed(2) },
        { title: '成率', align: 'right', render: (_: unknown, n: PricingStrategyNodeDto) => n.cost > 0 ? (n.price / n.cost).toFixed(4) : '—' },
      ]} />
      {legacy && <div style={{ ...muted, marginTop: 8 }}>旧版区间规则；编辑时可转换为曲线。</div>}
    </div>
    <div>
      <Typography.Text strong style={{ display: 'block', marginBottom: 8 }}>成率随成本变化</Typography.Text>
      <RateChart nodes={nodes} />
    </div>
  </div>
}
