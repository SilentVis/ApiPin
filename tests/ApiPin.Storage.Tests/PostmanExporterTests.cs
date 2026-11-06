using System.Text.Json;
using ApiPin.Core.Models;
using ApiPin.Storage.Postman;

namespace ApiPin.Storage.Tests;

public class PostmanExporterTests
{
    private static JsonElement Export(RequestCollection collection) => JsonDocument.Parse(PostmanExporter.Export(collection)).RootElement;

    private static JsonElement Request(RequestDefinition request)
    {
        var collection = new RequestCollection { Name = "C", Requests = [request] };
        return Export(collection).GetProperty("item")[0];
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() : null;

    [Fact]
    public void Collection_info_and_nested_folders()
    {
        var collection = new RequestCollection
        {
            Name = "Orders API",
            Description = "Docs",
            Folders = [new() { Name = "Auth", Seq = 1, Requests = [new() { Name = "Login" }] }, new() { Name = "Admin", Seq = 0 }],
            Requests = [new() { Name = "B", Seq = 2 }, new() { Name = "A", Seq = 1 }],
        };

        var root = Export(collection);

        var info = root.GetProperty("info");
        Assert.Equal(collection.Id.ToString(), Str(info, "_postman_id"));
        Assert.Equal("Orders API", Str(info, "name"));
        Assert.Equal("Docs", Str(info, "description"));
        Assert.Equal(PostmanExporter.SchemaUrl, Str(info, "schema"));

        var items = root.GetProperty("item").EnumerateArray().ToList();
        Assert.Equal(["Admin", "Auth", "A", "B"], items.Select(i => Str(i, "name")));
        Assert.Equal("Login", Str(items[1].GetProperty("item")[0], "name"));
        Assert.True(items[2].TryGetProperty("request", out _));
        Assert.Equal(0, items[2].GetProperty("response").GetArrayLength());
    }

    [Fact]
    public void Url_is_split_into_parts_with_query_and_path_variables()
    {
        var item = Request(new RequestDefinition
        {
            Method = "get",
            Url = "https://api.example.com:8443/orders/:id/items?expand=lines",
            QueryParams =
            [
                new() { Key = "expand", Value = "lines", Description = "include lines" },
                new() { Key = "debug", Value = "1", Enabled = false },
            ],
            PathParams = [new() { Key = "id", Value = "42" }],
        });

        var request = item.GetProperty("request");
        Assert.Equal("GET", Str(request, "method"));

        var url = request.GetProperty("url");
        Assert.Equal("https://api.example.com:8443/orders/:id/items?expand=lines", Str(url, "raw"));
        Assert.Equal("https", Str(url, "protocol"));
        Assert.Equal(["api", "example", "com"], url.GetProperty("host").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal("8443", Str(url, "port"));
        Assert.Equal(["orders", ":id", "items"], url.GetProperty("path").EnumerateArray().Select(h => h.GetString()));

        var query = url.GetProperty("query");
        Assert.Equal("include lines", Str(query[0], "description"));
        Assert.True(query[1].GetProperty("disabled").GetBoolean());

        var variable = url.GetProperty("variable")[0];
        Assert.Equal(("id", "42"), (Str(variable, "key"), Str(variable, "value")));
    }

    [Fact]
    public void Url_with_variables_and_no_scheme()
    {
        var url = Request(new RequestDefinition { Url = "{{baseUrl}}/health" }).GetProperty("request").GetProperty("url");

        Assert.False(url.TryGetProperty("protocol", out _));
        Assert.Equal("{{baseUrl}}", url.GetProperty("host")[0].GetString());
        Assert.Equal("health", url.GetProperty("path")[0].GetString());
    }

    [Fact]
    public void Headers_keep_disabled_flag()
    {
        var headers = Request(new RequestDefinition
        {
            Headers = [new() { Key = "X-A", Value = "1" }, new() { Key = "X-B", Value = "2", Enabled = false }, new()],
        }).GetProperty("request").GetProperty("header");

        Assert.Equal(2, headers.GetArrayLength());
        Assert.False(headers[0].TryGetProperty("disabled", out _));
        Assert.True(headers[1].GetProperty("disabled").GetBoolean());
    }

    private static Dictionary<string, string?> AuthValues(JsonElement auth, string type) =>
        auth.GetProperty(type).EnumerateArray().ToDictionary(e => Str(e, "key")!, e => Str(e, "value"));

    [Fact]
    public void Auth_types()
    {
        var basic = Request(new() { Auth = new BasicAuth { Username = "u", Password = "p" } }).GetProperty("request").GetProperty("auth");
        Assert.Equal("basic", Str(basic, "type"));
        Assert.Equal(new Dictionary<string, string?> { ["username"] = "u", ["password"] = "p" }, AuthValues(basic, "basic"));

        var bearer = Request(new() { Auth = new BearerAuth { Token = "t" } }).GetProperty("request").GetProperty("auth");
        Assert.Equal("t", AuthValues(bearer, "bearer")["token"]);

        var apiKey = Request(new() { Auth = new ApiKeyAuth { Key = "k", Value = "v", Location = ApiKeyLocation.Query } }).GetProperty("request").GetProperty("auth");
        Assert.Equal(new Dictionary<string, string?> { ["key"] = "k", ["value"] = "v", ["in"] = "query" }, AuthValues(apiKey, "apikey"));

        var ntlm = Request(new() { Auth = new WindowsAuth { UseCurrentUser = false, Username = "u", Domain = "D" } }).GetProperty("request").GetProperty("auth");
        Assert.Equal("D", AuthValues(ntlm, "ntlm")["domain"]);

        var none = Request(new() { Auth = new NoAuth() }).GetProperty("request").GetProperty("auth");
        Assert.Equal("noauth", Str(none, "type"));

        Assert.False(Request(new()).GetProperty("request").TryGetProperty("auth", out _)); // inherit
    }

    [Fact]
    public void Bearer_with_custom_prefix_becomes_a_header()
    {
        var request = Request(new() { Auth = new BearerAuth { Prefix = "Token", Token = "abc" } }).GetProperty("request");

        Assert.Equal("noauth", Str(request.GetProperty("auth"), "type"));
        var header = request.GetProperty("header")[0];
        Assert.Equal(("Authorization", "Token abc"), (Str(header, "key"), Str(header, "value")));
    }

    [Fact]
    public void Body_types()
    {
        var raw = Request(new() { Body = new RawBody { Language = RawLanguage.Xml, Content = "<a/>" } }).GetProperty("request").GetProperty("body");
        Assert.Equal(("raw", "<a/>", "xml"), (Str(raw, "mode"), Str(raw, "raw"), raw.GetProperty("options").GetProperty("raw").GetProperty("language").GetString()));

        var form = Request(new() { Body = new FormUrlEncodedBody { Fields = [new() { Key = "a", Value = "1", Enabled = false }] } }).GetProperty("request").GetProperty("body");
        Assert.Equal("urlencoded", Str(form, "mode"));
        Assert.True(form.GetProperty("urlencoded")[0].GetProperty("disabled").GetBoolean());

        var multipart = Request(new()
        {
            Body = new MultipartBody { Parts = [new() { Name = "t", Value = "v" }, new() { Name = "f", Kind = MultipartPartKind.File, Value = @"C:\a.png", ContentType = "image/png" }] },
        }).GetProperty("request").GetProperty("body");
        Assert.Equal("formdata", Str(multipart, "mode"));
        var parts = multipart.GetProperty("formdata");
        Assert.Equal(("text", "v"), (Str(parts[0], "type"), Str(parts[0], "value")));
        Assert.Equal(("file", @"C:\a.png", "image/png"), (Str(parts[1], "type"), Str(parts[1], "src"), Str(parts[1], "contentType")));

        var binaryRequest = Request(new() { Body = new BinaryFileBody { FilePath = @"C:\b.bin", ContentType = "application/pdf" } }).GetProperty("request");
        Assert.Equal(@"C:\b.bin", binaryRequest.GetProperty("body").GetProperty("file").GetProperty("src").GetString());
        Assert.Equal("application/pdf", Str(binaryRequest.GetProperty("header")[0], "value"));

        Assert.False(Request(new()).GetProperty("request").TryGetProperty("body", out _));
    }

    [Fact]
    public void Settings_map_to_protocol_profile_behavior()
    {
        var item = Request(new() { Settings = new() { FollowRedirects = false, VerifyTls = false } });
        var behavior = item.GetProperty("protocolProfileBehavior");
        Assert.False(behavior.GetProperty("followRedirects").GetBoolean());
        Assert.False(behavior.GetProperty("strictSSL").GetBoolean());

        Assert.False(Request(new()).TryGetProperty("protocolProfileBehavior", out _));
    }

    [Fact]
    public void File_name_follows_postman_convention()
    {
        Assert.Equal("orders-api.postman_collection.json", PostmanExporter.FileName(new RequestCollection { Name = "Orders API" }));
    }

    [Fact]
    public void Collection_and_folder_settings()
    {
        var request = new RequestDefinition { Name = "R", Headers = [new() { Key = "Accept", Value = "mine" }] };
        var folder = new RequestFolder
        {
            Name = "F",
            Auth = new BasicAuth { Username = "u", Password = "p" },
            Headers = [new() { Key = "X-Folder", Value = "f" }, new() { Key = "Accept", Value = "folder" }],
            Requests = [request],
        };
        var collection = new RequestCollection
        {
            Name = "C",
            Auth = new BearerAuth { Token = "{{token}}" },
            Headers = [new() { Key = "X-Tenant", Value = "t" }],
            Variables = [new() { Key = "token", Value = "abc" }, new() { Key = "pwd", Value = "hidden", Secret = true }],
            Folders = [folder],
        };

        var root = Export(collection);

        Assert.Equal("bearer", Str(root.GetProperty("auth"), "type"));
        var vars = root.GetProperty("variable").EnumerateArray().ToList();
        Assert.Equal(("token", "abc"), (Str(vars[0], "key"), Str(vars[0], "value")));
        Assert.Equal(("pwd", "", "secret"), (Str(vars[1], "key"), Str(vars[1], "value"), Str(vars[1], "type")));

        var folderItem = root.GetProperty("item")[0];
        Assert.Equal("basic", Str(folderItem.GetProperty("auth"), "type"));

        var exported = folderItem.GetProperty("item")[0].GetProperty("request");
        Assert.False(exported.TryGetProperty("auth", out _));
        var headers = exported.GetProperty("header").EnumerateArray().Select(h => (Str(h, "key"), Str(h, "value"))).ToList();
        Assert.Equal([("X-Tenant", "t"), ("X-Folder", "f"), ("Accept", "mine")], headers);
    }
}
