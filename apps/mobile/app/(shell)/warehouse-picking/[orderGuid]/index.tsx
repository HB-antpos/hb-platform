import { useMemo } from "react";
import { useLocalSearchParams } from "expo-router";
import { PickingScreen } from "@/modules/warehouse-picking";
import { parseClaimRouteParams } from "@/modules/warehouse-picking/pick-view-model";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function WarehousePickingOrderRoute() {
  const params = useLocalSearchParams();
  const focus = firstParam(params.focus);
  // 扫分单进入时带段号与领取提示；普通进单没有这些参数。
  const claim = useMemo(
    () =>
      parseClaimRouteParams({
        segment: firstParam(params.segment),
        claim: firstParam(params.claim),
        segments: firstParam(params.segments),
        lines: firstParam(params.lines),
        claimer: firstParam(params.claimer),
      }),
    [params.claim, params.claimer, params.lines, params.segment, params.segments],
  );
  // 完成页“去帮忙”带回的段号；helpAt 区分同一段的两次点击。
  const help = Number.parseInt(firstParam(params.help), 10);
  const helpAt = firstParam(params.helpAt);
  const helpRequest = useMemo(
    () => (Number.isInteger(help) && help > 0 && helpAt ? { segmentNo: help, nonce: helpAt } : null),
    [help, helpAt],
  );
  return (
    <PickingScreen
      orderGuid={firstParam(params.orderGuid)}
      focusDetailGuid={focus || null}
      focusSegmentNo={claim.segmentNo}
      claimNotice={claim.notice}
      helpRequest={helpRequest}
    />
  );
}
