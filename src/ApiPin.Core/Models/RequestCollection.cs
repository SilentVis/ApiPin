namespace ApiPin.Core.Models;

/// <summary>A folder of saved requests and sub-folders. A <see cref="RequestCollection"/> is the root folder.</summary>
public class RequestFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Notes in Markdown.</summary>
    public string? Description { get; set; }

    /// <summary>Order among its siblings.</summary>
    public int Seq { get; set; }

    /// <summary>Sent with every request inside; a request's own header of the same name wins.</summary>
    public List<KeyValueItem> Headers { get; set; } = [];

    /// <summary>Auth for requests inside that use <see cref="InheritAuth"/>. Inherit passes it on from the parent.</summary>
    public AuthConfig Auth { get; set; } = new InheritAuth();

    /// <summary>Variables for requests inside; inner folders win over outer ones and the collection.</summary>
    public List<Variable> Variables { get; set; } = [];

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

    /// <summary>True when the folder has headers, auth or variables of its own.</summary>
    public bool HasSettings => Headers.Any(h => h.IsActive) || Auth is not InheritAuth || Variables.Any(v => v.IsActive);

    /// <summary>A copy of the folder's own settings (not its children).</summary>
    public FolderSettings GetSettings() => new()
    {
        Description = Description,
        Headers = Headers.ConvertAll(h => h.Clone()),
        Auth = Auth.Clone(),
        Variables = Variables.ConvertAll(v => v.Clone()),
    };

    public void ApplySettings(FolderSettings settings)
    {
        Description = string.IsNullOrWhiteSpace(settings.Description) ? null : settings.Description;
        Headers = settings.Headers.Where(h => !h.IsEmpty).Select(h => h.Clone()).ToList();
        Auth = settings.Auth.Clone();
        Variables = settings.Variables.Where(v => !v.IsEmpty).Select(v => v.Clone()).ToList();
    }
}

public sealed class RequestCollection : RequestFolder
{
    public RequestCollection() => Auth = new NoAuth();
}

/// <summary>The editable settings of a folder or collection.</summary>
public sealed class FolderSettings
{
    public string? Description { get; set; }
    public List<KeyValueItem> Headers { get; set; } = [];
    public AuthConfig Auth { get; set; } = new InheritAuth();
    public List<Variable> Variables { get; set; } = [];
}
