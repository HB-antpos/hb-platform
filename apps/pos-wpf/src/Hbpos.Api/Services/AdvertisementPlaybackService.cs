using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Contracts.Advertisements;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface IAdvertisementPlaybackService
{
    Task<AdvertisementPlaybackResponse> GetActiveAsync(
        string storeCode,
        int take,
        CancellationToken cancellationToken);
}

public sealed class AdvertisementPlaybackService(HbposSqlSugarContext dbContext)
    : IAdvertisementPlaybackService
{
    private const int DefaultTake = 20;
    private const int MaxTake = 50;

    public async Task<AdvertisementPlaybackResponse> GetActiveAsync(
        string storeCode,
        int take,
        CancellationToken cancellationToken)
    {
        var normalizedStoreCode = NormalizeStoreCode(storeCode);
        var generatedAt = DateTimeOffset.UtcNow;
        var normalizedTake = NormalizeTake(take);

        // 直接查询管理主库，只返回当前门店可播放且仍在有效期内的广告。
        var advertisements = await dbContext.MainDb.Queryable<Advertisement>()
            .Where(ad => !ad.IsDeleted
                && ad.IsEnabled
                && ad.EffectiveStart <= generatedAt.UtcDateTime
                && ad.EffectiveEnd >= generatedAt.UtcDateTime
                && (!SqlFunc.Subqueryable<AdvertisementStore>()
                    .Where(store => store.AdvertisementId == ad.Id)
                    .Any()
                    || SqlFunc.Subqueryable<AdvertisementStore>()
                    .Where(store => !store.IsDeleted
                        && store.AdvertisementId == ad.Id
                        && store.StoreCode == normalizedStoreCode)
                    .Any()))
            .OrderBy(ad => ad.SortOrder, OrderByType.Asc)
            .OrderBy(ad => ad.CreatedAt, OrderByType.Desc)
            .Take(normalizedTake)
            .ToListAsync(cancellationToken);

        return new AdvertisementPlaybackResponse(
            normalizedStoreCode,
            generatedAt,
            advertisements.Select(MapToDto).ToArray());
    }

    private static AdvertisementPlaybackItemDto MapToDto(Advertisement advertisement)
    {
        return new AdvertisementPlaybackItemDto(
            advertisement.Id,
            advertisement.Title,
            advertisement.Description,
            NormalizeMediaType(advertisement.MediaType),
            advertisement.MediaUrl,
            advertisement.ThumbnailUrl,
            advertisement.ObjectKey,
            advertisement.OriginalFileName,
            advertisement.ContentType,
            advertisement.FileSize,
            ToOffset(advertisement.EffectiveStart),
            ToOffset(advertisement.EffectiveEnd),
            advertisement.SortOrder,
            NormalizeOrientation(advertisement.Orientation),
            advertisement.MediaWidth,
            advertisement.MediaHeight);
    }

    private static string NormalizeOrientation(string? value)
    {
        // 客显协议用小写；库里存 PascalCase，空值或未知值一律按 any（两处都播），与加版式前的行为一致。
        return AdvertisementOrientations.Normalize(value) switch
        {
            AdvertisementOrientations.Landscape => "landscape",
            AdvertisementOrientations.Portrait => "portrait",
            _ => "any",
        };
    }

    private static string NormalizeMediaType(string value)
    {
        return string.Equals(value.Trim(), "video", StringComparison.OrdinalIgnoreCase)
            ? "video"
            : "image";
    }

    private static DateTimeOffset ToOffset(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static int NormalizeTake(int take)
    {
        // 客显轮播默认取 20 条，并限制上限，避免门店一次拉取过多素材。
        return take <= 0 ? DefaultTake : Math.Clamp(take, 1, MaxTake);
    }

    private static string NormalizeStoreCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}
