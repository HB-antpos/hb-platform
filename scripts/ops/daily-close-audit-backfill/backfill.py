#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
日结记录历史回填：把 POSM.pos_operation_audit 里已有的 DAILY_CLOSE_SAVE 事件生成「占位记录」，
写入 POSM.POSM_DailyClose（该表由 Hbpos.Api 启动时建）。

占位记录只带审计事件里恢复得出的字段：
- 有 before/after_actual → DetailLevel=CashOnly（应有现金、实点现金、差额）
- 没有金额（1.0.44 之前的客户端没写金额）→ DetailLevel=TraceOnly（只有「保存过」的留痕）
营业日审计事件里没有，按 occurred_at_utc + 门店时区推算，并标 BusinessDateInferred=1。
终端升级后，客户端会按同一个日结 GUID 补传完整明细并覆盖占位（见上传接口的覆盖语义）。

默认 dry-run：全程只读，只打印计划。只有同时带 --apply 与 --expect-rows N 才会写库，
且写入前再次核对：目标表存在、将要插入的行数恰好等于 N、批次标记在表里还没有行。
写入走单个事务，按 DailyCloseGuid 幂等（NOT EXISTS），写后核对批次行数，不符则回滚。

用法：
    python3 backfill.py                              # dry-run，打印计划
    python3 backfill.py --apply --expect-rows 25     # 真正写入（须先部署 Hbpos.Api 建表）

回滚材料（仅 --apply 时落盘）在 ~/DEV/hb-platform-data-fixes/<日期>-daily-close-audit-backfill/，
回滚只删本批次、仍是占位的行，不会误删已被客户端完整上传覆盖的记录。
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
import uuid
from dataclasses import dataclass, field
from datetime import date, datetime, timezone
from decimal import ROUND_HALF_UP, Decimal
from pathlib import Path
from zoneinfo import ZoneInfo

# 稳定的主检出路径（不是 worktree 路径）：连接串始终从这里读，且不会被清理。
DEFAULT_APPSETTINGS_PATH = Path(
    "/Users/sean/DEV/hb-platform/services/backend/BlazorApp.Api/appsettings.Development.json"
)
DEFAULT_LEDGER_ROOT = Path.home() / "DEV" / "hb-platform-data-fixes"
DEFAULT_BATCH = "audit-backfill-20261007"
# 测试店不回填；用户 2026-10-07 确认「只回填 25 条」即 1013/1014 两家真实门店的 WPF 事件。
DEFAULT_EXCLUDED_STORES = ("1042",)
CENT = Decimal("0.01")


# ============================================================
# 纯函数（有单测）
# ============================================================


def money(value) -> Decimal | None:
    """金额统一取 2 位小数、四舍五入远离零，与 WPF 的 MidpointRounding.AwayFromZero 一致。"""
    if value is None:
        return None
    return Decimal(str(value)).quantize(CENT, rounding=ROUND_HALF_UP)


def canonical_guid(value) -> str | None:
    """日结 GUID：WPF 事件的 order_guid；解析失败返回 None（调用方跳过该事件）。"""
    if value is None:
        return None
    try:
        return str(uuid.UUID(str(value).strip()))
    except ValueError:
        return None


def client_kind_for(device_system: str | None) -> str | None:
    """目前只有 WPF（Windows）写过带 order_guid 的日结事件；其他来源不回填，宁缺毋滥。"""
    return "Wpf" if (device_system or "").strip().lower() == "windows" else None


def load_zone(tz_id: str | None) -> ZoneInfo | None:
    if not tz_id:
        return None
    try:
        return ZoneInfo(tz_id.strip())
    except Exception:  # noqa: BLE001 - 时区 ID 无效即视为缺失
        return None


def local_business_date(occurred_utc: datetime, zone: ZoneInfo) -> date:
    """营业日 = 保存时刻换算到门店本地时区后的日期。

    这是推算：日结页允许选择非当天的营业日，补做前一天日结的记录会被推成保存当天。
    所以占位记录一律标 BusinessDateInferred，待客户端补传后改用客户端声明的值。
    """
    aware = occurred_utc.replace(tzinfo=timezone.utc) if occurred_utc.tzinfo is None else occurred_utc
    return aware.astimezone(zone).date()


@dataclass
class PlannedRow:
    guid: str
    store_code: str
    device_code: str
    client_kind: str
    detail_level: str
    business_date: date
    saved_at_utc: datetime
    cashier_id: str
    cashier_name: str
    app_version: str | None
    expected_cash: Decimal | None
    counted_cash: Decimal | None
    cash_difference: Decimal | None
    event_id: str
    warnings: list[str] = field(default_factory=list)


def build_row(event: dict, tz_id: str | None, excluded_stores: tuple[str, ...]) -> tuple[PlannedRow | None, str | None]:
    """把一条审计事件映射成占位记录；返回 (行, None) 或 (None, 跳过原因)。"""
    store_code = (event.get("store_code") or "").strip()
    if store_code in excluded_stores:
        return None, "EXCLUDED_STORE"
    kind = client_kind_for(event.get("device_system"))
    if kind is None:
        return None, "UNSUPPORTED_CLIENT"
    guid = canonical_guid(event.get("order_guid"))
    if guid is None:
        return None, "NO_VALID_GUID"
    zone = load_zone(tz_id)
    if zone is None:
        # 不猜时区：宁可跳过并报出来，也不要把营业日推错。
        return None, "NO_TIMEZONE"
    device_code = (event.get("device_code") or "").strip()
    if not store_code or not device_code:
        return None, "NO_SCOPE"

    expected = money(event.get("before_actual"))
    counted = money(event.get("after_actual"))
    warnings: list[str] = []
    if expected is None or counted is None:
        level, expected, counted, difference = "TraceOnly", None, None, None
    else:
        level = "CashOnly"
        difference = money(counted - expected)
        delta = money(event.get("amount_delta"))
        if delta is not None and abs(delta - difference) > CENT:
            # 以实点 − 应有为准，同时把不一致报出来供人工核对。
            warnings.append(f"DELTA_MISMATCH(event={delta}, recomputed={difference})")

    occurred = event["occurred_at_utc"]
    return PlannedRow(
        guid=guid,
        store_code=store_code,
        device_code=device_code,
        client_kind=kind,
        detail_level=level,
        business_date=local_business_date(occurred, zone),
        saved_at_utc=occurred.replace(tzinfo=None) if occurred.tzinfo else occurred,
        cashier_id=(event.get("cashier_id") or "").strip(),
        cashier_name=(event.get("cashier_name") or "").strip(),
        app_version=(event.get("app_version") or None),
        expected_cash=expected,
        counted_cash=counted,
        cash_difference=difference,
        event_id=str(event.get("event_id")),
        warnings=warnings,
    ), None


def build_plan(events, tz_by_store: dict, excluded_stores: tuple[str, ...]):
    """返回 (计划行, 跳过统计 {原因: [event_id...]})。同一 GUID 出现多次只保留最早一条。"""
    rows: dict[str, PlannedRow] = {}
    skipped: dict[str, list[str]] = {}
    for event in events:
        row, reason = build_row(event, tz_by_store.get((event.get("store_code") or "").strip()), excluded_stores)
        if row is None:
            skipped.setdefault(reason or "UNKNOWN", []).append(str(event.get("event_id")))
            continue
        if row.guid in rows:
            skipped.setdefault("DUPLICATE_GUID", []).append(row.event_id)
            continue
        rows[row.guid] = row
    return list(rows.values()), skipped


def rollback_sql(batch: str) -> str:
    """只删本批次且仍是占位的行；DetailLevel<>'Full' 保证不会删掉已被客户端完整上传覆盖的记录。"""
    return (
        "-- 回滚：仅本批次、仍是占位的行。先 SELECT 核对行数再执行 DELETE。\n"
        f"SELECT COUNT(*) AS WillDelete FROM dbo.POSM_DailyClose WHERE BackfillBatch = N'{batch}' "
        "AND DataSource = N'AuditBackfill' AND DetailLevel <> N'Full';\n"
        f"-- DELETE FROM dbo.POSM_DailyClose WHERE BackfillBatch = N'{batch}' "
        "AND DataSource = N'AuditBackfill' AND DetailLevel <> N'Full';\n"
    )


# ============================================================
# 数据库访问
# ============================================================

EVENTS_SQL = """
SELECT event_id, occurred_at_utc, store_code, device_code, device_system, app_version,
       cashier_id, cashier_name, order_guid, before_actual, after_actual, amount_delta
FROM dbo.pos_operation_audit
WHERE operation_type = 'DAILY_CLOSE_SAVE' AND outcome = 'Succeeded'
ORDER BY occurred_at_utc, event_id
"""

INSERT_SQL = """
INSERT INTO dbo.POSM_DailyClose
    (DailyCloseGuid, StoreCode, DeviceCode, ClientKind, DetailLevel, DataSource, BackfillBatch,
     BusinessDate, BusinessDateInferred, CashierId, CashierName, SavedAtUtc, AppVersion,
     ExpectedCashAmount, CountedCashAmount, CashDifference)
SELECT %s, %s, %s, %s, %s, N'AuditBackfill', %s, %s, 1, %s, %s, %s, %s, %s, %s, %s
WHERE NOT EXISTS (SELECT 1 FROM dbo.POSM_DailyClose WHERE DailyCloseGuid = %s)
"""


def connect(appsettings: Path, name: str, database: str):
    import pymssql  # 延迟导入：单测不需要驱动

    config = json.loads(appsettings.read_text(encoding="utf-8"))["ConnectionStrings"][name]
    parts = {k.strip().lower(): v.strip() for k, _, v in (p.partition("=") for p in config.split(";")) if k.strip()}
    return pymssql.connect(
        server=parts.get("server") or parts.get("data source"),
        user=parts.get("user id") or parts.get("uid"),
        password=parts.get("password") or parts.get("pwd"),
        database=database,
        login_timeout=15,
        timeout=60,
        autocommit=False,
    )


def fetch_events(cur) -> list[dict]:
    cur.execute("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED")
    cur.execute(EVENTS_SQL)
    names = [d[0] for d in cur.description]
    return [dict(zip(names, row)) for row in cur.fetchall()]


def fetch_store_timezones(cur, store_codes: list[str]) -> dict[str, str | None]:
    if not store_codes:
        return {}
    cur.execute("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED")
    placeholders = ",".join(["%s"] * len(store_codes))
    cur.execute(
        f"SELECT StoreCode, TimeZoneId FROM dbo.Store WHERE IsDeleted = 0 AND StoreCode IN ({placeholders})",
        tuple(store_codes),
    )
    return {str(code).strip(): tz for code, tz in cur.fetchall()}


# ============================================================
# 主流程
# ============================================================


def print_plan(rows: list[PlannedRow], skipped: dict, existing: set[str], batch: str) -> None:
    by_level: dict[str, int] = {}
    for r in rows:
        by_level[r.detail_level] = by_level.get(r.detail_level, 0) + 1
    print(f"批次标记: {batch}")
    print(f"计划回填: {len(rows)} 行  ({', '.join(f'{k}={v}' for k, v in sorted(by_level.items())) or '无'})")
    for reason, ids in sorted(skipped.items()):
        print(f"跳过 {reason}: {len(ids)} 条")
    if existing:
        print(f"目标表已存在同 GUID 的行（将被 NOT EXISTS 跳过）: {len(existing & {r.guid for r in rows})} 行")
    print("-" * 100)
    print(f"{'店':<6}{'终端':<16}{'保存时刻(UTC)':<22}{'营业日':<12}{'级别':<10}{'应有':>10}{'实点':>10}{'差额':>8}  版本")
    for r in rows:
        print(
            f"{r.store_code:<6}{r.device_code:<16}{r.saved_at_utc:%Y-%m-%d %H:%M:%S}   {r.business_date}  "
            f"{r.detail_level:<10}{'' if r.expected_cash is None else r.expected_cash:>10}"
            f"{'' if r.counted_cash is None else r.counted_cash:>10}{'' if r.cash_difference is None else r.cash_difference:>8}"
            f"  {(r.app_version or '')[:12]}"
            + (f"  ⚠ {'; '.join(r.warnings)}" if r.warnings else "")
        )


def write_ledger(root: Path, rows: list[PlannedRow], skipped: dict, result: dict, batch: str) -> Path:
    folder = root / f"{datetime.now():%Y-%m-%d}-daily-close-audit-backfill"
    folder.mkdir(parents=True, exist_ok=True)
    with (folder / "plan.csv").open("w", newline="", encoding="utf-8") as fh:
        writer = csv.writer(fh)
        writer.writerow(["guid", "store", "device", "saved_at_utc", "business_date", "detail_level", "expected", "counted", "diff", "event_id"])
        for r in rows:
            writer.writerow([r.guid, r.store_code, r.device_code, r.saved_at_utc.isoformat(), r.business_date, r.detail_level, r.expected_cash, r.counted_cash, r.cash_difference, r.event_id])
    (folder / "result.json").write_text(json.dumps({"batch": batch, "skipped": skipped, **result}, ensure_ascii=False, indent=2), encoding="utf-8")
    (folder / "rollback.sql").write_text(rollback_sql(batch), encoding="utf-8")
    return folder


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="从操作审计日志回填日结记录占位行（默认 dry-run）")
    parser.add_argument("--appsettings", type=Path, default=DEFAULT_APPSETTINGS_PATH)
    parser.add_argument("--batch", default=DEFAULT_BATCH, help="回填批次标记，用于精确回滚")
    parser.add_argument("--exclude-store", action="append", default=None, help="不回填的门店编码，可重复；默认 1042 测试店")
    parser.add_argument("--apply", action="store_true", help="真正写库（默认只读演练）")
    parser.add_argument("--expect-rows", type=int, default=None, help="--apply 时必填：预期插入的行数，不符则中止")
    parser.add_argument("--ledger-root", type=Path, default=DEFAULT_LEDGER_ROOT)
    args = parser.parse_args(argv)
    excluded = tuple(args.exclude_store) if args.exclude_store else DEFAULT_EXCLUDED_STORES
    if args.apply and args.expect_rows is None:
        parser.error("--apply 必须同时给出 --expect-rows（先 dry-run 看计划行数）")

    posm = connect(args.appsettings, "HBPOSMConnection", "POSM")
    hbweb = connect(args.appsettings, "DefaultConnection", "HBweb")
    try:
        events = fetch_events(posm.cursor())
        stores = sorted({(e.get("store_code") or "").strip() for e in events} - set(excluded))
        tz_by_store = fetch_store_timezones(hbweb.cursor(), stores)
        rows, skipped = build_plan(events, tz_by_store, excluded)

        cur = posm.cursor()
        cur.execute("SELECT OBJECT_ID(N'dbo.POSM_DailyClose', N'U')")
        table_exists = cur.fetchone()[0] is not None
        existing: set[str] = set()
        if table_exists:
            cur.execute("SELECT DailyCloseGuid FROM dbo.POSM_DailyClose")
            existing = {str(g).lower() for (g,) in cur.fetchall()}
            cur.execute("SELECT COUNT(*) FROM dbo.POSM_DailyClose WHERE BackfillBatch = %s", (args.batch,))
            if cur.fetchone()[0] > 0:
                print(f"批次标记 {args.batch} 在表里已有行，拒绝重复回填（换批次名或先核对）", file=sys.stderr)
                return 3

        print(f"审计事件总数: {len(events)}；目标表 POSM_DailyClose {'已存在' if table_exists else '尚未创建（Hbpos.Api 未部署）'}")
        print_plan(rows, skipped, existing, args.batch)
        to_insert = [r for r in rows if r.guid not in existing]

        if not args.apply:
            print("\ndry-run 结束，未写入任何数据。确认计划无误后：--apply --expect-rows", len(to_insert))
            return 0
        if not table_exists:
            print("目标表不存在，先部署 Hbpos.Api 建表。", file=sys.stderr)
            return 2
        if len(to_insert) != args.expect_rows:
            print(f"将插入 {len(to_insert)} 行，与 --expect-rows {args.expect_rows} 不符，已中止。", file=sys.stderr)
            return 2

        # 单事务写入，按 GUID 幂等；写后核对批次行数，不符则回滚。
        cur = posm.cursor()
        inserted = 0
        for r in to_insert:
            cur.execute(
                INSERT_SQL,
                (r.guid, r.store_code, r.device_code, r.client_kind, r.detail_level, args.batch, r.business_date,
                 r.cashier_id, r.cashier_name, r.saved_at_utc, r.app_version,
                 r.expected_cash, r.counted_cash, r.cash_difference, r.guid),
            )
            inserted += cur.rowcount
        cur.execute("SELECT COUNT(*) FROM dbo.POSM_DailyClose WHERE BackfillBatch = %s", (args.batch,))
        counted = cur.fetchone()[0]
        if inserted != len(to_insert) or counted != len(to_insert):
            posm.rollback()
            print(f"写后核对失败（插入 {inserted}，批次行数 {counted}，预期 {len(to_insert)}），已回滚。", file=sys.stderr)
            return 4
        posm.commit()
        folder = write_ledger(args.ledger_root, rows, skipped, {"inserted": inserted, "batchRows": counted}, args.batch)
        print(f"\n已写入 {inserted} 行并核对通过。台账与回滚材料: {folder}")
        return 0
    finally:
        posm.close()
        hbweb.close()


if __name__ == "__main__":
    sys.exit(main())
