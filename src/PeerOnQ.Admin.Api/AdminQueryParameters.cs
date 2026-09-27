using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Admin.Api;

public sealed class AdminQueryParameters
{
    public int Offset { get; set; }
    public int Limit { get; set; } = 50;
    public string? Search { get; set; }
    public DateTimeOffset? FromUtc { get; set; }
    public DateTimeOffset? ToUtc { get; set; }
    public string? SortBy { get; set; }
    public bool Descending { get; set; } = true;

    public AdminQueryV1 ToContract(params string[] allowedSorts)
    {
        if (Offset < 0 || Limit is < 1 or > 200) throw new ArgumentException("Pagination is outside the allowed range.");
        if (Search?.Length > 128) throw new ArgumentException("Search text is too long.");
        if (FromUtc is not null && ToUtc is not null
            && (FromUtc > ToUtc || ToUtc - FromUtc > TimeSpan.FromDays(366)))
            throw new ArgumentException("The date range is invalid or exceeds 366 days.");
        var sort = string.IsNullOrWhiteSpace(SortBy) ? null : SortBy.Trim();
        if (sort is not null && !allowedSorts.Contains(sort, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The requested sort field is not supported.");
        return new AdminQueryV1(Offset, Limit, Search?.Trim(), FromUtc, ToUtc, sort, Descending);
    }
}
