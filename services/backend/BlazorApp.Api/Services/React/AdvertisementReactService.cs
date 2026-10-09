using System.Globalization;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class AdvertisementReactService : IAdvertisementReactService
    {
        private static readonly HashSet<string> AllowedContentTypes = new(
            new[]
            {
                "image/jpeg",
                "image/png",
                "image/webp",
                "image/gif",
                "video/mp4",
                "video/webm",
                "video/quicktime",
            },
            StringComparer.OrdinalIgnoreCase
        );

        private const long MaxImageFileSize = 20L * 1024 * 1024;
        private const long MaxVideoFileSize = 200L * 1024 * 1024;
        // 素材像素宽高的合法范围（Web 上传时读取，两者必须同时提供）。
        private const int MinMediaDimension = 1;
        private const int MaxMediaDimension = 20000;
        private static readonly Regex AdvertisementObjectKeyPattern = new(
            @"^ads/\d{4}/(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.(jpg|png|webp|gif|mp4|webm|mov)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        private static readonly Dictionary<string, string> ContentTypeExtensions = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
            ["image/gif"] = ".gif",
            ["video/mp4"] = ".mp4",
            ["video/webm"] = ".webm",
            ["video/quicktime"] = ".mov",
        };

        private readonly SqlSugarContext _context;
        private readonly TencentCloudUploadService _uploadService;
        private readonly TencentCloudSettings _tencentSettings;

        public AdvertisementReactService(
            SqlSugarContext context,
            TencentCloudUploadService uploadService,
            IOptions<TencentCloudSettings> tencentSettings
        )
        {
            _context = context;
            _uploadService = uploadService;
            _tencentSettings = tencentSettings.Value;
        }

        public async Task<GridResponseDto<AdvertisementListDto>> GetGridAsync(
            AdvertisementGridRequestDto request
        )
        {
            var query = _context
                .AdvertisementDb.AsQueryable()
                .Where(item => !item.IsDeleted);

            var globalSearch = !string.IsNullOrWhiteSpace(request.GlobalSearch)
                ? request.GlobalSearch
                : request.Title;
            if (!string.IsNullOrWhiteSpace(globalSearch))
            {
                var keyword = globalSearch.Trim();
                query = query.Where(item =>
                    item.Title.Contains(keyword)
                    || (item.Description != null && item.Description.Contains(keyword))
                    || item.OriginalFileName.Contains(keyword)
                    || item.ObjectKey.Contains(keyword)
                );
            }

            ApplySimpleFilters(ref query, request);
            ApplyFilterModel(ref query, request.FilterModel);
            ApplySorting(ref query, request.SortModel);

            var total = await query.CountAsync();
            var take = request.PageSize > 0 ? request.PageSize : Math.Max(20, request.EndRow - request.StartRow);
            var pageNumber = request.PageNumber.GetValueOrDefault();
            var startRow = pageNumber > 1
                ? (pageNumber - 1) * take
                : request.StartRow;
            var items = await query.Skip(Math.Max(0, startRow)).Take(take).ToListAsync();
            var storesByAdvertisementId = await GetStoresByAdvertisementIdsAsync(
                items.Select(item => item.Id).ToList()
            );

            return GridResponseDto<AdvertisementListDto>.OK(
                items.Select(item => MapToListDto(item, storesByAdvertisementId)).ToList(),
                total
            );
        }

        public async Task<ApiResponse<AdvertisementDetailDto>> GetByIdAsync(string id)
        {
            var entity = await _context
                .AdvertisementDb.AsQueryable()
                .FirstAsync(item => item.Id == id && !item.IsDeleted);
            if (entity == null)
            {
                return ApiResponse<AdvertisementDetailDto>.Error("广告不存在", "NOT_FOUND");
            }

            var storesByAdvertisementId = await GetStoresByAdvertisementIdsAsync(new List<string> { id });
            return ApiResponse<AdvertisementDetailDto>.OK(MapToDetailDto(entity, storesByAdvertisementId));
        }

        public async Task<ApiResponse<AdvertisementDetailDto>> CreateAsync(CreateAdvertisementDto dto)
        {
            var validationError = await ValidatePayloadAsync(dto);
            if (validationError != null)
            {
                return validationError;
            }

            var entity = new Advertisement
            {
                Id = UuidHelper.GenerateUuid7(),
                Title = dto.Title.Trim(),
                Description = dto.Description?.Trim(),
                MediaType = NormalizeMediaType(dto.MediaType),
                MediaUrl = BuildMediaUrl(dto.ObjectKey.Trim()),
                ThumbnailUrl = dto.ThumbnailUrl?.Trim(),
                ObjectKey = dto.ObjectKey.Trim(),
                OriginalFileName = dto.OriginalFileName.Trim(),
                ContentType = dto.ContentType.Trim(),
                FileSize = dto.FileSize,
                EffectiveStart = dto.EffectiveStart,
                EffectiveEnd = dto.EffectiveEnd,
                IsEnabled = dto.IsEnabled,
                SortOrder = dto.SortOrder,
                // 已通过校验：空值规范成 Any，其余规范成 PascalCase。
                Orientation = AdvertisementOrientations.Normalize(dto.Orientation)!,
                MediaWidth = dto.MediaWidth,
                MediaHeight = dto.MediaHeight,
            };

            var stores = BuildStoreRelations(entity.Id, dto.Stores);
            var result = await _context.Db.Ado.UseTranAsync(async () =>
            {
                await _context.AdvertisementDb.InsertAsync(entity);
                if (stores.Count > 0)
                {
                    await _context.AdvertisementStoreDb.InsertRangeAsync(stores);
                }
            });

            if (!result.IsSuccess)
            {
                return ApiResponse<AdvertisementDetailDto>.Error(
                    "创建广告失败",
                    "CREATE_FAILED",
                    result.ErrorException?.Message
                );
            }

            return await GetByIdAsync(entity.Id);
        }

        public async Task<ApiResponse<AdvertisementDetailDto>> UpdateAsync(
            string id,
            UpdateAdvertisementDto dto
        )
        {
            var validationError = await ValidatePayloadAsync(dto);
            if (validationError != null)
            {
                return validationError;
            }

            var entity = await _context
                .AdvertisementDb.AsQueryable()
                .FirstAsync(item => item.Id == id && !item.IsDeleted);
            if (entity == null)
            {
                return ApiResponse<AdvertisementDetailDto>.Error("广告不存在", "NOT_FOUND");
            }

            entity.Title = dto.Title.Trim();
            entity.Description = dto.Description?.Trim();
            entity.MediaType = NormalizeMediaType(dto.MediaType);
            entity.MediaUrl = BuildMediaUrl(dto.ObjectKey.Trim());
            entity.ThumbnailUrl = dto.ThumbnailUrl?.Trim();
            entity.ObjectKey = dto.ObjectKey.Trim();
            entity.OriginalFileName = dto.OriginalFileName.Trim();
            entity.ContentType = dto.ContentType.Trim();
            entity.FileSize = dto.FileSize;
            entity.EffectiveStart = dto.EffectiveStart;
            entity.EffectiveEnd = dto.EffectiveEnd;
            entity.IsEnabled = dto.IsEnabled;
            entity.SortOrder = dto.SortOrder;
            entity.Orientation = AdvertisementOrientations.Normalize(dto.Orientation)!;
            entity.MediaWidth = dto.MediaWidth;
            entity.MediaHeight = dto.MediaHeight;

            var stores = BuildStoreRelations(id, dto.Stores);
            var result = await _context.Db.Ado.UseTranAsync(async () =>
            {
                await _context.AdvertisementDb.UpdateAsync(entity);
                await _context.AdvertisementStoreDb.DeleteAsync(item => item.AdvertisementId == id);
                if (stores.Count > 0)
                {
                    await _context.AdvertisementStoreDb.InsertRangeAsync(stores);
                }
            });

            if (!result.IsSuccess)
            {
                return ApiResponse<AdvertisementDetailDto>.Error(
                    "更新广告失败",
                    "UPDATE_FAILED",
                    result.ErrorException?.Message
                );
            }

            return await GetByIdAsync(id);
        }

        public async Task<ApiResponse<bool>> DeleteAsync(string id)
        {
            var result = await _context.Db.Ado.UseTranAsync(async () =>
            {
                await _context.AdvertisementStoreDb.DeleteAsync(item => item.AdvertisementId == id);
                await _context.AdvertisementDb.DeleteAsync(item => item.Id == id);
            });

            if (!result.IsSuccess)
            {
                return ApiResponse<bool>.Error("删除广告失败", "DELETE_FAILED", result.ErrorException?.Message);
            }

            return ApiResponse<bool>.OK(true, "删除广告成功");
        }

        public async Task<ApiResponse<bool>> EnableAsync(string id, bool isEnabled)
        {
            var entity = await _context
                .AdvertisementDb.AsQueryable()
                .FirstAsync(item => item.Id == id && !item.IsDeleted);
            if (entity == null)
            {
                return ApiResponse<bool>.Error("广告不存在", "NOT_FOUND");
            }

            entity.IsEnabled = isEnabled;
            var ok = await _context.AdvertisementDb.UpdateAsync(entity);
            return ok
                ? ApiResponse<bool>.OK(true, "状态更新成功")
                : ApiResponse<bool>.Error("状态更新失败", "UPDATE_FAILED");
        }

        public Task<ApiResponse<AdvertisementUploadSignatureResponseDto>> GetUploadSignatureAsync(
            AdvertisementUploadSignatureRequestDto request
        )
        {
            if (request == null || string.IsNullOrWhiteSpace(request.FileName))
            {
                return Task.FromResult(
                    ApiResponse<AdvertisementUploadSignatureResponseDto>.Error(
                        "文件名不能为空",
                        "INVALID_REQUEST"
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(request.ContentType))
            {
                return Task.FromResult(
                    ApiResponse<AdvertisementUploadSignatureResponseDto>.Error(
                        "ContentType 不能为空",
                        "INVALID_REQUEST"
                    )
                );
            }

            if (!AllowedContentTypes.Contains(request.ContentType))
            {
                return Task.FromResult(
                    ApiResponse<AdvertisementUploadSignatureResponseDto>.Error(
                        "仅支持 jpg/png/webp/gif/mp4/webm/mov 文件",
                        "INVALID_CONTENT_TYPE"
                    )
                );
            }

            var fileSizeError = ValidateFileSize(request.ContentType, request.FileSize);
            if (fileSizeError != null)
            {
                return Task.FromResult(
                    ApiResponse<AdvertisementUploadSignatureResponseDto>.Error(
                        fileSizeError,
                        "INVALID_FILE_SIZE"
                    )
                );
            }

            if (
                string.IsNullOrWhiteSpace(_tencentSettings.SecretId)
                || string.IsNullOrWhiteSpace(_tencentSettings.SecretKey)
                || string.IsNullOrWhiteSpace(_tencentSettings.BucketName)
                || string.IsNullOrWhiteSpace(_tencentSettings.Region)
            )
            {
                return Task.FromResult(
                    ApiResponse<AdvertisementUploadSignatureResponseDto>.Error(
                        "腾讯云主桶配置不完整",
                        "COS_NOT_CONFIGURED"
                    )
                );
            }

            var extension = ResolveExtension(request.FileName, request.ContentType);
            var objectKey = $"ads/{DateTime.UtcNow:yyyy}/{UuidHelper.GenerateUuid7()}{extension}";
            var signature = _uploadService.GetDirectUploadSignature(objectKey, request.ContentType);
            var mediaUrl =
                $"https://{_tencentSettings.BucketName}.cos.{_tencentSettings.Region}.myqcloud.com/{objectKey}";

            return Task.FromResult(
                ApiResponse<AdvertisementUploadSignatureResponseDto>.OK(
                    new AdvertisementUploadSignatureResponseDto
                    {
                        ObjectKey = objectKey,
                        Url = signature.Url,
                        UploadUrl = signature.Url,
                        MediaUrl = mediaUrl,
                        Headers = signature.Headers,
                    },
                    "签名生成成功"
                )
            );
        }

        private static string ResolveExtension(string fileName, string contentType)
        {
            var extension = Path.GetExtension(fileName)?.Trim();
            var normalizedExtension = string.IsNullOrWhiteSpace(extension)
                ? null
                : (extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}");

            if (
                !string.IsNullOrWhiteSpace(normalizedExtension)
                && string.Equals(normalizedExtension, ContentTypeExtensions[contentType], StringComparison.OrdinalIgnoreCase)
            )
            {
                return normalizedExtension;
            }

            return ContentTypeExtensions[contentType];
        }

        private static string? ValidateFileSize(string contentType, long fileSize)
        {
            if (fileSize <= 0)
            {
                return "文件大小无效";
            }

            var maxSize = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? MaxVideoFileSize
                : MaxImageFileSize;

            return fileSize > maxSize
                ? $"文件大小超过限制，图片最大 {MaxImageFileSize / 1024 / 1024}MB，视频最大 {MaxVideoFileSize / 1024 / 1024}MB"
                : null;
        }

        private static string NormalizeMediaType(string mediaType)
        {
            return string.Equals(mediaType?.Trim(), "video", StringComparison.OrdinalIgnoreCase)
                ? "Video"
                : "Image";
        }

        private async Task<ApiResponse<AdvertisementDetailDto>?> ValidatePayloadAsync(CreateAdvertisementDto dto)
        {
            if (dto == null)
            {
                return ApiResponse<AdvertisementDetailDto>.Error("请求不能为空", "INVALID_REQUEST");
            }

            if (string.IsNullOrWhiteSpace(dto.Title))
            {
                return ApiResponse<AdvertisementDetailDto>.Error("标题不能为空", "INVALID_REQUEST");
            }

            var storeCodes = NormalizeStoreCodes(dto.Stores);

            var activeStoreCodes = await _context
                .StoreDb.AsQueryable()
                .Where(store => store.IsActive && !store.IsDeleted)
                .Select(store => store.StoreCode)
                .ToListAsync();
            var activeStoreCodeSet = new HashSet<string>(
                activeStoreCodes.Where(code => !string.IsNullOrWhiteSpace(code)),
                StringComparer.OrdinalIgnoreCase
            );
            var invalidStoreCodes = storeCodes
                .Where(storeCode => !activeStoreCodeSet.Contains(storeCode))
                .ToList();
            if (invalidStoreCodes.Count > 0)
            {
                return ApiResponse<AdvertisementDetailDto>.Error(
                    $"分店不存在或未启用: {string.Join(", ", invalidStoreCodes)}",
                    "INVALID_STORE_SCOPE"
                );
            }

            if (string.IsNullOrWhiteSpace(dto.OriginalFileName))
            {
                return ApiResponse<AdvertisementDetailDto>.Error("原始文件名不能为空", "INVALID_REQUEST");
            }

            if (string.IsNullOrWhiteSpace(dto.ContentType))
            {
                return ApiResponse<AdvertisementDetailDto>.Error("ContentType 不能为空", "INVALID_REQUEST");
            }

            if (!HasTencentMainBucketSettings())
            {
                return ApiResponse<AdvertisementDetailDto>.Error("腾讯云主桶配置不完整", "COS_NOT_CONFIGURED");
            }

            var objectKey = dto.ObjectKey?.Trim() ?? string.Empty;
            if (!AdvertisementObjectKeyPattern.IsMatch(objectKey))
            {
                return ApiResponse<AdvertisementDetailDto>.Error(
                    "ObjectKey 必须使用 ads/{yyyy}/{uuid}{ext} 格式",
                    "INVALID_OBJECT_KEY"
                );
            }

            if (!AllowedContentTypes.Contains(dto.ContentType))
            {
                return ApiResponse<AdvertisementDetailDto>.Error("文件类型不支持", "INVALID_CONTENT_TYPE");
            }

            var expectedExtension = ContentTypeExtensions[dto.ContentType];
            if (!string.Equals(Path.GetExtension(objectKey), expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse<AdvertisementDetailDto>.Error(
                    "ObjectKey 后缀与 ContentType 不匹配",
                    "INVALID_OBJECT_KEY"
                );
            }

            var fileSizeError = ValidateFileSize(dto.ContentType, dto.FileSize);
            if (fileSizeError != null)
            {
                return ApiResponse<AdvertisementDetailDto>.Error(fileSizeError, "INVALID_FILE_SIZE");
            }

            if (dto.EffectiveEnd < dto.EffectiveStart)
            {
                return ApiResponse<AdvertisementDetailDto>.Error("结束时间不能早于开始时间", "INVALID_DATE_RANGE");
            }

            var mediaType = NormalizeMediaType(dto.MediaType);
            if (
                (mediaType == "Image" && !dto.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                || (mediaType == "Video" && !dto.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            )
            {
                return ApiResponse<AdvertisementDetailDto>.Error(
                    "MediaType 与 ContentType 不匹配",
                    "INVALID_MEDIA_TYPE"
                );
            }

            // 版式只允许 Landscape / Portrait / Any（大小写不敏感），空值视为 Any。
            if (AdvertisementOrientations.Normalize(dto.Orientation) == null)
            {
                return ApiResponse<AdvertisementDetailDto>.Error("广告版式无效", "INVALID_ORIENTATION");
            }

            var mediaSizeError = ValidateMediaSize(dto.MediaWidth, dto.MediaHeight);
            if (mediaSizeError != null)
            {
                return ApiResponse<AdvertisementDetailDto>.Error(mediaSizeError, "INVALID_MEDIA_SIZE");
            }

            return null;
        }

        private static string? ValidateMediaSize(int? width, int? height)
        {
            // 宽高要么都不传（历史素材或读取失败），要么都传；只给一个无法判断横竖，视为错误。
            if (!width.HasValue && !height.HasValue)
            {
                return null;
            }

            if (!width.HasValue || !height.HasValue)
            {
                return "素材宽高必须同时提供";
            }

            return width.Value is < MinMediaDimension or > MaxMediaDimension
                || height.Value is < MinMediaDimension or > MaxMediaDimension
                ? $"素材宽高必须是 {MinMediaDimension}–{MaxMediaDimension} 的正整数"
                : null;
        }

        private bool HasTencentMainBucketSettings()
        {
            return !string.IsNullOrWhiteSpace(_tencentSettings.SecretId)
                && !string.IsNullOrWhiteSpace(_tencentSettings.SecretKey)
                && !string.IsNullOrWhiteSpace(_tencentSettings.BucketName)
                && !string.IsNullOrWhiteSpace(_tencentSettings.Region);
        }

        private string BuildMediaUrl(string objectKey)
        {
            return $"https://{_tencentSettings.BucketName}.cos.{_tencentSettings.Region}.myqcloud.com/{objectKey}";
        }

        private static void ApplySimpleFilters(
            ref ISugarQueryable<Advertisement> query,
            AdvertisementGridRequestDto request
        )
        {
            if (!string.IsNullOrWhiteSpace(request.StoreCode))
            {
                var storeCode = request.StoreCode.Trim();
                ApplyStoreScopeFilter(ref query, new[] { storeCode });
            }

            if (!string.IsNullOrWhiteSpace(request.MediaType))
            {
                var mediaType = NormalizeMediaType(request.MediaType);
                query = query.Where(item => item.MediaType == mediaType);
            }

            if (request.IsEnabled.HasValue)
            {
                query = query.Where(item => item.IsEnabled == request.IsEnabled.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Orientation))
            {
                ApplyOrientationFilter(ref query, new[] { request.Orientation });
            }

            if (request.EffectiveStart.HasValue)
            {
                var start = request.EffectiveStart.Value.Date;
                query = query.Where(item => item.EffectiveStart >= start);
            }

            if (request.EffectiveEnd.HasValue)
            {
                var endExclusive = request.EffectiveEnd.Value.Date.AddDays(1);
                query = query.Where(item => item.EffectiveEnd < endExclusive);
            }
        }

        private static void ApplyFilterModel(
            ref ISugarQueryable<Advertisement> query,
            Dictionary<string, FilterModelDto>? filterModel
        )
        {
            if (filterModel == null || filterModel.Count == 0)
            {
                return;
            }

            foreach (var (field, filter) in filterModel)
            {
                if (filter == null)
                {
                    continue;
                }

                switch (field)
                {
                    case "storeCode":
                        ApplyStoreCodeFilter(ref query, filter);
                        break;
                    case "mediaType":
                        ApplyMediaTypeFilter(ref query, filter);
                        break;
                    case "isEnabled":
                        ApplyBoolFilter(ref query, filter);
                        break;
                    case "orientation":
                        ApplyOrientationFilter(ref query, ExtractValues(filter));
                        break;
                    case "effectiveStart":
                        ApplyDateFilter(ref query, filter, true);
                        break;
                    case "effectiveEnd":
                        ApplyDateFilter(ref query, filter, false);
                        break;
                }
            }
        }

        private static void ApplyStoreCodeFilter(
            ref ISugarQueryable<Advertisement> query,
            FilterModelDto filter
        )
        {
            var values = ExtractValues(filter);
            if (values.Count == 0)
            {
                return;
            }

            ApplyStoreScopeFilter(ref query, values);
        }

        private static void ApplyStoreScopeFilter(
            ref ISugarQueryable<Advertisement> query,
            IReadOnlyCollection<string> storeCodes
        )
        {
            query = query.Where(item =>
                !SqlFunc
                    .Subqueryable<AdvertisementStore>()
                    .Where(store =>
                        store.AdvertisementId == item.Id
                    )
                    .Any()
                ||
                SqlFunc
                    .Subqueryable<AdvertisementStore>()
                    .Where(store =>
                        store.AdvertisementId == item.Id
                        && !store.IsDeleted
                        && storeCodes.Contains(store.StoreCode)
                    )
                    .Any()
            );
        }

        private static void ApplyMediaTypeFilter(
            ref ISugarQueryable<Advertisement> query,
            FilterModelDto filter
        )
        {
            var values = ExtractValues(filter)
                .Select(NormalizeMediaType)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count == 0)
            {
                return;
            }

            if (values.Count > 1)
            {
                query = query.Where(item => values.Contains(item.MediaType));
                return;
            }

            var value = values[0];
            switch (filter.Type?.Trim().ToLowerInvariant())
            {
                case "equals":
                    query = query.Where(item => item.MediaType == value);
                    return;
                default:
                    query = query.Where(item => item.MediaType.Contains(value));
                    return;
            }
        }

        private static void ApplyOrientationFilter(
            ref ISugarQueryable<Advertisement> query,
            IEnumerable<string> rawValues
        )
        {
            // 大小写不敏感精确匹配：先规范成库里存的 PascalCase；无法识别的取值原样保留，查询结果为空而不是放宽过滤。
            var values = rawValues
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => AdvertisementOrientations.Normalize(value) ?? value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (values.Count == 0)
            {
                return;
            }

            if (values.Count == 1)
            {
                var value = values[0];
                query = query.Where(item => item.Orientation == value);
                return;
            }

            query = query.Where(item => values.Contains(item.Orientation));
        }

        private static void ApplyBoolFilter(
            ref ISugarQueryable<Advertisement> query,
            FilterModelDto filter
        )
        {
            var raw = filter.Filter ?? filter.Values?.FirstOrDefault();
            if (!bool.TryParse(raw, out var enabled))
            {
                return;
            }

            query = query.Where(item => item.IsEnabled == enabled);
        }

        private static void ApplyDateFilter(
            ref ISugarQueryable<Advertisement> query,
            FilterModelDto filter,
            bool useEffectiveStart
        )
        {
            if (!TryParseDate(filter.Filter, out var fromDate))
            {
                return;
            }

            var type = filter.Type?.Trim().ToLowerInvariant();
            switch (type)
            {
                case "equals":
                    ApplyDateEquals(ref query, useEffectiveStart, fromDate);
                    break;
                case "lessthan":
                    query = useEffectiveStart
                        ? query.Where(item => item.EffectiveStart < fromDate)
                        : query.Where(item => item.EffectiveEnd < fromDate);
                    break;
                case "greaterthan":
                    query = useEffectiveStart
                        ? query.Where(item => item.EffectiveStart >= fromDate)
                        : query.Where(item => item.EffectiveEnd >= fromDate);
                    break;
                case "inrange":
                    if (!TryParseDate(filter.FilterTo, out var toDate))
                    {
                        return;
                    }

                    ApplyDateRange(ref query, useEffectiveStart, fromDate, toDate.AddDays(1));
                    break;
                default:
                    ApplyDateEquals(ref query, useEffectiveStart, fromDate);
                    break;
            }
        }

        private static void ApplyDateEquals(
            ref ISugarQueryable<Advertisement> query,
            bool useEffectiveStart,
            DateTime date
        )
        {
            var endExclusive = date.AddDays(1);
            query = useEffectiveStart
                ? query.Where(item => item.EffectiveStart >= date && item.EffectiveStart < endExclusive)
                : query.Where(item => item.EffectiveEnd >= date && item.EffectiveEnd < endExclusive);
        }

        private static void ApplyDateRange(
            ref ISugarQueryable<Advertisement> query,
            bool useEffectiveStart,
            DateTime fromDate,
            DateTime toDateExclusive
        )
        {
            query = useEffectiveStart
                ? query.Where(item => item.EffectiveStart >= fromDate && item.EffectiveStart < toDateExclusive)
                : query.Where(item => item.EffectiveEnd >= fromDate && item.EffectiveEnd < toDateExclusive);
        }

        private static bool TryParseDate(string? raw, out DateTime value)
        {
            return DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out value
            );
        }

        private static List<string> ExtractValues(FilterModelDto filter)
        {
            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(filter.Filter))
            {
                values.Add(filter.Filter.Trim());
            }

            if (filter.Values != null)
            {
                values.AddRange(
                    filter.Values
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim())
                );
            }

            return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ApplySorting(
            ref ISugarQueryable<Advertisement> query,
            List<SortModelDto>? sortModel
        )
        {
            var sortableColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = nameof(Advertisement.Title),
                ["mediaType"] = nameof(Advertisement.MediaType),
                ["effectiveStart"] = nameof(Advertisement.EffectiveStart),
                ["effectiveEnd"] = nameof(Advertisement.EffectiveEnd),
                ["isEnabled"] = nameof(Advertisement.IsEnabled),
                ["sortOrder"] = nameof(Advertisement.SortOrder),
                ["orientation"] = nameof(Advertisement.Orientation),
                ["createdAt"] = nameof(Advertisement.CreatedAt),
                ["updatedAt"] = nameof(Advertisement.UpdatedAt),
            };

            if (sortModel == null || sortModel.Count == 0)
            {
                query = query.OrderBy($"{nameof(Advertisement.SortOrder)} asc,{nameof(Advertisement.CreatedAt)} desc");
                return;
            }

            var clauses = new List<string>();
            foreach (var sort in sortModel)
            {
                if (!sortableColumns.TryGetValue(sort.ColId, out var column))
                {
                    continue;
                }

                clauses.Add($"{column} {(string.Equals(sort.Sort, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc")}");
            }

            if (clauses.Count == 0)
            {
                clauses.Add($"{nameof(Advertisement.SortOrder)} asc");
                clauses.Add($"{nameof(Advertisement.CreatedAt)} desc");
            }

            query = query.OrderBy(string.Join(",", clauses));
        }

        private async Task<Dictionary<string, List<AdvertisementStoreItemDto>>> GetStoresByAdvertisementIdsAsync(
            List<string> advertisementIds
        )
        {
            if (advertisementIds.Count == 0)
            {
                return new Dictionary<string, List<AdvertisementStoreItemDto>>();
            }

            var stores = await _context
                .AdvertisementStoreDb.AsQueryable()
                .Where(item => advertisementIds.Contains(item.AdvertisementId) && !item.IsDeleted)
                .OrderBy(item => item.StoreCode)
                .ToListAsync();

            var storeCodeKeys = stores
                .Select(item => item.StoreCode?.Trim())
                .Where(storeCode => !string.IsNullOrWhiteSpace(storeCode))
                .Select(storeCode => storeCode!.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var storeNamesByCode = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (storeCodeKeys.Count > 0)
            {
                // 一次性回填未软删除分店名称；历史停用分店仍应在广告范围中可读。
                var storeNames = await _context
                    .StoreDb.AsQueryable()
                    .Where(store =>
                        !store.IsDeleted
                        && storeCodeKeys.Contains(SqlFunc.ToUpper(store.StoreCode))
                    )
                    .Select(store => new { store.StoreCode, store.StoreName })
                    .ToListAsync();

                foreach (var store in storeNames)
                {
                    storeNamesByCode.TryAdd(store.StoreCode, store.StoreName);
                }
            }

            return stores
                .GroupBy(item => item.AdvertisementId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => new AdvertisementStoreItemDto
                    {
                        StoreCode = item.StoreCode,
                        StoreName = storeNamesByCode.TryGetValue(item.StoreCode, out var storeName)
                            ? storeName
                            : null,
                    }).ToList()
                );
        }

        private static AdvertisementListDto MapToListDto(
            Advertisement entity,
            IReadOnlyDictionary<string, List<AdvertisementStoreItemDto>> storesByAdvertisementId
        )
        {
            return new AdvertisementListDto
            {
                Id = entity.Id,
                Title = entity.Title,
                Description = entity.Description,
                MediaType = entity.MediaType,
                MediaUrl = entity.MediaUrl,
                ThumbnailUrl = entity.ThumbnailUrl,
                ObjectKey = entity.ObjectKey,
                OriginalFileName = entity.OriginalFileName,
                ContentType = entity.ContentType,
                FileSize = entity.FileSize,
                EffectiveStart = entity.EffectiveStart,
                EffectiveEnd = entity.EffectiveEnd,
                IsEnabled = entity.IsEnabled,
                SortOrder = entity.SortOrder,
                // 库里有 CHECK 约束兜底；这里仍按 Any 兜底，避免异常数据让列表整页失败。
                Orientation = AdvertisementOrientations.Normalize(entity.Orientation)
                    ?? AdvertisementOrientations.Any,
                MediaWidth = entity.MediaWidth,
                MediaHeight = entity.MediaHeight,
                CreatedAt = entity.CreatedAt,
                CreatedBy = entity.CreatedBy,
                UpdatedAt = entity.UpdatedAt,
                UpdatedBy = entity.UpdatedBy,
                Stores = storesByAdvertisementId.TryGetValue(entity.Id, out var stores)
                    ? stores
                    : new List<AdvertisementStoreItemDto>(),
            };
        }

        private static AdvertisementDetailDto MapToDetailDto(
            Advertisement entity,
            IReadOnlyDictionary<string, List<AdvertisementStoreItemDto>> storesByAdvertisementId
        )
        {
            var listDto = MapToListDto(entity, storesByAdvertisementId);
            return new AdvertisementDetailDto
            {
                Id = listDto.Id,
                Title = listDto.Title,
                Description = listDto.Description,
                MediaType = listDto.MediaType,
                MediaUrl = listDto.MediaUrl,
                ThumbnailUrl = listDto.ThumbnailUrl,
                ObjectKey = listDto.ObjectKey,
                OriginalFileName = listDto.OriginalFileName,
                ContentType = listDto.ContentType,
                FileSize = listDto.FileSize,
                EffectiveStart = listDto.EffectiveStart,
                EffectiveEnd = listDto.EffectiveEnd,
                IsEnabled = listDto.IsEnabled,
                SortOrder = listDto.SortOrder,
                Orientation = listDto.Orientation,
                MediaWidth = listDto.MediaWidth,
                MediaHeight = listDto.MediaHeight,
                CreatedAt = listDto.CreatedAt,
                CreatedBy = listDto.CreatedBy,
                UpdatedAt = listDto.UpdatedAt,
                UpdatedBy = listDto.UpdatedBy,
                Stores = listDto.Stores,
            };
        }

        private static List<AdvertisementStore> BuildStoreRelations(
            string advertisementId,
            IEnumerable<AdvertisementStoreItemDto>? stores
        )
        {
            return NormalizeStoreCodes(stores)
                .Select(storeCode => new AdvertisementStore
                {
                    Id = UuidHelper.GenerateUuid7(),
                    AdvertisementId = advertisementId,
                    StoreCode = storeCode,
                })
                .ToList();
        }

        private static List<string> NormalizeStoreCodes(IEnumerable<AdvertisementStoreItemDto>? stores)
        {
            return (stores ?? Array.Empty<AdvertisementStoreItemDto>())
                .Where(item => !string.IsNullOrWhiteSpace(item.StoreCode))
                .Select(item => item.StoreCode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
