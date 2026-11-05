using ApiPin.Core.Models;

namespace ApiPin.Storage.Tests;

public sealed class ScratchTabStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apipin-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "scratch.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Round_trips_all_auth_and_body_types()
    {
        RequestDefinition[] tabs =
        [
            new() { Name = "basic", Auth = new BasicAuth { Username = "u", Password = "p" }, Body = new RawBody { Language = RawLanguage.Xml, Content = "<a/>" } },
            new() { Auth = new BearerAuth { Token = "t", Prefix = "Token" }, Body = new FormUrlEncodedBody { Fields = [new() { Key = "k", Value = "v", Enabled = false }] } },
            new() { Auth = new ApiKeyAuth { Key = "key", Value = "v", Location = ApiKeyLocation.Query }, Body = new MultipartBody { Parts = [new() { Name = "f", Kind = MultipartPartKind.File, Value = @"C:\x.bin" }] } },
            new() { Auth = new WindowsAuth { UseCurrentUser = false, Domain = "D" }, Body = new BinaryFileBody { FilePath = @"C:\y.bin", ContentType = "image/png" } },
            new() { Method = "DELETE", Url = "http://h/:id?q=1", PathParams = [new() { Key = "id", Value = "7" }], Settings = new() { TimeoutMs = 5, VerifyTls = false, HttpVersion = "1.1" } },
        ];

        var store = new ScratchTabStore(FilePath);
        await store.SaveAsync(tabs, tabs[2].Id, TestContext.Current.CancellationToken);
        var loaded = store.Load();

        Assert.Equal(tabs[2].Id, loaded.ActiveTabId);
        Assert.Equal(5, loaded.Tabs.Count);
        Assert.Equal(tabs.Select(t => t.Id), loaded.Tabs.Select(t => t.Id));

        var basic = Assert.IsType<BasicAuth>(loaded.Tabs[0].Auth);
        Assert.Equal(("u", "p"), (basic.Username, basic.Password));
        Assert.Equal(RawLanguage.Xml, Assert.IsType<RawBody>(loaded.Tabs[0].Body).Language);

        Assert.Equal("Token", Assert.IsType<BearerAuth>(loaded.Tabs[1].Auth).Prefix);
        Assert.False(Assert.IsType<FormUrlEncodedBody>(loaded.Tabs[1].Body).Fields[0].Enabled);

        Assert.Equal(ApiKeyLocation.Query, Assert.IsType<ApiKeyAuth>(loaded.Tabs[2].Auth).Location);
        Assert.Equal(MultipartPartKind.File, Assert.IsType<MultipartBody>(loaded.Tabs[2].Body).Parts[0].Kind);

        Assert.False(Assert.IsType<WindowsAuth>(loaded.Tabs[3].Auth).UseCurrentUser);
        Assert.Equal("image/png", Assert.IsType<BinaryFileBody>(loaded.Tabs[3].Body).ContentType);

        Assert.Equal(("DELETE", "7", 5, false, "1.1"),
            (loaded.Tabs[4].Method, loaded.Tabs[4].PathParams[0].Value, loaded.Tabs[4].Settings.TimeoutMs, loaded.Tabs[4].Settings.VerifyTls, loaded.Tabs[4].Settings.HttpVersion));
        Assert.IsType<NoAuth>(loaded.Tabs[4].Auth);
        Assert.IsType<NoBody>(loaded.Tabs[4].Body);
    }

    [Fact]
    public void Missing_file_loads_empty()
    {
        var loaded = new ScratchTabStore(FilePath).Load();

        Assert.Empty(loaded.Tabs);
        Assert.Null(loaded.ActiveTabId);
    }

    [Fact]
    public void Corrupted_file_loads_empty_instead_of_throwing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        Assert.Empty(new ScratchTabStore(FilePath).Load().Tabs);
    }
}
