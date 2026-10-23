using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 修订版重新上传（方案 3.2）：供应商发来新版订单表时按货号对齐。已下的箱数保留；被删掉的商品标为「已下架」（不删数据、
/// 不计入汇总）；改价、改箱规的商品记下旧值，在管理端和下单页高亮。先预览差异，确认后再应用。
/// </summary>
public sealed partial class SupplierPreorderService
{
    /// <summary>新表比旧表少了一半以上的商品时提醒「是不是传错了表」。</summary>
    private const decimal SuspiciousRemovalRatio = 0.5m;

    private sealed record RevisionPlan(
        List<SupplierPreorderNormalizedRow> ValidRows,
        int InvalidCount,
        List<SupplierPreorderRevisionDiff> Diffs,
        FormSnapshot Snapshot);

    private async Task<RevisionPlan> PlanRevisionAsync(SupplierPreorderForm form, SupplierPreorderRevisionRequestDto request)
    {
        ValidateRequiredMapping(request.Mapping);
        var rows = SupplierPreorderItemRules.Normalize(request.Rows ?? new(), request.Mapping);
        var valid = rows.Where(r => !r.Invalid).ToList();
        if (valid.Count == 0)
        {
            throw new SupplierPreorderException("修订版里没有可导入的商品行", "NO_VALID_ROWS");
        }

        var snapshot = await LoadSnapshotAsync(form);
        // 不能导入的行只要有货号，对应的老商品就保持原样（不因为这一版漏填进价之类而被下架）
        var keepCodes = rows.Where(r => r.Invalid && !string.IsNullOrWhiteSpace(r.SupplierItemCode)).Select(r => r.SupplierItemCode);
        var diffs = SupplierPreorderRevisionRules.Diff(snapshot.Items, valid, keepCodes);
        return new RevisionPlan(valid, rows.Count - valid.Count, diffs, snapshot);
    }

    public async Task<SupplierPreorderRevisionPreviewDto> PreviewRevisionAsync(string formGuid, SupplierPreorderRevisionRequestDto request)
    {
        var form = await RequireFormAsync(formGuid);
        var plan = await PlanRevisionAsync(form, request);
        var ordered = plan.Snapshot.CountedLines()
            .GroupBy(x => x.Item.ItemGuid)
            .ToDictionary(g => g.Key, g => (Cartons: g.Sum(x => x.Line.Cartons), Stores: g.Select(x => x.Order.StoreName).Distinct().OrderBy(n => n).ToList()));

        var counts = new SupplierPreorderRevisionCountsDto { Total = plan.ValidRows.Count, Invalid = plan.InvalidCount };
        var entries = new List<SupplierPreorderRevisionEntryDto>();
        foreach (var diff in plan.Diffs)
        {
            switch (diff.Kind)
            {
                case SupplierPreorderRevisionRules.Added: counts.Added++; break;
                case SupplierPreorderRevisionRules.Removed: counts.Removed++; break;
                case SupplierPreorderRevisionRules.Restored: counts.Restored++; break;
                case SupplierPreorderRevisionRules.Changed: counts.Changed++; break;
                default: counts.Unchanged++; break;
            }

            if (diff.Changes.Contains(SupplierPreorderRevisionRules.ChangePrice))
            {
                counts.PriceChanged++;
            }

            if (diff.Changes.Contains(SupplierPreorderRevisionRules.ChangeCarton))
            {
                counts.CartonChanged++;
            }

            if (diff.Kind == SupplierPreorderRevisionRules.Unchanged)
            {
                continue;
            }

            var orderedInfo = diff.Existing is null ? default : ordered.GetValueOrDefault(diff.Existing.ItemGuid);
            if (diff.Kind == SupplierPreorderRevisionRules.Removed && orderedInfo.Cartons > 0)
            {
                counts.RemovedWithOrders++;
            }

            entries.Add(new SupplierPreorderRevisionEntryDto
            {
                Kind = diff.Kind,
                SupplierItemCode = diff.Row?.SupplierItemCode ?? diff.Existing!.SupplierItemCode,
                Name = diff.Row?.Name ?? diff.Existing!.Name,
                ItemGuid = diff.Existing?.ItemGuid,
                RowNo = diff.Row?.RowNo,
                Changes = diff.Changes,
                OldPrice = diff.Existing?.Price,
                NewPrice = diff.Row?.Price,
                OldCartonQty = diff.Existing?.CartonQty,
                NewCartonQty = diff.Row?.CartonQty,
                OldName = diff.Changes.Contains(SupplierPreorderRevisionRules.ChangeName) ? diff.Existing?.Name : null,
                OrderedCartons = orderedInfo.Cartons,
                OrderedStores = orderedInfo.Stores ?? new List<string>(),
            });
        }

        var warnings = new List<string>();
        var activeBefore = plan.Snapshot.Items.Count(i => i.IsActive);
        if (activeBefore > 0 && counts.Removed > activeBefore * SuspiciousRemovalRatio)
        {
            warnings.Add($"修订版比当前少了 {counts.Removed} 款（超过一半），请确认是不是同一张订单表");
        }

        if (plan.InvalidCount > 0)
        {
            warnings.Add($"修订版里有 {plan.InvalidCount} 行不能导入（缺货号、名称、进价或箱规），应用时跳过；其中货号还在的老商品保持原样，不会下架");
        }

        if (counts.RemovedWithOrders > 0)
        {
            warnings.Add($"有 {counts.RemovedWithOrders} 款下架商品已有分店下单：箱数会保留，但不再计入汇总和导出");
        }

        if (string.IsNullOrEmpty(request.SourceFileGuid) && !string.IsNullOrEmpty(form.SourceFileGuid))
        {
            warnings.Add("这次是粘贴的修订版：应用后不能再「填回原表」导出，只能导出通用格式");
        }

        var duplicates = plan.ValidRows.GroupBy(r => SupplierPreorderRevisionRules.CodeKey(r.SupplierItemCode)).Where(g => g.Count() > 1).Select(g => g.First().SupplierItemCode).ToList();
        if (duplicates.Count > 0)
        {
            warnings.Add($"修订版里有重复货号（按出现顺序对齐）：{string.Join("、", duplicates.Take(10))}{(duplicates.Count > 10 ? " 等" : string.Empty)}");
        }

        return new SupplierPreorderRevisionPreviewDto
        {
            CurrentRevision = form.Revision,
            Counts = counts,
            Entries = entries,
            Warnings = warnings,
        };
    }

    public async Task<SupplierPreorderFormDetailDto> ApplyRevisionAsync(string formGuid, SupplierPreorderRevisionRequestDto request)
    {
        var form = await RequireFormAsync(formGuid);
        var plan = await PlanRevisionAsync(form, request);

        SupplierPreorderSourceFile? sourceFile = null;
        if (!string.IsNullOrWhiteSpace(request.SourceFileGuid))
        {
            sourceFile = (await _db.Queryable<SupplierPreorderSourceFile>()
                    .Where(f => f.FileGuid == request.SourceFileGuid && f.FormGuid == null)
                    .Select(f => new SupplierPreorderSourceFile { FileGuid = f.FileGuid, FileName = f.FileName })
                    .FirstAsync())
                ?? throw new SupplierPreorderException("上传的原表已失效，请重新上传", "SOURCE_FILE_NOT_FOUND");
        }

        // 新增的商品照导入时的规则匹配我方商品（条码 → 货号 → 去年货号）
        var addedRows = plan.Diffs.Where(d => d.Kind == SupplierPreorderRevisionRules.Added).Select(d => d.Row!).ToList();
        var addedMatches = addedRows.Count == 0
            ? new List<SupplierPreorderMatchResult>()
            : SupplierPreorderItemRules.Match(addedRows, await LoadMatchCandidatesAsync(addedRows), form.LocalSupplierCode);

        var now = NowUtc;
        var revision = form.Revision + 1;
        var keepRowNo = sourceFile is not null;
        var updates = new List<SupplierPreorderFormItem>();
        var inserts = new List<SupplierPreorderFormItem>();
        var sortOrder = 0;
        var addedIndex = 0;
        foreach (var diff in plan.Diffs.Where(d => d.Row is not null))
        {
            sortOrder++;
            if (diff.Existing is not null)
            {
                SupplierPreorderRevisionRules.ApplyRow(diff.Existing, diff.Row!, diff, revision, sortOrder, keepRowNo);
                updates.Add(diff.Existing);
                continue;
            }

            var row = diff.Row!;
            var match = addedMatches[addedIndex++];
            var notes = match.Warnings.Count > 0 ? string.Join("；", match.Warnings) : null;
            inserts.Add(new SupplierPreorderFormItem
            {
                FormGuid = form.FormGuid,
                Seq = row.Seq,
                SourceRowNo = keepRowNo ? row.RowNo : 0,
                SortOrder = sortOrder,
                SupplierItemCode = row.SupplierItemCode,
                Barcode = row.Barcode,
                Name = row.Name,
                Unit = row.Unit,
                CartonQty = row.CartonQty,
                CartonCbm = row.CartonCbm,
                Price = row.Price,
                Category = row.Category,
                PrevYearItemCode = row.PrevYearItemCode,
                ImageSourceUrl = row.ImageSourceUrl,
                Notes = row.Notes,
                MatchedProductCode = match.Product?.ProductCode,
                MatchMethod = match.MatchMethod,
                MatchNote = notes is { Length: > 300 } ? notes[..300] : notes,
                IsActive = true,
                RevisionNo = revision,
                RevisionChange = SupplierPreorderRevisionRules.Added,
            });
        }

        foreach (var diff in plan.Diffs.Where(d => d.Kind == SupplierPreorderRevisionRules.Removed))
        {
            SupplierPreorderRevisionRules.MarkRemoved(diff.Existing!, revision, ++sortOrder, now);
            updates.Add(diff.Existing!);
        }

        // 以前就下架、这次也没出现的：行号对新表已无意义
        var touched = updates.Select(i => i.ItemGuid).ToHashSet();
        foreach (var item in plan.Snapshot.Items.Where(i => !touched.Contains(i.ItemGuid)))
        {
            item.SourceRowNo = 0;
            item.SortOrder = ++sortOrder;
            updates.Add(item);
        }

        var stored = ReadStoredMapping(form.ColumnMappingJson);
        form.Revision = revision;
        form.RevisedAtUtc = now;
        form.ItemCount = updates.Count(i => i.IsActive) + inserts.Count;
        form.SourceFileGuid = sourceFile?.FileGuid;
        form.ColumnMappingJson = SerializeMapping(new StoredMapping
        {
            SheetName = request.SheetName ?? stored?.SheetName,
            HeaderRowNo = request.HeaderRowNo,
            HeaderSignature = request.HeaderSignature,
            Mapping = request.Mapping,
        });
        form.UpdatedAtUtc = now;
        form.UpdatedBy = CurrentUserName;

        var counts = plan.Diffs.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count());
        await RunInTransactionAsync(async () =>
        {
            await _db.Updateable(form)
                .UpdateColumns(f => new { f.Revision, f.RevisedAtUtc, f.ItemCount, f.SourceFileGuid, f.ColumnMappingJson, f.UpdatedAtUtc, f.UpdatedBy })
                .ExecuteCommandAsync();
            foreach (var chunk in updates.Chunk(300))
            {
                await _db.Updateable(chunk.ToList()).ExecuteCommandAsync();
            }

            foreach (var chunk in inserts.Chunk(300))
            {
                await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
            }

            if (sourceFile is not null)
            {
                await _db.Updateable<SupplierPreorderSourceFile>()
                    .SetColumns(f => f.FormGuid == form.FormGuid)
                    .Where(f => f.FileGuid == sourceFile.FileGuid)
                    .ExecuteCommandAsync();
            }

            await SaveImportProfileAsync(form.LocalSupplierCode, request.HeaderSignature, request.Mapping, form.ExportQtyUnit);
        });

        var summary = $"第 {revision} 版：新增 {counts.GetValueOrDefault(SupplierPreorderRevisionRules.Added)}、下架 {counts.GetValueOrDefault(SupplierPreorderRevisionRules.Removed)}、"
                      + $"重新上架 {counts.GetValueOrDefault(SupplierPreorderRevisionRules.Restored)}、有改动 {counts.GetValueOrDefault(SupplierPreorderRevisionRules.Changed)}"
                      + (sourceFile is null ? "（粘贴）" : $"（{sourceFile.FileName}）");
        await WriteAuthLogAsync("FormStore", "FormRevised", summary, form.FormGuid);
        _logger.LogInformation("供应商预订单修订版已应用：{FormGuid} {Summary}", form.FormGuid, summary);

        // 新增商品要匹配历史与参考商品，换过的图要重新转存（转存任务会自动拾取）
        await TryRequestHistoryAsync(form.FormGuid);
        return await GetFormAsync(form.FormGuid);
    }

    /// <summary>手动提醒未提交的分店：只限进行中的订单表；同一分钟内重复点击不会重复发。</summary>
    public async Task<SupplierPreorderRemindResultDto> RemindPendingStoresAsync(string formGuid)
    {
        var form = await RequireFormAsync(formGuid);
        if (form.Status != SupplierPreorderFormStatuses.Open)
        {
            throw new SupplierPreorderException("只有进行中的订单表才能提醒分店", "FORM_NOT_OPEN", 409);
        }

        if (_notificationPublisher is null)
        {
            throw new SupplierPreorderException("通知服务不可用", "NOTIFICATION_UNAVAILABLE", 503);
        }

        var reminder = new SupplierPreorderReminderService(_db, _notificationPublisher, _logger, _timeProvider);
        var result = await reminder.RemindAsync(form, $"manual-{NowUtc:yyyyMMddHHmm}", $"user:{CurrentUserGuid}");
        await WriteAuthLogAsync("FormStore", "Reminded",
            $"未提交 {result.PendingStoreCount} 家，通知 {result.NotifiedUserCount} 人"
            + (result.StoresWithoutOrderers.Count > 0 ? $"；无人可提醒：{string.Join("、", result.StoresWithoutOrderers)}" : string.Empty),
            form.FormGuid);
        return result;
    }

    private static SupplierPreorderRevisionMarkDto? ToRevisionMark(SupplierPreorderFormItem item)
    {
        var changes = SupplierPreorderRevisionRules.ParseChanges(item.RevisionChange);
        return changes.Count == 0
            ? null
            : new SupplierPreorderRevisionMarkDto { Revision = item.RevisionNo, Changes = changes, PrevPrice = item.PrevPrice, PrevCartonQty = item.PrevCartonQty };
    }
}
