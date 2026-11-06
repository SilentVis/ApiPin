using System.Text;
using System.Text.Json;
using ApiPin.Core.Models;
using ApiPin.Core.Testing;
using ApiPin.Core.Variables;

namespace ApiPin.Core.Tests;

public sealed class TestingTests
{
    private static readonly JsonElement Doc = JsonDocument.Parse("""
        { "data": { "items": [ { "id": 1, "name": "a", "tags": ["x"] }, { "id": 2, "name": "b" } ], "total": 2 },
          "my-key": true, "token": "abc" }
        """).RootElement;

    [Theory]
    [InlineData("$.data.total", "2")]
    [InlineData("data.total", "2")]
    [InlineData("$.data.items[0].name", "a")]
    [InlineData("$.data.items[-1].id", "2")]
    [InlineData("$['my-key']", "true")]
    [InlineData("$.data.items[0].tags", "[\"x\"]")]
    [InlineData("$..name", "a")]
    public void JsonPath_selects(string path, string expected)
    {
        Assert.Equal(expected, JsonPath.Text(JsonPath.Select(Doc, path)[0]));
    }

    [Fact]
    public void JsonPath_wildcards_recursion_and_missing()
    {
        Assert.Equal(["1", "2"], JsonPath.Select(Doc, "$.data.items[*].id").Select(JsonPath.Text));
        Assert.Equal(["1", "2"], JsonPath.Select(Doc, "$..id").Select(JsonPath.Text));
        Assert.Empty(JsonPath.Select(Doc, "$.nope.deeper"));
        Assert.Throws<JsonPathException>(() => JsonPath.Select(Doc, "$.data.items[?(@.id==1)]"));
    }

    [Fact]
    public void Json_schema_validation_reports_paths()
    {
        var schema = JsonDocument.Parse("""
            { "type": "object", "required": ["data"], "properties": { "data": { "$ref": "#/$defs/data" } },
              "$defs": { "data": { "type": "object", "required": ["items", "total"],
                "properties": { "total": { "type": "integer", "minimum": 3 },
                                "items": { "type": "array", "items": { "type": "object", "required": ["id", "name"], "additionalProperties": false,
                                                                     "properties": { "id": { "type": "integer" }, "name": { "type": "string", "maxLength": 1 } } } } } } } }
            """).RootElement;

        var errors = JsonSchemaValidator.Validate(Doc, schema);

        Assert.Contains(errors, e => e.StartsWith("$.data.total: 2 is less than 3"));
        Assert.Contains(errors, e => e.Contains("$.data.items[0]: unexpected property 'tags'"));
        Assert.Equal(2, errors.Count);
        Assert.Empty(JsonSchemaValidator.Validate(JsonDocument.Parse("{\"a\":\"x\"}").RootElement,
            JsonDocument.Parse("{\"properties\":{\"a\":{\"enum\":[\"x\",\"y\"]}},\"required\":[\"a\"]}").RootElement));
    }

    private static ResponseResult Response(int status, string body, params (string, string)[] headers) => new()
    {
        StatusCode = status,
        Body = Encoding.UTF8.GetBytes(body),
        ContentType = "application/json",
        Headers = headers.Select(h => KeyValuePair.Create(h.Item1, h.Item2)).ToList(),
        Timings = new TimingInfo(TimeSpan.FromMilliseconds(120), TimeSpan.Zero, TimeSpan.Zero),
    };

    [Fact]
    public void Assertions_cover_status_headers_jsonpath_time_body_and_schema()
    {
        var request = new RequestDefinition
        {
            Assertions =
            [
                new() { Kind = AssertionKind.Status, Operator = AssertionOperator.Equals, Expected = "201" },
                new() { Kind = AssertionKind.Status, Operator = AssertionOperator.Between, Expected = "200-299" },
                new() { Kind = AssertionKind.Header, Target = "content-type", Operator = AssertionOperator.Contains, Expected = "json" },
                new() { Kind = AssertionKind.Header, Target = "X-Missing", Operator = AssertionOperator.NotExists },
                new() { Kind = AssertionKind.JsonPath, Target = "$.id", Operator = AssertionOperator.Equals, Expected = "{{expectedId}}" },
                new() { Kind = AssertionKind.JsonPath, Target = "$.tags", Operator = AssertionOperator.Equals, Expected = "[ \"a\", \"b\" ]" },
                new() { Kind = AssertionKind.ResponseTime, Operator = AssertionOperator.LessThan, Expected = "500" },
                new() { Kind = AssertionKind.Body, Operator = AssertionOperator.Matches, Expected = "\"id\":\\s*7" },
                new() { Kind = AssertionKind.JsonSchema, Expected = "{ \"required\": [\"id\"] }" },
                new() { Kind = AssertionKind.JsonPath, Target = "$.id", Operator = AssertionOperator.GreaterThan, Expected = "10" },
                new() { Kind = AssertionKind.Status, Enabled = false, Expected = "999" },
            ],
        };
        var vars = VariableContext.Empty.With(VariableSource.Runtime, null, new Dictionary<string, string> { ["expectedId"] = "7" });

        var report = ResponseTester.Run(request, Response(201, "{\"id\": 7, \"tags\": [\"a\",\"b\"]}", ("Content-Type", "application/json")), vars);

        Assert.Equal(10, report.Assertions.Count);
        Assert.Equal(9, report.Passed);
        var failed = Assert.Single(report.Assertions, a => !a.Passed);
        Assert.Equal("$.id > 10", failed.Description);
        Assert.Equal("Got 7.", failed.Message);
        Assert.False(report.AllPassed);
    }

    [Fact]
    public void Captures_from_jsonpath_header_regex_and_status()
    {
        var request = new RequestDefinition
        {
            Captures =
            [
                new() { Variable = "token", Expression = "$.token" },
                new() { Variable = "loc", Source = CaptureSource.Header, Expression = "location" },
                new() { Variable = "num", Source = CaptureSource.Regex, Expression = "\"id\":\\s*(\\d+)" },
                new() { Variable = "status", Source = CaptureSource.Status },
                new() { Variable = "missing", Expression = "$.nope" },
            ],
        };

        var report = ResponseTester.Run(request, Response(200, "{\"token\":\"t-1\",\"id\": 42}", ("Location", "/items/42")));

        Assert.Equal(["t-1", "/items/42", "42", "200", null], report.Captures.Select(c => c.Value));
        Assert.False(report.Captures[^1].Succeeded);
        Assert.Equal(1, report.Failed);

        var vars = new RunVariables { Environment = new EnvironmentDefinition { Name = "Local" } };
        vars.Apply(report.Captures);
        Assert.Equal("t-1", vars.Runtime["token"]);
        Assert.Equal("t-1", VariableResolver.Resolve("{{token}}", vars.Context()));
    }

    [Fact]
    public void Environment_captures_update_the_environment()
    {
        var env = new EnvironmentDefinition { Name = "Local", Variables = [new() { Key = "token", Value = "old" }] };
        EnvironmentDefinition? saved = null;
        var vars = new RunVariables { Environment = env, EnvironmentChanged = e => saved = e };
        vars.Runtime["token"] = "stale";

        vars.Apply([new CaptureResult(new CaptureRule { Variable = "token", Target = CaptureTarget.Environment }, "new", null)]);

        Assert.Equal("new", env.Variables.Single().Value);
        Assert.Same(env, saved);
        Assert.False(vars.Runtime.ContainsKey("token"));
    }

    [Fact]
    public void Failed_responses_fail_their_assertions()
    {
        var request = new RequestDefinition { Assertions = [new() { Expected = "200" }] };
        var report = ResponseTester.Run(request, ResponseResult.Failed(new ExecutionError(ErrorKind.ConnectionRefused, "Connection refused", "x"), null, TimingInfo.Zero));
        Assert.Equal("Connection refused", Assert.Single(report.Assertions).Message);
    }
}
