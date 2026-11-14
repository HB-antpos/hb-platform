#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
KFC（Uncle Bills，本地供应商 257）销量 · 库存 · 调拨分析脚本。

只读脚本：全程只执行 SELECT（READ UNCOMMITTED），不写任何库、不改任何数据。
每周一、周五由 Claude Code 本机定时任务调用；也可手动运行 / 回测。

口径、页面结构、访问控制与部署顺序见同目录 README.md；网页「附注」页签列出全部公式与参数。

用法：
    python3 analyze_kfc.py [--as-of YYYY-MM-DD] [--out-dir DIR] [--supplier-code 257]
                            [--since YYYY-MM-DD] [--upload-server]
                            [--backtest 2025-10-06,2025-11-03,2025-12-01]
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import math
import os
import re
import shutil
import sys
import time
import urllib.parse
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

try:
    import pymssql
    import requests
    from PIL import Image
except ImportError:  # pragma: no cover - 环境缺依赖时给出明确提示，不静默失败
    print("缺少依赖，请先 `pip3 install pymssql requests pillow`", file=sys.stderr)
    raise

SYDNEY_TZ = ZoneInfo("Australia/Sydney")

# 稳定的仓库主检出路径（不是 worktree 路径）：脚本长期独立运行，worktree 会被清理，
# 而这个路径是本机开发环境里始终存在的主仓库连接串来源。
DEFAULT_APPSETTINGS_PATH = Path(
    "/Users/sean/DEV/hb-platform/services/backend/BlazorApp.Api/appsettings.Development.json"
)


# ============================================================
# 配置
# ============================================================


@dataclass
class Config:
    supplier_code: str = "257"
    top_share: float = 0.30
    candidate_cap: int = 400
    high_value_unit_price: float = 10.0
    safety_factor: float = 1.2
    yoy_clamp: tuple = (0.5, 1.5)
    season_ratio_clamp: tuple = (0.25, 4.0)
    min_forecast_for_shortage: float = 3.0
    exclude_stores: set = field(default_factory=lambda: {"1042"})
    warehouse_store: str = "1006"
    appsettings_path: Path = DEFAULT_APPSETTINGS_PATH
    sell_through_since: date | None = None  # 进销累计页签起点；默认取最近一个 8 月 1 日
    sql_slow_threshold_seconds: float = 15.0


# ============================================================
# 数据库连接
# ============================================================


def _parse_conn_str(cs: str) -> dict:
    kv = {}
    for part in cs.split(";"):
        if "=" not in part:
            continue
        k, v = part.split("=", 1)
        kv[k.strip().lower()] = v.strip()
    return kv


def connect(appsettings_path: Path, conn_key: str, timeout: int = 20):
    """建立只读连接：READ UNCOMMITTED + 短锁超时，不打印连接串或密码。"""
    cfg = json.loads(appsettings_path.read_text(encoding="utf-8"))
    cs = cfg["ConnectionStrings"][conn_key]
    kv = _parse_conn_str(cs)
    server = kv.get("server") or kv.get("data source")
    database = kv.get("database") or kv.get("initial catalog")
    user = kv.get("user id") or kv.get("uid")
    password = kv.get("password") or kv.get("pwd")
    host, _, port = server.partition(",")
    conn = pymssql.connect(
        server=host,
        port=int(port or 1433),
        user=user,
        password=password,
        database=database,
        login_timeout=timeout,
        timeout=timeout,
    )
    cur = conn.cursor(as_dict=True)
    cur.execute("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED")
    cur.execute("SET LOCK_TIMEOUT 5000")
    cur.execute("SET DEADLOCK_PRIORITY LOW")
    return conn, cur


def timed_query(cur, timings: dict, label: str, sql: str, params: tuple = ()):
    t0 = time.time()
    cur.execute(sql, params)
    rows = cur.fetchall()
    timings[label] = round(time.time() - t0, 2)
    return rows


# ============================================================
# 日期 / 节日锚点
# ============================================================


def easter_sunday(year: int) -> date:
    """Meeus/Jones/Butcher 算法，计算公历复活节（星期日）。"""
    a = year % 19
    b = year // 100
    c = year % 100
    d = b // 4
    e = b % 4
    f = (b + 8) // 25
    g = (b - f + 1) // 3
    h = (19 * a + b - d - g + 15) % 30
    i = c // 4
    k = c % 4
    l = (32 + 2 * e + 2 * i - h - k) % 7
    m = (a + 11 * h + 22 * l) // 451
    month = (h + l - 7 * m + 114) // 31
    day = ((h + l - 7 * m + 114) % 31) + 1
    return date(year, month, day)


HOLIDAYS = [
    ("easter", easter_sunday),
    ("halloween", lambda y: date(y, 10, 31)),
    ("christmas", lambda y: date(y, 12, 25)),
]
HOLIDAY_WINDOW_DAYS = 21


def resolve_ly_anchor(d: date) -> tuple[date, str]:
    """
    去年同期锚点：固定日期节日（万圣、圣诞）每年只漂 0-1 天，按周对齐的 364 天
    会在节日前后系统性偏移；复活节按周移动，更须显式对齐。
    D 落在某节日 ±21 天窗口内时，按「距节日偏移天数」对齐去年同一节日；
    否则退回 364 天（52 周，星期对齐）。
    """
    best = None  # (abs_diff, holiday_name, this_year_date)
    for name, fn in HOLIDAYS:
        for candidate_year in (d.year - 1, d.year, d.year + 1):
            try:
                hd = fn(candidate_year)
            except ValueError:
                continue
            diff = (d - hd).days
            if abs(diff) <= HOLIDAY_WINDOW_DAYS:
                if best is None or abs(diff) < best[0]:
                    best = (abs(diff), name, hd)
    if best is None:
        return d - timedelta(days=364), "weekday_364"
    _, name, hd = best
    offset = (d - hd).days
    last_year_hd = HOLIDAYS[[h[0] for h in HOLIDAYS].index(name)][1](hd.year - 1)
    return last_year_hd + timedelta(days=offset), name


# ============================================================
# SQL 查询
# ============================================================


def fetch_local_supplier(cur, timings, supplier_code):
    rows = timed_query(
        cur,
        timings,
        "local_supplier",
        "SELECT LocalSupplierCode, Name, ImageBaseUrl FROM LocalSupplier WHERE LocalSupplierCode = %s",
        (supplier_code,),
    )
    return rows[0] if rows else None


def fetch_stores(cur, timings):
    return timed_query(
        cur,
        timings,
        "stores",
        "SELECT StoreCode, StoreName, BrandName, Address, TimeZoneId FROM Store WHERE IsActive = 1 AND IsDeleted = 0",
    )


# 门店所在州：Store 表没有州字段，与考勤公众假日同步（PublicHolidaySyncHelper）同一做法，
# 先取地址里最后一个 4 位澳洲邮编换算州；地址没写邮编时退回门店时区；都不行记为 None（页面显示「未知」）。
# 前后不能紧贴字母或数字，避免把「Shop2023B」这类铺位号当成邮编
_POSTCODE_RE = re.compile(r"(?<![0-9A-Za-z])(\d{4})(?![0-9A-Za-z])")
_TZ_STATE = {
    "Australia/Brisbane": "QLD", "Australia/Lindeman": "QLD",
    "Australia/Sydney": "NSW", "Australia/Melbourne": "VIC", "Australia/Hobart": "TAS",
    "Australia/Adelaide": "SA", "Australia/Perth": "WA", "Australia/Darwin": "NT",
}


def state_from_postcode(postcode: str | None) -> str | None:
    if not postcode:
        return None
    n = int(postcode)
    # 澳洲邮政编码分段（ACT 段夹在 NSW 段中间，要先判断）
    if 2600 <= n <= 2618 or 2900 <= n <= 2920 or 200 <= n <= 299:
        return "ACT"
    if 1000 <= n <= 2999:
        return "NSW"
    if 3000 <= n <= 3999 or 8000 <= n <= 8999:
        return "VIC"
    if 4000 <= n <= 4999 or 9000 <= n <= 9999:
        return "QLD"
    if 5000 <= n <= 5999:
        return "SA"
    if 6000 <= n <= 6999:
        return "WA"
    if 7000 <= n <= 7999:
        return "TAS"
    if 800 <= n <= 999:
        return "NT"
    return None


def resolve_store_state(address: str | None, time_zone_id: str | None) -> str | None:
    codes = _POSTCODE_RE.findall(address or "")
    return state_from_postcode(codes[-1] if codes else None) or _TZ_STATE.get((time_zone_id or "").strip())


def fetch_products(cur, timings, supplier_code):
    return timed_query(
        cur,
        timings,
        "products",
        """
        SELECT ProductCode, ItemNumber, Barcode, ProductName, RetailPrice, ProductImage, CreatedAt
        FROM Product WHERE LocalSupplierCode = %s AND IsDeleted = 0
        """,
        (supplier_code,),
    )


def fetch_freshness(cur, timings, d1: date, d2: date):
    rows = timed_query(
        cur,
        timings,
        "freshness",
        """
        SELECT Date, COUNT(DISTINCT BranchCode) AS branches, MAX(UpdateTime) AS max_update
        FROM ProductStoreDailySalesStatistic
        WHERE Date IN (%s, %s)
        GROUP BY Date
        """,
        (d1, d2),
    )
    out = {}
    for r in rows:
        key = r["Date"].date() if hasattr(r["Date"], "date") else r["Date"]
        out[key] = {"branches": r["branches"], "max_update": r["max_update"]}
    return out


def fetch_present_stores(cur, timings, d: date):
    rows = timed_query(
        cur,
        timings,
        "present_stores",
        "SELECT DISTINCT BranchCode FROM ProductStoreDailySalesStatistic WHERE Date = %s",
        (d,),
    )
    return {r["BranchCode"] for r in rows}


def fetch_sales(cur, timings, supplier_code, cur_start, cur_end, ly_start, ly_end):
    """
    一条查询覆盖「当前期」与「去年同期」两个窗口，只取列存索引覆盖的列，
    避免带 ProductName/Barcode/UpdateTime 等列导致回表、退化成全表扫描。
    WHERE 必须带日期下界（否则按供应商过滤在 4.5 万行/日的宽表上要扫 60 秒+）。
    """
    return timed_query(
        cur,
        timings,
        "sales",
        """
        SELECT Date, BranchCode, ProductCode, TotalQuantity, TotalAmount
        FROM ProductStoreDailySalesStatistic
        WHERE SupplierCode = %s
          AND ( (Date >= %s AND Date <= %s) OR (Date >= %s AND Date <= %s) )
        """,
        (supplier_code, cur_start, cur_end, ly_start, ly_end),
    )


def fetch_purchases(cur, timings, supplier_code, start_date):
    """
    明细 ProductCode 可能为空（3,654/10,420 行，2026 年实测），按 StoreProductCode
    回查 StoreRetailPrice.UUID 取商品编码，与 ProductMovementReportService 的既有口径一致。
    IsDeleted 写字面量（不是 COALESCE），否则会让过滤索引失效退化全表扫描。
    """
    return timed_query(
        cur,
        timings,
        "purchases",
        """
        SELECT i.StoreCode, i.InvoiceNo, i.EffectivePurchaseDate, i.FlowStatus,
               COALESCE(NULLIF(d.ProductCode, N''), NULLIF(srp.ProductCode, N'')) AS ProductCode,
               d.ItemNumber, d.Barcode, COALESCE(d.Quantity, 0) AS Quantity, COALESCE(d.Amount, 0) AS Amount
        FROM StoreLocalSupplierInvoice i
        INNER JOIN StoreLocalSupplierInvoiceDetails d
            ON d.InvoiceGUID = i.InvoiceGUID AND d.IsDeleted = 0
        LEFT JOIN StoreRetailPrice srp
            ON NULLIF(d.ProductCode, N'') IS NULL
           AND srp.UUID = d.StoreProductCode
           AND srp.IsDeleted = 0
        WHERE i.SupplierCode = %s
          AND i.IsDeleted = 0
          AND i.EffectivePurchaseDate >= %s
          AND COALESCE(i.InboundDate, i.OrderDate) IS NOT NULL
        """,
        (supplier_code, start_date),
    )


def fetch_actual_sales_window(cur, timings, supplier_code, start_date, end_date):
    """回测用：给定历史窗口的实际全链销量（按商品汇总）。"""
    rows = timed_query(
        cur,
        timings,
        "backtest_actual",
        """
        SELECT ProductCode, SUM(TotalQuantity) AS qty
        FROM ProductStoreDailySalesStatistic
        WHERE SupplierCode = %s AND Date >= %s AND Date <= %s
        GROUP BY ProductCode
        """,
        (supplier_code, start_date, end_date),
    )
    return {r["ProductCode"]: (r["qty"] or 0) for r in rows}


# ============================================================
# 图片地址
# ============================================================


def build_image_url(item_number, product_image, image_base_template, supplier_code):
    if product_image:
        return product_image
    if image_base_template and item_number:
        return image_base_template.replace(
            "{supplierCode}", urllib.parse.quote(supplier_code, safe="")
        ).replace("{itemNumber}", urllib.parse.quote(item_number, safe=""))
    return None


# ============================================================
# 核心计算
# ============================================================


class DailyIndex:
    """product/(store,product) -> {date: qty} 的稀疏索引，窗口求和用。"""

    def __init__(self):
        self.by_product: dict[str, dict[date, float]] = defaultdict(dict)
        self.by_product_amt: dict[str, dict[date, float]] = defaultdict(dict)
        self.by_store_product: dict[tuple, dict[date, float]] = defaultdict(dict)
        self.by_store_product_amt: dict[tuple, dict[date, float]] = defaultdict(dict)

    def add(self, d: date, store: str, product: str, qty: float, amt: float):
        self.by_product[product][d] = self.by_product[product].get(d, 0.0) + qty
        self.by_product_amt[product][d] = self.by_product_amt[product].get(d, 0.0) + amt
        self.by_store_product[(store, product)][d] = qty  # PK 唯一，无需累加
        self.by_store_product_amt[(store, product)][d] = amt

    def sum_product(self, product, start, end, amount=False):
        table = self.by_product_amt[product] if amount else self.by_product[product]
        total = 0.0
        d = start
        while d <= end:
            total += table.get(d, 0.0)
            d += timedelta(days=1)
        return total

    def sum_store_product(self, store, product, start, end, amount=False):
        source = self.by_store_product_amt if amount else self.by_store_product
        table = source.get((store, product))
        if not table:
            return 0.0
        total = 0.0
        d = start
        while d <= end:
            total += table.get(d, 0.0)
            d += timedelta(days=1)
        return total


def clamp(x, lo, hi):
    return max(lo, min(hi, x))


def resolve_sell_through_since(as_of: date, override: date | None) -> date:
    """进销累计起点：显式指定优先，否则取不晚于运行日的最近一个 8 月 1 日（旺季备货起点）。"""
    if override:
        return override
    aug1 = date(as_of.year, 8, 1)
    return aug1 if as_of >= aug1 else date(as_of.year - 1, 8, 1)


def build_sell_through(since, E, cur_idx, purchases_ps, purchase_amt_ps, product_info, store_name, include_stores, unmatched_purchases):
    """
    进销累计页签：since 以来逐商品、逐店的进货量与累计已售。
    - 只收 since 以来有进货的商品；进货按单据有效日期，销量按日统计净销量（含退货）。
    - 日期存成相对 since 的天数偏移，累计值与图表由页面 JS 计算，控制 JSON 体积。
    - 8 月前的期初库存不计入进货，所以售出率可能超过 100%；没有商品编码的进货明细
      归不到商品，对应商品进货量偏低、售出率偏高，缺口数量单独给出供页面披露。
    """
    def offset(d):
        return (d - since).days

    def iso(d):
        return d.isoformat() if d else None

    def avg(amount, qty):
        return round(amount / qty, 2) if qty > 0 and amount > 0 else None

    per_product = defaultdict(dict)  # product -> store -> {"purchases", "sales", "p_amount", "s_amount"}
    for (store, product), entries in purchases_ps.items():
        if store not in include_stores or product not in product_info:
            continue
        days = defaultdict(float)
        for d, q in entries:
            if since <= d <= E:
                days[d] += q
        if days:
            p_amount = sum(a for d, a in purchase_amt_ps.get((store, product), []) if since <= d <= E)
            per_product[product][store] = {"purchases": dict(days), "sales": {}, "p_amount": p_amount, "s_amount": 0.0}

    # 这些商品在各店的销量，包括没有进货记录但有销量的店（货可能从别处来）
    for (store, product), table in cur_idx.by_store_product.items():
        if product not in per_product or store not in include_stores:
            continue
        sales = {d: q for d, q in table.items() if since <= d <= E and q != 0}
        if sales:
            entry = per_product[product].setdefault(store, {"purchases": {}, "sales": {}, "p_amount": 0.0, "s_amount": 0.0})
            entry["sales"] = sales
            amt_table = cur_idx.by_store_product_amt.get((store, product), {})
            entry["s_amount"] = sum(a for d, a in amt_table.items() if since <= d <= E)

    rows = []
    for product, stores in per_product.items():
        info = product_info[product]
        chain_p = defaultdict(float)
        chain_s = defaultdict(float)
        chain_p_amount = 0.0
        chain_s_amount = 0.0
        stores_out = []
        for store, st in stores.items():
            chain_p_amount += st["p_amount"]
            chain_s_amount += st["s_amount"]
            for d, q in st["purchases"].items():
                chain_p[d] += q
            for d, q in st["sales"].items():
                chain_s[d] += q
            p_total = sum(st["purchases"].values())
            s_total = sum(st["sales"].values())
            last_sale = max((d for d, q in st["sales"].items() if q > 0), default=None)
            stores_out.append({
                "store_code": store,
                "purchased": round(p_total, 1),
                "sold": round(s_total, 1),
                "rate": round(s_total / p_total, 4) if p_total > 0 else None,
                "avg_purchase_price": avg(st["p_amount"], p_total),
                "avg_sale_price": avg(st["s_amount"], s_total),
                "last_purchase": iso(max(st["purchases"])) if st["purchases"] else None,
                "last_sale": iso(last_sale),
                "purchases": [[offset(d), round(q, 1)] for d, q in sorted(st["purchases"].items())],
                "sales": [[offset(d), round(q, 1)] for d, q in sorted(st["sales"].items())],
            })
        purchased = sum(chain_p.values())
        sold = sum(chain_s.values())
        if purchased <= 0:
            continue
        stores_out.sort(key=lambda s: (-s["sold"], -s["purchased"], s["store_code"]))
        last_sale = max((d for d, q in chain_s.items() if q > 0), default=None)
        rows.append({
            "product_code": product,
            "item_number": info["item_number"],
            "name": info["name"],
            "image_url": info["image_url"],
            "purchased": round(purchased, 1),
            "sold": round(sold, 1),
            "rate": round(sold / purchased, 4),
            "avg_purchase_price": avg(chain_p_amount, purchased),
            "avg_sale_price": avg(chain_s_amount, sold),
            "first_purchase": iso(min(chain_p)),
            "last_purchase": iso(max(chain_p)),
            "last_sale": iso(last_sale),
            "store_count": len(stores_out),
            "purchases": [[offset(d), round(q, 1)] for d, q in sorted(chain_p.items())],
            "sales": [[offset(d), round(q, 1)] for d, q in sorted(chain_s.items()) if q != 0],
            "stores": stores_out,
        })
    rows.sort(key=lambda r: (-r["sold"], -r["purchased"], r["product_code"]))

    unmatched_qty = sum(q for s, d, q in unmatched_purchases if s in include_stores and since <= d <= E)
    store_codes = sorted({s["store_code"] for r in rows for s in r["stores"]})
    return {
        "since": since.isoformat(),
        "cutoff": E.isoformat(),
        "days": (E - since).days,
        "product_count": len(rows),
        "total_purchased": round(sum(r["purchased"] for r in rows), 1),
        "total_sold": round(sum(r["sold"] for r in rows), 1),
        "unmatched_qty": round(unmatched_qty, 1),
        "store_names": {s: store_name.get(s, s) for s in store_codes},
        "rows": rows,
    }


def run_analysis(cfg: Config, as_of: date, warn_out: list) -> dict:
    """核心流程：给定运行日 as_of，跑完整套查询与公式，返回 report.json 的字典结构。"""
    timings: dict = {}
    warnings = warn_out
    # 页面支持中英切换：每条中文警告都配一条英文，按同一下标并存于 meta.warnings / meta.warnings_en。
    warnings_en: list = []

    def add_warning(zh: str, en: str):
        warnings.append(zh)
        warnings_en.append(en)

    conn, cur = connect(cfg.appsettings_path, "DefaultConnection")

    supplier = fetch_local_supplier(cur, timings, cfg.supplier_code)
    supplier_name = supplier["Name"] if supplier else cfg.supplier_code
    image_base_template = supplier["ImageBaseUrl"] if supplier else None

    stores = fetch_stores(cur, timings)
    store_name = {r["StoreCode"]: r["StoreName"] for r in stores}
    store_brand = {r["StoreCode"]: r["BrandName"] for r in stores}
    store_state = {r["StoreCode"]: resolve_store_state(r["Address"], r["TimeZoneId"]) for r in stores}
    active_store_codes = {r["StoreCode"] for r in stores}

    products = fetch_products(cur, timings, cfg.supplier_code)
    product_info = {}
    for r in products:
        product_info[r["ProductCode"]] = {
            "item_number": r["ItemNumber"],
            "name": r["ProductName"],
            "retail_price": float(r["RetailPrice"]) if r["RetailPrice"] is not None else None,
            "image_url": build_image_url(
                r["ItemNumber"], r["ProductImage"], image_base_template, cfg.supplier_code
            ),
            "created_at": r["CreatedAt"],
        }

    # ---------- 数据新鲜度 / 截止日 ----------
    e_candidate = as_of - timedelta(days=1)
    e_prior_week = e_candidate - timedelta(days=7)
    fresh = fetch_freshness(cur, timings, e_candidate, e_prior_week)
    branches_now = fresh.get(e_candidate, {}).get("branches", 0)
    branches_prior = fresh.get(e_prior_week, {}).get("branches", 0)
    cutoff_downgraded = False
    if branches_prior > 0 and branches_now < 0.8 * branches_prior:
        E = e_candidate - timedelta(days=1)
        cutoff_downgraded = True
        add_warning(
            f"数据截止日退到 {E.isoformat()}：{e_candidate.isoformat()} 只有 {branches_now} 个店有数据"
            f"（一周前同日 {branches_prior} 个店）",
            f"Data cutoff moved back to {E.isoformat()}: only {branches_now} stores had data on "
            f"{e_candidate.isoformat()} (vs {branches_prior} a week earlier)",
        )
    else:
        E = e_candidate

    present = fetch_present_stores(cur, timings, E)
    missing_stores = sorted(active_store_codes - present)

    # ---------- 去年同期锚点 ----------
    D_ly, ly_anchor_kind = resolve_ly_anchor(as_of)

    # ---------- 销量：一次查询覆盖当前期 + 去年同期 ----------
    # 进销累计页签从 since 起算；次年上半年运行时 since 可能早于 E-199，窗口要一并覆盖
    since = resolve_sell_through_since(as_of, cfg.sell_through_since)
    cur_start = min(E - timedelta(days=199), since)
    ly_start = D_ly - timedelta(days=90)
    ly_end = D_ly + timedelta(days=60)

    sales_rows = fetch_sales(cur, timings, cfg.supplier_code, cur_start, E, ly_start, ly_end)

    cur_idx = DailyIndex()
    ly_idx = DailyIndex()
    store_recent90_total = defaultdict(float)  # 该店近 90 天全链销量，用于 share() 折算
    for r in sales_rows:
        d = r["Date"].date() if hasattr(r["Date"], "date") else r["Date"]
        store = r["BranchCode"]
        product = r["ProductCode"]
        if not product:
            continue
        qty = float(r["TotalQuantity"] or 0)
        amt = float(r["TotalAmount"] or 0)
        if cur_start <= d <= E:
            cur_idx.add(d, store, product, qty, amt)
            if d >= E - timedelta(days=89):
                store_recent90_total[store] += qty
        if ly_start <= d <= ly_end:
            ly_idx.add(d, store, product, qty, amt)

    total_recent90 = sum(store_recent90_total.values())
    store_share = {
        s: (store_recent90_total[s] / total_recent90 if total_recent90 > 0 else 0.0)
        for s in active_store_codes
    }

    # ---------- 进货单 ----------
    purchase_start = as_of - timedelta(days=400)
    purchase_rows = fetch_purchases(cur, timings, cfg.supplier_code, purchase_start)
    conn.close()

    purchases_ps = defaultdict(list)  # (store,product) -> [(date, qty)]
    # 进货金额单独存一份（行金额 = 数量 × 进货价，2026-09-23 核对 8 月以来 10,230 行全部成立），
    # 不改 purchases_ps 的二元组结构，避免牵连余量、缺口等已有计算
    purchase_amt_ps = defaultdict(list)  # (store,product) -> [(date, amount)]
    unmatched_lines = 0
    # 明细没有商品编码、门店商品编码也回查不到时，按货号、再按条码回查本供应商商品主档；
    # 只接受唯一匹配（同一货号/条码对应多个商品时不猜，记为无法匹配）。
    # 2026-09-23 实测：3,654 行无编码明细里 3,648 行可按货号唯一匹配（13.7 万件），只剩 6 行对不上。
    item_to_codes = defaultdict(set)
    barcode_to_codes = defaultdict(set)
    for r in products:
        if r["ItemNumber"]:
            item_to_codes[r["ItemNumber"].strip().upper()].add(r["ProductCode"])
        if r["Barcode"]:
            barcode_to_codes[r["Barcode"].strip()].add(r["ProductCode"])
    match_stats = {"by_code": 0, "by_item_number": 0, "by_barcode": 0, "unmatched": 0}

    unmatched_purchases = []  # (store, date, qty)：所有回查方式都匹配不到的进货明细
    for r in purchase_rows:
        product = r["ProductCode"]
        if product:
            match_stats["by_code"] += 1
        else:
            item = (r["ItemNumber"] or "").strip().upper()
            barcode = (r["Barcode"] or "").strip()
            if item and len(item_to_codes.get(item, ())) == 1:
                product = next(iter(item_to_codes[item]))
                match_stats["by_item_number"] += 1
            elif barcode and len(barcode_to_codes.get(barcode, ())) == 1:
                product = next(iter(barcode_to_codes[barcode]))
                match_stats["by_barcode"] += 1
        d = r["EffectivePurchaseDate"]
        if hasattr(d, "date"):
            d = d.date()
        qty = float(r["Quantity"] or 0)
        if not product:
            unmatched_lines += 1
            match_stats["unmatched"] += 1
            unmatched_purchases.append((r["StoreCode"], d, qty))
            continue
        purchases_ps[(r["StoreCode"], product)].append((d, qty))
        purchase_amt_ps[(r["StoreCode"], product)].append((d, float(r["Amount"] or 0)))

    last_purchase = {}  # (store,product) -> {date, qty_same_day}
    for key, entries in purchases_ps.items():
        last_date = max(e[0] for e in entries)
        qty_same_day = sum(q for d, q in entries if d == last_date)
        last_purchase[key] = {"date": last_date, "qty": qty_same_day}

    purchase_qty_180 = defaultdict(float)  # (store,product) -> 近180天进货量（次要参考列用）
    for key, entries in purchases_ps.items():
        purchase_qty_180[key] = sum(q for d, q in entries if d >= E - timedelta(days=179))

    # 库存估算基数：近 180 天内「所有批次」到货量之和，销量从第一批到货日起算。
    # 之前只取最近一天的到货量，会漏掉更早批次还没卖完的货（例如 9 月到 240 件、10 月又到 60 件，
    # 旧口径只算 60 件，把大量在手库存误判成缺货）。近 180 天内没有任何到货时，退回只看最近一批。
    stock_basis = {}  # (store,product) -> {first_date, total_qty, batches}
    window_start = E - timedelta(days=179)
    for key, entries in purchases_ps.items():
        in_window = [(d, q) for d, q in entries if d >= window_start]
        if in_window:
            stock_basis[key] = {
                "first_date": min(d for d, _ in in_window),
                "total_qty": sum(q for _, q in in_window),
                "batches": len({d for d, _ in in_window}),
            }
        else:
            lp0 = last_purchase[key]
            stock_basis[key] = {"first_date": lp0["date"], "total_qty": lp0["qty"], "batches": 1}

    # ---------- 全链（chain-level）指标：同比、季节比、走势 ----------
    universe_products = set(cur_idx.by_product) | set(ly_idx.by_product)

    def ly28_extended(product):
        """去年同期 28 天样本太薄时，向前延展到最多 84 天，按比例折算回 28 天等效量。"""
        for window in (28, 42, 56, 70, 84):
            total = ly_idx.sum_product(
                product, D_ly - timedelta(days=window), D_ly - timedelta(days=1)
            )
            if total >= 30 or window == 84:
                return total * 28.0 / window
        return 0.0

    recent28_by_product = {p: cur_idx.sum_product(p, E - timedelta(days=27), E) for p in universe_products}
    recent28_amt_by_product = {
        p: cur_idx.sum_product(p, E - timedelta(days=27), E, amount=True) for p in universe_products
    }
    ly28_ext_by_product = {p: ly28_extended(p) for p in universe_products}

    def yoy_of(product, price_band_fallback):
        denom = ly28_ext_by_product.get(product, 0.0)
        num = recent28_by_product.get(product, 0.0)
        if denom > 0:
            return clamp(num / denom, *cfg.yoy_clamp)
        return price_band_fallback

    recent28_supplier = sum(recent28_by_product.values())
    recent28_amt_supplier = sum(recent28_amt_by_product.values())
    ly28_supplier = sum(ly28_ext_by_product.values())
    yoy_supplier = clamp(recent28_supplier / ly28_supplier, *cfg.yoy_clamp) if ly28_supplier > 0 else 1.0

    # 价格带回退（高价 >=$10 / 低价 <$10）：先按均价分两组各自求 yoy
    def avg_price(product):
        qty = recent28_by_product.get(product, 0.0)
        amt = recent28_amt_by_product.get(product, 0.0)
        if qty > 0:
            return amt / qty
        rp = product_info.get(product, {}).get("retail_price")
        return rp if rp else 0.0

    band_recent = defaultdict(float)
    band_ly = defaultdict(float)
    for p in universe_products:
        band = "high" if avg_price(p) >= cfg.high_value_unit_price else "low"
        band_recent[band] += recent28_by_product.get(p, 0.0)
        band_ly[band] += ly28_ext_by_product.get(p, 0.0)
    yoy_band = {
        band: (clamp(band_recent[band] / band_ly[band], *cfg.yoy_clamp) if band_ly[band] > 0 else yoy_supplier)
        for band in ("high", "low")
    }

    def yoy_for(product):
        band = "high" if avg_price(product) >= cfg.high_value_unit_price else "low"
        return yoy_of(product, yoy_band[band])

    # 季节比 r：未来 H 天（去年同期）相对去年同期前 28 天的比值，H/28 归一化
    ly_fwd7_supplier = sum(ly_idx.sum_product(p, D_ly, D_ly + timedelta(days=6)) for p in universe_products)
    ly_fwd14_supplier = sum(ly_idx.sum_product(p, D_ly, D_ly + timedelta(days=13)) for p in universe_products)
    season_ratio = (
        clamp(ly_fwd14_supplier / (ly28_supplier * 14.0 / 28.0), *cfg.season_ratio_clamp)
        if ly28_supplier > 0
        else 1.0
    )
    if season_ratio >= 1.5:
        trend_phase = "上行"
    elif season_ratio <= 0.6:
        trend_phase = "下行"
    elif recent28_supplier > 0 and recent28_supplier >= ly28_supplier * 0.8:
        trend_phase = "峰值"
    else:
        trend_phase = "淡季"

    # ---------- (store, product) 级别：预测 / 余量 / 缺口 ----------
    universe_ps = (
        set(cur_idx.by_store_product) | set(ly_idx.by_store_product) | set(purchases_ps)
    )

    def forecast_h(store, product, h_days):
        """加权融合：去年基线权重随 LY28 样本量线性升到 1；缺基线时退化为近期外推×季节比。"""
        ly28_p = ly28_ext_by_product.get(product, 0.0)
        yoy = yoy_for(product)
        recent28 = cur_idx.sum_store_product(store, product, E - timedelta(days=27), E)
        recent_rate = max(recent28, 0.0) / 28.0

        created_at = product_info.get(product, {}).get("created_at")
        product_is_new = bool(created_at and hasattr(created_at, "date") and created_at.date() > D_ly)
        if ly28_p <= 0 or product_is_new:
            return recent_rate * h_days * season_ratio, "新品，无去年基线"

        ly_h_store = ly_idx.sum_store_product(store, product, D_ly, D_ly + timedelta(days=h_days - 1))
        basis = "去年同期+同比"
        if ly_h_store <= 0:
            ly_h_chain = ly_idx.sum_product(product, D_ly, D_ly + timedelta(days=h_days - 1))
            ly_h_store = ly_h_chain * store_share.get(store, 0.0)
            basis = "按店份额折算"

        w = min(1.0, ly28_p / 30.0)
        value = w * ly_h_store * yoy + (1 - w) * recent_rate * h_days * season_ratio
        return max(value, 0.0), basis

    def confidence_of(store, product):
        key = (store, product)
        if key not in last_purchase:
            return "低"
        remaining = remaining_of(store, product)
        return "中" if remaining < 0 else "高"

    def remaining_of(store, product):
        key = (store, product)
        basis = stock_basis.get(key)
        if basis is None:
            return None
        sold_since = cur_idx.sum_store_product(store, product, basis["first_date"], E)
        return basis["total_qty"] - sold_since

    ps_rows = {}
    for store, product in universe_ps:
        if store in cfg.exclude_stores or product not in product_info:
            continue
        f7, basis7 = forecast_h(store, product, 7)
        f14, basis14 = forecast_h(store, product, 14)
        f28, _ = forecast_h(store, product, 28)
        target14 = f14 * cfg.safety_factor
        remaining = remaining_of(store, product)
        conf = confidence_of(store, product)
        gap = target14 - max(remaining or 0, 0)
        est180 = purchase_qty_180.get((store, product), 0.0) - cur_idx.sum_store_product(
            store, product, E - timedelta(days=179), E
        )
        lp = last_purchase.get((store, product))
        basis = stock_basis.get((store, product))
        # 「到货后销量」与余量同一起点：从窗口内第一批到货日起算，才能与累计到货量对得上
        sold_since_purchase = (
            cur_idx.sum_store_product(store, product, basis["first_date"], E) if basis else None
        )
        ps_rows[(store, product)] = {
            "forecast7": f7,
            "forecast14": f14,
            "forecast28": f28,
            "target14": target14,
            "remaining": remaining,
            "confidence": conf,
            "gap": gap,
            "est180": est180,
            "last_purchase_date": lp["date"].isoformat() if lp else None,
            "last_purchase_qty": lp["qty"] if lp else None,
            "purchase_total_qty": basis["total_qty"] if basis else None,
            "purchase_batches": basis["batches"] if basis else None,
            "sold_since_purchase": sold_since_purchase,
            "basis": basis14,
            "is_shortage": conf != "低" and f14 >= cfg.min_forecast_for_shortage and gap > 0,
            "is_urgent": False,  # 下面补
        }
        row = ps_rows[(store, product)]
        row["is_urgent"] = row["is_shortage"] and f7 > max(remaining or 0, 0)

    # ---------- 调拨来源：按商品分组，贪心分配 ----------
    demand_stores = active_store_codes - cfg.exclude_stores - {cfg.warehouse_store}
    supply_stores = active_store_codes - cfg.exclude_stores
    unknown_state = sorted(s for s in supply_stores if not store_state.get(s))
    if unknown_state:
        add_warning(
            f"以下门店地址里没有邮编、时区也无法判断州，按「未知」显示：{', '.join(unknown_state)}",
            f"State could not be derived from address or time zone for: {', '.join(unknown_state)} (shown as Unknown)",
        )

    by_product_ps = defaultdict(list)
    for (store, product), row in ps_rows.items():
        by_product_ps[product].append((store, row))

    for product, entries in by_product_ps.items():
        surplus_pool = {}
        for store, row in entries:
            if store not in supply_stores or row["confidence"] != "高":
                continue
            surplus = (row["remaining"] or 0) - row["forecast28"]
            if surplus > 0:
                surplus_pool[store] = surplus
        targets = sorted(
            (
                (store, row)
                for store, row in entries
                if store in demand_stores and row["is_urgent"]
            ),
            key=lambda sr: (-sr[1]["gap"], sr[0]),
        )
        for store, row in targets:
            candidates = sorted(
                ((t, s) for t, s in surplus_pool.items() if t != store and s > 0),
                key=lambda ts: (-ts[1], ts[0]),
            )[:3]
            transfer_from = []
            remaining_gap = row["gap"]
            for t, s in candidates:
                take = min(s, remaining_gap)
                if take <= 0:
                    continue
                transfer_from.append(
                    {
                        "store_code": t,
                        "store_name": store_name.get(t, t),
                        "qty": math.ceil(take),
                        "priority": t == cfg.warehouse_store,
                    }
                )
                surplus_pool[t] -= take
                remaining_gap -= take
            row["transfer_from"] = transfer_from
    for row in ps_rows.values():
        row.setdefault("transfer_from", [])

    # ---------- 视图：全部门店一份 + 每家门店各一份 ----------
    # 报告按门店授权：data/all.json 给有「全部门店」权限的人，data/store-{门店}.json 给关联了该店的人。
    # 预测模型参数（同比、季节比、去年基线）仍按全链计算；排名、汇总、缺口、进销累计只看视图内的门店。
    def top_share_set(mapping, share):
        items = [(p, v) for p, v in mapping.items() if v > 0]
        # 并列值按商品编码定序，保证同一天的数据每次运行选出同一批商品
        items.sort(key=lambda x: (-x[1], x[0]))
        k = max(1, math.ceil(len(items) * share))
        return {p for p, _ in items[:k]}

    # 商品是否在近 90 天内任一门店有进货记录：判断「疑似停售」要用真实最近进货日（全链口径）
    product_has_recent_purchase = set()
    for (store_key, product_key), entries in purchases_ps.items():
        if any(d >= E - timedelta(days=90) for d, _q in entries):
            product_has_recent_purchase.add(product_key)
    ly14_chain = {p: ly_idx.sum_product(p, D_ly, D_ly + timedelta(days=13)) for p in universe_products}

    # 每个（门店, 商品）的窗口销量先算好一次，各视图只做加总，避免逐日重复扫描
    W_Q28, W_A28, W_Q7, W_LY14, W_LY14A, W_LY28F, W_LY28FA, W_LY7, W_LY28B = range(9)
    e28, e7 = E - timedelta(days=27), E - timedelta(days=6)
    windows = {}
    products_by_store = defaultdict(set)
    for (s, p) in universe_ps:
        if s in cfg.exclude_stores or p not in product_info:
            continue
        windows[(s, p)] = (
            cur_idx.sum_store_product(s, p, e28, E),
            cur_idx.sum_store_product(s, p, e28, E, amount=True),
            cur_idx.sum_store_product(s, p, e7, E),
            ly_idx.sum_store_product(s, p, D_ly, D_ly + timedelta(days=13)),
            ly_idx.sum_store_product(s, p, D_ly, D_ly + timedelta(days=13), amount=True),
            ly_idx.sum_store_product(s, p, D_ly, D_ly + timedelta(days=27)),
            ly_idx.sum_store_product(s, p, D_ly, D_ly + timedelta(days=27), amount=True),
            ly_idx.sum_store_product(s, p, D_ly, D_ly + timedelta(days=6)),
            ly_idx.sum_store_product(s, p, D_ly - timedelta(days=28), D_ly - timedelta(days=1)),
        )
        products_by_store[s].add(p)

    # 全部门店视图按全链口径（含已停用门店去年的销量），排名、去年同期与拆分视图前的报告完全一致
    chain_metrics = {
        p: [
            cur_idx.sum_product(p, e28, E),
            cur_idx.sum_product(p, e28, E, amount=True),
            cur_idx.sum_product(p, e7, E),
            ly14_chain.get(p, 0.0),
            ly_idx.sum_product(p, D_ly, D_ly + timedelta(days=13), amount=True),
            ly_idx.sum_product(p, D_ly, D_ly + timedelta(days=27)),
            ly_idx.sum_product(p, D_ly, D_ly + timedelta(days=27), amount=True),
            ly_idx.sum_product(p, D_ly, D_ly + timedelta(days=6)),
            ly_idx.sum_product(p, D_ly - timedelta(days=28), D_ly - timedelta(days=1)),
        ]
        for p in universe_products
    }

    def build_view(scope, sell_scope, scope_info):
        scope = set(scope)
        chain_view = scope_info["kind"] == "all"
        if chain_view:
            view_products = set(universe_products)
            metrics = chain_metrics
        else:
            view_products = set()
            for s in scope:
                view_products |= products_by_store.get(s, set())
            metrics = {
                p: [sum(windows[(s, p)][i] for s in scope if (s, p) in windows) for i in range(9)]
                for p in view_products
            }

        def view_avg_price(p):
            qty, amt = metrics[p][W_Q28], metrics[p][W_A28]
            if qty > 0:
                return amt / qty
            rp = product_info.get(p, {}).get("retail_price")
            return rp if rp else 0.0

        candidates = set()
        for idx in (W_Q28, W_A28, W_LY14, W_LY14A, W_LY28F, W_LY28FA):
            candidates |= top_share_set({p: v[idx] for p, v in metrics.items()}, cfg.top_share)
        if len(candidates) > cfg.candidate_cap:
            candidates = set(sorted(candidates, key=lambda p: (-metrics[p][W_Q28], p))[: cfg.candidate_cap])

        # 汇总只算视图内的需求侧门店（全部门店视图不含 1006 仓库与测试店）
        summary_stores = sorted(scope & demand_stores) or sorted(scope)

        def summary(product):
            rows = [(s, ps_rows[(s, product)]) for s in summary_stores if (s, product) in ps_rows]
            f7 = sum(r["forecast7"] for _, r in rows)
            f14 = sum(r["forecast14"] for _, r in rows)
            target14 = sum(r["target14"] for _, r in rows)
            remaining_trusted = sum(r["remaining"] for _, r in rows if r["confidence"] != "低" and r["remaining"] is not None)
            gap_trusted = sum(r["gap"] for _, r in rows if r["confidence"] != "低" and r["gap"] > 0)
            gap_unknown_target = sum(r["target14"] for _, r in rows if r["confidence"] == "低")
            shortage_stores = sorted(((s, r["gap"]) for s, r in rows if r["is_shortage"]), key=lambda x: (-x[1], x[0]))

            # 分店销量明细：近 28 天有销量、或预测 14 天至少 1 件的门店
            by_store = []
            for s in summary_stores:
                row = ps_rows.get((s, product))
                w = windows.get((s, product))
                q28 = w[W_Q28] if w else 0.0
                f14_store = row["forecast14"] if row else 0.0
                if q28 <= 0 and f14_store < 1:
                    continue
                remaining_store = row["remaining"] if row else None
                gap_store = row["gap"] if row else 0.0
                by_store.append({
                    "store_code": s,
                    "store_name": store_name.get(s, s),
                    "qty7": round(w[W_Q7], 1) if w else 0.0,
                    "qty28": round(q28, 1),
                    "amt28": round(w[W_A28], 2) if w else 0.0,
                    "forecast14": math.ceil(f14_store),
                    "remaining": math.ceil(remaining_store) if remaining_store is not None else None,
                    "gap": math.ceil(gap_store) if gap_store > 0 else 0,
                    "confidence": row["confidence"] if row else "低",
                })
            store_qty_total = sum(max(e["qty28"], 0) for e in by_store)
            for e in by_store:
                e["share"] = round(max(e["qty28"], 0) / store_qty_total, 3) if store_qty_total > 0 else 0.0
            by_store.sort(key=lambda e: (-e["qty28"], -e["forecast14"], e["store_code"]))

            info = product_info[product]
            m = metrics.get(product) or [0.0] * 9
            return {
                "product_code": product,
                "store_count": sum(1 for e in by_store if e["qty28"] > 0),
                "by_store": by_store,
                "item_number": info["item_number"],
                "name": info["name"],
                "image_url": info["image_url"],
                "qty28": round(m[W_Q28], 1),
                "amt28": round(m[W_A28], 2),
                "avg_price": round(view_avg_price(product), 2),
                "ly14_qty": round(m[W_LY14], 1),
                "ly7_qty": round(m[W_LY7], 1),
                "forecast7": math.ceil(f7),
                "forecast14": math.ceil(f14),
                "target14": math.ceil(target14),
                "remaining_trusted": math.ceil(remaining_trusted) if remaining_trusted else 0,
                "gap_trusted": math.ceil(gap_trusted) if gap_trusted > 0 else 0,
                "gap_unknown_target": math.ceil(gap_unknown_target) if gap_unknown_target > 0 else 0,
                "top_shortage_stores": [
                    {"store_code": s, "store_name": store_name.get(s, s), "gap": math.ceil(g)}
                    for s, g in shortage_stores[:5]
                ],
                # 标记按全链口径：新品看有无去年基线，疑似停售看全链近期销售与进货
                "flag": (
                    "疑似停售"
                    if recent28_by_product.get(product, 0.0) == 0
                    and product not in product_has_recent_purchase
                    and ly14_chain.get(product, 0.0) > 0
                    else ("新品" if ly28_ext_by_product.get(product, 0.0) <= 0 else None)
                ),
            }

        summaries = {p: summary(p) for p in candidates}
        bestsellers = sorted(summaries.values(), key=lambda r: (-r["qty28"], r["product_code"]))
        forecast_board = sorted(summaries.values(), key=lambda r: (-r["forecast14"], r["product_code"]))

        # 高价大件：不限于候选集，覆盖视图内全部商品，按金额排序
        high_value = []
        for p in sorted(view_products):
            if view_avg_price(p) >= cfg.high_value_unit_price:
                high_value.append(summaries.get(p) or summary(p))
        high_value.sort(key=lambda r: (-r["amt28"], r["product_code"]))
        high_value = high_value[: cfg.candidate_cap]

        # 分店缺口与调拨 / 无进货记录：只列视图内的需求侧门店（仓库只作调拨来源）
        store_gaps, no_purchase = [], []
        for store in sorted(scope & demand_stores):
            shortage_rows, no_purchase_rows = [], []
            for product in sorted(products_by_store.get(store, set())):
                row = ps_rows.get((store, product))
                if row is None:
                    continue
                info = product_info[product]
                if row["is_shortage"]:
                    shortage_rows.append({
                        "product_code": product,
                        "item_number": info["item_number"],
                        "name": info["name"],
                        "image_url": info["image_url"],
                        "last_purchase_date": row["last_purchase_date"],
                        "last_purchase_qty": row["last_purchase_qty"],
                        "purchase_total_qty": row["purchase_total_qty"],
                        "purchase_batches": row["purchase_batches"],
                        "sales_since_purchase": (
                            math.ceil(row["sold_since_purchase"]) if row["sold_since_purchase"] is not None else None
                        ),
                        "remaining": math.ceil(row["remaining"]) if row["remaining"] is not None else None,
                        "est180": math.ceil(row["est180"]) if row["est180"] is not None else None,
                        "forecast7": math.ceil(row["forecast7"]),
                        "forecast14": math.ceil(row["forecast14"]),
                        "target14": math.ceil(row["target14"]),
                        "gap": math.ceil(row["gap"]) if row["gap"] > 0 else 0,
                        "urgent": row["is_urgent"],
                        "confidence": row["confidence"],
                        "transfer_from": row["transfer_from"],
                        "basis": row["basis"],
                    })
                elif row["confidence"] == "低" and row["forecast14"] >= cfg.min_forecast_for_shortage:
                    no_purchase_rows.append({
                        "product_code": product,
                        "item_number": info["item_number"],
                        "name": info["name"],
                        "image_url": info["image_url"],
                        "target14": math.ceil(row["target14"]),
                    })
            if shortage_rows:
                shortage_rows.sort(key=lambda r: (-r["gap"], r["product_code"]))
                store_gaps.append({"store_code": store, "store_name": store_name.get(store, store),
                                   "brand_name": store_brand.get(store), "state": store_state.get(store),
                                   "rows": shortage_rows})
            if no_purchase_rows:
                no_purchase_rows.sort(key=lambda r: (-r["target14"], r["product_code"]))
                no_purchase.append({"store_code": store, "store_name": store_name.get(store, store),
                                    "brand_name": store_brand.get(store), "state": store_state.get(store),
                                    "rows": no_purchase_rows})

        # 各店余量：缺口表里出现的每个商品，列出所有门店（含视图外门店、余量为 0 或负数、没有进货记录的店）
        # 的余量与销量，供调货时看全貌；页面的「有货门店」摘要只统计余量 ≥ 1 的店，展开明细列全部分店。
        # 按商品只存一份，缺口行用商品编码引用，避免同一份门店清单在多家缺货店的行里重复。
        # 门店名与州不逐条存，页面从外壳的门店名单（BOOT.stores）查，控制单店数据文件体积；
        # 连销量和余量都没有的门店不存，由页面按门店名单补成「无记录」行。
        # surplus = 余量 − 未来 28 天预测，> 0 才算有富余可调；余量是进货减销量的估算，含在途，不是货架实数。
        stock_holders = {}
        for product in sorted({r["product_code"] for sg in store_gaps for r in sg["rows"]}):
            holders = []
            for s, row in by_product_ps.get(product, []):
                if s not in supply_stores:
                    continue
                w = windows.get((s, product))
                remaining = row["remaining"]
                holders.append({
                    "store_code": s,
                    "remaining": math.floor(remaining) if remaining is not None else None,
                    "surplus": math.floor(remaining - row["forecast28"]) if remaining is not None else None,
                    "qty28": round(w[W_Q28], 1) if w else 0.0,
                    "forecast28": math.ceil(row["forecast28"]),
                    "confidence": row["confidence"],
                    "warehouse": s == cfg.warehouse_store,
                })
            if holders:
                holders.sort(key=lambda h: (
                    store_state.get(h["store_code"]) or "~",
                    -(h["remaining"] if h["remaining"] is not None else -10**9),
                    h["store_code"],
                ))
                stock_holders[product] = holders

        recent28_q = sum(v[W_Q28] for v in metrics.values())
        if chain_view:
            # 全链同比就是预测模型用的供应商整体同比（薄样本商品已向前延展折算）
            view_yoy = yoy_supplier
        else:
            # 单店同比：本店近 28 天 / 本店去年锚点前 28 天，只作展示，不参与预测
            ly28_back = sum(v[W_LY28B] for v in metrics.values())
            view_yoy = clamp(recent28_q / ly28_back, *cfg.yoy_clamp) if ly28_back > 0 else None
        return {
            "scope": scope_info,
            "meta_overrides": {
                "recent28_qty": round(recent28_q, 1),
                "recent28_amt": round(sum(v[W_A28] for v in metrics.values()), 2),
                "yoy_supplier": round(view_yoy, 3) if view_yoy is not None else None,
                "ly14_qty_supplier": round(sum(v[W_LY14] for v in metrics.values()), 1),
                "candidate_count": len(candidates),
                "shortage_store_count": len(store_gaps),
                "shortage_product_count": len({r["product_code"] for sg in store_gaps for r in sg["rows"]}),
                "urgent_count": sum(1 for sg in store_gaps for r in sg["rows"] if r["urgent"]),
            },
            "bestsellers": bestsellers,
            "high_value": high_value,
            "forecast": forecast_board,
            "store_gaps": store_gaps,
            "stock_holders": stock_holders,
            "no_purchase": no_purchase,
            "sell_through": build_sell_through(
                since, E, cur_idx, purchases_ps, purchase_amt_ps, product_info, store_name,
                set(sell_scope), unmatched_purchases,
            ),
        }

    report_stores = sorted(active_store_codes - cfg.exclude_stores)
    views = {"all": build_view(demand_stores, active_store_codes - cfg.exclude_stores, {"kind": "all"})}
    for s in report_stores:
        views[f"store-{s}"] = build_view(
            {s}, {s},
            {"kind": "store", "store_code": s, "store_name": store_name.get(s, s), "brand_name": store_brand.get(s),
             "state": store_state.get(s)},
        )

    slow_sql = {k: v for k, v in timings.items() if v >= cfg.sql_slow_threshold_seconds}
    if slow_sql:
        add_warning(
            f"以下 SQL 耗时超过 {cfg.sql_slow_threshold_seconds:.0f} 秒：{slow_sql}",
            f"These SQL queries took longer than {cfg.sql_slow_threshold_seconds:.0f}s: {slow_sql}",
        )

    shared_meta = {
        "generated_at": datetime.now(SYDNEY_TZ).isoformat(),
        "as_of": as_of.isoformat(),
        "data_cutoff": E.isoformat(),
        "cutoff_downgraded": cutoff_downgraded,
        "missing_stores": missing_stores,
        "supplier_code": cfg.supplier_code,
        "supplier_name": supplier_name,
        "ly_anchor_date": D_ly.isoformat(),
        "ly_anchor_kind": ly_anchor_kind,
        "season_ratio": round(season_ratio, 3),
        "trend_phase": trend_phase,
        "unmatched_purchase_lines": unmatched_lines,
        "purchase_match_stats": match_stats,
        "sql_timings": timings,
        "warnings": warnings,
        "warnings_en": warnings_en,
        "params": {
            "supplier_code": cfg.supplier_code,
            "top_share": cfg.top_share,
            "candidate_cap": cfg.candidate_cap,
            "high_value_unit_price": cfg.high_value_unit_price,
            "safety_factor": cfg.safety_factor,
            "yoy_clamp": list(cfg.yoy_clamp),
            "season_ratio_clamp": list(cfg.season_ratio_clamp),
            "min_forecast_for_shortage": cfg.min_forecast_for_shortage,
            "exclude_stores": sorted(cfg.exclude_stores),
            "warehouse_store": cfg.warehouse_store,
        },
    }
    result = {
        # meta 取全部门店视图的汇总，供定时任务读 latest_meta.json 做摘要
        "meta": {**shared_meta, **views["all"]["meta_overrides"]},
        "shared_meta": shared_meta,
        "stores": [
            {"store_code": s, "store_name": store_name.get(s, s), "brand_name": store_brand.get(s),
             "state": store_state.get(s)}
            for s in report_stores
        ],
        "views": views,
        # rows_flat 供 rows.csv 与下次运行做差值对比，不进页面数据。
        "_rows_flat": [
            {
                "store_code": s,
                "product_code": p,
                "item_number": product_info.get(p, {}).get("item_number"),
                "forecast14": round(row["forecast14"], 2),
                "remaining": row["remaining"],
                "gap": round(row["gap"], 2),
                "confidence": row["confidence"],
            }
            for (s, p), row in ps_rows.items()
        ],
    }
    return result


# ============================================================
# 输出：JSON / CSV / HTML
# ============================================================


def load_prev_rows(path: Path):
    if not path.exists():
        return {}
    prev = {}
    with path.open("r", encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            prev[(row["store_code"], row["product_code"])] = row
    return prev


def apply_change_vs_prev(result: dict, prev_rows: dict):
    store_groups = [sg for view in result["views"].values() for sg in view["store_gaps"]]
    for sg in store_groups:
        for r in sg["rows"]:
            key = (sg["store_code"], r["product_code"])
            prev = prev_rows.get(key)
            if prev and prev.get("remaining") not in (None, "", "None"):
                try:
                    r["change_vs_prev"] = r["remaining"] - float(prev["remaining"])
                except (TypeError, ValueError):
                    r["change_vs_prev"] = None
            else:
                r["change_vs_prev"] = None


def write_rows_csv(path: Path, rows_flat: list):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(
            f, fieldnames=["store_code", "product_code", "item_number", "forecast14", "remaining", "gap", "confidence"]
        )
        writer.writeheader()
        for row in rows_flat:
            writer.writerow(row)


def render_html(template_path: Path, data: dict) -> str:
    template = template_path.read_text(encoding="utf-8")
    payload = json.dumps(data, ensure_ascii=False, default=str)
    if "__REPORT_DATA_JSON__" not in template:
        raise RuntimeError("模板缺少 __REPORT_DATA_JSON__ 占位符")
    return template.replace("__REPORT_DATA_JSON__", payload)


def load_backtest_summary(out_dir: Path):
    """把最近一次 --backtest 的结果并入正式报告的 meta，供附注页签展示。
    回测三个历史节点（2025-10-06/11-03/12-01）显示预测总量平均只有实际值的约 63%，
    是系统性偏低（同比增速可能超过夹紧上限 1.5 倍、纯新品分支覆盖不到的增量），
    不是随机噪声，因此页面上必须原样披露，不能悄悄放大公式掩盖不确定性。
    """
    path = out_dir / "runs" / "backtest.json"
    if not path.exists():
        return None
    try:
        entries = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return None
    if not entries:
        return None
    ratios = [
        e["total_forecast14"] / e["total_actual14"]
        for e in entries
        if e.get("total_actual14")
    ]
    avg_ratio = sum(ratios) / len(ratios) if ratios else None
    return {
        "entries": entries,
        "avg_forecast_actual_ratio": round(avg_ratio, 2) if avg_ratio is not None else None,
    }


# ============================================================
# 商品图片
# Artifact 页面运行在沙箱域名里，CSP 会静默拦截外部图片域名（腾讯云 COS），
# 所以图片必须随页面一起发布：缩略图内嵌为 data URI；放大图单页放不下
# （约 750 张 × 18KB，超过 16MB 单页上限），拆成固定 8 个附属脚本按需加载。
# 本地只缓存压缩后的 WebP，原图平均 1.4MB 不落盘；以后每次只下载新增商品的图。
# ============================================================

IMAGE_THUMB_PX = 72
IMAGE_LARGE_PX = 480
IMAGE_MISSING_RETRY_DAYS = 7


def image_key(url: str) -> str:
    return hashlib.sha1(url.encode("utf-8")).hexdigest()[:16]


def _encode_webp(im, max_px: int, quality: int) -> bytes:
    copy = im.copy()
    copy.thumbnail((max_px, max_px))
    buf = io.BytesIO()
    copy.save(buf, "WEBP", quality=quality, method=4)
    return buf.getvalue()


def _download_and_derive(url: str, thumb_path: Path, large_path: Path, missing_path: Path) -> str:
    try:
        resp = requests.get(url, timeout=25)
        if resp.status_code != 200 or not resp.content:
            missing_path.write_text(f"HTTP {resp.status_code}", encoding="utf-8")
            return "missing"
        im = Image.open(io.BytesIO(resp.content))
        # 透明背景（PNG/调色板图）铺白底，避免转换后背景变黑
        if im.mode in ("RGBA", "LA", "P"):
            im = im.convert("RGBA")
            bg = Image.new("RGB", im.size, (255, 255, 255))
            bg.paste(im, mask=im.split()[-1])
            im = bg
        else:
            im = im.convert("RGB")
        thumb_path.write_bytes(_encode_webp(im, IMAGE_THUMB_PX, 70))
        large_path.write_bytes(_encode_webp(im, IMAGE_LARGE_PX, 72))
        if missing_path.exists():
            missing_path.unlink()
        return "downloaded"
    except Exception as exc:  # noqa: BLE001 - 单张图失败只降级为占位图
        missing_path.write_text(str(exc)[:200], encoding="utf-8")
        return "missing"


def ensure_images(urls, cache_dir: Path):
    """返回 {url: key}（已有缩略图与大图的）以及统计。下载失败的图 7 天内不重试。"""
    thumb_dir = cache_dir / "thumb"
    large_dir = cache_dir / "large"
    miss_dir = cache_dir / "missing"
    for d in (thumb_dir, large_dir, miss_dir):
        d.mkdir(parents=True, exist_ok=True)

    stats = {"total": len(urls), "cached": 0, "downloaded": 0, "missing": 0}
    todo = []
    now = time.time()
    for url in urls:
        k = image_key(url)
        tp, lp, mp = thumb_dir / f"{k}.webp", large_dir / f"{k}.webp", miss_dir / f"{k}.txt"
        if tp.exists() and lp.exists():
            stats["cached"] += 1
        elif mp.exists() and now - mp.stat().st_mtime < IMAGE_MISSING_RETRY_DAYS * 86400:
            stats["missing"] += 1
        else:
            todo.append((url, tp, lp, mp))

    t0 = time.time()
    if todo:
        with ThreadPoolExecutor(max_workers=12) as ex:
            for outcome in ex.map(lambda a: _download_and_derive(*a), todo):
                stats[outcome] += 1
    stats["seconds"] = round(time.time() - t0, 1)

    available = {}
    for url in urls:
        k = image_key(url)
        if (thumb_dir / f"{k}.webp").exists() and (large_dir / f"{k}.webp").exists():
            available[url] = k
    return available, stats


# ============================================================
# 上传到 hotbargain.vip：/www/HBWeb/reports/kfc-uncle-bills/ 由 nginx 提供，
# 访问经 auth_request 调主站后端鉴权（需主站登录）。目录在前端 webroot 之外，
# 前端发版不会删到它；这里的 --delete 只作用于这一个报告目录。
# ============================================================

SERVER_SSH_KEY = Path(
    "/Users/sean/Library/CloudStorage/OneDrive-个人/SynologyDrive/Key/hotbargain.vip.pem"
)
SERVER_TARGET = "ubuntu@hotbargain.vip:/www/HBWeb/reports/kfc-uncle-bills/"
SERVER_URL = "https://hotbargain.vip/reports/kfc-uncle-bills/"


def upload_site(site_dir: Path, attempts: int = 2) -> None:
    import subprocess

    if not SERVER_SSH_KEY.exists():
        raise RuntimeError(f"找不到服务器 SSH 密钥：{SERVER_SSH_KEY}")
    cmd = [
        "rsync", "-az", "--delete", "--delay-updates", "--chmod=u=rwX,go=rX",
        "-e", f'ssh -i "{SERVER_SSH_KEY}" -o StrictHostKeyChecking=no -o ConnectTimeout=20',
        f"{site_dir}/", SERVER_TARGET,
    ]
    last_err = ""
    for attempt in range(1, attempts + 1):
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        if proc.returncode == 0:
            return
        last_err = (proc.stderr or proc.stdout).strip()[-500:]
        if attempt < attempts:
            time.sleep(30)
    raise RuntimeError(f"上传到服务器失败：{last_err}")


def sync_image_files(keys, src_dir: Path, dst_dir: Path) -> int:
    """把缓存里的 WebP 同步到站点目录，返回总字节数。
    只复制新增或变化的文件并保留修改时间，rsync 上传时不会重传没变的图；本期不再用到的旧图删掉。"""
    dst_dir.mkdir(parents=True, exist_ok=True)
    wanted = {f"{k}.webp" for k in keys}
    for old in dst_dir.glob("*.webp"):
        if old.name not in wanted:
            old.unlink()
    total = 0
    for name in sorted(wanted):
        src, dst = src_dir / name, dst_dir / name
        st = src.stat()
        if not dst.exists() or dst.stat().st_size != st.st_size or int(dst.stat().st_mtime) != int(st.st_mtime):
            shutil.copy2(src, dst)
        total += st.st_size
    return total


def _image_rows(result: dict) -> list:
    rows = []
    for view in result["views"].values():
        for tab in ("bestsellers", "high_value", "forecast"):
            rows.extend(view[tab])
        rows.extend(view["sell_through"]["rows"])
        for group in ("store_gaps", "no_purchase"):
            for g in view[group]:
                rows.extend(g["rows"])
    return rows


def write_outputs(result: dict, out_dir: Path, template_path: Path, as_of: date, publish_dir: str | None = None):
    run_dir = out_dir / "runs" / as_of.isoformat()
    run_dir.mkdir(parents=True, exist_ok=True)

    result["meta"]["backtest"] = load_backtest_summary(out_dir)

    prev_rows_path = out_dir / "latest_rows.csv"
    prev_rows = load_prev_rows(prev_rows_path)
    apply_change_vs_prev(result, prev_rows)

    rows_flat = result.pop("_rows_flat")
    write_rows_csv(run_dir / "rows.csv", rows_flat)
    write_rows_csv(prev_rows_path, rows_flat)  # 供下次运行读取

    # ---------- 图片：下载缓存，并给每行挂上图片 key ----------
    image_rows = _image_rows(result)
    urls = sorted({r["image_url"] for r in image_rows if r.get("image_url")})
    cache_dir = out_dir / "image_cache"
    available, image_stats = ensure_images(urls, cache_dir)
    for r in image_rows:
        r["img"] = available.get(r.get("image_url"))
    result["meta"]["image_stats"] = image_stats
    if image_stats["total"] and image_stats["missing"] > image_stats["total"] * 0.1:
        result["meta"]["warnings"].append(
            f"{image_stats['missing']}/{image_stats['total']} 张商品图下载失败或不存在，已用占位图代替"
        )
        result["meta"]["warnings_en"].append(
            f"{image_stats['missing']} of {image_stats['total']} product images failed to download and show a placeholder"
        )

    # ---------- JSON（不含图片数据）----------
    json_text = json.dumps(result, ensure_ascii=False, indent=2, default=str)
    json_path = run_dir / "report.json"
    json_path.write_text(json_text, encoding="utf-8")
    (out_dir / "latest_report.json").write_text(json_text, encoding="utf-8")
    # 定时任务只需要摘要，单独落一个小文件，避免去读几 MB 的完整 JSON
    meta_path = out_dir / "latest_meta.json"
    meta_path.write_text(json.dumps(result["meta"], ensure_ascii=False, indent=2, default=str), encoding="utf-8")

    # ---------- 站点包：index.html + 每张图一个 WebP 文件 ----------
    # 图片是公共文件（有查看权限即可读），不放进 data/（那里每个文件都按门店鉴权）。
    # 一张图一个文件，页面用 loading="lazy" 只加载看得到的缩略图，放大图点开才下载；
    # 不再整包下发 3.8MB 的 thumbs.json 和 8 个约 7.7MB 的放大图分块（那是 Artifact 的 CSP 限制下的做法）。
    site_dir = out_dir / "site"
    img_dir = site_dir / "img"
    img_dir.mkdir(parents=True, exist_ok=True)
    for legacy in [site_dir / "thumbs.json"] + list(img_dir.glob("lg-*.js")):
        if legacy.exists():
            legacy.unlink()
    keys = sorted(set(available.values()))
    thumb_bytes = sync_image_files(keys, cache_dir / "thumb", img_dir / "t")
    large_bytes = sync_image_files(keys, cache_dir / "large", img_dir / "l")

    # 数据文件：all.json 需全部门店权限，store-{门店}.json 需全部门店权限或关联该店。
    # 先清掉上一期的数据文件（只删本脚本生成的 all.json / store-*.json），避免停用门店的旧文件残留。
    data_dir = site_dir / "data"
    data_dir.mkdir(parents=True, exist_ok=True)
    for old in list(data_dir.glob("store-*.json")) + list(data_dir.glob("all.json")):
        old.unlink()
    view_meta_base = {**result["shared_meta"], "backtest": result["meta"].get("backtest"),
                      "image_stats": result["meta"].get("image_stats")}
    data_files = []
    for name, view in result["views"].items():
        payload = {
            "meta": {**view_meta_base, **view["meta_overrides"], "scope": view["scope"]},
            **{k: view[k] for k in ("bestsellers", "high_value", "forecast", "store_gaps", "stock_holders", "no_purchase", "sell_through")},
        }
        rel = f"data/{name}.json"
        (site_dir / rel).write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":"), default=str), encoding="utf-8")
        data_files.append(rel)

    # 页面外壳只带启动信息（门店名单、生成时间），不含任何业务数据
    bootstrap = {
        "generated_at": result["meta"]["generated_at"],
        "as_of": result["meta"]["as_of"],
        "data_cutoff": result["meta"]["data_cutoff"],
        "supplier_name": result["meta"]["supplier_name"],
        "supplier_code": result["meta"]["supplier_code"],
        "stores": result["stores"],
    }
    html = render_html(template_path, bootstrap)
    index_path = site_dir / "index.html"
    index_path.write_text(html, encoding="utf-8")

    publish_root = None
    if publish_dir:
        # 只覆盖同名文件，不删除目录，避免误删
        publish_root = Path(publish_dir).expanduser()
        shutil.copytree(site_dir, publish_root, dirs_exist_ok=True)

    return {
        "json_path": json_path,
        "meta_path": meta_path,
        "index_path": index_path,
        "image_count": len(keys),
        "data_files": data_files,
        "publish_root": publish_root,
        "page_bytes": len(html.encode("utf-8")),
        "thumb_bytes": thumb_bytes,
        "large_bytes": large_bytes,
        "data_bytes": sum((site_dir / f).stat().st_size for f in data_files),
    }


# ============================================================
# 回测
# ============================================================


def run_backtest(cfg: Config, dates: list, out_dir: Path):
    results = []
    for d in dates:
        warnings = []
        analysis = run_analysis(cfg, d, warn_out=warnings)
        forecast_by_product = {r["product_code"]: r["forecast14"] for r in analysis["views"]["all"]["forecast"]}
        conn, cur = connect(cfg.appsettings_path, "DefaultConnection")
        timings = {}
        actual = fetch_actual_sales_window(
            cur, timings, cfg.supplier_code, d, d + timedelta(days=13)
        )
        conn.close()

        errors = []
        total_forecast = 0
        total_actual = 0
        for product, forecast in forecast_by_product.items():
            actual_qty = actual.get(product, 0)
            total_forecast += forecast
            total_actual += actual_qty
            if actual_qty > 0:
                errors.append(abs(forecast - actual_qty) / actual_qty)
        mape = sum(errors) / len(errors) if errors else None
        results.append(
            {
                "as_of": d.isoformat(),
                "data_cutoff": analysis["meta"]["data_cutoff"],
                "product_count": len(forecast_by_product),
                "mape": round(mape, 3) if mape is not None else None,
                "total_forecast14": total_forecast,
                "total_actual14": total_actual,
            }
        )
        print(f"[回测] {d.isoformat()}: MAPE={mape}, 预测总量={total_forecast}, 实际总量={total_actual}")

    out_path = out_dir / "runs" / "backtest.json"
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"回测结果已写入 {out_path}")


# ============================================================
# 入口
# ============================================================


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--as-of", help="运行日 YYYY-MM-DD，默认按 Australia/Sydney 的今天")
    parser.add_argument(
        "--out-dir", default=str(Path.home() / "Documents/HB-Reports/kfc-uncle-bills")
    )
    parser.add_argument("--supplier-code", default="257")
    parser.add_argument("--since", help="进销累计页签起点 YYYY-MM-DD，默认最近一个 8 月 1 日")
    parser.add_argument("--backtest", help="逗号分隔的历史日期列表，跑回测而不生成正式报告")
    parser.add_argument(
        "--upload-server",
        action="store_true",
        help="生成后把站点包同步到 hotbargain.vip 的报告目录（需主站登录访问）",
    )
    parser.add_argument(
        "--publish-dir",
        help="把站点包（index.html + img/）复制到这个目录，供 Artifact 发布；应位于发布会话的 scratchpad 内",
    )
    parser.add_argument(
        "--appsettings-path",
        default=str(DEFAULT_APPSETTINGS_PATH),
        help="appsettings.Development.json 路径（读取数据库连接串）",
    )
    args = parser.parse_args()

    cfg = Config(
        supplier_code=args.supplier_code,
        appsettings_path=Path(args.appsettings_path),
        sell_through_since=datetime.strptime(args.since, "%Y-%m-%d").date() if args.since else None,
    )
    out_dir = Path(args.out_dir).expanduser()
    template_path = Path(__file__).parent / "report_template.html"

    if args.backtest:
        dates = [datetime.strptime(s.strip(), "%Y-%m-%d").date() for s in args.backtest.split(",")]
        run_backtest(cfg, dates, out_dir)
        return

    as_of = (
        datetime.strptime(args.as_of, "%Y-%m-%d").date()
        if args.as_of
        else datetime.now(SYDNEY_TZ).date()
    )

    t0 = time.time()
    warnings = []
    try:
        result = run_analysis(cfg, as_of, warnings)
    except Exception as exc:  # noqa: BLE001 - 顶层兜底，打印清晰错误供定时任务汇报
        print(f"分析失败：{exc}", file=sys.stderr)
        raise

    out = write_outputs(result, out_dir, template_path, as_of, args.publish_dir)
    elapsed = round(time.time() - t0, 1)
    stats = result["meta"]["image_stats"]

    print(f"完成，用时 {elapsed} 秒")
    print(
        f"图片：共 {stats['total']} 张，缓存 {stats['cached']}，新下载 {stats['downloaded']}，缺失 {stats['missing']}；"
        f"页面外壳 {out['page_bytes'] / 1e3:.0f}KB，数据文件 {len(out['data_files'])} 个共 {out['data_bytes'] / 1e6:.1f}MB，"
        f"图片 {out['image_count']} 张（缩略图共 {out['thumb_bytes'] / 1e6:.1f}MB、放大图共 {out['large_bytes'] / 1e6:.1f}MB，按需加载）"
    )
    print(f"REPORT_JSON={out['json_path']}")
    print(f"META_JSON={out['meta_path']}")
    print(f"SITE_INDEX={out['index_path']}")
    if args.upload_server:
        try:
            upload_site(out["index_path"].parent)
        except Exception as exc:  # noqa: BLE001 - 上传失败要让定时任务明确汇报
            print(f"UPLOAD_FAILED={exc}", file=sys.stderr)
            sys.exit(2)
        print(f"SERVER_URL={SERVER_URL}")
    if out["publish_root"]:
        print(f"PUBLISH_ROOT={out['publish_root']}")
        print(f"PUBLISH_FILE={out['publish_root'] / 'index.html'}")
        print(f"PUBLISH_FILES={','.join(out['data_files'])}")
    print(
        f"摘要：数据截止 {result['meta']['data_cutoff']}，候选商品 {result['meta']['candidate_count']} 个，"
        f"缺货店 {result['meta']['shortage_store_count']} 家，缺货商品 {result['meta']['shortage_product_count']} 个，"
        f"紧急 {result['meta']['urgent_count']} 项，走势「{result['meta']['trend_phase']}」"
    )
    if warnings:
        print("警告：")
        for w in warnings:
            print(f"  - {w}")


if __name__ == "__main__":
    main()
