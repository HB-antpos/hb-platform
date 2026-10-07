import type { CashContext } from '../../../types/storeCash'
import type { CashPageQuery, ResolvedCashFilters } from './logic'
import type { Tr } from './parts'

/** 四个页签共用的入参：context、实际查询条件、地址栏参数与文案函数。 */
export interface CashTabProps {
  context: CashContext
  filters: ResolvedCashFilters
  query: CashPageQuery
  /** 页面处于激活状态且当前页签被选中时才取数。 */
  active: boolean
  /** 页面文案：自动补 storeCash. 前缀。 */
  tr: Tr
  /** 完整键名的文案（类别、核对状态等映射表里的键）。 */
  tf: Tr
  /** 请求异常 → 可展示文案；取消的请求返回 null（不提示）。 */
  errorText: (error: unknown) => string | null
  onQueryChange: (patch: Partial<CashPageQuery>) => void
}
