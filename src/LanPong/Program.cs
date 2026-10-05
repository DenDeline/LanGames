using System.Net.Sockets;
using LanPong;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PongPeer>();
builder.Services.AddHostedService(services => services.GetRequiredService<PongPeer>());
var app = builder.Build();
var peer = app.Services.GetRequiredService<PongPeer>();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", () => peer.Snapshot());

app.MapPost("/api/host", async (HostRequest request) =>
{
    try
    {
        await peer.HostAsync(request.Port);
        return Results.Ok(peer.Snapshot());
    }
    catch (Exception ex) when (ex is ArgumentException or SocketException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/join", async (JoinRequest request, CancellationToken cancellationToken) =>
{
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, app.Lifetime.ApplicationStopping);
    try
    {
        await peer.JoinAsync(request.Address, request.Port, stop.Token);
        return Results.Ok(peer.Snapshot());
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is ArgumentException or SocketException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/leave", async () =>
{
    await peer.LeaveAsync();
    return Results.Ok(peer.Snapshot());
});

app.MapPost("/api/restart", () =>
{
    try
    {
        peer.Restart();
        return Results.Ok(peer.Snapshot());
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/discover", async (int? port, CancellationToken cancellationToken) =>
{
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken, app.Lifetime.ApplicationStopping);
    try
    {
        var hosts = await peer.DiscoverAsync(port ?? NetworkConstants.DefaultUdpPort, stop.Token);
        return Results.Ok(new { hosts });
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Map("/ws", context => PongWebSocketEndpoint.HandleAsync(context, peer, app.Lifetime.ApplicationStopping));

app.Run();

internal sealed record HostRequest(int Port);
internal sealed record JoinRequest(string Address, int Port);
