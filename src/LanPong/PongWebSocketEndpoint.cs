using System.Net.WebSockets;
using System.Text.Json;

namespace LanPong;

internal static class PongWebSocketEndpoint
{
    private const int MaxControlBytes = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(HttpContext context, PongPeer peer)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var controllerId = Guid.NewGuid();
        try
        {
            await RunSessionAsync(socket, peer, controllerId, stop);
        }
        finally
        {
            stop.Cancel();
            peer.RemoveController(controllerId);
        }
    }

    private static async Task RunSessionAsync(
        WebSocket socket, PongPeer peer, Guid controllerId, CancellationTokenSource stop)
    {
        var receiveTask = ReceiveControlsAsync(socket, peer, controllerId, stop.Token);
        var sendTask = SendSnapshotsAsync(socket, peer, stop.Token);
        try
        {
            await Task.WhenAny(receiveTask, sendTask);
        }
        finally
        {
            stop.Cancel();
        }

        try
        {
            await Task.WhenAll(receiveTask, sendTask);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) { }

        // Reply to a normal browser close only after the snapshot sender has stopped.
        if (receiveTask.IsCompletedSuccessfully && receiveTask.Result && socket.State == WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(NetworkConstants.WebSocketCloseTimeout);
            try
            {
                await socket.CloseOutputAsync(
                    socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    socket.CloseStatusDescription, timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }
    }

    private static async Task SendSnapshotsAsync(WebSocket socket, PongPeer peer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(NetworkConstants.BrowserSnapshotInterval);
        do
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(peer.Snapshot(), JsonOptions);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        while (socket.State == WebSocketState.Open && await timer.WaitForNextTickAsync(cancellationToken));
    }

    private static async Task<bool> ReceiveControlsAsync(
        WebSocket socket, PongPeer peer, Guid controllerId, CancellationToken cancellationToken)
    {
        var chunk = new byte[MaxControlBytes];
        var message = new byte[MaxControlBytes];
        while (!cancellationToken.IsCancellationRequested)
        {
            var length = 0;
            var discard = false;
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return true;

                if (result.MessageType != WebSocketMessageType.Text || result.Count > message.Length - length)
                    discard = true;
                if (!discard)
                {
                    chunk.AsSpan(0, result.Count).CopyTo(message.AsSpan(length));
                    length += result.Count;
                }
                if (result.EndOfMessage) break;
            }

            if (discard) continue;
            try
            {
                using var document = JsonDocument.Parse(message.AsMemory(0, length));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("axis", out var axis) &&
                    axis.ValueKind == JsonValueKind.Number &&
                    axis.TryGetInt32(out var direction))
                    peer.SetInput(controllerId, direction);
            }
            catch (JsonException) { /* Ignore malformed local UI messages. */ }
        }
        return false;
    }
}
