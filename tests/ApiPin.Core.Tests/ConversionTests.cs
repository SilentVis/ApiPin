using System.Text.Json;
using ApiPin.Core.Conversion;
using ApiPin.Core.Http;
using ApiPin.Core.Models;

namespace ApiPin.Core.Tests;

public sealed class ConversionTests
{
    [Fact]
    public void Parses_a_bash_curl_with_headers_json_body_and_flags()
    {
        var request = CurlParser.Parse("""
            curl -X PUT 'https://api.test/orders/7?expand=items' \
              -H 'Content-Type: application/json' \
              -H "X-Trace: a b" \
              --data-raw '{"status":"shipped"}' -k -L --compressed
            """);

        Assert.Equal("PUT", request.Method);
        Assert.Equal("https://api.test/orders/7?expand=items", request.Url);
        Assert.Equal("items", request.QueryParams.Single().Value);
        Assert.Equal(("X-Trace", "a b"), (request.Headers.Single().Key, request.Headers.Single().Value)); // Content-Type implied by the JSON body
        var body = Assert.IsType<RawBody>(request.Body);
        Assert.Equal((RawLanguage.Json, "{\"status\":\"shipped\"}"), (body.Language, body.Content));
        Assert.False(request.Settings.VerifyTls);
        Assert.True(request.Settings.FollowRedirects);
    }

    [Fact]
    public void Defaults_to_post_with_form_data_and_reads_basic_and_bearer_auth()
    {
        var form = CurlParser.Parse("curl https://h/login -d user=ada -d 'pass=a%26b' -u admin:pw");
        Assert.Equal("POST", form.Method);
        var fields = Assert.IsType<FormUrlEncodedBody>(form.Body).Fields;
        Assert.Equal([("user", "ada"), ("pass", "a&b")], fields.Select(f => (f.Key, f.Value)));
        Assert.Equal(("admin", "pw"), (((BasicAuth)form.Auth).Username, ((BasicAuth)form.Auth).Password));

        var bearer = CurlParser.Parse("curl https://h/me -H 'Authorization: Bearer abc.def'");
        Assert.Equal("GET", bearer.Method);
        Assert.Equal("abc.def", Assert.IsType<BearerAuth>(bearer.Auth).Token);
        Assert.Empty(bearer.Headers);
    }

    [Fact]
    public void Parses_multipart_files_and_get_with_data()
    {
        var upload = CurlParser.Parse("curl -F 'title=Hi' -F 'photo=@C:/pics/cat.png;type=image/png' https://h/upload");
        var parts = Assert.IsType<MultipartBody>(upload.Body).Parts;
        Assert.Equal(MultipartPartKind.File, parts[1].Kind);
        Assert.Equal(("C:/pics/cat.png", "image/png"), (parts[1].Value, parts[1].ContentType));

        var search = CurlParser.Parse("curl -G https://h/search --data-urlencode 'q=a b' -d page=2");
        Assert.Equal("GET", search.Method);
        Assert.Equal("https://h/search?q=a%20b&page=2", search.Url);
    }

    [Fact]
    public void Parses_chromes_copy_as_curl_cmd_format()
    {
        var request = CurlParser.Parse("curl ^\"https://api.test/items^\" ^\n  -H ^\"accept: application/json^\" ^\n  --data-raw ^\"^{^\\^\"name^\\^\":^\\^\"x^\\^\"^}^\"");

        Assert.Equal("https://api.test/items", request.Url);
        Assert.Equal("accept", request.Headers.Single().Key);
        Assert.Equal("{\"name\":\"x\"}", Assert.IsType<RawBody>(request.Body).Content);
    }

    [Fact]
    public void Rejects_text_that_is_not_curl()
    {
        Assert.Throws<CurlParseException>(() => CurlParser.Parse("wget https://h"));
        Assert.Throws<CurlParseException>(() => CurlParser.Parse("curl -H 'a: b'"));
        Assert.False(CurlParser.LooksLikeCurl("https://h"));
    }

    private static RequestDefinition Sample() => new()
    {
        Method = "POST",
        Url = "https://api.test/orders?x=1",
        Headers = [new() { Key = "X-Trace", Value = "it's" }],
        Auth = new BearerAuth { Token = "tok" },
        Body = new RawBody { Content = "{\"a\":\"b\"}" },
    };

    [Fact]
    public void Curl_snippet_round_trips_through_the_parser()
    {
        var snippet = CodeSnippets.Generate(Sample(), SnippetLanguage.Curl);
        var parsed = CurlParser.Parse(snippet);

        Assert.Equal("POST", parsed.Method);
        Assert.Equal("https://api.test/orders?x=1", parsed.Url);
        Assert.Equal("it's", parsed.Headers.Single(h => h.Key == "X-Trace").Value);
        Assert.Equal("tok", Assert.IsType<BearerAuth>(parsed.Auth).Token);
        Assert.Equal("{\"a\":\"b\"}", Assert.IsType<RawBody>(parsed.Body).Content);

        var cmd = CurlParser.Parse(CodeSnippets.Generate(Sample(), SnippetLanguage.CurlCmd).Replace(" ^\n", " "));
        Assert.Equal("{\"a\":\"b\"}", Assert.IsType<RawBody>(cmd.Body).Content);
    }

    [Theory]
    [InlineData(SnippetLanguage.PowerShell, "Invoke-RestMethod", "-Method POST")]
    [InlineData(SnippetLanguage.CSharp, "new HttpRequestMessage(HttpMethod.Post", "Bearer tok")]
    [InlineData(SnippetLanguage.JavaScript, "await fetch(\"https://api.test/orders?x=1\"", "\"Authorization\": \"Bearer tok\"")]
    [InlineData(SnippetLanguage.Python, "requests.request(\"POST\"", "\"X-Trace\": \"it's\"")]
    public void Snippets_include_method_url_headers_and_auth(SnippetLanguage language, string expected1, string expected2)
    {
        var code = CodeSnippets.Generate(Sample(), language);
        Assert.Contains(expected1, code);
        Assert.Contains(expected2, code);
        Assert.DoesNotContain("ApiPin/", code); // the default user agent is left out
    }

    [Fact]
    public void GraphQl_body_is_sent_as_json()
    {
        var body = new GraphQlBody { Query = "query($id: ID!) { order(id: $id) { id } }", Variables = "{ \"id\": 7, }", OperationName = "Q" };

        var json = JsonDocument.Parse(HttpRequestBuilder.GraphQlJson(body)).RootElement;

        Assert.Equal(body.Query, json.GetProperty("query").GetString());
        Assert.Equal(7, json.GetProperty("variables").GetProperty("id").GetInt32());
        Assert.Equal("Q", json.GetProperty("operationName").GetString());

        using var message = HttpRequestBuilder.Build(new RequestDefinition { Method = "POST", Url = "http://h/graphql", Body = body });
        Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
        Assert.Throws<RequestBuildException>(() => HttpRequestBuilder.GraphQlJson(new GraphQlBody { Query = "{a}", Variables = "{nope" }));
    }
}
