namespace NewStore.Catalog;

/// <summary>One page of a larger result: <paramref name="Total"/> counts all matches, not just this page.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>The paging query string shared by the list endpoints: <c>page</c> from 1 and <c>pageSize</c> 1..100.</summary>
public static class Paging
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 100;

    /// <summary>Returns null when the values are fine, otherwise the errors for a 400 validation problem.</summary>
    public static Dictionary<string, string[]>? Validate(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be 1 or more."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        return errors.Count == 0 ? null : errors;
    }
}
