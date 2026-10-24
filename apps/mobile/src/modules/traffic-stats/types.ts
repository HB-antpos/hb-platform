// 客流统计页的数据类型（对应后端 GET /react/v1/face-match/traffic/overview）。
// 页面只展示计数，不含任何生物信息、照片。

export type TrafficPeriod = "day" | "week" | "month";
export const TRAFFIC_PERIODS: readonly TrafficPeriod[] = ["day", "week", "month"];

export interface TrafficCounts {
  /** 进店人次 = 抓拍 − 员工 − 低质量 − 识别失败（含当日重复到访），页面主指标。 */
  visitCount: number;
  captureCount: number;
  staffMatchCount: number;
  /** 估算值：只有开启去重的门店才有意义；周 / 月是「各日去重人数相加」。 */
  uniqueVisitorCount: number;
  repeatCount: number;
  lowQualityCount: number;
  apiFailureCount: number;
}

export interface TrafficDay {
  /** 门店本地日期 yyyy-MM-dd。 */
  date: string;
  counts: TrafficCounts;
}

export interface TrafficHour {
  /** 门店本地小时 0–23。 */
  hour: number;
  counts: TrafficCounts;
}

export interface TrafficPeriodData {
  days: TrafficDay[];
  /** 恒为 24 项。day：该日分小时；week / month：已结束且有抓拍的日子各小时计数之和（除以 hourlyDays 得日均）。 */
  hours: TrafficHour[];
  total: TrafficCounts;
  /** 参与 hours 汇总的天数（day 视图为 0 或 1）。 */
  hourlyDays: number;
}

export interface TrafficOverview {
  storeCode: string;
  storeName: string | null;
  timeZoneId: string;
  period: TrafficPeriod;
  /** 当期完整周期（含未到的日子）。 */
  from: string;
  to: string;
  /** 对照周期：昨天 / 上一整周 / 上一整月。 */
  prevFrom: string;
  prevTo: string;
  /** 门店本地今天，客户端不用手机日期判断「今天」。 */
  today: string;
  /** 取数时刻的门店本地时间 yyyy-MM-ddTHH:mm。 */
  asOfLocal: string;
  /** 当期包含今天（数据仍在增长）。 */
  isCurrent: boolean;
  /** 门店是否开启客流计数（Match 模式）；未开启时只显示说明页。 */
  countingEnabled: boolean;
  /** 门店是否开启去重；未开启时不展示独立访客 / 重复到访。 */
  trafficDedupEnabled: boolean;
  current: TrafficPeriodData;
  previous: TrafficPeriodData;
}

export interface TrafficOverviewQuery {
  storeCode: string;
  period: TrafficPeriod;
  /** 周期内任一日期 yyyy-MM-dd；缺省 = 门店本地今天。 */
  anchor?: string | null;
}
