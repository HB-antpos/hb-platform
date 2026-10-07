#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""回填脚本的纯函数单测：映射、营业日推算、跳过原因。不连接任何数据库。

运行：python3 -m unittest scripts/ops/daily-close-audit-backfill/test_backfill.py -v
"""

import sys
import unittest
from datetime import date, datetime
from decimal import Decimal
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import backfill  # noqa: E402

GUID_A = "a3f29c01-7b4e-4d2a-9c3e-5f1d8e60b274"
GUID_B = "5c0d2f94-3a6e-4b17-8f21-9d7a60c4b1e3"
BRISBANE = "Australia/Brisbane"


def event(**overrides):
    base = {
        "event_id": "evt-1",
        "occurred_at_utc": datetime(2026, 10, 6, 7, 4, 0),
        "store_code": "1013",
        "device_code": "POS_1013_0222",
        "device_system": "Windows",
        "app_version": "1.0.44+e8874ba3",
        "cashier_id": "C001",
        "cashier_name": "Mei Lin",
        "order_guid": GUID_A,
        "before_actual": Decimal("231.22"),
        "after_actual": Decimal("231.30"),
        "amount_delta": Decimal("0.08"),
    }
    base.update(overrides)
    return base


class BuildRowTests(unittest.TestCase):
    def test_有金额的事件映射为_CashOnly_并推算营业日(self):
        row, reason = backfill.build_row(event(), BRISBANE, ())
        self.assertIsNone(reason)
        self.assertEqual(row.detail_level, "CashOnly")
        self.assertEqual(row.client_kind, "Wpf")
        self.assertEqual(row.expected_cash, Decimal("231.22"))
        self.assertEqual(row.counted_cash, Decimal("231.30"))
        self.assertEqual(row.cash_difference, Decimal("0.08"))
        # 07:04 UTC + 10h = 17:04 本地，同一天
        self.assertEqual(row.business_date, date(2026, 10, 6))
        self.assertEqual(row.guid, GUID_A)
        self.assertEqual(row.warnings, [])

    def test_没有金额的事件映射为_TraceOnly(self):
        row, _ = backfill.build_row(event(before_actual=None, after_actual=None, amount_delta=None), BRISBANE, ())
        self.assertEqual(row.detail_level, "TraceOnly")
        self.assertIsNone(row.expected_cash)
        self.assertIsNone(row.counted_cash)
        self.assertIsNone(row.cash_difference)

    def test_只有一边金额也按_TraceOnly_不猜另一边(self):
        row, _ = backfill.build_row(event(before_actual=None), BRISBANE, ())
        self.assertEqual(row.detail_level, "TraceOnly")

    def test_差额以实点减应有重算_事件里的_delta_不一致时给出警告(self):
        row, _ = backfill.build_row(event(amount_delta=Decimal("0.50")), BRISBANE, ())
        self.assertEqual(row.cash_difference, Decimal("0.08"))
        self.assertEqual(len(row.warnings), 1)
        self.assertIn("DELTA_MISMATCH", row.warnings[0])

    def test_金额四舍五入远离零(self):
        row, _ = backfill.build_row(event(before_actual=Decimal("1.005"), after_actual=Decimal("2.005"), amount_delta=None), BRISBANE, ())
        self.assertEqual(row.expected_cash, Decimal("1.01"))
        self.assertEqual(row.counted_cash, Decimal("2.01"))

    def test_负数差额为短款(self):
        row, _ = backfill.build_row(event(before_actual=Decimal("100.00"), after_actual=Decimal("96.50"), amount_delta=Decimal("-3.50")), BRISBANE, ())
        self.assertEqual(row.cash_difference, Decimal("-3.50"))


class SkipReasonTests(unittest.TestCase):
    def test_排除门店(self):
        self.assertEqual(backfill.build_row(event(store_code="1042"), BRISBANE, ("1042",))[1], "EXCLUDED_STORE")

    def test_非_Windows_来源不回填(self):
        self.assertEqual(backfill.build_row(event(device_system=None), BRISBANE, ())[1], "UNSUPPORTED_CLIENT")
        self.assertEqual(backfill.build_row(event(device_system="Android"), BRISBANE, ())[1], "UNSUPPORTED_CLIENT")

    def test_没有有效_GUID(self):
        self.assertEqual(backfill.build_row(event(order_guid=None), BRISBANE, ())[1], "NO_VALID_GUID")
        self.assertEqual(backfill.build_row(event(order_guid="not-a-guid"), BRISBANE, ())[1], "NO_VALID_GUID")

    def test_门店没有有效时区时不猜(self):
        self.assertEqual(backfill.build_row(event(), None, ())[1], "NO_TIMEZONE")
        self.assertEqual(backfill.build_row(event(), "Not/AZone", ())[1], "NO_TIMEZONE")

    def test_缺门店或终端编码(self):
        self.assertEqual(backfill.build_row(event(device_code=""), BRISBANE, ())[1], "NO_SCOPE")


class BusinessDateTests(unittest.TestCase):
    def test_UTC_14_点之后在布里斯班已是次日(self):
        # 说明推算的敏感点：布里斯班 UTC+10，UTC 14:00 起本地进入次日。
        zone = backfill.load_zone(BRISBANE)
        self.assertEqual(backfill.local_business_date(datetime(2026, 10, 6, 13, 59), zone), date(2026, 10, 6))
        self.assertEqual(backfill.local_business_date(datetime(2026, 10, 6, 14, 0), zone), date(2026, 10, 7))

    def test_悉尼夏令时按本地偏移计算(self):
        zone = backfill.load_zone("Australia/Sydney")
        # 2026-10-04 起悉尼进入夏令时（UTC+11）：UTC 13:00 已是次日 00:00
        self.assertEqual(backfill.local_business_date(datetime(2026, 10, 6, 13, 0), zone), date(2026, 10, 7))


class BuildPlanTests(unittest.TestCase):
    def test_同一_GUID_只保留最早一条并记为重复(self):
        events = [event(event_id="e1"), event(event_id="e2")]
        rows, skipped = backfill.build_plan(events, {"1013": BRISBANE}, ())
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0].event_id, "e1")
        self.assertEqual(skipped["DUPLICATE_GUID"], ["e2"])

    def test_汇总跳过原因(self):
        events = [
            event(event_id="ok", order_guid=GUID_A),
            event(event_id="test", store_code="1042", order_guid=GUID_B),
            event(event_id="noguid", order_guid=None),
        ]
        rows, skipped = backfill.build_plan(events, {"1013": BRISBANE, "1042": BRISBANE}, ("1042",))
        self.assertEqual([r.event_id for r in rows], ["ok"])
        self.assertEqual(skipped, {"EXCLUDED_STORE": ["test"], "NO_VALID_GUID": ["noguid"]})


class RollbackSqlTests(unittest.TestCase):
    def test_回滚只限本批次且仍是占位(self):
        sql = backfill.rollback_sql("audit-backfill-20261007")
        self.assertIn("BackfillBatch = N'audit-backfill-20261007'", sql)
        self.assertIn("DataSource = N'AuditBackfill'", sql)
        self.assertIn("DetailLevel <> N'Full'", sql)
        # DELETE 默认注释掉，须人工核对 SELECT 行数后再放开
        self.assertIn("-- DELETE FROM", sql)


if __name__ == "__main__":
    unittest.main()
