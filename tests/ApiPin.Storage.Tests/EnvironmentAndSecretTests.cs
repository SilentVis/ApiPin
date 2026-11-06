using ApiPin.Core.Models;

namespace ApiPin.Storage.Tests;

public sealed class EnvironmentAndSecretTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apipin-tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "ws");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static EnvironmentDefinition Env(string name, params Variable[] vars) => new() { Name = name, Variables = [.. vars] };

    [Fact]
    public void Environments_round_trip_with_readable_files()
    {
        var store = new WorkspaceStore(Root);
        store.Load();
        var local = Env("Local", new Variable { Key = "baseUrl", Value = "https://localhost:5001" });
        var staging = Env("Staging", new Variable { Key = "baseUrl", Value = "https://staging.example.test" }, new Variable { Key = "off", Enabled = false });
        store.SaveEnvironment(local);
        store.SaveEnvironment(staging);

        Assert.True(File.Exists(Path.Combine(Root, "environments", "local.env.json")));
        var loaded = new WorkspaceStore(Root).Load();

        Assert.Equal(["Local", "Staging"], loaded.OrderedEnvironments.Select(e => e.Name));
        var s = loaded.Environments.Single(e => e.Id == staging.Id);
        Assert.Equal("https://staging.example.test", s.Variables[0].Value);
        Assert.False(s.Variables[1].Enabled);
    }

    [Fact]
    public void Renaming_and_deleting_environments_follow_on_disk()
    {
        var store = new WorkspaceStore(Root);
        store.Load();
        var env = Env("Dev");
        store.SaveEnvironment(env);
        env.Name = "Development";
        store.SaveEnvironment(env);

        Assert.Equal(["development.env.json"], Directory.GetFiles(Path.Combine(Root, "environments")).Select(Path.GetFileName));

        store.Delete(env.Id);
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "environments")));
    }

    [Fact]
    public void Active_environment_is_local_state_ignored_by_git()
    {
        var store = new WorkspaceStore(Root);
        store.Load();
        var env = Env("Local");
        store.SaveEnvironment(env);
        store.SaveActiveEnvironment(env.Id);

        Assert.Equal(env.Id, new WorkspaceStore(Root).Load().ActiveEnvironmentId);
        Assert.True(File.Exists(Path.Combine(Root, ".apipin", "state.json")));
        Assert.Contains(".apipin/", File.ReadAllText(Path.Combine(Root, ".gitignore")));

        // A deleted environment is no longer active.
        store.Delete(env.Id);
        Assert.Null(new WorkspaceStore(Root).Load().ActiveEnvironmentId);
    }

    [Fact]
    public void Existing_gitignore_gets_the_line_appended_once()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, ".gitignore"), "bin/");

        new WorkspaceStore(Root).Load();
        new WorkspaceStore(Root).Load();

        Assert.Equal("bin/" + Environment.NewLine + ".apipin/" + Environment.NewLine, File.ReadAllText(Path.Combine(Root, ".gitignore")));
    }

    [Fact]
    public void Workspace_gets_a_stable_id_and_globals()
    {
        var store = new WorkspaceStore(Root);
        var first = store.Load();
        first.Globals = [new Variable { Key = "tenant", Value = "acme" }];
        store.SaveGlobals(first);

        var again = new WorkspaceStore(Root).Load();
        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(Guid.Empty, again.Id);
        Assert.Equal("acme", again.Globals.Single().Value);
    }

    [Fact]
    public void Secret_values_never_reach_workspace_files()
    {
        const string secret = "s3cr3t-value-that-must-not-leak";
        var secrets = new InMemorySecretStore();
        var store = new WorkspaceStore(Root, secrets);
        var workspace = store.Load();

        var env = Env("Staging", new Variable { Key = "apiKey", Value = secret, Secret = true }, new Variable { Key = "plain", Value = "visible" });
        store.SaveEnvironment(env);
        workspace.Globals = [new Variable { Key = "globalSecret", Value = secret + "-g", Secret = true }];
        store.SaveGlobals(workspace);
        var collection = new RequestCollection { Name = "API", Variables = [new Variable { Key = "token", Value = secret + "-c", Secret = true }] };
        store.SaveCollection(collection);
        var folder = new RequestFolder { Name = "F", Variables = [new Variable { Key = "pwd", Value = secret + "-f", Secret = true }] };
        collection.Folders.Add(folder);
        store.SaveFolder(collection, folder);

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("s3cr3t", File.ReadAllText(file));
        Assert.Contains("visible", File.ReadAllText(Path.Combine(Root, "environments", "staging.env.json")));

        // The same user on the same machine gets them back.
        var loaded = new WorkspaceStore(Root, secrets).Load();
        Assert.Equal(secret, loaded.Environments.Single().Variables.Single(v => v.Key == "apiKey").Value);
        Assert.Equal(secret + "-g", loaded.Globals.Single().Value);
        Assert.Equal(secret + "-c", loaded.Collections.Single().Variables.Single().Value);
        Assert.Equal(secret + "-f", loaded.Collections.Single().Folders.Single().Variables.Single().Value);
    }

    [Fact]
    public void After_cloning_secrets_show_as_missing()
    {
        var store = new WorkspaceStore(Root, new InMemorySecretStore());
        store.Load();
        store.SaveEnvironment(Env("Staging", new Variable { Key = "apiKey", Value = "abc", Secret = true }, new Variable { Key = "x", Value = "1" }));

        // Another machine: same files, empty secret store.
        var cloned = new WorkspaceStore(Root, new InMemorySecretStore()).Load().Environments.Single();

        var missing = Assert.Single(cloned.MissingSecrets);
        Assert.Equal("apiKey", missing.Key);
        Assert.Equal("", missing.Value);
    }

    [Fact]
    public void Removed_secrets_are_dropped_from_the_store()
    {
        var secrets = new InMemorySecretStore();
        var store = new WorkspaceStore(Root, secrets);
        store.Load();
        var env = Env("E", new Variable { Key = "a", Value = "1", Secret = true }, new Variable { Key = "b", Value = "2", Secret = true });
        store.SaveEnvironment(env);
        Assert.Equal(2, secrets.Snapshot.Count);

        env.Variables.RemoveAt(0);
        store.SaveEnvironment(env);
        Assert.Single(secrets.Snapshot);

        store.Delete(env.Id);
        Assert.Empty(secrets.Snapshot);
    }

    [Fact]
    public void Secrets_of_different_workspaces_do_not_mix()
    {
        var secrets = new InMemorySecretStore();
        var a = new WorkspaceStore(Path.Combine(_dir, "a"), secrets);
        var b = new WorkspaceStore(Path.Combine(_dir, "b"), secrets);
        a.Load();
        b.Load();
        var env = Env("Same", new Variable { Key = "k", Value = "from-a", Secret = true });
        a.SaveEnvironment(env);

        // Copy the env file into workspace b: same environment id, different workspace id.
        Directory.CreateDirectory(Path.Combine(_dir, "b", "environments"));
        File.Copy(Path.Combine(_dir, "a", "environments", "same.env.json"), Path.Combine(_dir, "b", "environments", "same.env.json"));

        Assert.Equal("", new WorkspaceStore(Path.Combine(_dir, "b"), secrets).Load().Environments.Single().Variables.Single().Value);
    }

    [Fact]
    public void Dpapi_store_round_trips_and_encrypts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "secrets.dat");
        var store = new DpapiSecretStore(path);
        store.ReplaceScope("ws/env/", new Dictionary<string, string> { ["apiKey"] = "plain-text-secret" });

        Assert.DoesNotContain("plain-text-secret", File.ReadAllText(path));
        Assert.Equal("plain-text-secret", new DpapiSecretStore(path).Get("ws/env/apiKey"));

        File.WriteAllText(path, "garbage");
        Assert.Null(new DpapiSecretStore(path).Get("ws/env/apiKey")); // unreadable: treated as empty
    }
}
