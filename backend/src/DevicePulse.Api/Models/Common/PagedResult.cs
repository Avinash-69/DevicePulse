namespace DevicePulse.Api.Models.Common;

/// <summary>
/// One pagination shape, used by every list endpoint (Appendix D.1). An inconsistent
/// pagination contract across endpoints is a classic "nobody reviewed this" signal, and it
/// forces the Angular client to special-case each call.
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public static PagedResult<T> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}

/// <summary>
/// Shared paging input. Clamped rather than rejected: a client asking for pageSize=100000
/// gets the maximum, not a 400 — but it never gets to ask the database for everything.
/// </summary>
public record PagedQuery
{
    private const int MaxPageSize = 200;
    private int _page = 1;
    private int _pageSize = 20;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value switch
        {
            < 1 => 20,
            > MaxPageSize => MaxPageSize,
            _ => value
        };
    }

    public int Skip => (Page - 1) * PageSize;
}
