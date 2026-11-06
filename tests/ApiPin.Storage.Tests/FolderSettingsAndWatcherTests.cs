using ApiPin.Core.Models;

namespace ApiPin.Storage.Tests;

public sealed class FolderSettingsAndWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apipin-tests", Guid.NewGuid().ToString("N"), "ws");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }

    private (WorkspaceStore Store, RequestCollection Collection, RequestFolder A, RequestFolder B) Seed()
    {
        var store = new WorkspaceStore(_root);
        store.Load();
        var collection = new RequestCollection { Name = "API" };
        store.SaveCollection(collection);
        var a = new RequestFolder { Name = "A" };
        var b = new RequestFolder { Name = "B", Seq = 1 };
        collection.Folders.AddRange([a, b]);
        store.SaveFolder(collection, a);
        store.SaveFolder(collection, b);
        return (store, collection, a, b);
    }

    [Fact]
    public void Folder_and_collection_settings_round_trip()
    {
        var (store, collection, a, _) = Seed();
        collection.Auth = new BearerAuth { Token = "{{token}}" };
        collection.Variables = [new() { Key = "token", Value = "abc" }, new() { Key = "off", Enabled = false }];
        collection.Description = "# Orders\nNotes";
        a.Headers = [new() { Key = "X-Tenant", Value = "t1" }];
        a.Auth = new BasicAuth { Username = "u" };
        store.SaveCollection(collection);
        store.SaveFolder(collection, a);

        var loaded = new WorkspaceStore(_root).Load().Collections.Single();

        Assert.Equal("{{token}}", Assert.IsType<BearerAuth>(loaded.Auth).Token);
        Assert.Equal(["token", "off"], loaded.Variables.Select(v => v.Key));
        Assert.False(loaded.Variables[1].Enabled);
        Assert.Equal("# Orders\nNotes", loaded.Description);
        var folderA = loaded.Folders.Single(f => f.Name == "A");
        Assert.Equal("t1", folderA.Headers.Single().Value);
        Assert.Equal("u", Assert.IsType<BasicAuth>(folderA.Auth).Username);
        Assert.IsType<InheritAuth>(loaded.Folders.Single(f => f.Name == "B").Auth);
    }

    [Fact]
    public void Folders_without_settings_keep_small_files_and_old_files_still_load()
    {
        var (_, _, _, _) = Seed();
        var json = File.ReadAllText(Path.Combine(_root, "collections", "api", "a", "folder.json"));
        Assert.DoesNotContain("headers", json);
        Assert.DoesNotContain("auth", json);

        // A collection.json written before folder settings existed.
        File.WriteAllText(Path.Combine(_root, "collections", "api", "collection.json"),
            $"{{ \"id\": \"{Guid.NewGuid()}\", \"name\": \"API\", \"seq\": 0 }}");
        var loaded = new WorkspaceStore(_root).Load().Collections.Single();
        Assert.IsType<NoAuth>(loaded.Auth);
        Assert.Empty(loaded.Headers);
    }

    [Fact]
    public void Moving_a_folder_moves_its_directory_and_children()
    {
        var (store, collection, a, b) = Seed();
        var request = new RequestDefinition { Name = "Req" };
        a.Requests.Add(request);
        store.SaveRequest(a, request);

        collection.Folders.Remove(a);
        b.Folders.Add(a);
        store.SaveFolder(b, a);
        request.Url = "http://after-move";
        store.SaveRequest(a, request);

        Assert.True(File.Exists(Path.Combine(_root, "collections", "api", "b", "a", "req.req.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "collections", "api", "a")));
        var loaded = new WorkspaceStore(_root).Load().Collections.Single();
        Assert.Equal("http://after-move", loaded.Folders.Single().Folders.Single().Requests.Single().Url);
    }

    [Fact]
    public void Store_remembers_its_own_changes()
    {
        var (store, _, _, _) = Seed();
        var dir = Path.Combine(_root, "collections", "api", "a");

        Assert.True(store.IsOwnChange(Path.Combine(dir, "folder.json"), TimeSpan.FromSeconds(5)));
        Assert.True(store.IsOwnChange(Path.Combine(dir, "folder.json.tmp"), TimeSpan.FromSeconds(5)));
        Assert.False(store.IsOwnChange(Path.Combine(_root, "collections", "other", "x.req.json"), TimeSpan.FromSeconds(5)));
        Assert.False(store.IsOwnChange(Path.Combine(dir, "folder.json"), TimeSpan.Zero));
    }

    [Fact]
    public async Task Watcher_reports_external_changes_but_not_its_own()
    {
        var (store, _, a, _) = Seed();
        using var watcher = new WorkspaceWatcher(store);
        var changed = 0;
        watcher.Changed += () => Interlocked.Increment(ref changed);

        // Our own write: ignored.
        var request = new RequestDefinition { Name = "Mine" };
        a.Requests.Add(request);
        store.SaveRequest(a, request);
        await Task.Delay(1000, Ct);
        Assert.Equal(0, changed);

        // Someone else's: reported once, after the debounce.
        await File.WriteAllTextAsync(Path.Combine(_root, "collections", "api", "external.req.json"), "{}", Ct);
        await File.AppendAllTextAsync(Path.Combine(_root, "collections", "api", "external.req.json"), " ", Ct);
        for (var i = 0; i < 50 && Volatile.Read(ref changed) == 0; i++) await Task.Delay(100, Ct);
        Assert.Equal(1, changed);

        // Non-JSON files and .git are ignored.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".git", "index.json"), "x", Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "x", Ct);
        await Task.Delay(1000, Ct);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Recent_workspaces_are_most_recent_first_without_duplicates()
    {
        var settings = new AppSettings()
            .WithWorkspace(@"C:\a")
            .WithWorkspace(@"C:\b")
            .WithWorkspace(@"c:\A");

        Assert.Equal([@"c:\A", @"C:\b"], settings.RecentWorkspaces);
        Assert.Equal(@"c:\A", settings.LastWorkspace);
        Assert.Equal([@"c:\A"], settings.WithoutRecent(@"C:\B").RecentWorkspaces);

        var many = Enumerable.Range(0, 20).Aggregate(new AppSettings(), (s, i) => s.WithWorkspace($@"C:\{i}"));
        Assert.Equal(AppSettings.MaxRecent, many.RecentWorkspaces.Count);

        var path = Path.Combine(_root, "settings.json");
        var store = new AppSettingsStore(path);
        store.Save(settings);
        Assert.Equal(settings.RecentWorkspaces, store.Load().RecentWorkspaces);
    }
}
