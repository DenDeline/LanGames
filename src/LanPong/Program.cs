using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanPong;
using LanPong.Bots.Catalog;
using LanPong.Bots.Configuration;
using LanPong.Bots.Runtime;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddConfiguredBots(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
builder.Services.AddSingleton<PongPeer>();
builder.Services.AddHostedService(services => services.GetRequiredService<PongPeer>());
var app = builder.Build();
// Freeze the startup configuration even before gameplay starts consuming the catalog.
_ = app.Services.GetRequiredService<BotCatalog>();
var peer = app.Services.GetRequiredService<PongPeer>();
var bots = app.Services.GetRequiredService<BotRuntime>();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", () => peer.Snapshot());
app.MapGet("/api/bots", () => bots.DescribeCatalog());

app.MapPost("/api/quick", async (QuickGameRequest request) =>
{
    try
    {
        await peer.QuickGameAsync(request.Nickname);
        return Results.Ok(peer.Snapshot());
    }
    catch (Exception ex) when (ex is ArgumentException or SocketException or InvalidOperationException)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapPost("/api/local-opponent", async (LocalOpponentRequest request) =>
{
    try
    {
        await peer.StartBotAsync(request.Nickname, request.BotId, request.Side);
        return Results.Ok(peer.Snapshot());
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapPost("/api/host", async (HostRequest request) =>
{
    try
    {
        await peer.HostAsync(request.Port, request.Nickname);
        return Results.Ok(peer.Snapshot());
    }
    catch (Exception ex) when (ex is ArgumentException or SocketException or InvalidOperationException)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapPost("/api/join", async (JoinRequest request, CancellationToken cancellationToken) =>
{
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, app.Lifetime.ApplicationStopping);
    try
    {
        await peer.JoinAsync(request.Address, request.Port, request.Nickname, stop.Token);
        return Results.Ok(peer.Snapshot());
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is ArgumentException or SocketException or InvalidOperationException)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapPost("/api/leave", async () =>
{
    await peer.LeaveAsync();
    return Results.Ok(peer.Snapshot());
});

app.MapPost("/api/accept", async () =>
{
    try
    {
        await peer.AcceptChallengeAsync();
        return Results.Ok(peer.Snapshot());
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapPost("/api/decline", async () =>
{
    try
    {
        await peer.DeclineChallengeAsync();
        return Results.Ok(peer.Snapshot());
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapPost("/api/restart", (RestartRequest request) =>
{
    try
    {
        if (!Guid.TryParseExact(request.MatchId, "N", out var matchId) ||
            request.MatchId != matchId.ToString("N"))
            return Results.BadRequest(new ErrorResponse("Нужен идентификатор текущей игры."));
        peer.Restart(matchId, request.ExpectedRoundId);
        return Results.Ok(peer.Snapshot());
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
});

app.MapGet("/api/discover", async (CancellationToken cancellationToken) =>
{
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, app.Lifetime.ApplicationStopping);
    try
    {
        var hosts = await peer.DiscoverAsync(stop.Token);
        return Results.Ok(new DiscoverResponse(hosts.ToArray()));
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Map("/ws", context => PongWebSocketEndpoint.HandleAsync(context, peer, app.Lifetime.ApplicationStopping));

app.Run();

internal sealed record HostRequest(int Port, string Nickname);
internal sealed record QuickGameRequest(string Nickname);
internal sealed record LocalOpponentRequest(string Nickname, string? BotId,
    [property: JsonRequired] InitialSidePreference Side);
internal sealed record RestartRequest([property: JsonRequired] string MatchId,
    [property: JsonRequired] int ExpectedRoundId);
internal sealed record JoinRequest(string Address, int Port, string Nickname);
internal sealed record ErrorResponse(string Error);
internal sealed record DiscoverResponse(DiscoveredHost[] Hosts);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PongSnapshot))]
[JsonSerializable(typeof(BotCatalogResponse))]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(QuickGameRequest))]
[JsonSerializable(typeof(LocalOpponentRequest))]
[JsonSerializable(typeof(JoinRequest))]
[JsonSerializable(typeof(RestartRequest))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(DiscoverResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;
