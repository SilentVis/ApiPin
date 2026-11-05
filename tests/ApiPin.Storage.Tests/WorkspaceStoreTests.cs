using ApiPin.Core.Models;

namespace ApiPin.Storage.Tests;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apipin-tests", Guid.NewGuid().ToString("N"), "my-api-tests");

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }

    private string Collections => Path.Combine(_root, "collections");

    /// <summary>Saves Orders API / auth / login plus a top-level request, the way the UI does.</summary>
    private (WorkspaceStore Store, RequestCollection Collection, RequestFolder Folder, RequestDefinition Login, RequestDefinition List) Seed()
    {
        var store = new WorkspaceStore(_root);
        store.Load();

        var collection = new RequestCollection { Name = "Orders API" };
        store.SaveCollection(collection);

        var folder = new RequestFolder { Name = "Auth" };
        collection.Folders.Add(folder);
        store.SaveFolder(collection, folder);

        var login = new RequestDefinition { Name = "Log in", Method = "POST", Url = "{{base}}/login", Body = new RawBody { Content = "{}" } };
        folder.Requests.Add(login);
        store.SaveRequest(folder, login);

        var list = new RequestDefinition { Name = "List orders", Url = "http://h/orders", Seq = 1 };
        collection.Requests.Add(list);
        store.SaveRequest(collection, list);

        return (store, collection, folder, login, list);
    }

    [Fact]
    public void New_workspace_gets_metadata_and_collections_folder()
    {
        var workspace = new WorkspaceStore(_root).Load();

        Assert.Equal("my-api-tests", workspace.Name);
        Assert.True(File.Exists(Path.Combine(_root, WorkspaceStore.WorkspaceFileName)));
        Assert.True(Directory.Exists(Collections));
        Assert.Empty(workspace.Collections);
        Assert.Empty(workspace.Problems);
    }

    [Fact]
    public void Saves_readable_layout_on_disk()
    {
        Seed();

        Assert.True(File.Exists(Path.Combine(Collections, "orders-api", "collection.json")));
        Assert.True(File.Exists(Path.Combine(Collections, "orders-api", "auth", "folder.json")));
        Assert.True(File.Exists(Path.Combine(Collections, "orders-api", "auth", "log-in.req.json")));
        Assert.True(File.Exists(Path.Combine(Collections, "orders-api", "list-orders.req.json")));

        var json = File.ReadAllText(Path.Combine(Collections, "orders-api", "auth", "log-in.req.json"));
        Assert.Contains("\"method\": \"POST\"", json);
        Assert.Contains("\n  \"url\"", json.ReplaceLineEndings("\n")); // 2-space indent for clean diffs
    }

    [Fact]
    public void Round_trips_tree_from_a_fresh_store()
    {
        var (_, collection, folder, login, list) = Seed();

        var loaded = new WorkspaceStore(_root).Load();

        var c = Assert.Single(loaded.Collections);
        Assert.Equal((collection.Id, "Orders API"), (c.Id, c.Name));
        var f = Assert.Single(c.Folders);
        Assert.Equal((folder.Id, "Auth"), (f.Id, f.Name));
        var l = Assert.Single(f.Requests);
        Assert.Equal((login.Id, "POST", "{{base}}/login"), (l.Id, l.Method, l.Url));
        Assert.IsType<RawBody>(l.Body);
        Assert.Equal(list.Id, Assert.Single(c.Requests).Id);
        Assert.Empty(loaded.Problems);
    }

    [Fact]
    public void Renaming_moves_files_and_keeps_ids()
    {
        var (store, collection, folder, login, _) = Seed();

        login.Name = "Sign in";
        store.SaveRequest(folder, login);
        folder.Name = "Security";
        store.SaveFolder(collection, folder);
        collection.Name = "Shop API";
        store.SaveCollection(collection);

        Assert.True(File.Exists(Path.Combine(Collections, "shop-api", "security", "sign-in.req.json")));
        Assert.False(Directory.Exists(Path.Combine(Collections, "orders-api")));
        Assert.Single(Directory.GetFiles(Path.Combine(Collections, "shop-api", "security"), "*.req.json"));

        // Paths of children follow the renamed folders, so later saves land in the right place.
        login.Url = "http://changed";
        store.SaveRequest(folder, login);
        var reloaded = new WorkspaceStore(_root).Load();
        Assert.Equal("http://changed", reloaded.Collections[0].Folders[0].Requests[0].Url);
        Assert.Equal(login.Id, reloaded.Collections[0].Folders[0].Requests[0].Id);
    }

    [Fact]
    public void Same_names_get_numbered_files()
    {
        var (store, collection, _, _, _) = Seed();

        var copy = new RequestDefinition { Name = "List orders" };
        collection.Requests.Add(copy);
        store.SaveRequest(collection, copy);

        Assert.True(File.Exists(Path.Combine(Collections, "orders-api", "list-orders-2.req.json")));

        // Saving again doesn't keep renumbering.
        store.SaveRequest(collection, copy);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Collections, "orders-api"), "*.req.json").Length);
    }

    [Fact]
    public void Moving_a_request_to_another_folder()
    {
        var (store, collection, folder, _, list) = Seed();

        collection.Requests.Remove(list);
        folder.Requests.Add(list);
        store.SaveRequest(folder, list);

        Assert.False(File.Exists(Path.Combine(Collections, "orders-api", "list-orders.req.json")));
        Assert.True(File.Exists(Path.Combine(Collections, "orders-api", "auth", "list-orders.req.json")));
    }

    [Fact]
    public void Delete_removes_folder_with_contents()
    {
        var (store, collection, folder, login, _) = Seed();

        store.Delete(folder.Id);

        Assert.False(Directory.Exists(Path.Combine(Collections, "orders-api", "auth")));
        Assert.Null(store.PathOf(login.Id));
        Assert.Empty(new WorkspaceStore(_root).Load().Collections[0].Folders);
        Assert.NotNull(store.PathOf(collection.Id));
    }

    [Fact]
    public void Bad_files_are_reported_and_skipped()
    {
        var (_, _, _, _, _) = Seed();
        File.WriteAllText(Path.Combine(Collections, "orders-api", "broken.req.json"), "{ nope");
        Directory.CreateDirectory(Path.Combine(Collections, "not-a-collection"));
        File.Copy(Path.Combine(Collections, "orders-api", "list-orders.req.json"), Path.Combine(Collections, "orders-api", "copied.req.json"));

        var loaded = new WorkspaceStore(_root).Load();

        Assert.Equal(3, loaded.Problems.Count);
        Assert.Contains(loaded.Problems, p => p.Path.EndsWith("broken.req.json"));
        Assert.Contains(loaded.Problems, p => p.Path.EndsWith("not-a-collection"));
        Assert.Contains(loaded.Problems, p => p.Message.Contains("Duplicate id"));

        var requests = loaded.Collections[0].Requests;
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].Id, requests[1].Id);
    }

    [Theory]
    [InlineData("Orders API", "orders-api")]
    [InlineData("  Get /orders/:id ", "get-orders-id")]
    [InlineData("Ünïcode name", "ünïcode-name")]
    [InlineData("***", "fallback")]
    [InlineData("CON", "_con")]
    public void Slugs(string name, string expected)
    {
        Assert.Equal(expected, Slug.From(name, "fallback"));
    }
}
