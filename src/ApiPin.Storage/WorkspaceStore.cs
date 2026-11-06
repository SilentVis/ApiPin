using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApiPin.Core.Models;

namespace ApiPin.Storage;

public sealed record WorkspaceProblem(string Path, string Message);

public sealed class Workspace
{
    public required string Root { get; init; }
    public required string Name { get; set; }
    public List<RequestCollection> Collections { get; } = [];

    /// <summary>Files that could not be read. They are skipped and left untouched on disk.</summary>
    public List<WorkspaceProblem> Problems { get; } = [];

    public IEnumerable<RequestCollection> OrderedCollections =>
        Collections.OrderBy(c => c.Seq).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A workspace is a plain folder that can live in Git next to the API's code:
/// <code>
/// apipin.workspace.json
/// collections/&lt;collection&gt;/collection.json
/// collections/&lt;collection&gt;/&lt;folder&gt;/folder.json
/// collections/&lt;collection&gt;/[&lt;folder&gt;/]&lt;request&gt;.req.json
/// </code>
/// Names on disk are slugs of the display names and follow renames; identity comes from the ids inside the files.
/// </summary>
public sealed partial class WorkspaceStore(string root)
{
    public const string WorkspaceFileName = "apipin.workspace.json";
    public const string CollectionFileName = "collection.json";
    public const string FolderFileName = "folder.json";
    public const string RequestSuffix = ".req.json";
    private const int CurrentVersion = 1;

    /// <summary>Where each loaded or saved item lives: a directory for collections and folders, a file for requests.</summary>
    private readonly Dictionary<Guid, string> _paths = [];

    /// <summary>Paths this store wrote, moved or deleted recently, so a file watcher can ignore its own changes.</summary>
    private readonly ConcurrentDictionary<string, (DateTime At, bool Recursive)> _touched = new(StringComparer.OrdinalIgnoreCase);

    public string Root { get; } = Path.GetFullPath(root);
    private string CollectionsDir => Path.Combine(Root, "collections");

    /// <summary>Reads the workspace, creating the folder structure if it doesn't exist yet. Never throws for bad files.</summary>
    public Workspace Load()
    {
        _paths.Clear();
        Directory.CreateDirectory(CollectionsDir);

        var problems = new List<WorkspaceProblem>();
        var metaPath = Path.Combine(Root, WorkspaceFileName);
        var meta = File.Exists(metaPath) ? Read<WorkspaceFile>(metaPath, problems) : null;
        if (meta is null && !File.Exists(metaPath))
        {
            meta = new WorkspaceFile(CurrentVersion, new DirectoryInfo(Root).Name);
            Write(metaPath, meta);
        }

        var workspace = new Workspace { Root = Root, Name = meta?.Name ?? new DirectoryInfo(Root).Name };
        workspace.Problems.AddRange(problems);

        foreach (var dir in Directory.EnumerateDirectories(CollectionsDir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(dir, CollectionFileName);
            if (!File.Exists(file))
            {
                workspace.Problems.Add(new(dir, $"Skipped: no {CollectionFileName}."));
                continue;
            }
            if (Read<FolderFile>(file, workspace.Problems) is not { } dto) continue;

            var collection = new RequestCollection { Name = dto.Name, Seq = dto.Seq };
            dto.ApplyTo(collection);
            collection.Id = Register(dto.Id, dir, workspace.Problems);
            LoadChildren(collection, dir, workspace.Problems);
            workspace.Collections.Add(collection);
        }
        return workspace;
    }

    public void RenameWorkspace(Workspace workspace, string name)
    {
        workspace.Name = name;
        Write(Path.Combine(Root, WorkspaceFileName), new WorkspaceFile(CurrentVersion, name));
    }

    /// <summary>Creates or updates a collection; its directory follows the name.</summary>
    public void SaveCollection(RequestCollection collection)
    {
        var dir = PlaceDirectory(collection.Id, CollectionsDir, collection.Name, "collection");
        Write(Path.Combine(dir, CollectionFileName), FolderFile.From(collection));
    }

    /// <summary>Creates, updates or moves a folder into <paramref name="parent"/>.</summary>
    public void SaveFolder(RequestFolder parent, RequestFolder folder)
    {
        var dir = PlaceDirectory(folder.Id, DirectoryOf(parent), folder.Name, "folder");
        Write(Path.Combine(dir, FolderFileName), FolderFile.From(folder));
    }

    /// <summary>Creates, updates or moves a request into <paramref name="parent"/>.</summary>
    public void SaveRequest(RequestFolder parent, RequestDefinition request)
    {
        var parentDir = DirectoryOf(parent);
        var slug = Slug.From(request.Name, "request");

        if (!_paths.TryGetValue(request.Id, out var path) || !IsPlaced(path, parentDir, slug, RequestSuffix))
        {
            var target = UniquePath(parentDir, slug, RequestSuffix);
            Touch(target);
            if (path is not null && File.Exists(path))
            {
                Touch(path);
                File.Move(path, target);
            }
            path = _paths[request.Id] = target;
        }
        Write(path, request);
    }

    /// <summary>Deletes a collection, folder (with everything in it) or request.</summary>
    public void Delete(Guid id)
    {
        if (!_paths.Remove(id, out var path)) return;
        Touch(path, recursive: true);

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            foreach (var key in _paths.Where(p => IsUnder(p.Value, path)).Select(p => p.Key).ToList()) _paths.Remove(key);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Path on disk of a loaded or saved item, for display.</summary>
    public string? PathOf(Guid id) => _paths.GetValueOrDefault(id);

    /// <summary>
    /// True when this store changed <paramref name="path"/> within <paramref name="window"/>: the path itself, a folder
    /// above something it wrote (folders get a change event when their contents change), or anything inside a folder
    /// it moved or deleted.
    /// </summary>
    public bool IsOwnChange(string path, TimeSpan window)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        var cutoff = DateTime.UtcNow - window;
        foreach (var (touched, (at, recursive)) in _touched)
        {
            if (at < cutoff) _touched.TryRemove(touched, out _);
            else if (IsUnder(touched, path) || (recursive && IsUnder(path, touched))) return true;
        }
        return false;
    }

    private void Touch(string path, bool recursive = false) => _touched[Path.TrimEndingDirectorySeparator(path)] = (DateTime.UtcNow, recursive);

    private void LoadChildren(RequestFolder folder, string dir, List<WorkspaceProblem> problems)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(sub, FolderFileName);
            if (!File.Exists(file))
            {
                problems.Add(new(sub, $"Skipped: no {FolderFileName}."));
                continue;
            }
            if (Read<FolderFile>(file, problems) is not { } dto) continue;

            var child = new RequestFolder { Name = dto.Name, Seq = dto.Seq };
            dto.ApplyTo(child);
            child.Id = Register(dto.Id, sub, problems);
            LoadChildren(child, sub, problems);
            folder.Folders.Add(child);
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*" + RequestSuffix).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Read<RequestDefinition>(file, problems) is not { } request) continue;
            request.Id = Register(request.Id, file, problems);
            folder.Requests.Add(request);
        }
    }

    /// <summary>Records where an id lives; a duplicate id (e.g. a copied file) gets a fresh one in memory.</summary>
    private Guid Register(Guid id, string path, List<WorkspaceProblem> problems)
    {
        if (id == Guid.Empty || _paths.ContainsKey(id))
        {
            if (id != Guid.Empty) problems.Add(new(path, "Duplicate id; it gets a new id when saved."));
            id = Guid.NewGuid();
        }
        _paths[id] = path;
        return id;
    }

    private string DirectoryOf(RequestFolder folder) =>
        _paths.TryGetValue(folder.Id, out var dir)
            ? dir
            : throw new InvalidOperationException($"Folder '{folder.Name}' has not been saved yet.");

    private string PlaceDirectory(Guid id, string parentDir, string name, string fallback)
    {
        var slug = Slug.From(name, fallback);
        if (_paths.TryGetValue(id, out var current) && IsPlaced(current, parentDir, slug, "")) return current;

        var target = UniquePath(parentDir, slug, "");
        Touch(target);
        if (current is not null && Directory.Exists(current))
        {
            Touch(current, recursive: true);
            Touch(target, recursive: true);
            Directory.Move(current, target);
            foreach (var (key, path) in _paths.Where(p => IsUnder(p.Value, current)).ToList())
                _paths[key] = target + path[current.Length..];
        }
        else
        {
            Directory.CreateDirectory(target);
        }

        _paths[id] = target;
        return target;
    }

    /// <summary>True when the path is already in the right folder with this slug (or slug-N).</summary>
    private static bool IsPlaced(string path, string parentDir, string slug, string suffix)
    {
        if (!string.Equals(Path.GetDirectoryName(path), parentDir, StringComparison.OrdinalIgnoreCase)) return false;
        var name = Path.GetFileName(path);
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        name = name[..^suffix.Length];
        return name == slug || (name.StartsWith(slug + "-", StringComparison.Ordinal) && NumberSuffix().IsMatch(name[(slug.Length + 1)..]));
    }

    private static string UniquePath(string parentDir, string slug, string suffix)
    {
        var candidate = Path.Combine(parentDir, slug + suffix);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(parentDir, $"{slug}-{i}{suffix}");
        return candidate;
    }

    private static bool IsUnder(string path, string dir) =>
        path.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static T? Read<T>(string path, List<WorkspaceProblem> problems) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options)
                   ?? throw new JsonException("The file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problems.Add(new(path, $"Skipped: {ex.Message}"));
            return null;
        }
    }

    private void Write<T>(string path, T value)
    {
        Touch(path);
        Touch(path + ".tmp");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, JsonDefaults.Options) + Environment.NewLine);
    }

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex NumberSuffix();

    private sealed record WorkspaceFile(int Version, string Name);

    /// <summary>collection.json / folder.json. Empty settings are left out so the files stay small.</summary>
    private sealed record FolderFile(
        Guid Id,
        string Name,
        string? Description,
        int Seq,
        AuthConfig? Auth = null,
        List<KeyValueItem>? Headers = null,
        List<Variable>? Variables = null)
    {
        public static FolderFile From(RequestFolder folder) => new(
            folder.Id,
            folder.Name,
            folder.Description,
            folder.Seq,
            folder.Auth is InheritAuth || (folder is RequestCollection && folder.Auth is NoAuth) ? null : folder.Auth,
            folder.Headers.Count == 0 ? null : folder.Headers,
            folder.Variables.Count == 0 ? null : folder.Variables);

        public void ApplyTo(RequestFolder folder)
        {
            folder.Description = Description;
            if (Auth is not null) folder.Auth = Auth;
            folder.Headers = Headers ?? [];
            folder.Variables = Variables ?? [];
        }
    }
}
