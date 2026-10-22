import { PlusOutlined } from '@ant-design/icons'
import { Alert, Button, InputNumber, Popconfirm, Segmented, Slider, type InputNumberProps } from 'antd'
import { useRef, useState } from 'react'
import { defaultCurve, fixedRules, fmtRate } from './pricingCurve'
import {
  MAX_INTERVALS, UNBOUNDED_COST, actualPrice, addInterval, appendInterval, arcLimit, breakLink, chartRange, endPrice, hasCurveSide, intervalAt, intervalsError,
  formatBound, isFixed, linkedFlags, parseBound, priceDrops, relink, removeInterval, setBend, setBoundary, setEdge, setEndpoint, setEndpointRate, setKind, setRate, setShape,
  shiftBrokenAfterAdd, shiftBrokenAfterRemove, startPrice, theoryPrice, visibleMaxPrice, type Result, type Rule,
} from './pricingIntervals'

// 画布（viewBox 坐标）与绘图区边距；拖动、悬停换算都依赖这组常量
const VW = 760, VH = 600, PAD_L = 54, PAD_R = 22, PAD_T = 70, PAD_B = 44
const PLOT_W = VW - PAD_L - PAD_R, PLOT_H = VH - PAD_T - PAD_B
const GOLD = '#d48806', BLUE = '#1677ff', ORANGE = '#fa8c16'
const RATE_PRESETS = [1.5, 2, 2.5, 3, 3.5, 5]
const LEGEND = { display: 'inline-block', verticalAlign: 'middle', marginRight: 5 } as const

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
const tickText = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(2).replace(/0$/, ''))
const money = (n: number) => n.toFixed(2)
const costText = (r: Rule) => `${money(r.minPrice)} ~ ${r.maxPrice >= UNBOUNDED_COST ? '不限' : money(r.maxPrice)}`

/**
 * 数字输入框：输入过程中只保存草稿、不校验，失焦或回车才提交（Esc 还原）；点上下箭头直接提交。
 * 否则每敲一个字符就校验，会把「想输 20，敲 2 时小于下限」这类中间状态拒掉并弹回原值，输入就像被锁死。
 * onCommit 返回 false（校验不通过）时，输入框还原为原值，错误提示由编辑器顶部显示。
 */
function NumberField({ value, onCommit, ariaLabel, ...rest }: Omit<InputNumberProps<number>, 'value' | 'onChange'> & { value: number; onCommit: (v: number) => boolean; ariaLabel: string }) {
  const [draft, setDraftState] = useState<number | null | undefined>(undefined)
  // 用 ref 同步保存最新草稿：Esc 与失焦可能在同一个事件循环里先后触发，闭包里的 state 还没更新。
  const draftRef = useRef<number | null | undefined>(undefined)
  const setDraft = (v: number | null | undefined) => { draftRef.current = v; setDraftState(v) }
  const commit = (v: number | null | undefined) => {
    setDraft(undefined)
    if (v === null || v === undefined || Number.isNaN(v) || v === value) return
    onCommit(v)
  }
  return <InputNumber<number> {...rest} aria-label={ariaLabel} value={draft === undefined ? value : draft}
    onChange={v => setDraft(v)} onBlur={() => commit(draftRef.current)} onPressEnter={() => commit(draftRef.current)} onStep={v => commit(v)}
    onKeyDown={e => { if (e.key === 'Escape') setDraft(undefined) }} />
}

interface Hover { cost: number; price: number; theory: number; index: number }
type Drag = { kind: 'endpoint'; i: number; which: 'start' | 'end' } | { kind: 'boundary'; i: number }
interface Props { value?: Rule[]; onChange?: (rules: Rule[]) => void }

/**
 * 区间式定价编辑器：每个区间设置进货价上下限，并选择「固定成率」或「曲线」。
 * 相邻区间共用交界；交界默认联动（曲线端点跟随相邻固定价 / 邻居曲线端点），可以断开后独立设置，
 * 断开后交界处的售价允许回落（阶梯定价），由图、列表和保存时的二次确认提示。
 */
export default function IntervalEditor({ value = [], onChange }: Props) {
  const rules = value
  const [selected, setSelected] = useState(0)
  // 显示成本上限：null 表示显示全部；快捷范围取自各交界成本
  const [maxCost, setMaxCost] = useState<number | null>(null)
  const [notice, setNotice] = useState('')
  const [hover, setHover] = useState<Hover | null>(null)
  // 用户主动断开联动的交界下标（库里不存联动状态，重新打开时按「售价是否连续」推断）
  const [broken, setBroken] = useState<ReadonlySet<number>>(new Set())
  const drag = useRef<Drag | null>(null)
  const svg = useRef<SVGSVGElement>(null)
  if (!rules.length) return <Alert type="error" message="至少需要一个定价区间" />

  const n = rules.length
  const index = Math.min(selected, n - 1), sel = rules[index]
  const flags = linkedFlags(rules, broken)
  const drops = priceDrops(rules)
  const error = intervalsError(rules)
  const { first, fullEnd, end, presets } = chartRange(rules, maxCost)
  const maxY = Math.max(visibleMaxPrice(rules, end) * 1.2, 5)
  const X = (x: number) => PAD_L + (x - first) / (end - first) * PLOT_W
  const Y = (y: number) => VH - PAD_B - y / maxY * PLOT_H

  const apply = (result: Result): boolean => {
    if (!result.rules) { setNotice(result.error ?? ''); return false }
    setNotice(''); onChange?.(result.rules); return true
  }
  const pointerPrice = (clientY: number) => {
    const box = svg.current!.getBoundingClientRect()
    return ((VH - PAD_B) - (clientY - box.top) * VH / box.height) / PLOT_H * maxY
  }
  const pointerCost = (clientX: number) => {
    const box = svg.current!.getBoundingClientRect()
    return first + Math.max(0, Math.min(1, ((clientX - box.left) * VW / box.width - PAD_L) / PLOT_W)) * (end - first)
  }

  // 添加区间：接在最后一个区间后面（新区间不在已有区间里面）；拆分区间：把选中的区间从中点切成两个
  const onAppend = () => { if (apply(appendInterval(rules))) setSelected(n) }
  const onSplit = () => {
    if (apply(addInterval(rules, index))) { setBroken(shiftBrokenAfterAdd(broken, index)); setSelected(index + 1) }
  }
  const onRemove = () => {
    if (apply(removeInterval(rules, index))) { setBroken(shiftBrokenAfterRemove(broken, index)); setSelected(Math.max(0, index - 1)) }
  }
  const reset = (next: Rule[]) => { setBroken(new Set()); setSelected(0); setMaxCost(null); setNotice(''); onChange?.(next) }

  // 端点 / 交界的联动状态与按钮
  const linkRow = (i: number, side: 'start' | 'end') => {
    const b = side === 'start' ? i - 1 : i
    if (b < 0 || b >= n - 1) return null
    const nb = rules[side === 'start' ? i - 1 : i + 1]
    const linked = flags[b]
    const nbText = isFixed(nb) ? `固定 ${fmtRate(nb.startRate)}×` : `区间 ${side === 'start' ? i : i + 2}`
    return linked
      ? <div style={{ fontSize: 12, color: BLUE, marginTop: 4 }}>🔗 跟随{nbText}
        <a style={{ marginLeft: 6 }} onClick={() => { if (apply(breakLink(rules, b))) setBroken(new Set([...broken, b])) }}>断开</a></div>
      : <div style={{ fontSize: 12, color: '#8c8c8c', marginTop: 4 }}>已断开，可独立设置
        <a style={{ marginLeft: 6 }} onClick={() => { if (apply(relink(rules, b, side === 'start' ? 'right' : 'left'))) { const next = new Set(broken); next.delete(b); setBroken(next) } }}>重新联动</a></div>
  }

  const card = { border: '1px solid #f0f0f0', borderRadius: 8, background: '#fff' } as const
  const fieldLabel = { display: 'block', fontSize: 12, color: '#8c8c8c', marginBottom: 2 } as const
  const tag = (r: Rule) => isFixed(r)
    ? <span style={{ marginLeft: 'auto', fontSize: 12, color: GOLD, background: '#fffbe6', border: '1px solid #ffe58f', borderRadius: 4, padding: '0 8px', lineHeight: '20px' }}>固定 {fmtRate(r.startRate)}×</span>
    : <span style={{ marginLeft: 'auto', fontSize: 12, color: BLUE, background: '#e6f4ff', border: '1px solid #91caff', borderRadius: 4, padding: '0 8px', lineHeight: '20px' }}>曲线 · {money(startPrice(r))} → {money(endPrice(r))}</span>

  // 图：每个区间的折线点
  const intervalPaths = rules.map(r => {
    if (r.minPrice >= end) return null
    const lo = r.minPrice, hi = Math.min(r.maxPrice, end)
    if (isFixed(r)) return { fixed: true, line: `M${X(lo)},${Y(lo * r.startRate)} L${X(hi)},${Y(hi * r.startRate)}`, steps: '' }
    let line = '', steps = ''
    for (let k = 0; k <= 240; k++) {
      const c = lo + (hi - lo) * k / 240, p = theoryPrice(r, c), f = actualPrice(r, c)
      line += `${k ? 'L' : 'M'}${X(c).toFixed(1)},${Y(p).toFixed(1)}`
      steps += k ? `H${X(c).toFixed(1)}V${Y(f).toFixed(1)}` : `M${X(c).toFixed(1)},${Y(f).toFixed(1)}`
    }
    return { fixed: false, line, steps }
  })
  const hiddenCount = rules.filter(r => r.minPrice >= end).length
  const shown = hover ?? { cost: sel.minPrice, price: actualPrice(sel, sel.minPrice), theory: theoryPrice(sel, sel.minPrice), index }
  const tip = hover && (() => {
    const x = X(hover.cost), y = Y(hover.price), w = 196, h = 58
    return { w, h, left: x + 14 + w > VW - PAD_R ? x - 14 - w : x + 14, top: y - 14 - h < PAD_T ? y + 14 : y - 14 - h }
  })()

  return <div style={{ marginBottom: 16 }}>
    <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap', marginBottom: 10 }}>
      <b style={{ fontSize: 15 }}>价格区间</b><span style={{ color: '#8c8c8c', fontSize: 13 }}>· {n} 个</span>
      <span style={{ color: '#8c8c8c', fontSize: 12 }}>每个区间设置进货价的上下限，并选择「固定成率」或「曲线」；相邻区间共用交界，进货价等于交界值时按上一个区间算</span>
      <span style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>
        <Popconfirm title="替换当前所有区间？" description="整段使用同一个固定成率 2.5×" okText="替换" cancelText="取消" onConfirm={() => reset(fixedRules(2.5))}><Button size="small">整段固定 2.5×</Button></Popconfirm>
        <Popconfirm title="替换当前所有区间？" description="恢复为默认曲线" okText="替换" cancelText="取消" onConfirm={() => reset(defaultCurve())}><Button size="small">默认曲线</Button></Popconfirm>
        <Button size="small" disabled={n >= MAX_INTERVALS} title={n >= MAX_INTERVALS ? `最多 ${MAX_INTERVALS} 个区间` : `把区间 ${index + 1} 从中点切成两个`} onClick={onSplit}>拆分区间</Button>
        <Button size="small" type="primary" ghost icon={<PlusOutlined />} disabled={n >= MAX_INTERVALS || rules[n - 1].maxPrice >= UNBOUNDED_COST} title={n >= MAX_INTERVALS ? `最多 ${MAX_INTERVALS} 个区间` : rules[n - 1].maxPrice >= UNBOUNDED_COST ? '最后一个区间的上限是「不限」，请用「拆分区间」，或先给它设一个具体上限' : `在末尾追加区间 ${n + 1}（从 ${money(rules[n - 1].maxPrice)} 往上）`} onClick={onAppend}>添加区间</Button>
      </span>
    </div>

    <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap', alignItems: 'stretch' }}>
      <div style={{ ...card, flex: '1 1 560px', minWidth: 0, padding: '12px 14px 10px', display: 'flex', flexDirection: 'column' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', marginBottom: 4 }}>
          <span style={{ color: '#595959', fontSize: 13 }}>显示成本</span>
          <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            {[{ label: '全部', cost: null as number | null }, ...presets.map(c => ({ label: `≤ ${Number(c.toFixed(2))}`, cost: c as number | null }))].map(p => {
              const active = p.cost === null ? end === fullEnd : Math.abs(end - p.cost) < 1e-9
              return <Button key={p.label} size="small" shape="round" type={active ? 'primary' : 'default'} ghost={active} aria-pressed={active} onClick={() => setMaxCost(p.cost)}>{p.label}</Button>
            })}
          </div>
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 14, fontSize: 12, color: '#595959', flexWrap: 'wrap' }}>
            <span><i style={{ ...LEGEND, width: 18, height: 3, background: GOLD }} />固定成率</span>
            <span><i style={{ ...LEGEND, width: 18, height: 3, background: BLUE }} />曲线理论价</span>
            <span><i style={{ ...LEGEND, width: 18, height: 0, borderTop: `2px solid ${ORANGE}` }} />曲线实际价</span>
            <span><i style={{ ...LEGEND, width: 14, height: 10, background: '#f6ffed', border: '1px solid #b7eb8f' }} />1.5～5 倍</span>
          </div>
        </div>
        {notice && <Alert type="warning" showIcon message={notice} style={{ marginBottom: 6 }} />}
        <svg ref={svg} viewBox={`0 0 ${VW} ${VH}`} role="img" aria-label="各区间的成本与零售价曲线，可通过右侧数字输入编辑" style={{ width: '100%', display: 'block', touchAction: 'none' }}
          onPointerMove={e => {
            const d = drag.current
            if (d?.kind === 'endpoint') { apply(setEndpoint(rules, d.i, d.which, pointerPrice(e.clientY), flags)); return }
            if (d?.kind === 'boundary') { apply(setBoundary(rules, d.i, pointerCost(e.clientX), flags)); return }
            const cost = pointerCost(e.clientX), i = intervalAt(rules, cost)
            if (i >= 0) setHover({ cost, price: actualPrice(rules[i], cost), theory: theoryPrice(rules[i], cost), index: i })
          }}
          onPointerUp={() => { drag.current = null }} onPointerCancel={() => { drag.current = null }} onPointerLeave={() => setHover(null)}>
          <defs><clipPath id="interval-plot"><rect x={PAD_L} y={PAD_T} width={PLOT_W} height={PLOT_H} /></clipPath></defs>
          <g clipPath="url(#interval-plot)">
            <polygon points={`${X(first)},${Y(first * 1.5)} ${X(end)},${Y(end * 1.5)} ${X(end)},${Y(end * 5)} ${X(first)},${Y(first * 5)}`} fill="#f6ffed" stroke="#d9f7be" />
            {rules.map((r, i) => r.minPrice < end && <rect key={i} x={X(r.minPrice)} y={PAD_T} width={Math.max(X(Math.min(r.maxPrice, end)) - X(r.minPrice), 0)} height={PLOT_H} fill={isFixed(r) ? '#fffbe6' : '#e6f4ff'} opacity=".55" />)}
          </g>
          {niceTicks(0, maxY, 4).map(t => <g key={`y${t}`}><line x1={PAD_L} x2={VW - PAD_R} y1={Y(t)} y2={Y(t)} stroke="#f0f0f0" /><text x={PAD_L - 8} y={Y(t) + 4} textAnchor="end" fontSize="11" fill="#8c8c8c">{tickText(t)}</text></g>)}
          {niceTicks(first, end, 5).map(t => <text key={`x${t}`} x={X(t)} y={VH - PAD_B + 16} textAnchor="middle" fontSize="11" fill="#8c8c8c">{tickText(t)}</text>)}
          <text x={PAD_L} y={PAD_T - 6} fontSize="11" fill="#8c8c8c">零售价（元）</text>
          <text x={(PAD_L + VW - PAD_R) / 2} y={VH - 6} textAnchor="middle" fontSize="11" fill="#8c8c8c">进货成本（元）</text>

          {/* 顶部区间条：点击选中；窄区间用两行短标签 */}
          {rules.map((r, i) => {
            if (r.minPrice >= end) return null
            const x0 = X(r.minPrice), x1 = X(Math.min(r.maxPrice, end)), w = x1 - x0, fx = isFixed(r), color = fx ? GOLD : BLUE
            return <g key={`s${i}`} onClick={() => setSelected(i)} style={{ cursor: 'pointer' }}>
              <rect x={x0 + 1} y={10} width={Math.max(w - 2, 0)} height={44} rx="6" fill={fx ? '#fffbe6' : '#e6f4ff'} stroke={i === index ? BLUE : fx ? '#ffe58f' : '#91caff'} strokeWidth={i === index ? 2 : 1} />
              {w < 100
                ? <><text x={(x0 + x1) / 2} y={29} textAnchor="middle" fontSize="11" fontWeight="600" fill={color}>{i + 1} {fx ? '固定' : '曲线'}</text>{fx && <text x={(x0 + x1) / 2} y={44} textAnchor="middle" fontSize="11" fontWeight="600" fill={GOLD}>{fmtRate(r.startRate)}×</text>}</>
                : <><text x={(x0 + x1) / 2} y={30} textAnchor="middle" fontSize="12" fontWeight="600" fill={color}>{i + 1} · {fx ? `固定 ${fmtRate(r.startRate)}×` : '曲线'}</text><text x={(x0 + x1) / 2} y={45} textAnchor="middle" fontSize="11" fill="#8c8c8c">{costText(r)}</text></>}
            </g>
          })}
          {hiddenCount > 0 && <text x={VW - PAD_R - 4} y={PAD_T + 16} textAnchor="end" fontSize="11" fill="#8c8c8c" pointerEvents="none">{`还有 ${hiddenCount} 个区间在显示范围外 →`}</text>}

          <g clipPath="url(#interval-plot)">
            {intervalPaths.map((p, i) => p && (p.fixed
              ? <path key={i} d={p.line} fill="none" stroke={GOLD} strokeWidth="3" />
              : <g key={i}><path d={p.steps} fill="none" stroke={ORANGE} strokeWidth="2" /><path d={p.line} fill="none" stroke={BLUE} strokeWidth="2.5" /></g>))}
          </g>
          {rules.map((r, i) => isFixed(r) && r.minPrice < end && (() => { const c = (r.minPrice + Math.min(r.maxPrice, end)) / 2; return <text key={`f${i}`} x={X(c)} y={Y(c * r.startRate) - 12} textAnchor="middle" fontSize="11" fontWeight="600" fill={GOLD} pointerEvents="none">×{fmtRate(r.startRate)}</text> })())}

          {/* 交界：虚线 + ◇ 把手（可左右拖动）+ 回落标注 */}
          {rules.slice(0, -1).map((r, i) => {
            if (r.maxPrice >= end) return null
            const x = X(r.maxPrice), drop = drops.find(d => d.index === i)
            return <g key={`b${i}`}>
              <line x1={x} x2={x} y1={PAD_T} y2={VH - PAD_B} stroke={drop ? ORANGE : '#bfbfbf'} strokeDasharray="4 3" pointerEvents="none" />
              {drop && <><line x1={x} x2={x} y1={Y(drop.from)} y2={Y(drop.to)} stroke="#d4380d" strokeWidth="3" pointerEvents="none" />
                <rect x={x + 10} y={Math.max(Y(drop.from) - 70, PAD_T + 4)} width="98" height="38" rx="6" fill="#fff7e6" stroke="#ffbb96" pointerEvents="none" />
                <text x={x + 18} y={Math.max(Y(drop.from) - 70, PAD_T + 4) + 17} fontSize="11" fontWeight="600" fill="#d4380d" pointerEvents="none">▼ 回落 {money(drop.to - drop.from)}</text>
                <text x={x + 18} y={Math.max(Y(drop.from) - 70, PAD_T + 4) + 32} fontSize="11" fill="#ad4e00" pointerEvents="none">{money(drop.from)} → {money(drop.to)}</text></>}
              <rect x={x - 6} y={VH - PAD_B - 6} width="12" height="12" transform={`rotate(45 ${x} ${VH - PAD_B})`} fill="#fff" stroke="#8c8c8c" strokeWidth="1.5" tabIndex={0} role="slider"
                aria-label={`交界 ${i + 1} 成本`} aria-valuenow={r.maxPrice} aria-valuemin={r.minPrice} aria-valuemax={rules[i + 1].maxPrice} style={{ cursor: 'ew-resize' }}
                onFocus={() => setSelected(i)} onKeyDown={e => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); apply(setBoundary(rules, i, r.maxPrice + (e.key === 'ArrowRight' ? 1 : -1), flags)) } }}
                onPointerDown={e => { setSelected(i); drag.current = { kind: 'boundary', i }; e.currentTarget.setPointerCapture(e.pointerId) }}><title>{`交界 ${money(r.maxPrice)}：≤ 属于区间 ${i + 1}，> 属于区间 ${i + 2}`}</title></rect>
            </g>
          })}

          {/* 曲线端点：可上下拖动；跟随相邻固定区间的端点锁定 */}
          {rules.map((r, i) => {
            if (isFixed(r)) return null
            const ends: Array<['start' | 'end', number, number]> = [['start', r.minPrice, startPrice(r)], ['end', r.maxPrice, endPrice(r)]]
            return ends.map(([which, c, p]) => {
              if (c > end) return null
              const b = which === 'start' ? i - 1 : i, nb = rules[which === 'start' ? i - 1 : i + 1]
              const locked = b >= 0 && b < n - 1 && flags[b] && !!nb && isFixed(nb), on = i === index
              return <g key={`${i}${which}`}>
                {on && <circle cx={X(c)} cy={Y(p)} r="14" fill={BLUE} fillOpacity=".12" pointerEvents="none" />}
                <circle cx={X(c)} cy={Y(p)} r={on ? 8 : 6} fill={locked ? '#f5f5f5' : '#fff'} stroke={locked ? '#8c8c8c' : BLUE} strokeWidth="3" tabIndex={locked ? -1 : 0} role="slider"
                  aria-label={`区间 ${i + 1} ${which === 'start' ? '起点' : '终点'}售价`} aria-valuenow={p} aria-disabled={locked} style={{ cursor: locked ? 'not-allowed' : 'ns-resize' }}
                  onFocus={() => setSelected(i)}
                  onKeyDown={e => { if (!locked && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) { e.preventDefault(); apply(setEndpoint(rules, i, which, p + (e.key === 'ArrowUp' ? .51 : -.51), flags)) } }}
                  onPointerDown={e => { setSelected(i); if (locked) return; drag.current = { kind: 'endpoint', i, which }; e.currentTarget.setPointerCapture(e.pointerId) }}>
                  <title>{`成本 ${money(c)} · 售价 ${money(p)} · 成率 ${(p / c).toFixed(4)}${locked ? '（跟随固定区间，需先断开）' : ''}`}</title></circle>
              </g>
            })
          })}

          {hover && tip && <g pointerEvents="none">
            <line x1={X(hover.cost)} x2={X(hover.cost)} y1={PAD_T} y2={VH - PAD_B} stroke="#bfbfbf" strokeDasharray="3 3" />
            <circle cx={X(hover.cost)} cy={Y(hover.price)} r="4" fill={ORANGE} />
            <rect x={tip.left} y={tip.top} width={tip.w} height={tip.h} rx="6" fill="#262626" />
            <text x={tip.left + 12} y={tip.top + 18} fontSize="12" fill="#fff">成本 {money(hover.cost)} · 区间 {hover.index + 1}（{isFixed(rules[hover.index]) ? '固定' : '曲线'}）</text>
            <text x={tip.left + 12} y={tip.top + 35} fontSize="12" fill="#d9d9d9">理论 {money(hover.theory)}</text>
            <text x={tip.left + 12} y={tip.top + 52} fontSize="12" fill="#fff">实际 {money(hover.price)} · 成率 {(hover.price / hover.cost).toFixed(4)}</text>
          </g>}
        </svg>
        <div style={{ marginTop: 'auto', padding: '8px 12px', background: '#fafafa', borderRadius: 6, display: 'flex', gap: 22, alignItems: 'center', flexWrap: 'wrap', fontSize: 13, color: '#595959' }}>
          <span>成本 <b style={{ color: '#1f1f1f' }}>{money(shown.cost)}</b> 元</span>
          <span>区间 <b style={{ color: '#1f1f1f' }}>{shown.index + 1}</b>（{isFixed(rules[shown.index]) ? '固定' : '曲线'}）</span>
          <span>实际售价 <b style={{ color: '#1f1f1f' }}>{money(shown.price)}</b> 元</span>
          <span>成率 <b style={{ color: '#1f1f1f' }}>{(shown.price / shown.cost).toFixed(4)}</b></span>
          <span style={{ marginLeft: 'auto', color: '#8c8c8c', fontSize: 12 }}>拖动曲线端点改售价 · 拖动 ◇ 调整交界成本 · 键盘 ↑↓ ←→ 微调</span>
        </div>
      </div>

      <div style={{ ...card, flex: '0 0 400px', maxWidth: '100%', display: 'flex', flexDirection: 'column' }}>
        <div style={{ padding: '12px 14px', borderBottom: '1px solid #f0f0f0', display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontWeight: 600 }}>
          <span>区间列表</span><span style={{ fontWeight: 400, fontSize: 12, color: '#8c8c8c' }}>点击展开编辑</span>
        </div>
        <div role="listbox" aria-label="定价区间" style={{ padding: '10px 12px' }}>
          {rules.map((r, i) => {
            const open = i === index, fx = isFixed(r), first0 = i === 0, last = i === n - 1, drop = i < n - 1 ? drops.find(d => d.index === i) : undefined
            return <div key={i}>
              <div role="option" aria-selected={open} style={{ ...card, borderColor: open ? '#91caff' : '#f0f0f0', boxShadow: open ? '0 0 0 2px rgba(22,119,255,.08)' : undefined }}>
                <div tabIndex={0} onClick={() => setSelected(i)} onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setSelected(i) } }} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '9px 12px', cursor: 'pointer' }}>
                  <span style={{ width: 22, height: 22, borderRadius: '50%', fontSize: 12, textAlign: 'center', lineHeight: '22px', flex: 'none', background: open ? BLUE : '#f0f0f0', color: open ? '#fff' : '#595959' }}>{i + 1}</span>
                  <b>{costText(r)}</b>{tag(r)}<span style={{ color: '#8c8c8c' }}>{open ? '⌃' : '⌄'}</span>
                </div>
                {open && <div style={{ padding: '2px 12px 12px', borderTop: '1px dashed #f0f0f0' }}>
                  <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, marginTop: 10 }}>
                    <div><label style={fieldLabel}>{first0 ? '下限（进货价 ≥）' : '下限（进货价 >）'}</label>
                      {first0
                        ? <NumberField ariaLabel="区间下限" min={0.1} precision={2} value={r.minPrice} onCommit={v => apply(setEdge(rules, 'min', v))} style={{ width: '100%' }} />
                        : <><InputNumber aria-label="区间下限" disabled precision={2} value={r.minPrice} style={{ width: '100%' }} /><div style={{ fontSize: 12, color: '#8c8c8c', marginTop: 2 }}>= 区间 {i} 上限</div></>}</div>
                    <div><label style={fieldLabel}>上限（进货价 ≤）</label>
                      <NumberField ariaLabel="区间上限" min={0.11} precision={2} value={r.maxPrice} style={{ width: '100%' }}
                        formatter={(v, info) => formatBound(v, info)} parser={parseBound}
                        onCommit={v => apply(last ? setEdge(rules, 'max', v) : setBoundary(rules, i, v, flags))} /></div>
                  </div>
                  <div style={{ marginTop: 10 }}><label style={fieldLabel}>定价方式</label>
                    <Segmented block aria-label="定价方式" value={fx ? 'fixed' : 'curve'} options={[{ value: 'fixed', label: '固定成率' }, { value: 'curve', label: '曲线' }]} onChange={v => apply(setKind(rules, i, v as 'fixed' | 'curve', flags))} /></div>
                  {fx ? <div style={{ marginTop: 10 }}>
                    <label style={fieldLabel}>成率（倍）</label>
                    <NumberField ariaLabel="固定成率" size="large" min={1.5} max={5} step={0.1} precision={4} value={r.startRate} onCommit={v => apply(setRate(rules, i, v, flags))} addonAfter="倍" style={{ width: '100%', fontWeight: 600 }} />
                    <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 8 }}>
                      {RATE_PRESETS.map(p => <Button key={p} size="small" shape="round" type={r.startRate === p ? 'primary' : 'default'} ghost={r.startRate === p} onClick={() => apply(setRate(rules, i, p, flags))}>{p}</Button>)}
                    </div>
                    <div style={{ marginTop: 8, fontSize: 12, color: '#8c8c8c' }}>零售价 = 进货价 × {fmtRate(r.startRate)}，到分，不调整尾数；区间内 {money(startPrice(r))} → {r.maxPrice >= UNBOUNDED_COST ? '不限' : money(endPrice(r))}</div>
                  </div> : <>
                    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, marginTop: 10 }}>
                      {(['start', 'end'] as const).map(which => {
                        const b = which === 'start' ? i - 1 : i, nb = rules[which === 'start' ? i - 1 : i + 1]
                        const locked = b >= 0 && b < n - 1 && flags[b] && !!nb && isFixed(nb), p = which === 'start' ? startPrice(r) : endPrice(r)
                        const cost = which === 'start' ? r.minPrice : r.maxPrice
                        // 输入的是成率；零售价由 成本 × 成率 算出并取合法尾数后显示。跟随固定区间时显示固定成率（不可改）。
                        const rate = locked ? nb.startRate : (which === 'start' ? r.startRate : r.endRate)
                        return <div key={which}><label style={fieldLabel}>{which === 'start' ? '起点' : '终点'}成率（成本 {money(cost)}）</label>
                          <NumberField ariaLabel={which === 'start' ? '起点成率' : '终点成率'} disabled={locked} min={1.5} max={5} step={0.05} precision={4} value={rate} onCommit={v => apply(setEndpointRate(rules, i, which, v, flags))} addonAfter="倍" style={{ width: '100%' }} />
                          <div style={{ fontSize: 12, color: '#595959', marginTop: 2 }}>零售价 <b style={{ color: '#1677ff' }}>{money(p)}</b>{!locked && Math.abs(cost * rate - p) > 0.004 && <span style={{ color: '#8c8c8c' }} title={`成本 × 成率 = ${money(cost * rate)}，取最近的 .50 / .99 尾数`}>（{money(cost * rate)} 取尾数）</span>}</div>
                          {linkRow(i, which)}</div>
                      })}
                    </div>
                    <div style={{ marginTop: 10 }}><label style={fieldLabel}>曲线形状</label>
                      <Segmented block aria-label="曲线形状" value={r.algorithm as 'Linear' | 'ArcUp' | 'ArcDown'} options={[{ value: 'Linear', label: '直线' }, { value: 'ArcUp', label: '上弧线' }, { value: 'ArcDown', label: '下弧线' }]} onChange={v => apply(setShape(rules, i, v as 'Linear' | 'ArcUp' | 'ArcDown'))} /></div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginTop: 8, fontSize: 13, color: '#8c8c8c' }}>
                      <span>弧度</span>
                      <Slider ariaLabelForHandle="区间弧度" disabled={r.algorithm === 'Linear' || arcLimit(r) === 0} min={0} max={100} value={arcLimit(r) ? Math.abs(r.curveBend ?? 0) / arcLimit(r) * 100 : 0} onChange={v => apply(setBend(rules, i, v))} style={{ flex: 1, margin: '6px 4px' }} />
                      <span style={{ width: 34, textAlign: 'right' }}>{Math.round(arcLimit(r) ? Math.abs(r.curveBend ?? 0) / arcLimit(r) * 100 : 0)}%</span>
                    </div>
                  </>}
                  <div style={{ marginTop: 10, textAlign: 'right' }}><Button size="small" danger disabled={n <= 1} onClick={onRemove}>删除区间</Button></div>
                </div>}
              </div>
              {i < n - 1 && (() => {
                const from = endPrice(r), to = startPrice(rules[i + 1]), down = !!drop
                return <div style={{ margin: '4px 0 4px 22px', padding: '3px 10px 3px 14px', fontSize: 12, display: 'flex', gap: 8, alignItems: 'center', borderLeft: `2px solid ${down ? ORANGE : '#b7eb8f'}`, color: down ? '#ad4e00' : '#8c8c8c', background: down ? '#fff7e6' : undefined, borderRadius: down ? '0 6px 6px 0' : undefined }}>
                  <b style={{ color: down ? '#d4380d' : '#52c41a' }}>{down ? '▼' : '✓'}</b>
                  <span>交界 {money(r.maxPrice)} · {money(from)} → {money(to)}　{down ? `回落 ${money(from - to)}` : Math.abs(to - from) <= 0.005 ? '连续' : `上调 ${money(to - from)}`}</span>
                  {hasCurveSide(rules, i) && !flags[i] && <span style={{ marginLeft: 'auto', color: '#8c8c8c' }}>已断开</span>}
                </div>
              })()}
            </div>
          })}
        </div>
        <div style={{ margin: '0 12px 12px', padding: '10px 12px', background: '#fafafa', borderRadius: 6, fontSize: 12, color: '#595959', lineHeight: 1.8 }}>
          {error
            ? <div style={{ color: '#cf1322' }}>✗ {error}</div>
            : <>
              <div><span style={{ color: '#52c41a', fontWeight: 600 }}>✓</span> 每个区间成率均在 1.5 ～ 5 之间</div>
              <div><span style={{ color: '#52c41a', fontWeight: 600 }}>✓</span> 区间首尾相接，覆盖进货价 {money(first)} ～ {rules[n - 1].maxPrice >= UNBOUNDED_COST ? '不限' : money(rules[n - 1].maxPrice)}</div>
              <div><span style={{ color: '#52c41a', fontWeight: 600 }}>✓</span> 每个区间内售价不下降、成率不上升</div>
              {drops.length > 0
                ? <div><span style={{ color: '#d4380d', fontWeight: 600 }}>⚠</span> {drops.length} 处交界售价回落（阶梯定价），保存时需确认</div>
                : <div><span style={{ color: '#52c41a', fontWeight: 600 }}>✓</span> 所有交界售价连续</div>}
              {rules[n - 1].maxPrice < UNBOUNDED_COST && <div style={{ color: '#8c8c8c' }}>高于 {money(rules[n - 1].maxPrice)} 的进货价不会命中本策略</div>}
            </>}
        </div>
      </div>
    </div>
  </div>
}

