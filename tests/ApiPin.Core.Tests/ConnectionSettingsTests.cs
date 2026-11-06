using System.Text.RegularExpressions;
using ApiPin.Core.Http;
using ApiPin.Core.Models;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Tests;

public sealed class ConnectionSettingsTests
{
    [Theory]
    [InlineData("*.corp.local", "http://api.corp.local", true)]
    [InlineData("*.corp.local", "https://api.corp.local:8443", true)]
    [InlineData("*.corp.local", "http://corp.local.evil.test", false)]
    [InlineData("10.0.*", "http://10.0.3.4:5000", true)]
    [InlineData("example.test", "http://www.example.test", false)]
    public void Proxy_bypass_wildcards_match_webproxy_inputs(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, Regex.IsMatch(input, HandlerPool.WildcardToRegex(pattern), RegexOptions.IgnoreCase));
    }

    [Fact]
    public void Bypass_entries_split_on_common_separators()
    {
        Assert.Equal(["a", "*.b", "c"], new ProxySettings { Bypass = "a, *.b;c" }.BypassEntries);
    }

    [Fact]
    public void Client_certificate_fields_resolve_variables_and_clone_deeply()
    {
        var request = new RequestDefinition
        {
            Settings = new RequestSettings { ClientCertificate = new() { FilePath = @"{{dir}}\client.pfx", Password = "{{pwd}}" } },
        };
        var ctx = VariableContext.Empty.With(VariableSource.Environment, "e", [new Variable { Key = "dir", Value = @"C:\certs" }, new Variable { Key = "pwd", Value = "x" }]);

        var resolved = VariableResolver.ResolveRequest(request, ctx);

        Assert.Equal((@"C:\certs\client.pfx", "x"), (resolved.Settings.ClientCertificate!.FilePath, resolved.Settings.ClientCertificate.Password));
        Assert.Equal("{{pwd}}", request.Settings.ClientCertificate!.Password);

        var clone = request.Clone();
        clone.Settings.ClientCertificate!.Password = "changed";
        Assert.Equal("{{pwd}}", request.Settings.ClientCertificate.Password);
    }

    [Fact]
    public void Missing_certificate_file_is_a_friendly_error()
    {
        var ex = Assert.Throws<RequestBuildException>(() =>
            ClientCertificateLoader.Load(new ClientCertificateSettings { FilePath = @"C:\nope\missing.pfx" }));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void OAuth_fields_resolve_variables()
    {
        var auth = new OAuth2Auth { TokenUrl = "{{idp}}/token", ClientId = "{{id}}", ClientSecret = "{{secret}}", GrantType = OAuth2GrantType.Password };
        var ctx = VariableContext.Empty.With(VariableSource.Environment, "e",
            [new Variable { Key = "idp", Value = "https://idp" }, new Variable { Key = "id", Value = "app" }, new Variable { Key = "secret", Value = "s" }]);

        var resolved = Assert.IsType<OAuth2Auth>(VariableResolver.ResolveRequest(new RequestDefinition { Auth = auth }, ctx).Auth);

        Assert.Equal(("https://idp/token", "app", "s", OAuth2GrantType.Password), (resolved.TokenUrl, resolved.ClientId, resolved.ClientSecret, resolved.GrantType));
    }
}
