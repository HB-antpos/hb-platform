namespace Hbpos.Contracts.Catalog;

/// <summary>
/// 整文件下载的格式标识。文件是 gzip 压缩的 NDJSON：第一行是头，其后每行一条记录；
/// 同一版本的文件发布后不再改写，客户端以 Sha256 校验整份内容后再写入本地库。
/// </summary>
public static class CatalogFileFormats
{
    public const string FullV1 = "hbpos-catalog-full-v1";
    public const string DeltaV1 = "hbpos-catalog-delta-v1";
    public const string ContentType = "application/gzip";
}

public static class CatalogFileKinds
{
    public const string Full = "full";
    public const string Delta = "delta";
}

/// <summary>
/// 可下载的不可变目录文件。Path 是相对 API 根的路径（不带前导 /），客户端与自身 BaseAddress 拼接；
/// Bytes/Sha256 针对 gzip 压缩后的原始字节，支持 HTTP Range 断点续传。
/// </summary>
public sealed record CatalogDownloadFileDto(
    string Kind,
    string Format,
    string Path,
    long Bytes,
    string Sha256);

/// <summary>
/// 整文件模式的同步计划。Mode 沿用 <see cref="CatalogSyncModes"/>；noChange 时 File 为 null。
/// 不发放下载租约：文件按版本落盘，服务端无需把目录版本钉在内存里。
/// </summary>
public sealed record CatalogFileSyncPlanResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    string Mode,
    string? BaseCatalogVersion,
    string TargetCatalogVersion,
    int TargetTotal,
    CatalogDownloadFileDto? File,
    int? DeltaOperationCount = null);

/// <summary>全量文件第一行。其后 TotalCount 行，每行一条 <see cref="CatalogLookupItemDto"/>，按 LookupCodeNormalized 序号升序。</summary>
public sealed record CatalogFullFileHeader(
    string Format,
    string StoreCode,
    string CatalogVersion,
    DateTimeOffset GeneratedAt,
    int TotalCount);

/// <summary>增量文件第一行。其后 OperationCount 行，每行一条 <see cref="CatalogDeltaFileLine"/>，按 LookupCodeNormalized 序号升序。</summary>
public sealed record CatalogDeltaFileHeader(
    string Format,
    string StoreCode,
    string BaseCatalogVersion,
    string TargetCatalogVersion,
    DateTimeOffset GeneratedAt,
    int TargetTotal,
    int OperationCount);

/// <summary>增量文件的一行：Item 为 upsert，Deleted 为精确删除，二者恰有其一。</summary>
public sealed record CatalogDeltaFileLine(
    CatalogLookupItemDto? Item,
    DeletedLookupDto? Deleted);
