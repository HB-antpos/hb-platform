using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobEmployeeBindingService
{
    Task<ApiResponse<MyobBindingSnapshotDto>> GetSnapshotAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken);

    Task<ApiResponse<MyobBindingCandidatesDto>> GetCandidatesAsync(
        string connectionGuid, string userGuid, string? keyword, CancellationToken cancellationToken);

    Task<ApiResponse<MyobConfirmBindingsResult>> ConfirmAsync(
        MyobConfirmBindingsRequest request, string actorUserGuid, CancellationToken cancellationToken);

    Task<ApiResponse<bool>> UnbindAsync(string linkGuid, string actorUserGuid, CancellationToken cancellationToken);

    Task<ApiResponse<bool>> IgnoreAsync(MyobIgnoreEmployeeRequest request, string actorUserGuid, CancellationToken cancellationToken);
}

/// <summary>
/// 员工绑定（方案第 6 节）：把我方员工与 MYOB 公司文件里的员工卡配对。
/// 只读 MYOB（Contact/Employee、EmployeePayrollDetails），不往 MYOB 写任何东西；
/// 打分只给「建议」，绑定必须人工确认；高分（邮箱 + 生日都一致）才允许批量确认。
/// </summary>
public sealed class MyobEmployeeBindingService : IMyobEmployeeBindingService
{
    private const int MaxCandidates = 30;

    private readonly ISqlSugarClient _db;
    private readonly IMyobEmployeeDirectory _directory;
    private readonly TimeProvider _time;

    public MyobEmployeeBindingService(
        SqlSugarContext context,
        IMyobEmployeeDirectory directory,
        TimeProvider? timeProvider = null)
        : this(context.Db, directory, timeProvider)
    {
    }

    internal MyobEmployeeBindingService(ISqlSugarClient db, IMyobEmployeeDirectory directory, TimeProvider? timeProvider = null)
    {
        _db = db;
        _directory = directory;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── 快照 ─────────────────────────

    public async Task<ApiResponse<MyobBindingSnapshotDto>> GetSnapshotAsync(
        string connectionGuid,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var connection = await FindActiveConnectionAsync(connectionGuid);
        if (connection is null)
        {
            return ApiResponse<MyobBindingSnapshotDto>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        MyobEmployeeDirectoryData directory;
        try
        {
            directory = await _directory.GetAsync(connectionGuid, refresh, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobBindingSnapshotDto>.Error(ex.Message, ex.Code);
        }

        var today = _time.GetUtcNow().UtcDateTime.Date;
        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(today);
        var links = await _db.Queryable<EmployeeMyobLink>()
            .Where(item => item.ConnectionGuid == connectionGuid && item.UnboundAt == null)
            .ToListAsync();
        var ignored = (await _db.Queryable<MyobEmployeeIgnore>()
                .Where(item => item.ConnectionGuid == connectionGuid)
                .ToListAsync())
            .Select(item => item.MyobEmployeeUid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshot = BuildSnapshot(connection, directory, workforce, links, ignored, await LoadNamesAsync(links));
        return ApiResponse<MyobBindingSnapshotDto>.OK(snapshot);
    }

    internal static MyobBindingSnapshotDto BuildSnapshot(
        MyobConnection connection,
        MyobEmployeeDirectoryData directory,
        Workforce workforce,
        IReadOnlyList<EmployeeMyobLink> links,
        ISet<string> ignoredCardUids,
        IReadOnlyDictionary<string, string> actorNames)
    {
        var cardsByUid = directory.Cards.ToDictionary(card => card.Dto.Uid, StringComparer.OrdinalIgnoreCase);
        var linkByUser = links.ToDictionary(link => link.UserGuid, StringComparer.OrdinalIgnoreCase);
        var linkByCard = links.ToDictionary(link => link.MyobEmployeeUid, StringComparer.OrdinalIgnoreCase);
        var connectionGuid = connection.ConnectionGuid;

        // 本公司的员工：至少有一个上班分店今天由本公司发薪；已绑定本公司卡的员工也算（便于解绑）。
        var memberGuids = workforce.Pairs
            .Where(pair => string.Equals(pair.EffectiveConnectionGuid, connectionGuid, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.UserGuid)
            .Concat(links.Select(link => link.UserGuid))
            .Where(workforce.Users.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var crossCompany = workforce.Pairs
            .Where(pair => string.Equals(pair.EffectiveConnectionGuid, connectionGuid, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(pair.DefaultConnectionGuid, connectionGuid, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.UserGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 候选分配：全部（未绑定员工 × 未绑定的卡）按得分从高到低贪心分配，一张卡只建议给一个人。
        var unboundUsers = memberGuids.Where(guid => !linkByUser.ContainsKey(guid)).ToList();
        var freeCards = directory.Cards.Where(card => !linkByCard.ContainsKey(card.Dto.Uid)).ToList();
        var assignment = AssignCandidates(unboundUsers, freeCards, workforce);

        var rows = new List<MyobBindingRowDto>();
        foreach (var userGuid in memberGuids)
        {
            var user = workforce.Users[userGuid];
            var row = new MyobBindingRowDto
            {
                UserGuid = userGuid,
                UserName = user.Name,
                Email = user.Email,
                EmployeeType = user.EmployeeType?.ToString(),
                StoreLabels = workforce.StoreLabelsOf(userGuid, connectionGuid),
                IsCrossCompany = crossCompany.Contains(userGuid),
            };

            if (linkByUser.TryGetValue(userGuid, out var link))
            {
                row.Status = MyobBindingStatuses.Bound;
                row.Link = ToLinkDto(link, cardsByUid.ContainsKey(link.MyobEmployeeUid), actorNames);
            }
            else if (assignment.TryGetValue(userGuid, out var chosen))
            {
                row.Candidate = ToCandidateDto(chosen.Card, chosen.Evaluation, null, null);
                row.Status = chosen.Evaluation.IsHigh ? MyobBindingStatuses.Suggested : MyobBindingStatuses.NeedsReview;
            }
            else
            {
                // 没有可分配的空闲卡：看看是不是最像的卡已被别人绑走（冲突），否则就是真的没有。
                var conflict = FindConflict(user, directory.Cards, linkByCard, workforce);
                if (conflict is not null)
                {
                    row.Candidate = conflict;
                    row.Status = MyobBindingStatuses.Conflict;
                }
                else
                {
                    row.Status = MyobBindingStatuses.Unmatched;
                }
            }

            rows.Add(row);
        }

        rows = rows
            .OrderBy(row => StatusRank(row.Status))
            .ThenByDescending(row => row.Candidate?.Score ?? 0)
            .ThenBy(row => row.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var onlyInOurs = rows
            .Where(row => row.Status == MyobBindingStatuses.Unmatched)
            .Select(row => ToOnlyInOurs(workforce.Users[row.UserGuid], row))
            .ToList();

        var claimedCards = assignment.Values.Select(item => item.Card.Dto.Uid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 只列启用的卡：MYOB 里已停用的多半是离职人员的历史卡，没有需要处理的事（线上实测 63 张卡里 48 张已停用），列出来只会淹没真正缺档的人。
        var onlyInMyob = freeCards
            .Where(card => card.Dto.IsActive && !claimedCards.Contains(card.Dto.Uid) && !ignoredCardUids.Contains(card.Dto.Uid))
            .OrderBy(card => card.Dto.Name, StringComparer.OrdinalIgnoreCase)
            .Select(card => new MyobOnlyInMyobDto { Card = card.Dto })
            .ToList();

        var typeIssues = BuildTypeIssues(rows, cardsByUid, workforce, connectionGuid);

        var counts = new MyobBindingCountsDto
        {
            All = rows.Count,
            Bound = rows.Count(row => row.Status == MyobBindingStatuses.Bound),
            Suggested = rows.Count(row => row.Status == MyobBindingStatuses.Suggested),
            NeedsReview = rows.Count(row => row.Status is MyobBindingStatuses.NeedsReview or MyobBindingStatuses.Conflict),
            Unmatched = rows.Count(row => row.Status == MyobBindingStatuses.Unmatched),
            OnlyInOurs = onlyInOurs.Count,
            OnlyInMyob = onlyInMyob.Count,
            TypeIssues = typeIssues.Count,
        };
        counts.BatchConfirmable = counts.Suggested;

        return new MyobBindingSnapshotDto
        {
            ConnectionGuid = connectionGuid,
            ConnectionName = connection.BusinessName,
            FetchedAt = directory.FetchedAt,
            Counts = counts,
            Rows = rows,
            OnlyInOurs = onlyInOurs,
            OnlyInMyob = onlyInMyob,
            TypeIssues = typeIssues,
        };
    }

    /// <summary>
    /// 贪心分配：所有「有正向证据」的（员工, 空闲卡）组合按得分降序（同分按员工 GUID、卡 UID 稳定排序），
    /// 谁先拿到卡谁得到它；一个员工只拿一张卡，一张卡只给一个员工。
    /// </summary>
    internal static Dictionary<string, (MyobDirectoryCard Card, MyobMatchEvaluation Evaluation)> AssignCandidates(
        IReadOnlyList<string> userGuids,
        IReadOnlyList<MyobDirectoryCard> freeCards,
        Workforce workforce)
    {
        var pairs = new List<(string UserGuid, MyobDirectoryCard Card, MyobMatchEvaluation Evaluation)>();
        foreach (var userGuid in userGuids)
        {
            var subject = ToSubject(workforce.Users[userGuid]);
            foreach (var card in freeCards)
            {
                var evaluation = MyobEmployeeMatcher.Evaluate(subject, card.Match);
                if (evaluation.HasPositiveEvidence)
                {
                    pairs.Add((userGuid, card, evaluation));
                }
            }
        }

        var result = new Dictionary<string, (MyobDirectoryCard, MyobMatchEvaluation)>(StringComparer.OrdinalIgnoreCase);
        var usedCards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs
                     .OrderByDescending(item => item.Evaluation.Score)
                     .ThenByDescending(item => item.Card.Dto.IsActive)
                     .ThenBy(item => item.UserGuid, StringComparer.Ordinal)
                     .ThenBy(item => item.Card.Dto.Uid, StringComparer.Ordinal))
        {
            if (result.ContainsKey(pair.UserGuid) || usedCards.Contains(pair.Card.Dto.Uid))
            {
                continue;
            }

            result[pair.UserGuid] = (pair.Card, pair.Evaluation);
            usedCards.Add(pair.Card.Dto.Uid);
        }

        return result;
    }

    /// <summary>员工没分到空闲卡时：最像的卡（得分 ≥ 45，即至少邮箱一致或生日加姓名一致）已绑给别人 → 冲突。</summary>
    private static MyobMatchCandidateDto? FindConflict(
        WorkforceUser user,
        IReadOnlyList<MyobDirectoryCard> cards,
        IReadOnlyDictionary<string, EmployeeMyobLink> linkByCard,
        Workforce workforce)
    {
        var subject = ToSubject(user);
        var best = cards
            .Where(card => linkByCard.ContainsKey(card.Dto.Uid))
            .Select(card => (Card: card, Evaluation: MyobEmployeeMatcher.Evaluate(subject, card.Match)))
            .Where(item => item.Evaluation.HasPositiveEvidence && item.Evaluation.Score >= 45)
            .OrderByDescending(item => item.Evaluation.Score)
            .FirstOrDefault();
        if (best.Card is null)
        {
            return null;
        }

        var owner = linkByCard[best.Card.Dto.Uid];
        var ownerName = workforce.Users.TryGetValue(owner.UserGuid, out var ownerUser) ? ownerUser.Name : null;
        return ToCandidateDto(best.Card, best.Evaluation, owner.UserGuid, ownerName);
    }

    private static List<MyobTypeIssueDto> BuildTypeIssues(
        IReadOnlyList<MyobBindingRowDto> rows,
        IReadOnlyDictionary<string, MyobDirectoryCard> cardsByUid,
        Workforce workforce,
        string connectionGuid)
    {
        var issues = new List<MyobTypeIssueDto>();
        foreach (var row in rows.Where(item => item.Status == MyobBindingStatuses.Bound && item.Link is not null))
        {
            if (!cardsByUid.TryGetValue(row.Link!.MyobEmployeeUid, out var card)
                || !Enum.TryParse<EmployeeType>(row.EmployeeType, out var type))
            {
                continue;
            }

            string? code = null;
            if (type is EmployeeType.FullTime or EmployeeType.PartTime
                && !(card.Dto.HasAnnualEntitlement && card.Dto.HasSickEntitlement))
            {
                code = MyobTypeIssueCodes.MissingLeaveEntitlement;
            }
            else if (type == EmployeeType.Temporary
                && (card.Dto.HasAnnualEntitlement || card.Dto.HasSickEntitlement))
            {
                code = MyobTypeIssueCodes.TemporaryHasLeaveEntitlement;
            }

            if (code is null)
            {
                continue;
            }

            issues.Add(new MyobTypeIssueDto
            {
                UserGuid = row.UserGuid,
                UserName = row.UserName,
                StoreLabels = row.StoreLabels,
                OurType = type.ToString(),
                MyobStatus = card.Dto.EmploymentStatus,
                HasAnnualEntitlement = card.Dto.HasAnnualEntitlement,
                HasSickEntitlement = card.Dto.HasSickEntitlement,
                IssueCode = code,
            });
        }

        return issues.OrderBy(item => item.UserName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────────────────── 候选（手动选卡） ─────────────────────────

    public async Task<ApiResponse<MyobBindingCandidatesDto>> GetCandidatesAsync(
        string connectionGuid,
        string userGuid,
        string? keyword,
        CancellationToken cancellationToken)
    {
        if (await FindActiveConnectionAsync(connectionGuid) is null)
        {
            return ApiResponse<MyobBindingCandidatesDto>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        MyobEmployeeDirectoryData directory;
        try
        {
            directory = await _directory.GetAsync(connectionGuid, false, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobBindingCandidatesDto>.Error(ex.Message, ex.Code);
        }

        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(_time.GetUtcNow().UtcDateTime.Date);
        if (!workforce.Users.TryGetValue(userGuid, out var user))
        {
            return ApiResponse<MyobBindingCandidatesDto>.Error("员工不存在或已停用", MyobBindErrorCodes.UserNotFound);
        }

        var links = await _db.Queryable<EmployeeMyobLink>()
            .Where(item => item.ConnectionGuid == connectionGuid && item.UnboundAt == null)
            .ToListAsync();
        var linkByCard = links.ToDictionary(link => link.MyobEmployeeUid, StringComparer.OrdinalIgnoreCase);

        var subject = ToSubject(user);
        var term = keyword?.Trim();
        IEnumerable<(MyobDirectoryCard Card, MyobMatchEvaluation Evaluation)> scored = directory.Cards
            .Select(card => (Card: card, Evaluation: MyobEmployeeMatcher.Evaluate(subject, card.Match)));
        scored = string.IsNullOrEmpty(term)
            ? scored.Where(item => item.Evaluation.HasPositiveEvidence)
            : scored.Where(item => MatchesKeyword(item.Card, term));

        var candidates = scored
            .OrderByDescending(item => item.Evaluation.Score)
            .ThenBy(item => item.Card.Dto.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .Select(item =>
            {
                string? ownerGuid = null;
                string? ownerName = null;
                if (linkByCard.TryGetValue(item.Card.Dto.Uid, out var owner) && owner.UserGuid != userGuid)
                {
                    ownerGuid = owner.UserGuid;
                    ownerName = workforce.Users.TryGetValue(owner.UserGuid, out var ownerUser) ? ownerUser.Name : "其他员工";
                }

                return ToCandidateDto(item.Card, item.Evaluation, ownerGuid, ownerName);
            })
            .ToList();

        return ApiResponse<MyobBindingCandidatesDto>.OK(new MyobBindingCandidatesDto
        {
            UserGuid = userGuid,
            UserName = user.Name,
            Email = user.Email,
            PhoneMasked = MyobEmployeeMatcher.MaskPhone(user.Phone),
            Birthday = user.Birthday?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EmployeeType = user.EmployeeType?.ToString(),
            StoreLabels = workforce.StoreLabelsOf(userGuid, connectionGuid),
            Candidates = candidates,
        });
    }

    private static bool MatchesKeyword(MyobDirectoryCard card, string term)
    {
        var tokens = MyobEmployeeMatcher.Tokens(term);
        var name = string.Join(' ', MyobEmployeeMatcher.Tokens(card.Dto.Name));
        if (tokens.Count > 0 && tokens.All(token => name.Contains(token, StringComparison.Ordinal)))
        {
            return true;
        }

        if (card.Dto.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        // 手机：输入的数字是任一号码的后缀（至少 3 位，避免一两位数字命中所有人）。
        var digits = new string(term.Where(char.IsDigit).ToArray());
        return digits.Length >= 3
            && card.Match.Phones.Any(phone => new string(phone.Where(char.IsDigit).ToArray()).EndsWith(digits, StringComparison.Ordinal));
    }

    // ───────────────────────── 确认 / 解绑 / 忽略 ─────────────────────────

    public async Task<ApiResponse<MyobConfirmBindingsResult>> ConfirmAsync(
        MyobConfirmBindingsRequest request,
        string actorUserGuid,
        CancellationToken cancellationToken)
    {
        var connection = await FindActiveConnectionAsync(request.ConnectionGuid);
        if (connection is null)
        {
            return ApiResponse<MyobConfirmBindingsResult>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        if (request.Items.Count == 0)
        {
            return ApiResponse<MyobConfirmBindingsResult>.OK(new MyobConfirmBindingsResult());
        }

        MyobEmployeeDirectoryData directory;
        try
        {
            directory = await _directory.GetAsync(request.ConnectionGuid, false, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobConfirmBindingsResult>.Error(ex.Message, ex.Code);
        }

        var workforce = await new MyobWorkforceLoader(_db).LoadAsync(_time.GetUtcNow().UtcDateTime.Date);
        var cardsByUid = directory.Cards.ToDictionary(card => card.Dto.Uid, StringComparer.OrdinalIgnoreCase);
        var result = new MyobConfirmBindingsResult();
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var item in request.Items)
        {
            var failure = await TryBindAsync(item, request.ConnectionGuid, workforce, cardsByUid, actorUserGuid, now);
            if (failure is null)
            {
                result.Confirmed++;
            }
            else
            {
                result.Failed.Add(failure);
            }
        }

        return ApiResponse<MyobConfirmBindingsResult>.OK(
            result,
            result.Failed.Count == 0 ? "绑定成功" : $"已绑定 {result.Confirmed} 条，{result.Failed.Count} 条失败");
    }

    private async Task<MyobConfirmFailure?> TryBindAsync(
        MyobConfirmBindingItem item,
        string connectionGuid,
        Workforce workforce,
        IReadOnlyDictionary<string, MyobDirectoryCard> cardsByUid,
        string actorUserGuid,
        DateTime now)
    {
        MyobConfirmFailure Fail(string code, string message) =>
            new() { UserGuid = item.UserGuid, Code = code, Message = message };

        if (!workforce.Users.TryGetValue(item.UserGuid ?? string.Empty, out var user))
        {
            return Fail(MyobBindErrorCodes.UserNotFound, "员工不存在或已停用");
        }

        if (!cardsByUid.TryGetValue(item.MyobEmployeeUid ?? string.Empty, out var card))
        {
            return Fail(MyobBindErrorCodes.CardNotFound, "MYOB 里找不到这张员工卡，请刷新后重试");
        }

        // 唯一约束靠库里的过滤唯一索引兜底；这里先查一遍，给出明确的失败原因（测试库没有该索引，也靠这里保证）。
        var userLink = await _db.Queryable<EmployeeMyobLink>()
            .FirstAsync(link => link.ConnectionGuid == connectionGuid && link.UserGuid == item.UserGuid && link.UnboundAt == null);
        if (userLink is not null)
        {
            return Fail(MyobBindErrorCodes.UserAlreadyBound, $"{user.Name} 在这个公司文件里已经绑定了员工卡");
        }

        var cardLink = await _db.Queryable<EmployeeMyobLink>()
            .FirstAsync(link => link.ConnectionGuid == connectionGuid && link.MyobEmployeeUid == card.Dto.Uid && link.UnboundAt == null);
        if (cardLink is not null)
        {
            var ownerName = workforce.Users.TryGetValue(cardLink.UserGuid, out var owner) ? owner.Name : "其他员工";
            return Fail(MyobBindErrorCodes.CardAlreadyBound, $"这张员工卡已绑定给 {ownerName}");
        }

        var evaluation = MyobEmployeeMatcher.Evaluate(ToSubject(user), card.Match);
        if (!item.Manual && !evaluation.IsHigh)
        {
            return Fail(MyobBindErrorCodes.NotHighScore, "这条不再是高分建议（资料可能已变化），请逐条核对后手动绑定");
        }

        var link = new EmployeeMyobLink
        {
            LinkGuid = Guid.NewGuid().ToString(),
            UserGuid = item.UserGuid!,
            ConnectionGuid = connectionGuid,
            MyobEmployeeUid = card.Dto.Uid,
            MyobName = Truncate(card.Dto.Name, 200),
            MatchMethod = item.Manual ? MyobMatchMethods.Manual : MyobMatchMethods.Suggested,
            MatchScore = evaluation.Score,
            BoundByUserGuid = string.IsNullOrWhiteSpace(actorUserGuid) ? null : actorUserGuid,
            BoundAt = now,
        };
        try
        {
            await _db.Insertable(link).ExecuteCommandAsync();
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return Fail(MyobBindErrorCodes.CardAlreadyBound, "这张员工卡或这位员工刚刚已被绑定，请刷新后重试");
        }

        return null;
    }

    public async Task<ApiResponse<bool>> UnbindAsync(string linkGuid, string actorUserGuid, CancellationToken cancellationToken)
    {
        var link = await _db.Queryable<EmployeeMyobLink>()
            .FirstAsync(item => item.LinkGuid == linkGuid && item.UnboundAt == null);
        if (link is null)
        {
            return ApiResponse<bool>.Error("绑定不存在或已解除", MyobBindErrorCodes.LinkNotFound);
        }

        link.UnboundAt = _time.GetUtcNow().UtcDateTime;
        link.UnboundByUserGuid = string.IsNullOrWhiteSpace(actorUserGuid) ? null : actorUserGuid;
        await _db.Updateable(link)
            .UpdateColumns(item => new { item.UnboundAt, item.UnboundByUserGuid })
            .ExecuteCommandAsync();
        return ApiResponse<bool>.OK(true, "已解除绑定");
    }

    public async Task<ApiResponse<bool>> IgnoreAsync(
        MyobIgnoreEmployeeRequest request,
        string actorUserGuid,
        CancellationToken cancellationToken)
    {
        if (await FindActiveConnectionAsync(request.ConnectionGuid) is null)
        {
            return ApiResponse<bool>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        var uid = request.MyobEmployeeUid?.Trim();
        if (string.IsNullOrEmpty(uid))
        {
            return ApiResponse<bool>.Error("员工卡不能为空", MyobBindErrorCodes.CardNotFound);
        }

        var existing = await _db.Queryable<MyobEmployeeIgnore>()
            .FirstAsync(item => item.ConnectionGuid == request.ConnectionGuid && item.MyobEmployeeUid == uid);
        if (request.Ignored && existing is null)
        {
            await _db.Insertable(new MyobEmployeeIgnore
            {
                ConnectionGuid = request.ConnectionGuid,
                MyobEmployeeUid = uid,
                IgnoredByUserGuid = string.IsNullOrWhiteSpace(actorUserGuid) ? null : actorUserGuid,
                IgnoredAt = _time.GetUtcNow().UtcDateTime,
            }).ExecuteCommandAsync();
        }
        else if (!request.Ignored && existing is not null)
        {
            await _db.Deleteable<MyobEmployeeIgnore>()
                .Where(item => item.ConnectionGuid == request.ConnectionGuid && item.MyobEmployeeUid == uid)
                .ExecuteCommandAsync();
        }

        return ApiResponse<bool>.OK(true);
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private Task<MyobConnection?> FindActiveConnectionAsync(string connectionGuid) =>
        _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == connectionGuid && item.Status == MyobConnectionStatuses.Active)!;

    private async Task<IReadOnlyDictionary<string, string>> LoadNamesAsync(IReadOnlyList<EmployeeMyobLink> links)
    {
        var guids = links.Select(link => link.BoundByUserGuid).Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid!).Distinct().ToList();
        if (guids.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var users = await _db.Queryable<User>()
            .Where(user => guids.Contains(user.UserGUID))
            .Select(user => new { user.UserGUID, user.FullName, user.Username })
            .ToListAsync();
        return users.ToDictionary(
            user => user.UserGUID,
            user => string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!,
            StringComparer.OrdinalIgnoreCase);
    }

    internal static MyobMatchSubject ToSubject(WorkforceUser user) =>
        new(user.UserGuid, user.GivenName, user.FamilyName, user.FullName ?? user.Name, user.Email, user.Phone, user.Birthday);

    private static MyobMatchCandidateDto ToCandidateDto(
        MyobDirectoryCard card,
        MyobMatchEvaluation evaluation,
        string? boundToUserGuid,
        string? boundToUserName) =>
        new()
        {
            Card = card.Dto,
            Score = evaluation.Score,
            Basis = new MyobMatchBasisDto
            {
                Email = evaluation.Email,
                Birthday = evaluation.Birthday,
                Name = evaluation.Name,
                Phone = evaluation.Phone,
            },
            Notes = evaluation.Notes.ToList(),
            BoundToUserGuid = boundToUserGuid,
            BoundToUserName = boundToUserName,
        };

    private static MyobLinkDto ToLinkDto(EmployeeMyobLink link, bool cardExists, IReadOnlyDictionary<string, string> actorNames) =>
        new()
        {
            LinkGuid = link.LinkGuid,
            MyobEmployeeUid = link.MyobEmployeeUid,
            MyobName = link.MyobName,
            MatchMethod = link.MatchMethod,
            MatchScore = link.MatchScore,
            BoundByName = link.BoundByUserGuid is not null && actorNames.TryGetValue(link.BoundByUserGuid, out var name) ? name : null,
            BoundAt = link.BoundAt,
            CardMissing = !cardExists,
        };

    private static MyobOnlyInOursDto ToOnlyInOurs(WorkforceUser user, MyobBindingRowDto row)
    {
        var given = user.GivenName?.Trim();
        var family = user.FamilyName?.Trim();
        if (string.IsNullOrWhiteSpace(given) && string.IsNullOrWhiteSpace(family))
        {
            // 没有拆好的名和姓：按空格拆全名，最后一个词当姓。
            var parts = user.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            given = parts.Length > 1 ? string.Join(' ', parts[..^1]) : user.Name;
            family = parts.Length > 1 ? parts[^1] : null;
        }

        // 「复制资料」用来在 MYOB 里新建员工卡：英文字段名与 MYOB 界面一致，手机给完整号码（调用者有绑定权限）。
        var lines = new List<string> { $"First name: {given}", $"Last name: {family}" };
        if (!string.IsNullOrWhiteSpace(user.Email)) lines.Add($"Email: {user.Email}");
        if (user.Birthday.HasValue) lines.Add($"Date of birth: {user.Birthday.Value:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(user.Phone)) lines.Add($"Phone: {user.Phone}");

        return new MyobOnlyInOursDto
        {
            UserGuid = user.UserGuid,
            UserName = user.Name,
            Email = user.Email,
            EmployeeType = user.EmployeeType?.ToString(),
            StoreLabels = row.StoreLabels,
            JoinedAt = user.CreatedAt,
            CopyText = string.Join('\n', lines),
        };
    }

    private static int StatusRank(string status) => status switch
    {
        MyobBindingStatuses.Suggested => 0,
        MyobBindingStatuses.NeedsReview => 1,
        MyobBindingStatuses.Conflict => 1,
        MyobBindingStatuses.Unmatched => 2,
        _ => 3,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>SQL Server 唯一索引冲突（2601 / 2627）；其他异常原样抛出。</summary>
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var text = current.Message;
            if (text.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || text.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Violation of UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
