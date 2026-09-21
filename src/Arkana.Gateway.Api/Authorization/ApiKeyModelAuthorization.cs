using Arkana.Domain.Entities;

namespace Arkana.Gateway.Api.Authorization;

/// <summary>
/// Server-side enforcement for the model allow-list attached to the
/// authenticated API key. An empty allow-list is the explicit legacy meaning
/// "all tenant models"; a non-empty list is authoritative and must be checked
/// before any provider or connector is selected.
/// </summary>
public static class ApiKeyModelAuthorization
{
    private const string ModelIdsItem = "ApiKeyModelIds";

    public static bool IsRestricted(HttpContext context)
        => TryGetAllowedIds(context, out var ids) && ids.Count > 0;

    public static bool IsAllowed(HttpContext context, Model model)
        => !TryGetAllowedIds(context, out var ids) || ids.Count == 0 || ids.Contains(model.Id);

    private static bool TryGetAllowedIds(HttpContext context, out IReadOnlySet<Guid> ids)
    {
        if (context.Items.TryGetValue(ModelIdsItem, out var value))
        {
            switch (value)
            {
                case Guid[] array:
                    ids = array.ToHashSet();
                    return true;
                case IReadOnlyCollection<Guid> collection:
                    ids = collection.ToHashSet();
                    return true;
                case IEnumerable<Guid> enumerable:
                    ids = enumerable.ToHashSet();
                    return true;
            }
        }

        ids = Array.Empty<Guid>().ToHashSet();
        return false;
    }
}
