using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Tests;

public sealed class VariableResolverTests
{
    private static VariableContext Context(VariableSource source, params (string Key, string Value)[] vars) =>
        VariableContext.Empty.With(source, source.ToString(), vars.Select(v => new Variable { Key = v.Key, Value = v.Value }));

    [Fact]
    public void Replaces_known_variables_and_leaves_unknown_ones()
    {
        var ctx = Context(VariableSource.Environment, ("baseUrl", "https://localhost:5001"), ("id", "42"));
        var report = new ResolveReport();

        var result = VariableResolver.Resolve("{{baseUrl}}/orders/{{ id }}?x={{missing}}", ctx, report);

        Assert.Equal("https://localhost:5001/orders/42?x={{missing}}", result);
        Assert.Equal(["baseUrl", "id"], report.Used.Order());
        Assert.Equal(["missing"], report.Unresolved);
    }

    [Fact]
    public void Higher_scopes_win()
    {
        var ctx = VariableContext.Empty
            .With(VariableSource.Global, "Globals", [new Variable { Key = "host", Value = "global" }, new Variable { Key = "g", Value = "1" }])
            .With(VariableSource.Collection, "Orders", [new Variable { Key = "host", Value = "collection" }])
            .With(VariableSource.Environment, "Local", [new Variable { Key = "host", Value = "env" }])
            .With(VariableSource.Runtime, null, [new Variable { Key = "token", Value = "abc" }]);

        Assert.Equal("env/1/abc", VariableResolver.Resolve("{{host}}/{{g}}/{{token}}", ctx));
        Assert.Equal(VariableSource.Environment, ctx.Lookup("host")!.Source);

        var host = ctx.Effective().Single(e => e.Variable.Name == "host");
        Assert.Equal(["collection", "global"], host.Shadowed.Select(s => s.Value));
    }

    [Fact]
    public void Inner_folders_win_over_outer_folders()
    {
        var ctx = VariableContext.Empty
            .With(VariableSource.Folder, "inner", [new Variable { Key = "v", Value = "inner" }])
            .With(VariableSource.Folder, "outer", [new Variable { Key = "v", Value = "outer" }])
            .With(VariableSource.Collection, "c", [new Variable { Key = "v", Value = "collection" }]);

        Assert.Equal("inner", VariableResolver.Resolve("{{v}}", ctx));
    }

    [Fact]
    public void Disabled_and_blank_variables_are_ignored()
    {
        var ctx = VariableContext.Empty.With(VariableSource.Environment, "e",
        [
            new Variable { Key = "a", Value = "1", Enabled = false },
            new Variable { Key = "  ", Value = "2" },
        ]);

        Assert.Equal("{{a}}", VariableResolver.Resolve("{{a}}", ctx));
        Assert.Empty(ctx.Effective());
    }

    [Fact]
    public void Resolves_nested_values()
    {
        var ctx = Context(VariableSource.Environment, ("baseUrl", "{{scheme}}://{{host}}/v{{version}}"), ("scheme", "https"), ("host", "api.test"), ("version", "2"));

        Assert.Equal("https://api.test/v2/orders", VariableResolver.Resolve("{{baseUrl}}/orders", ctx));
    }

    [Fact]
    public void Stops_at_cycles_and_reports_them()
    {
        var ctx = Context(VariableSource.Environment, ("a", "x{{b}}"), ("b", "y{{a}}"), ("self", "{{self}}"));
        var report = new ResolveReport();

        Assert.Equal("xy{{a}}", VariableResolver.Resolve("{{a}}", ctx, report));
        Assert.Equal("{{self}}", VariableResolver.Resolve("{{self}}", ctx, report));
        Assert.Contains("a", report.Cyclic);
        Assert.Contains("self", report.Cyclic);
        Assert.True(report.HasProblems);
    }

    [Fact]
    public void Stops_at_the_depth_limit()
    {
        var vars = Enumerable.Range(0, VariableResolver.MaxDepth + 5).Select(i => ($"v{i}", $"{{{{v{i + 1}}}}}")).ToArray();
        var ctx = Context(VariableSource.Environment, vars);
        var report = new ResolveReport();

        var result = VariableResolver.Resolve("{{v0}}", ctx, report);

        Assert.StartsWith("{{v", result);
        Assert.NotEmpty(report.Cyclic);
    }

    [Fact]
    public void Generates_dynamic_values_for_each_occurrence()
    {
        var report = new ResolveReport();
        var result = VariableResolver.Resolve("{{$guid}} {{$guid}} {{$timestamp}} {{$isoTimestamp}} {{$randomInt}} {{$randomEmail}}", VariableContext.Empty, report);

        var parts = result.Split(' ');
        Assert.True(Guid.TryParse(parts[0], out _));
        Assert.NotEqual(parts[0], parts[1]);
        Assert.InRange(long.Parse(parts[2]), DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
        Assert.True(DateTimeOffset.TryParse(parts[3], out _));
        Assert.InRange(int.Parse(parts[4]), 0, 1000);
        Assert.EndsWith("@example.com", parts[5]);
        Assert.Empty(report.Unresolved);
    }

    [Fact]
    public void A_defined_variable_wins_over_a_dynamic_one()
    {
        var ctx = Context(VariableSource.Global, ("$guid", "fixed"));
        Assert.Equal("fixed", VariableResolver.Resolve("{{$guid}}", ctx));
    }

    [Fact]
    public void Finds_references_with_positions()
    {
        var refs = VariableResolver.FindReferences("a{{x}}b{{ $guid }}{{not closed").ToList();

        Assert.Equal(2, refs.Count);
        Assert.Equal(new VariableReference("x", 1, 5), refs[0]);
        Assert.Equal("$guid", refs[1].Name);
        Assert.True(refs[1].IsDynamic);
        Assert.False(VariableResolver.HasReferences("{{}} {{a b}}"));
    }

    [Fact]
    public void Resolves_every_part_of_a_request()
    {
        var ctx = Context(VariableSource.Environment, ("base", "http://h"), ("id", "7"), ("tok", "secret"), ("user", "bob"), ("file", @"C:\f.txt"));
        var request = new RequestDefinition
        {
            Url = "{{base}}/items/:id?q={{id}}",
            QueryParams = [new() { Key = "q", Value = "{{id}}" }, new() { Key = "off", Value = "{{id}}", Enabled = false }],
            PathParams = [new() { Key = "id", Value = "{{id}}" }],
            Headers = [new() { Key = "X-{{user}}", Value = "{{tok}}" }],
            Auth = new BasicAuth { Username = "{{user}}", Password = "{{tok}}" },
            Body = new MultipartBody
            {
                Parts =
                [
                    new() { Name = "n", Value = "{{user}}" },
                    new() { Name = "f", Kind = MultipartPartKind.File, Value = "{{file}}" },
                ],
            },
        };

        var resolved = VariableResolver.ResolveRequest(request, ctx);

        Assert.Equal("http://h/items/:id?q=7", resolved.Url);
        Assert.Equal("7", resolved.QueryParams[0].Value);
        Assert.Equal("{{id}}", resolved.QueryParams[1].Value); // disabled rows are left alone
        Assert.Equal("7", resolved.PathParams[0].Value);
        Assert.Equal(("X-bob", "secret"), (resolved.Headers[0].Key, resolved.Headers[0].Value));
        Assert.Equal(("bob", "secret"), (((BasicAuth)resolved.Auth).Username, ((BasicAuth)resolved.Auth).Password));
        var parts = ((MultipartBody)resolved.Body).Parts;
        Assert.Equal("bob", parts[0].Value);
        Assert.Equal(@"C:\f.txt", parts[1].Value);

        // The original is untouched.
        Assert.Equal("{{user}}", ((BasicAuth)request.Auth).Username);
        Assert.Equal(request.Id, resolved.Id);
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("apikey")]
    [InlineData("windows")]
    [InlineData("raw")]
    [InlineData("form")]
    [InlineData("binary")]
    public void Resolves_auth_and_body_types(string kind)
    {
        var ctx = Context(VariableSource.Environment, ("v", "X"));
        var request = new RequestDefinition();
        switch (kind)
        {
            case "bearer": request.Auth = new BearerAuth { Token = "{{v}}", Prefix = "{{v}}" }; break;
            case "apikey": request.Auth = new ApiKeyAuth { Key = "{{v}}", Value = "{{v}}" }; break;
            case "windows": request.Auth = new WindowsAuth { UseCurrentUser = false, Username = "{{v}}", Password = "{{v}}", Domain = "{{v}}" }; break;
            case "raw": request.Body = new RawBody { Content = "{\"a\":\"{{v}}\"}" }; break;
            case "form": request.Body = new FormUrlEncodedBody { Fields = [new() { Key = "{{v}}", Value = "{{v}}" }] }; break;
            case "binary": request.Body = new BinaryFileBody { FilePath = "{{v}}.bin", ContentType = "{{v}}/y" }; break;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(VariableResolver.ResolveRequest(request, ctx));

        Assert.DoesNotContain("{{v}}", json);
        Assert.Contains("X", json);
    }
}
