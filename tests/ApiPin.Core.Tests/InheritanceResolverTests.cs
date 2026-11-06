using ApiPin.Core.Execution;
using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Tests;

public sealed class InheritanceResolverTests
{
    private static (RequestCollection Collection, RequestFolder Outer, RequestFolder Inner) Tree()
    {
        var inner = new RequestFolder { Name = "Inner" };
        var outer = new RequestFolder { Name = "Outer", Folders = [inner] };
        var collection = new RequestCollection { Name = "API", Folders = [outer] };
        return (collection, outer, inner);
    }

    [Fact]
    public void Inherit_walks_up_to_the_nearest_folder_with_auth()
    {
        var (collection, outer, inner) = Tree();
        collection.Auth = new BasicAuth { Username = "c" };
        outer.Auth = new BearerAuth { Token = "outer" };

        var effective = InheritanceResolver.EffectiveAuth(new InheritAuth(), [collection, outer, inner]);

        Assert.Equal("outer", Assert.IsType<BearerAuth>(effective.Auth).Token);
        Assert.Same(outer, effective.From);
    }

    [Fact]
    public void Inherit_reaches_the_collection_and_falls_back_to_no_auth()
    {
        var (collection, outer, inner) = Tree();

        Assert.IsType<NoAuth>(InheritanceResolver.EffectiveAuth(new InheritAuth(), [collection, outer, inner]).Auth);
        Assert.IsType<NoAuth>(InheritanceResolver.EffectiveAuth(new InheritAuth(), []).Auth);

        collection.Auth = new ApiKeyAuth { Value = "k" };
        var effective = InheritanceResolver.EffectiveAuth(new InheritAuth(), [collection, outer, inner]);
        Assert.IsType<ApiKeyAuth>(effective.Auth);
        Assert.Same(collection, effective.From);
    }

    [Fact]
    public void A_request_with_its_own_auth_keeps_it()
    {
        var (collection, _, _) = Tree();
        collection.Auth = new BasicAuth();

        var effective = InheritanceResolver.EffectiveAuth(new NoAuth(), [collection]);

        Assert.IsType<NoAuth>(effective.Auth);
        Assert.Null(effective.From);
    }

    [Fact]
    public void Headers_merge_with_closer_levels_and_the_request_winning()
    {
        var (collection, outer, inner) = Tree();
        collection.Headers = [new() { Key = "X-Tenant", Value = "c" }, new() { Key = "Accept", Value = "c" }, new() { Key = "X-Off", Value = "1", Enabled = false }];
        outer.Headers = [new() { Key = "x-tenant", Value = "outer" }];
        var request = new RequestDefinition { Headers = [new() { Key = "Accept", Value = "request" }] };

        var inherited = InheritanceResolver.InheritedHeaders(request.Headers, [collection, outer, inner]);
        Assert.Equal(
            [("X-Tenant", true), ("Accept", true), ("x-tenant", false)],
            inherited.Select(h => (h.Header.Key, h.Overridden)));

        var applied = InheritanceResolver.Apply(request, [collection, outer, inner]);
        Assert.Equal([("x-tenant", "outer"), ("Accept", "request")], applied.Headers.Select(h => (h.Key, h.Value)));
        Assert.Single(request.Headers); // original untouched
    }

    [Fact]
    public void Folder_variables_win_over_collection_variables_but_not_environment()
    {
        var (collection, outer, inner) = Tree();
        collection.Variables = [new() { Key = "v", Value = "collection" }, new() { Key = "c", Value = "only-collection" }];
        outer.Variables = [new() { Key = "v", Value = "outer" }];
        inner.Variables = [new() { Key = "v", Value = "inner" }];

        var ctx = InheritanceResolver.WithFolderVariables(VariableContext.Empty, [collection, outer, inner]);
        Assert.Equal("inner/only-collection", VariableResolver.Resolve("{{v}}/{{c}}", ctx));

        var withEnv = InheritanceResolver.WithFolderVariables(
            VariableContext.Empty.With(VariableSource.Environment, "Local", [new Variable { Key = "v", Value = "env" }]),
            [collection, outer, inner]);
        Assert.Equal("env", VariableResolver.Resolve("{{v}}", withEnv));
    }

    [Fact]
    public void Prepare_applies_inheritance_then_variables()
    {
        var (collection, outer, _) = Tree();
        collection.Variables = [new() { Key = "token", Value = "abc" }, new() { Key = "base", Value = "http://h" }];
        collection.Auth = new BearerAuth { Token = "{{token}}" };
        outer.Headers = [new() { Key = "X-Base", Value = "{{base}}" }];
        var request = new RequestDefinition { Url = "{{base}}/x/{{missing}}" };

        var prepared = RequestPreparer.Prepare(request, [collection, outer], VariableContext.Empty);

        Assert.Equal("http://h/x/{{missing}}", prepared.Request.Url);
        Assert.Equal("abc", Assert.IsType<BearerAuth>(prepared.Request.Auth).Token);
        Assert.Equal("http://h", prepared.Request.Headers.Single(h => h.Key == "X-Base").Value);
        Assert.Equal(["missing"], prepared.Report.Unresolved);
        Assert.IsType<InheritAuth>(request.Auth);
    }
}
