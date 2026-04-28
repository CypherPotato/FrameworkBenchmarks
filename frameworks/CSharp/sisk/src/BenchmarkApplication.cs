using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Npgsql;
using NpgsqlTypes;
using Sisk.Core.Http;
using Sisk.Core.Routing;

namespace SiskBenchmarks;

internal static class BenchmarkApplication
{
    private const string DbConnection = "Server=tfb-database;Database=hello_world;User Id=benchmarkdbuser;Password=benchmarkdbpass;SSL Mode=Disable;Maximum Pool Size=18;Enlist=false;Max Auto Prepare=4;Multiplexing=true;Write Coalescing Buffer Threshold Bytes=1000;";
    private const string SelectWorldSql = "SELECT id, randomnumber FROM world WHERE id = $1";
    private const string UpdateWorldSql = "UPDATE world w SET randomnumber = u.new_val FROM (SELECT unnest($1) as id, unnest($2) as new_val) u WHERE w.id = u.id";

    private static readonly byte[] PlainTextBytes = "Hello, World!"u8.ToArray();
    private static readonly byte[] JsonBytes = """{"message":"Hello, World!"}"""u8.ToArray();
    private static readonly NpgsqlDataSource DataSource = CreateDataSource();
    private static readonly World?[] CachedWorlds = new World?[10001];
    private static readonly HtmlEncoder HtmlEncoder = CreateHtmlEncoder();
    private static readonly Comparison<Fortune> FortuneSortComparison = (a, b) => string.CompareOrdinal(a.Message, b.Message);

    public static void Map(Router router)
    {
        router.SetRoute(RouteMethod.Get, "/plaintext", Plaintext);
        router.SetRoute(RouteMethod.Get, "/json", Json);
        router.SetRoute(RouteMethod.Get, "/db", SingleQuery);
        router.SetRoute(RouteMethod.Get, "/queries", MultipleQueries);
        router.SetRoute(RouteMethod.Get, "/queries/<queries>", MultipleQueries);
        router.SetRoute(RouteMethod.Get, "/cached-worlds", CachedQueries);
        router.SetRoute(RouteMethod.Get, "/cached-worlds/<queries>", CachedQueries);
        router.SetRoute(RouteMethod.Get, "/fortunes", Fortunes);
        router.SetRoute(RouteMethod.Get, "/updates", Updates);
        router.SetRoute(RouteMethod.Get, "/updates/<queries>", Updates);
    }

    private static HttpResponse Plaintext(HttpRequest request) => WriteBytes(request, PlainTextBytes, "text/plain");

    private static HttpResponse Json(HttpRequest request) => WriteBytes(request, JsonBytes, "application/json");

    private static async Task<HttpResponse> SingleQuery(HttpRequest request)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        using var command = CreateReadCommand(connection, out var idParameter);

        idParameter.TypedValue = Random.Shared.Next(1, 10001);
        var world = await ReadSingleRow(command);

        return WriteJson(request, world, BenchmarkJsonContext.Default.World);
    }

    private static async Task<HttpResponse> MultipleQueries(HttpRequest request)
    {
        var count = ParseQueryCount(request);
        var worlds = new World[count];

        await using var connection = await DataSource.OpenConnectionAsync();
        using var command = CreateReadCommand(connection, out var idParameter);

        for (var i = 0; i < count; i++)
        {
            idParameter.TypedValue = Random.Shared.Next(1, 10001);
            worlds[i] = await ReadSingleRow(command);
        }

        return WriteJson(request, worlds, BenchmarkJsonContext.Default.WorldArray);
    }

    private static async Task<HttpResponse> CachedQueries(HttpRequest request)
    {
        var count = ParseQueryCount(request);
        var worlds = new World[count];

        NpgsqlConnection? connection = null;
        NpgsqlCommand? command = null;
        NpgsqlParameter<int>? idParameter = null;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var id = Random.Shared.Next(1, 10001);
                var world = CachedWorlds[id];

                if (world is null)
                {
                    connection ??= await DataSource.OpenConnectionAsync();
                    command ??= CreateReadCommand(connection, out idParameter);

                    idParameter!.TypedValue = id;
                    world = await ReadSingleRow(command);
                    CachedWorlds[id] = world;
                }

                worlds[i] = world;
            }
        }
        finally
        {
            command?.Dispose();

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }

        return WriteJson(request, worlds, BenchmarkJsonContext.Default.WorldArray);
    }

    private static async Task<HttpResponse> Updates(HttpRequest request)
    {
        var count = ParseQueryCount(request);
        var worlds = new World[count];
        var ids = new int[count];
        var randomNumbers = new int[count];

        for (var i = 0; i < count; i++)
        {
            ids[i] = Random.Shared.Next(1, 10001);
        }

        Array.Sort(ids);

        for (var i = 1; i < count; i++)
        {
            if (ids[i] == ids[i - 1])
            {
                ids[i] = ids[i] % 10000 + 1;
            }
        }

        await using var connection = await DataSource.OpenConnectionAsync();
        using var readCommand = CreateReadCommand(connection, out var idParameter);

        for (var i = 0; i < count; i++)
        {
            idParameter.TypedValue = ids[i];
            worlds[i] = await ReadSingleRow(readCommand);
        }

        for (var i = 0; i < count; i++)
        {
            var randomNumber = Random.Shared.Next(1, 10001);

            if (worlds[i].RandomNumber == randomNumber)
            {
                randomNumber = randomNumber % 10000 + 1;
            }

            worlds[i].RandomNumber = randomNumber;
            randomNumbers[i] = randomNumber;
        }

        using var updateCommand = new NpgsqlCommand(UpdateWorldSql, connection);
        updateCommand.Parameters.Add(new NpgsqlParameter<int[]> { TypedValue = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        updateCommand.Parameters.Add(new NpgsqlParameter<int[]> { TypedValue = randomNumbers, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });

        await updateCommand.ExecuteNonQueryAsync();

        return WriteJson(request, worlds, BenchmarkJsonContext.Default.WorldArray);
    }

    private static async Task<HttpResponse> Fortunes(HttpRequest request)
    {
        var fortunes = new List<Fortune>(16);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT id, message FROM fortune", connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        fortunes.Add(new Fortune(0, "Additional fortune added at request time."));
        fortunes.Sort(FortuneSortComparison);

        return WriteBytes(request, Encoding.UTF8.GetBytes(RenderFortunes(fortunes)), "text/html; charset=utf-8");
    }

    private static string RenderFortunes(List<Fortune> fortunes)
    {
        var html = new StringBuilder(512 + fortunes.Count * 128);

        html.Append("<!DOCTYPE html><html><head><title>Fortunes</title></head><body><table><tr><th>id</th><th>message</th></tr>");

        for (var i = 0; i < fortunes.Count; i++)
        {
            var fortune = fortunes[i];
            html.Append("<tr><td>");
            html.Append(fortune.Id);
            html.Append("</td><td>");
            html.Append(HtmlEncoder.Encode(fortune.Message));
            html.Append("</td></tr>");
        }

        html.Append("</table></body></html>");

        return html.ToString();
    }

    private static NpgsqlCommand CreateReadCommand(NpgsqlConnection connection, out NpgsqlParameter<int> idParameter)
    {
        var command = new NpgsqlCommand(SelectWorldSql, connection);
        idParameter = new NpgsqlParameter<int> { NpgsqlDbType = NpgsqlDbType.Integer };
        command.Parameters.Add(idParameter);

        return command;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static async Task<World> ReadSingleRow(NpgsqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
        await reader.ReadAsync();

        return new World
        {
            Id = reader.GetInt32(0),
            RandomNumber = reader.GetInt32(1)
        };
    }

    private static HttpResponse WriteJson<T>(HttpRequest request, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => WriteBytes(request, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), "application/json");

    private static HttpResponse WriteBytes(HttpRequest request, byte[] bytes, string contentType)
    {
        var response = request.GetResponseStream();

        response.SetStatus(200);
        response.SetHeader(HttpKnownHeaderNames.ContentType, contentType);
        response.SetContentLength(bytes.Length);
        response.Write(bytes);

        return response.Close();
    }

    private static int ParseQueryCount(HttpRequest request)
    {
        var value = request.RouteParameters["queries"].GetString();

        if (string.IsNullOrEmpty(value))
        {
            value = request.Query["queries"].GetString();
        }

        return int.TryParse(value, out var count) ? Math.Clamp(count, 1, 500) : 1;
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION") ?? DbConnection;

        return new NpgsqlSlimDataSourceBuilder(connectionString).EnableArrays().Build();
    }

    private static HtmlEncoder CreateHtmlEncoder()
    {
        var settings = new TextEncoderSettings(UnicodeRanges.BasicLatin, UnicodeRanges.Katakana, UnicodeRanges.Hiragana);
        settings.AllowCharacter('\u2014');

        return HtmlEncoder.Create(settings);
    }
}

internal sealed class World
{
    public int Id { get; set; }
    public int RandomNumber { get; set; }
}

internal sealed record Fortune(int Id, string Message);

[JsonSerializable(typeof(World))]
[JsonSerializable(typeof(World[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
