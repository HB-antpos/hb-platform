import { PlusOutlined } from '@ant-design/icons'
import { Alert, Button, InputNumber, Segmented, Slider } from 'antd'
import { useMemo, useRef, useState } from 'react'
import type { PricingStrategyRuleDto } from '../../../types/pricingStrategy'
import { boundedPrice, curveError, finalPrice, nodesFromRules, rawPrice, roundMoney, rulesFromNodes, safeBend, tailCeil, tailFloor } from './pricingCurve'

// 画布（viewBox 坐标）与绘图区边距；拖动、悬停换算都依赖这组常量
const VW = 760, VH = 470, PAD_L = 54, PAD_R = 20, PAD_T = 16, PAD_B = 44
const PLOT_W = VW - PAD_L - PAD_R, PLOT_H = VH - PAD_T - PAD_B
const RULE_TEXTS = ['成率均在 1.5 ～ 5 之间', '成本增加时售价不下降、成率不上升', '节点售价均为合法尾数（.50 / .99）']
const LEGEND_LINE = { display: 'inline-block', verticalAlign: 'middle', marginRight: 5 } as const

/** 取 1 / 2 / 2.5 / 5 × 10^k 的整齐刻度，用于坐标轴。 */
function niceTicks(lo: number, hi: number, count: number): number[] {
  const raw = (hi - lo) / count
  if (!(raw > 0)) return [lo]
  const mag = 10 ** Math.floor(Math.log10(raw))
  const step = ([1, 2, 2.5, 5, 10].find(m => m * mag >= raw) ?? 10) * mag
  const ticks: number[] = []
  for (let t = Math.ceil(lo / step) * step; t <= hi + 1e-9; t += step) ticks.push(Math.round(t * 1e6) / 1e6)
  return ticks
}
const tickText = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(1))

interface Props { value?: PricingStrategyRuleDto[]; onChange?: (rules: PricingStrategyRuleDto[]) => void }
interface Hover { cost: number; price: number; theory: number }

export default function PricingCurveEditor({ value = [], onChange }: Props) {
  const nodes = useMemo(() => nodesFromRules(value), [value])
  const [selected, setSelected] = useState(0)
  // 显示成本上限：null 表示显示全部；快捷范围取自中间节点的成本
  const [maxCost, setMaxCost] = useState<number | null>(null)
  const [notice, setNotice] = useState('')
  const [hover, setHover] = useState<Hover | null>(null)
  const drag = useRef<number | null>(null)
  const svg = useRef<SVGSVGElement>(null)
  if (nodes.length < 2) return <Alert type="error" message="至少需要两个定价节点" />
  const index = Math.min(selected, nodes.length - 1), node = nodes[index]
  const first = nodes[0].cost, last = nodes[nodes.length - 1].cost
  // 上限已不在当前节点范围内（节点被改动/删除）时回到显示全部
  const end = maxCost !== null && maxCost > first && maxCost < last ? maxCost : last
  const maxY = end * 5
  const X = (x: number) => PAD_L + (x - first) / (end - first) * PLOT_W
  const Y = (y: number) => VH - PAD_B - y / maxY * PLOT_H
  const samples = Array.from({ length: 501 }, (_, i) => {
    const cost = first + (end - first) * i / 500
    const rule = value.find(r => cost >= r.minPrice && cost <= r.maxPrice) ?? value[value.length - 1]
    const price = rawPrice(rule, cost)
    return { cost, price, final: finalPrice(cost, price) }
  })
  const line = samples.map((p, i) => `${i ? 'L' : 'M'}${X(p.cost)},${Y(p.price)}`).join(' ')
  const steps = samples.map((p, i) => i ? `H${X(p.cost)} V${Y(p.final)}` : `M${X(p.cost)},${Y(p.final)}`).join(' ')
  const apply = (next: PricingStrategyRuleDto[]) => {
    const error = curveError(next)
    if (error) { setNotice(error); return false }
    setNotice(''); onChange?.(next); return true
  }
  const updateNode = (i: number, cost: number, wanted: number) => {
    const previous = nodes[i - 1], next = nodes[i + 1]
    if (cost < .1 || (previous && cost <= previous.cost) || (next && cost >= next.cost)) { setNotice('节点成本必须严格递增，且不低于 0.10 元'); return }
    const lo = Math.max(1.5 * cost, previous?.price ?? 0, next ? cost * next.price / next.cost : 0)
    const hi = Math.min(5 * cost, next?.price ?? Infinity, previous ? cost * previous.price / previous.cost : Infinity)
    const bottom = tailCeil(lo), top = tailFloor(hi)
    if (bottom > top + 1e-8) { setNotice('该位置没有满足成率和相邻节点约束的尾数售价'); return }
    const price = roundMoney(Math.min(top, Math.max(bottom, boundedPrice(cost, wanted))))
    const changed = nodes.map((n, j) => j === i ? { cost, price } : n)
    apply(rulesFromNodes(changed, value))
  }
  const segment = Math.min(index, value.length - 1), rule = value[segment]
  const d = (rule.endRetailPrice ?? 0) - (rule.startRetailPrice ?? 0)
  const c = rule.minPrice * (rule.endRetailPrice ?? 0) - rule.maxPrice * (rule.startRetailPrice ?? 0)
  const arcLimit = d > 0 ? Math.max(0, Math.min(1, rule.algorithm === 'ArcDown' ? -c / rule.maxPrice / d : -c / rule.minPrice / d)) : 0
  const bendPercent = arcLimit ? Math.abs(rule.curveBend ?? 0) / arcLimit * 100 : 0
  const setShape = (algorithm: 'Linear' | 'ArcUp' | 'ArcDown') => apply(value.map((r, i) => i === segment ? { ...r, algorithm, curveBend: 0 } : r))
  const pointerValue = (clientY: number) => {
    const box = svg.current!.getBoundingClientRect()
    return ((VH - PAD_B) - (clientY - box.top) * VH / box.height) / PLOT_H * maxY
  }
  const deleteNode = () => {
    const kept = value.filter((_, i) => i !== index)
    kept[index - 1] = { ...kept[index - 1], algorithm: 'Linear', curveBend: 0 }
    if (apply(rulesFromNodes(nodes.filter((_, i) => i !== index), kept))) setSelected(Math.max(0, index - 1))
  }
  const addNode = () => {
    const r = value[segment], cost = roundMoney((r.minPrice + r.maxPrice) / 2)
    const added = [...nodes]; added.splice(segment + 1, 0, { cost, price: boundedPrice(cost, rawPrice(r, cost)) })
    const split = [...value]; split.splice(segment, 1, { ...r, algorithm: 'Linear', curveBend: 0 }, { ...r, algorithm: 'Linear', curveBend: 0 })
    if (apply(rulesFromNodes(added, split))) setSelected(segment + 1)
  }

  // 显示成本快捷范围：全部 + 每个中间节点的成本
  const presets = [{ label: '全部', cost: null as number | null }, ...nodes.slice(1, -1).map(n => ({ label: `≤ ${Number(n.cost.toFixed(2))}`, cost: n.cost as number | null }))]
  const presetActive = (cost: number | null) => cost === null ? end === last : Math.abs(end - cost) < 1e-9
  const shown = hover ?? { cost: node.cost, price: node.price }
  const error = curveError(value)
  // 悬停提示卡：靠近右/下边缘时翻到另一侧，避免被裁掉
  const tip = hover && (() => {
    const x = X(hover.cost), y = Y(hover.price), w = 176, h = 58
    return { w, h, left: x + 14 + w > VW - PAD_R ? x - 14 - w : x + 14, top: y + 14 + h > VH - PAD_B ? y - 14 - h : y + 14 }
  })()
  const card = { border: '1px solid #f0f0f0', borderRadius: 8, background: '#fff' } as const
  const label = { display: 'block', fontSize: 12, color: '#8c8c8c', marginBottom: 2 } as const

  return <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'stretch' }}>
    <div style={{ ...card, flex: '1 1 560px', minWidth: 0, padding: '12px 14px 10px', display: 'flex', flexDirection: 'column' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', marginBottom: 6 }}>
        <span style={{ color: '#595959', fontSize: 13 }}>显示成本</span>
        <div style={{ display: 'flex', gap: 6 }}>
          {presets.map(p => <Button key={p.label} size="small" shape="round" type={presetActive(p.cost) ? 'primary' : 'default'} ghost={presetActive(p.cost)} aria-pressed={presetActive(p.cost)} onClick={() => setMaxCost(p.cost)}>{p.label}</Button>)}
        </div>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: 14, fontSize: 12, color: '#595959', flexWrap: 'wrap' }}>
          <span><i style={{ ...LEGEND_LINE, width: 18, height: 3, background: '#1677ff' }} />理论售价</span>
          <span><i style={{ ...LEGEND_LINE, width: 18, height: 0, borderTop: '2px solid #fa8c16' }} />实际售价</span>
          <span><i style={{ ...LEGEND_LINE, width: 14, height: 10, background: '#f6ffed', border: '1px solid #b7eb8f' }} />1.5～5 倍范围</span>
        </div>
      </div>
      {notice && <Alert type="warning" showIcon message={notice} style={{ marginBottom: 6 }} />}
      <svg ref={svg} viewBox={`0 0 ${VW} ${VH}`} role="img" aria-label="成本与零售价曲线，可通过右侧数字输入编辑节点" style={{ width: '100%', display: 'block', touchAction: 'none' }} onPointerMove={e => {
        if (drag.current !== null) { updateNode(drag.current, nodes[drag.current].cost, pointerValue(e.clientY)); return }
        const box = e.currentTarget.getBoundingClientRect(), cost = first + Math.max(0, Math.min(1, ((e.clientX - box.left) * VW / box.width - PAD_L) / PLOT_W)) * (end - first)
        const r = value.find(item => cost >= item.minPrice && cost <= item.maxPrice)
        if (r) setHover({ cost, price: finalPrice(cost, rawPrice(r, cost)), theory: rawPrice(r, cost) })
      }} onPointerUp={() => { drag.current = null }} onPointerCancel={() => { drag.current = null }} onPointerLeave={() => setHover(null)}>
        <defs><clipPath id="pricing-curve-plot"><rect x={PAD_L} y={PAD_T} width={PLOT_W} height={PLOT_H} /></clipPath></defs>
        {niceTicks(0, maxY, 4).map(t => <g key={`y${t}`}><line x1={PAD_L} x2={VW - PAD_R} y1={Y(t)} y2={Y(t)} stroke="#f0f0f0" /><text x={PAD_L - 8} y={Y(t) + 4} textAnchor="end" fontSize="11" fill="#8c8c8c">{tickText(t)}</text></g>)}
        {niceTicks(first, end, 5).map(t => <text key={`x${t}`} x={X(t)} y={VH - PAD_B + 16} textAnchor="middle" fontSize="11" fill="#8c8c8c">{tickText(t)}</text>)}
        <text x={PAD_L} y="10" fontSize="11" fill="#8c8c8c">零售价（元）</text>
        <text x={(PAD_L + VW - PAD_R) / 2} y={VH - 6} textAnchor="middle" fontSize="11" fill="#8c8c8c">进货成本（元）</text>
        <g clipPath="url(#pricing-curve-plot)">
          <path d={`M${X(first)},${Y(first * 1.5)} L${X(end)},${Y(end * 1.5)} L${X(end)},${Y(end * 5)} L${X(first)},${Y(first * 5)} Z`} fill="#f6ffed" stroke="#d9f7be" />
          <path d={steps} fill="none" stroke="#fa8c16" strokeWidth="2" />
          <path d={line} fill="none" stroke="#1677ff" strokeWidth="2.5" />
        </g>
        {hover && <g pointerEvents="none"><line x1={X(hover.cost)} x2={X(hover.cost)} y1={PAD_T} y2={VH - PAD_B} stroke="#bfbfbf" strokeDasharray="3 3" /><circle cx={X(hover.cost)} cy={Y(hover.price)} r="4" fill="#fa8c16" /></g>}
        {last > end && <text x={VW - PAD_R - 6} y={PAD_T + 16} textAnchor="end" fontSize="11" fill="#8c8c8c" pointerEvents="none">{`节点 ${nodes.length}（成本 ${Number(last.toFixed(2))}）在显示范围外 →`}</text>}
        {nodes.map((n, i) => n.cost <= end && <g key={i}>
          {index === i && <circle cx={X(n.cost)} cy={Y(n.price)} r="14" fill="#1677ff" fillOpacity=".12" pointerEvents="none" />}
          <circle cx={X(n.cost)} cy={Y(n.price)} r={index === i ? 8 : 6} fill="white" stroke="#1677ff" strokeWidth="3" tabIndex={0} role="slider" aria-label={`节点 ${i + 1} 售价`} aria-valuemin={n.cost * 1.5} aria-valuemax={n.cost * 5} aria-valuenow={n.price} onFocus={() => setSelected(i)} onKeyDown={e => { if (e.key === 'ArrowUp' || e.key === 'ArrowDown') { e.preventDefault(); updateNode(i, n.cost, n.price + (e.key === 'ArrowUp' ? .51 : -.51)) } }} onPointerDown={e => { setSelected(i); drag.current = i; e.currentTarget.setPointerCapture(e.pointerId) }} style={{ cursor: 'ns-resize' }}><title>{`成本 ${n.cost.toFixed(2)} · 售价 ${n.price.toFixed(2)} · 成率 ${(n.price / n.cost).toFixed(4)}`}</title></circle>
          <text x={X(n.cost)} y={Y(n.price) - (index === i ? 18 : 13)} textAnchor="middle" fontSize="11" fontWeight="600" fill="#1677ff" pointerEvents="none">{i + 1}</text>
        </g>)}
        {hover && tip && <g pointerEvents="none">
          <rect x={tip.left} y={tip.top} width={tip.w} height={tip.h} rx="6" fill="#262626" />
          <text x={tip.left + 12} y={tip.top + 18} fontSize="12" fill="#fff">成本 {hover.cost.toFixed(2)}</text>
          <text x={tip.left + 12} y={tip.top + 35} fontSize="12" fill="#d9d9d9">理论 {hover.theory.toFixed(2)}</text>
          <text x={tip.left + 12} y={tip.top + 52} fontSize="12" fill="#fff">实际 {hover.price.toFixed(2)} · 成率 {(hover.price / hover.cost).toFixed(4)}</text>
        </g>}
      </svg>
      <div style={{ marginTop: 'auto', padding: '8px 12px', background: '#fafafa', borderRadius: 6, display: 'flex', gap: 22, alignItems: 'center', flexWrap: 'wrap', fontSize: 13, color: '#595959' }}>
        <span>成本 <b style={{ color: '#1f1f1f' }}>{shown.cost.toFixed(2)}</b> 元</span>
        <span>实际售价 <b style={{ color: '#1f1f1f' }}>{shown.price.toFixed(2)}</b> 元</span>
        <span>成率 <b style={{ color: '#1f1f1f' }}>{(shown.price / shown.cost).toFixed(4)}</b></span>
        <span style={{ marginLeft: 'auto', color: '#8c8c8c', fontSize: 12 }}>↑ ↓ 微调选中节点 · 拖动圆点改售价</span>
      </div>
    </div>

    <div style={{ ...card, flex: '0 0 372px', maxWidth: '100%', display: 'flex', flexDirection: 'column' }}>
      <div style={{ padding: '12px 14px', display: 'flex', justifyContent: 'space-between', alignItems: 'center', borderBottom: '1px solid #f0f0f0', fontWeight: 600 }}>
        <span>定价节点 <span style={{ fontWeight: 400, color: '#8c8c8c' }}>· {nodes.length} 个</span></span>
        <Button size="small" icon={<PlusOutlined />} title={`在区间 ${segment + 1} 的中点添加节点`} onClick={addNode}>添加节点</Button>
      </div>
      <div role="listbox" aria-label="定价节点" style={{ padding: '6px 8px', fontVariantNumeric: 'tabular-nums' }}>
        <div style={{ display: 'grid', gridTemplateColumns: '22px 1fr 1fr 64px', gap: 8, padding: '0 8px 2px', fontSize: 12, color: '#8c8c8c' }}><span /><span>成本</span><span style={{ textAlign: 'right' }}>零售价</span><span style={{ textAlign: 'right' }}>成率</span></div>
        {nodes.map((n, i) => <div key={i} role="option" aria-selected={index === i} tabIndex={0} onClick={() => setSelected(i)} onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setSelected(i) } }}
          style={{ display: 'grid', gridTemplateColumns: '22px 1fr 1fr 64px', gap: 8, alignItems: 'center', padding: '7px 8px', borderRadius: 6, fontSize: 13, cursor: 'pointer', background: index === i ? '#e6f4ff' : undefined, outline: index === i ? '1px solid #91caff' : undefined }}>
          <span style={{ width: 20, height: 20, borderRadius: '50%', fontSize: 12, textAlign: 'center', lineHeight: '20px', background: index === i ? '#1677ff' : '#f0f0f0', color: index === i ? '#fff' : '#595959' }}>{i + 1}</span>
          <span>{n.cost.toFixed(2)}</span><span style={{ textAlign: 'right' }}>{n.price.toFixed(2)}</span><span style={{ textAlign: 'right', color: '#595959' }}>{(n.price / n.cost).toFixed(4)}</span>
        </div>)}
        <div style={{ padding: '4px 8px 0', fontSize: 12, color: '#8c8c8c' }}>添加 / 删除节点会把相邻区间重置为直线</div>
      </div>
      <div style={{ margin: '4px 14px 0', padding: 12, border: '1px solid #f0f0f0', borderRadius: 8 }}>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
          <div><label style={label}>节点 {index + 1} 成本</label><InputNumber aria-label="节点成本" min={.1} precision={2} value={node.cost} onChange={v => v !== null && updateNode(index, v, node.price)} style={{ width: '100%' }} /></div>
          <div><label style={label}>节点 {index + 1} 零售价</label><InputNumber aria-label="节点零售价" min={node.cost * 1.5} max={node.cost * 5} precision={2} value={node.price} onChange={v => v !== null && updateNode(index, node.cost, v)} style={{ width: '100%' }} /></div>
        </div>
        <div style={{ marginTop: 8, display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
          <span style={{ fontSize: 12, color: '#8c8c8c' }}>可选 .50 / .99 尾数，范围 {(node.cost * 1.5).toFixed(2)}～{(node.cost * 5).toFixed(2)}</span>
          <Button size="small" danger disabled={index === 0 || index === nodes.length - 1} onClick={deleteNode}>删除节点</Button>
        </div>
      </div>
      <div style={{ margin: '12px 14px 0', paddingTop: 12, borderTop: '1px dashed #d9d9d9' }}>
        <div style={{ fontWeight: 600, marginBottom: 8, display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
          <span>区间 {segment + 1} · {rule.minPrice.toFixed(2)} ～ {rule.maxPrice.toFixed(2)}</span>
          <span style={{ fontWeight: 400, color: '#8c8c8c', fontSize: 12 }}>节点 {segment + 1} → 节点 {segment + 2}</span>
        </div>
        <Segmented block aria-label="曲线形状" value={rule.algorithm as 'Linear' | 'ArcUp' | 'ArcDown'} onChange={v => setShape(v as 'Linear' | 'ArcUp' | 'ArcDown')} options={[{ value: 'Linear', label: '直线' }, { value: 'ArcUp', label: '上弧线' }, { value: 'ArcDown', label: '下弧线' }]} />
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginTop: 8, fontSize: 13, color: '#8c8c8c' }}>
          <span>弧度</span>
          <Slider ariaLabelForHandle="区间弧度" disabled={rule.algorithm === 'Linear' || arcLimit === 0} min={0} max={100} value={bendPercent} onChange={v => apply(value.map((r, i) => i === segment ? { ...r, curveBend: safeBend((rule.algorithm === 'ArcDown' ? -1 : 1) * arcLimit * v / 100) } : r))} style={{ flex: 1, margin: '6px 4px' }} />
          <span style={{ width: 34, textAlign: 'right' }}>{Math.round(bendPercent)}%</span>
        </div>
        <div style={{ fontSize: 12, color: '#8c8c8c' }}>选择弧线后可调弧度，已自动限制在安全范围内</div>
      </div>
      <div style={{ margin: '12px 14px 14px', padding: '10px 12px', background: '#fafafa', borderRadius: 6, fontSize: 12, color: '#595959', lineHeight: 1.8 }}>
        {error ? <div style={{ color: '#cf1322' }}>✗ {error}</div> : RULE_TEXTS.map(text => <div key={text}><span style={{ color: '#52c41a', fontWeight: 600 }}>✓</span> {text}</div>)}
      </div>
    </div>
  </div>
}
