import { PricingStrategyEditScreen } from "@/modules/pricing-strategies/PricingStrategyEditScreen";

/** 策略编号放在查询串（?id=）里；没有 id 时是新建。 */
export default function PricingStrategyEditRoute() {
  return <PricingStrategyEditScreen />;
}
