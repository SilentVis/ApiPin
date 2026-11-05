namespace ApiPin.Core.Models;

/// <summary>A folder of saved requests and sub-folders. A <see cref="RequestCollection"/> is the root folder.</summary>
public class RequestFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Order among its siblings.</summary>
    public int Seq { get; set; }

    public List<RequestFolder> Folders { get; set; } = [];
    public List<RequestDefinition> Requests { get; set; } = [];

    public IEnumerable<RequestFolder> OrderedFolders => Folders.OrderBy(f => f.Seq).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<RequestDefinition> OrderedRequests => Requests.OrderBy(r => r.Seq).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The next Seq value for a new child.</summary>
    public int NextSeq => Folders.Select(f => f.Seq).Concat(Requests.Select(r => r.Seq)).DefaultIfEmpty(-1).Max() + 1;

    /// <summary>This folder and every folder below it.</summary>
    public IEnumerable<RequestFolder> SelfAndDescendants() =>
        Folders.SelectMany(f => f.SelfAndDescendants()).Prepend(this);

    public IEnumerable<RequestDefinition> AllRequests() => SelfAndDescendants().SelectMany(f => f.Requests);
}

public sealed class RequestCollection : RequestFolder;
