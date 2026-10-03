using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using LanPong;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PongPeer>();
var app = builder.Build();
var peer = app.Services.GetRequiredService<PongPeer>();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

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

app.MapPost("/api/join", async (JoinRequest request) =>
{
    try
    {
        await peer.JoinAsync(request.Address, request.Port);
        return Results.Ok(peer.Snapshot());
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
    try
    {
        var hosts = await peer.DiscoverAsync(port ?? 47777, cancellationToken);
        return Results.Ok(new { hosts });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var controllerId = Guid.NewGuid();
    var receiveTask = ReceiveControlsAsync(socket, peer, controllerId, stop.Token);
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 30));
    try
    {
        do
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(peer.Snapshot(), jsonOptions);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token);
        }
        while (socket.State == WebSocketState.Open && await timer.WaitForNextTickAsync(stop.Token));
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    finally
    {
        stop.Cancel();
        try { await receiveTask; }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        peer.RemoveController(controllerId);
    }
});

app.Run();

static async Task ReceiveControlsAsync(WebSocket socket, PongPeer peer, Guid controllerId, CancellationToken cancellationToken)
{
    var buffer = new byte[256];
    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
        var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close) break;
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) continue;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            if (document.RootElement.TryGetProperty("axis", out var axis) && axis.TryGetInt32(out var direction))
                peer.SetInput(controllerId, direction);
        }
        catch (JsonException) { /* Ignore malformed local UI messages. */ }
    }
}

internal sealed record HostRequest(int Port);
internal sealed record JoinRequest(string Address, int Port);
