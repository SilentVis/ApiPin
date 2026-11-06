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

    /// <summary>Stable id from apipin.workspace.json; keys this workspace's secrets in the secret store.</summary>
    public Guid Id { get; init; }

    public List<RequestCollection> Collections { get; } = [];
    public List<EnvironmentDefinition> Environments { get; } = [];

    /// <summary>Workspace-wide variables, lowest precedence.</summary>
    public List<Variable> Globals { get; set; } = [];

    /// <summary>The environment picked on this computer (kept in .apipin/, not in Git).</summary>
    public Guid? ActiveEnvironmentId { get; set; }

    /// <summary>Files that could not be read. They are skipped and left untouched on disk.</summary>
    public List<WorkspaceProblem> Problems { get; } = [];

    public IEnumerable<RequestCollection> OrderedCollections =>
        Collections.OrderBy(c => c.Seq).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<EnvironmentDefinition> OrderedEnvironments =>
        Environments.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    public EnvironmentDefinition? ActiveEnvironment => Environments.FirstOrDefault(e => e.Id == ActiveEnvironmentId);
}

/// <summary>
/// A workspace is a plain folder that can live in Git next to the API's code:
/// <code>
/// apipin.workspace.json                       name, id, global variables
/// .gitignore                                  generated: ignores .apipin/
/// environments/&lt;environment&gt;.env.json
/// collections/&lt;collection&gt;/collection.json
/// collections/&lt;collection&gt;/&lt;folder&gt;/folder.json
/// collections/&lt;collection&gt;/[&lt;folder&gt;/]&lt;request&gt;.req.json
/// .apipin/state.json                          local only: the picked environment
/// </code>
/// Names on disk are slugs of the display names and follow renames; identity comes from the ids inside the files.
/// Values of secret variables never go into these files; they live in the <see cref="ISecretStore"/>.
/// </summary>
public sealed partial class WorkspaceStore(string root, ISecretStore? secrets = null)
{
    public const string WorkspaceFileName = "apipin.workspace.json";
    public const string CollectionFileName = "collection.json";
    public const string FolderFileName = "folder.json";
    public const string RequestSuffix = ".req.json";
    public const string EnvironmentSuffix = ".env.json";
    public const string LocalDirName = ".apipin";
    private const int CurrentVersion = 1;

    private readonly ISecretStore _secrets = secrets ?? new InMemorySecretStore();

    /// <summary>Where each loaded or saved item lives: a directory for collections and folders, a file for requests and environments.</summary>
    private readonly Dictionary<Guid, string> _paths = [];

    /// <summary>Paths this store wrote, moved or deleted recently, so a file watcher can ignore its own changes.</summary>
    private readonly ConcurrentDictionary<string, (DateTime At, bool Recursive)> _touched = new(StringComparer.OrdinalIgnoreCase);

    private Guid _workspaceId;
    private string _name = "";

    public string Root { get; } = Path.GetFullPath(root);
    private string CollectionsDir => Path.Combine(Root, "collections");
    private string EnvironmentsDir => Path.Combine(Root, "environments");
    private string LocalStatePath => Path.Combine(Root, LocalDirName, "state.json");

    /// <summary>Reads the workspace, creating the folder structure if it doesn't exist yet. Never throws for bad files.</summary>
    public Workspace Load()
    {
        _paths.Clear();
        Directory.CreateDirectory(CollectionsDir);
        EnsureGitIgnore();

        var problems = new List<WorkspaceProblem>();
        var metaPath = Path.Combine(Root, WorkspaceFileName);
        var exists = File.Exists(metaPath);
        var meta = exists ? Read<WorkspaceFile>(metaPath, problems) : null;

        _name = meta?.Name ?? new DirectoryInfo(Root).Name;
        _workspaceId = meta?.Id ?? Guid.NewGuid();
        var globals = Reveal(GlobalsScope, meta?.Globals ?? []);
        if (!exists || (meta is not null && meta.Id is null))
            WriteMeta(globals); // new workspace, or one written before ids existed

        var workspace = new Workspace { Root = Root, Name = _name, Id = _workspaceId, Globals = globals };
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
            ApplyFolderFile(dto, collection);
            collection.Id = Register(dto.Id, dir, workspace.Problems);
            LoadChildren(collection, dir, workspace.Problems);
            workspace.Collections.Add(collection);
        }

        if (Directory.Exists(EnvironmentsDir))
        {
            foreach (var file in Directory.EnumerateFiles(EnvironmentsDir, "*" + EnvironmentSuffix).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Read<EnvironmentFile>(file, workspace.Problems) is not { } env) continue;
                workspace.Environments.Add(new EnvironmentDefinition
                {
                    Id = Register(env.Id, file, workspace.Problems),
                    Name = env.Name,
                    Variables = Reveal(EnvironmentScope(env.Id), env.Variables ?? []),
                });
            }
        }

        var state = LoadLocalState();
        workspace.ActiveEnvironmentId = workspace.Environments.Any(e => e.Id == state?.ActiveEnvironmentId) ? state!.ActiveEnvironmentId : null;
        return workspace;
    }

    public void RenameWorkspace(Workspace workspace, string name)
    {
        workspace.Name = _name = name;
        WriteMeta(workspace.Globals);
    }

    /// <summary>Saves the workspace-wide variables (secret values go to the secret store).</summary>
    public void SaveGlobals(Workspace workspace) => WriteMeta(workspace.Globals);

    /// <summary>Creates or updates a collection; its directory follows the name.</summary>
    public void SaveCollection(RequestCollection collection)
    {
        var dir = PlaceDirectory(collection.Id, CollectionsDir, collection.Name, "collection");
        Write(Path.Combine(dir, CollectionFileName), ToFolderFile(collection));
    }

    /// <summary>Creates, updates or moves a folder into <paramref name="parent"/>.</summary>
    public void SaveFolder(RequestFolder parent, RequestFolder folder)
    {
        var dir = PlaceDirectory(folder.Id, DirectoryOf(parent), folder.Name, "folder");
        Write(Path.Combine(dir, FolderFileName), ToFolderFile(folder));
    }

    /// <summary>Creates, updates or moves a request into <paramref name="parent"/>.</summary>
    public void SaveRequest(RequestFolder parent, RequestDefinition request) =>
        Write(PlaceFile(request.Id, DirectoryOf(parent), request.Name, "request", RequestSuffix), request);

    /// <summary>Creates or updates an environment file; it follows the name.</summary>
    public void SaveEnvironment(EnvironmentDefinition environment)
    {
        var path = PlaceFile(environment.Id, EnvironmentsDir, environment.Name, "environment", EnvironmentSuffix);
        Write(path, new EnvironmentFile(environment.Id, environment.Name, Protect(EnvironmentScope(environment.Id), environment.Variables)));
    }

    /// <summary>Deletes a collection, folder (with everything in it), request or environment, and their secrets.</summary>
    public void Delete(Guid id)
    {
        if (!_paths.Remove(id, out var path)) return;
        Touch(path, recursive: true);
        ForgetSecrets(id);

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            foreach (var key in _paths.Where(p => IsUnder(p.Value, path)).Select(p => p.Key).ToList())
            {
                _paths.Remove(key);
                ForgetSecrets(key);
            }
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Remembers the picked environment on this computer only.</summary>
    public void SaveActiveEnvironment(Guid? id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalStatePath)!);
        AtomicFile.WriteAllText(LocalStatePath, JsonSerializer.Serialize(new LocalState(id), JsonDefaults.Options));
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
            ApplyFolderFile(dto, child);
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

    /// <summary>The file for an item, created or moved/renamed so it sits in <paramref name="parentDir"/> with a name following <paramref name="name"/>.</summary>
    private string PlaceFile(Guid id, string parentDir, string name, string fallback, string suffix)
    {
        var slug = Slug.From(name, fallback);
        if (_paths.TryGetValue(id, out var path) && IsPlaced(path, parentDir, slug, suffix)) return path;

        Directory.CreateDirectory(parentDir);
        var target = UniquePath(parentDir, slug, suffix);
        Touch(target);
        if (path is not null && File.Exists(path))
        {
            Touch(path);
            File.Move(path, target);
        }
        return _paths[id] = target;
    }

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

    private void WriteMeta(List<Variable> globals)
    {
        var protectedGlobals = Protect(GlobalsScope, globals);
        Write(Path.Combine(Root, WorkspaceFileName), new WorkspaceFile(CurrentVersion, _name, _workspaceId, protectedGlobals.Count == 0 ? null : protectedGlobals));
    }

    private LocalState? LoadLocalState()
    {
        try
        {
            return File.Exists(LocalStatePath) ? JsonSerializer.Deserialize<LocalState>(File.ReadAllText(LocalStatePath), JsonDefaults.Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps .apipin/ (local UI state) out of Git.</summary>
    private void EnsureGitIgnore()
    {
        var path = Path.Combine(Root, ".gitignore");
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (existing is not null && existing.Split('\n').Any(l => l.Trim().TrimEnd('/') == LocalDirName)) return;
            var text = existing is null
                ? $"# Local ApiPin state (picked environment); never commit it{Environment.NewLine}{LocalDirName}/{Environment.NewLine}"
                : $"{(existing.Length == 0 || existing.EndsWith('\n') ? "" : Environment.NewLine)}{LocalDirName}/{Environment.NewLine}";
            File.AppendAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only workspace still works.
        }
    }

    // ---------- Secrets ----------

    private const string GlobalsScope = "globals";
    private static string EnvironmentScope(Guid id) => $"env/{id:N}";
    private static string FolderScope(Guid id) => $"folder/{id:N}";
    private string Prefix(string scope) => $"{_workspaceId:N}/{scope}/";

    /// <summary>Stores the values of secret variables and returns copies with those values blanked, for writing to disk.</summary>
    private List<Variable> Protect(string scope, List<Variable> variables)
    {
        var values = variables
            .Where(v => v.Secret && v.Key.Trim().Length > 0 && v.Value.Length > 0)
            .GroupBy(v => v.Key.Trim())
            .ToDictionary(g => g.Key, g => g.First().Value);
        _secrets.ReplaceScope(Prefix(scope), values);
        return variables.ConvertAll(v =>
        {
            var copy = v.Clone();
            if (copy.Secret) copy.Value = "";
            return copy;
        });
    }

    /// <summary>Fills in secret values from the store; missing ones stay empty.</summary>
    private List<Variable> Reveal(string scope, List<Variable> variables)
    {
        foreach (var v in variables.Where(v => v.Secret))
            v.Value = _secrets.Get(Prefix(scope) + v.Key.Trim()) ?? "";
        return variables;
    }

    private void ForgetSecrets(Guid id)
    {
        _secrets.ReplaceScope(Prefix(FolderScope(id)), new Dictionary<string, string>());
        _secrets.ReplaceScope(Prefix(EnvironmentScope(id)), new Dictionary<string, string>());
    }

    // ---------- File formats ----------

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex NumberSuffix();

    private sealed record WorkspaceFile(int Version, string Name, Guid? Id = null, List<Variable>? Globals = null);

    private sealed record EnvironmentFile(Guid Id, string Name, List<Variable>? Variables);

    private sealed record LocalState(Guid? ActiveEnvironmentId);

    /// <summary>collection.json / folder.json. Empty settings are left out so the files stay small.</summary>
    private sealed record FolderFile(
        Guid Id,
        string Name,
        string? Description,
        int Seq,
        AuthConfig? Auth = null,
        List<KeyValueItem>? Headers = null,
        List<Variable>? Variables = null);

    private FolderFile ToFolderFile(RequestFolder folder)
    {
        var variables = Protect(FolderScope(folder.Id), folder.Variables);
        return new FolderFile(
            folder.Id,
            folder.Name,
            folder.Description,
            folder.Seq,
            folder.Auth is InheritAuth || (folder is RequestCollection && folder.Auth is NoAuth) ? null : folder.Auth,
            folder.Headers.Count == 0 ? null : folder.Headers,
            variables.Count == 0 ? null : variables);
    }

    private void ApplyFolderFile(FolderFile dto, RequestFolder folder)
    {
        folder.Description = dto.Description;
        if (dto.Auth is not null) folder.Auth = dto.Auth;
        folder.Headers = dto.Headers ?? [];
        folder.Variables = Reveal(FolderScope(dto.Id), dto.Variables ?? []);
    }
}
