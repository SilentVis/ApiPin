using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Execution;

/// <summary>Where an inherited setting comes from.</summary>
public sealed record InheritedAuth(AuthConfig Auth, RequestFolder? From);

/// <summary>A header added by a folder or collection.</summary>
public sealed record InheritedHeader(KeyValueItem Header, RequestFolder From, bool Overridden);

/// <summary>
/// Applies collection and folder settings to a request. <c>chain</c> is the path from the collection
/// (first) down to the request's own folder (last); empty for a request that isn't saved in a collection.
/// </summary>
public static class InheritanceResolver
{
    /// <summary>The auth a request ends up with: its own, or the nearest folder/collection auth that isn't Inherit.</summary>
    public static InheritedAuth EffectiveAuth(AuthConfig own, IReadOnlyList<RequestFolder> chain)
    {
        if (own is not InheritAuth) return new(own, null);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Auth is not InheritAuth) return new(chain[i].Auth, chain[i]);
        }
        return new(new NoAuth(), null);
    }

    /// <summary>Headers from the folders and collection, outermost first, marking the ones a closer level or the request overrides.</summary>
    public static IReadOnlyList<InheritedHeader> InheritedHeaders(IEnumerable<KeyValueItem> own, IReadOnlyList<RequestFolder> chain)
    {
        var taken = new HashSet<string>(own.Where(h => h.IsActive).Select(h => h.Key.Trim()), StringComparer.OrdinalIgnoreCase);
        var result = new List<InheritedHeader>();

        // Walk inner to outer so a closer level claims a name first, then present outermost first.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var level = chain[i].Headers.Where(h => h.IsActive).ToList();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var levelResult = level.Select(h => new InheritedHeader(h, chain[i], taken.Contains(h.Key.Trim()))).ToList();
            foreach (var h in level) names.Add(h.Key.Trim());
            taken.UnionWith(names);
            result.InsertRange(0, levelResult);
        }
        return result;
    }

    /// <summary>A copy of the request with the inherited auth and headers filled in.</summary>
    public static RequestDefinition Apply(RequestDefinition request, IReadOnlyList<RequestFolder> chain)
    {
        var result = request.Clone(newId: false);
        result.Auth = EffectiveAuth(request.Auth, chain).Auth.Clone();

        var inherited = InheritedHeaders(request.Headers, chain).Where(h => !h.Overridden).Select(h => h.Header.Clone());
        result.Headers = [.. inherited, .. result.Headers];
        return result;
    }

    /// <summary>Adds the folder (innermost first) and collection variables to a context.</summary>
    public static VariableContext WithFolderVariables(VariableContext context, IReadOnlyList<RequestFolder> chain)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var source = chain[i] is RequestCollection ? VariableSource.Collection : VariableSource.Folder;
            context = context.With(source, chain[i].Name, chain[i].Variables);
        }
        return context;
    }
}
