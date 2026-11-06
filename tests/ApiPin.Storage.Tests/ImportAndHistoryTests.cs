using ApiPin.Core.Models;
using ApiPin.Storage.OpenApi;
using ApiPin.Storage.Postman;

namespace ApiPin.Storage.Tests;

public sealed class ImportAndHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apipin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Postman_export_then_import_keeps_the_collection()
    {
        var original = new RequestCollection
        {
            Name = "Orders",
            Auth = new BearerAuth { Token = "{{token}}" },
            Variables = [new() { Key = "baseUrl", Value = "http://h" }],
            Folders =
            [
                new RequestFolder
                {
                    Name = "Auth",
                    Auth = new BasicAuth { Username = "u", Password = "p" },
                    Requests = [new() { Name = "Login", Method = "POST", Url = "{{baseUrl}}/login", Body = new FormUrlEncodedBody { Fields = [new() { Key = "a", Value = "1" }] } }],
                },
            ],
            Requests =
            [
                new() { Name = "Get", Url = "{{baseUrl}}/orders/:id?x=1", PathParams = [new() { Key = "id", Value = "7" }], QueryParams = [new() { Key = "x", Value = "1" }, new() { Key = "off", Value = "2", Enabled = false }] },
                new() { Name = "Graph", Method = "POST", Url = "{{baseUrl}}/graphql", Body = new GraphQlBody { Query = "{ a }", Variables = "{}" }, Seq = 1 },
            ],
        };

        var result = PostmanImporter.Import(PostmanExporter.Export(original));

        var c = result.Collection!;
        Assert.Empty(result.Warnings);
        Assert.Equal("Orders", c.Name);
        Assert.Equal("{{token}}", Assert.IsType<BearerAuth>(c.Auth).Token);
        Assert.Equal("http://h", c.Variables.Single().Value);
        var folder = c.Folders.Single();
        Assert.Equal("u", Assert.IsType<BasicAuth>(folder.Auth).Username);
        var login = folder.Requests.Single();
        Assert.IsType<InheritAuth>(login.Auth);
        Assert.Equal("1", Assert.IsType<FormUrlEncodedBody>(login.Body).Fields.Single().Value);
        var get = c.Requests.Single(r => r.Name == "Get");
        Assert.Equal("{{baseUrl}}/orders/:id?x=1", get.Url);
        Assert.Equal("7", get.PathParams.Single().Value);
        Assert.Contains(get.QueryParams, q => q.Key == "off" && !q.Enabled);
        Assert.Equal("{ a }", Assert.IsType<GraphQlBody>(c.Requests.Single(r => r.Name == "Graph").Body).Query);
    }

    [Fact]
    public void Postman_scripts_are_reported_and_environments_import()
    {
        const string collection = """
            {
              "info": { "name": "S", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [ { "name": "R", "event": [ { "listen": "test", "script": { "exec": ["pm.test('ok', () => {})"] } } ],
                          "request": { "method": "GET", "url": "https://h/x", "auth": { "type": "awsv4" } } } ]
            }
            """;
        var result = PostmanImporter.Import(collection);
        Assert.Equal(2, result.Warnings.Count);
        Assert.IsType<NoAuth>(result.Collection!.Requests.Single().Auth);

        const string environment = """
            { "name": "Staging", "values": [ { "key": "baseUrl", "value": "https://s", "enabled": true }, { "key": "pwd", "value": "x", "type": "secret", "enabled": false } ] }
            """;
        var env = PostmanImporter.Import(environment).Environment!;
        Assert.Equal("Staging", env.Name);
        Assert.True(env.Variables[1].Secret);
        Assert.False(env.Variables[1].Enabled);

        Assert.Throws<ImportException>(() => PostmanImporter.Import("{\"hello\": 1}"));
    }

    [Fact]
    public void OpenApi_yaml_becomes_folders_requests_and_auth()
    {
        const string yaml = """
            openapi: 3.0.1
            info: { title: Pet Store, version: "1" }
            servers: [ { url: "https://{env}.pets.test/v1", variables: { env: { default: api } } } ]
            components:
              securitySchemes:
                bearer: { type: http, scheme: bearer }
              schemas:
                Pet:
                  type: object
                  required: [name]
                  properties:
                    id: { type: integer, readOnly: true }
                    name: { type: string, example: Rex }
                    tags: { type: array, items: { type: string } }
                    owner: { $ref: '#/components/schemas/Pet' }
            security: [ { bearer: [] } ]
            tags: [ { name: pets, description: Pets } ]
            paths:
              /pets/{petId}:
                get:
                  tags: [pets]
                  summary: Get a pet
                  parameters:
                    - { name: petId, in: path, required: true, schema: { type: integer }, example: 42 }
                    - { name: verbose, in: query, schema: { type: boolean } }
                    - { name: X-Trace, in: header, required: true, schema: { type: string } }
              /pets:
                post:
                  tags: [pets]
                  operationId: createPet
                  requestBody:
                    content:
                      application/json:
                        schema: { $ref: '#/components/schemas/Pet' }
              /health:
                get: { responses: { "200": { description: ok } } }
            """;

        var result = OpenApiImporter.Import(yaml);
        var c = result.Collection!;

        Assert.Equal("Pet Store", c.Name);
        Assert.Equal("https://api.pets.test/v1", c.Variables.Single(v => v.Key == "baseUrl").Value);
        Assert.Equal("{{token}}", Assert.IsType<BearerAuth>(c.Auth).Token);
        Assert.True(c.Variables.Single(v => v.Key == "token").Secret);

        var pets = c.Folders.Single();
        Assert.Equal(("pets", "Pets"), (pets.Name, pets.Description));
        var get = pets.Requests.Single(r => r.Name == "Get a pet");
        Assert.Equal("{{baseUrl}}/pets/:petId", get.Url);
        Assert.Equal("42", get.PathParams.Single().Value);
        Assert.Contains(get.QueryParams, q => q.Key == "verbose" && !q.Enabled);
        Assert.Equal("X-Trace", get.Headers.Single().Key);

        var create = pets.Requests.Single(r => r.Name == "createPet");
        Assert.Equal("POST", create.Method);
        var body = System.Text.Json.JsonDocument.Parse(Assert.IsType<RawBody>(create.Body).Content).RootElement;
        Assert.Equal("Rex", body.GetProperty("name").GetString());
        Assert.False(body.TryGetProperty("id", out _)); // read-only
        Assert.Equal(System.Text.Json.JsonValueKind.Array, body.GetProperty("tags").ValueKind);

        Assert.Equal("GET /health", c.Requests.Single().Name);
        Assert.True(OpenApiImporter.LooksLikeOpenApi(yaml));
    }

    [Fact]
    public void History_keeps_entries_newest_first_and_trims_to_the_limits()
    {
        var path = Path.Combine(_dir, "history.jsonl");
        var store = new HistoryStore(path);
        var settings = new HistorySettings { MaxEntries = 3, MaxBodyKb = 1 };
        for (var i = 0; i < 5; i++)
        {
            var response = new ResponseResult { StatusCode = 200 + i, Body = new byte[2000], SizeBytes = 2000, Request = new SentRequestInfo("GET", $"http://h/{i}", "1.1", [], null) };
            store.Add(HistoryEntry.From(new RequestDefinition { Url = "{{base}}/" + i }, response, settings, null, null, "Local"), settings);
        }
        store.Apply(settings);

        var entries = new HistoryStore(path).Entries;
        Assert.Equal([204, 203, 202], entries.Select(e => e.Status!.Value));
        Assert.Equal("http://h/4", entries[0].Url);
        Assert.Equal("{{base}}/4", entries[0].Request.Url);
        Assert.True(entries[0].BodyTruncated);
        Assert.Equal(1024, entries[0].ToResponse().Body.Length);

        store.Delete(entries[0].Id);
        Assert.Equal(2, new HistoryStore(path).Entries.Count);
        store.Clear();
        Assert.Empty(new HistoryStore(path).Entries);
    }

    [Fact]
    public void History_skips_corrupt_lines_and_can_be_disabled()
    {
        var path = Path.Combine(_dir, "history.jsonl");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json\n");
        var store = new HistoryStore(path);

        store.Add(HistoryEntry.From(new RequestDefinition(), new ResponseResult { StatusCode = 200 }, new HistorySettings(), null, null, null), new HistorySettings { Enabled = false });
        Assert.Empty(store.Entries);

        store.Add(HistoryEntry.From(new RequestDefinition(), new ResponseResult { StatusCode = 200 }, new HistorySettings { SaveBodies = false }, null, null, null), new HistorySettings());
        Assert.Single(new HistoryStore(path).Entries);
        Assert.Null(store.Entries[0].Body);
    }
}
