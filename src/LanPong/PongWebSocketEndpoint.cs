using System.Net.WebSockets;
using System.Text.Json;

namespace LanPong;

internal static class PongWebSocketEndpoint
{
    private const int MaxControlBytes = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(HttpContext context, PongPeer peer, CancellationToken shutdownToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stop = new CancellationTokenSource();
        var controllerId = Guid.NewGuid();
        try
        {
            await RunSessionAsync(socket, peer, controllerId, stop, context.RequestAborted, shutdownToken);
        }
        finally
        {
            stop.Cancel();
            peer.RemoveController(controllerId);
        }
    }

    private static async Task RunSessionAsync(
        WebSocket socket, PongPeer peer, Guid controllerId, CancellationTokenSource stop,
        CancellationToken requestAborted, CancellationToken shutdownToken)
    {
        // Canceling an in-flight ReceiveAsync aborts ManagedWebSocket, preventing a close frame.
        // Observe shutdown separately so the receiver can read the client's close response.
        var receiveTask = ReceiveControlsAsync(socket, peer, controllerId);
        var sendTask = SendSnapshotsAsync(socket, peer, stop.Token);
        Task firstCompleted;
        using (var endSignal = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, requestAborted))
        {
            var endTask = Task.Delay(Timeout.InfiniteTimeSpan, endSignal.Token);
            try
            {
                firstCompleted = await Task.WhenAny(receiveTask, sendTask, endTask);
            }
            finally
            {
                endSignal.Cancel();
            }
        }
        stop.Cancel();

        // The snapshot sender must release its send before CloseOutputAsync uses the socket.
        try
        {
            await sendTask.WaitAsync(NetworkConstants.WebSocketCloseTimeout);
        }
        catch (TimeoutException)
        {
            socket.Abort();
            ObserveFault(sendTask);
            ObserveFault(receiveTask);
            return;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }

        if (socket.State == WebSocketState.CloseReceived)
        {
            // Reply to a normal browser close after the snapshot sender has stopped.
            await CloseOutputAsync(socket, socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                socket.CloseStatusDescription);
        }
        else if (shutdownToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await CloseOutputAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "Server shutting down");
        }
        else if (firstCompleted == sendTask || requestAborted.IsCancellationRequested)
        {
            socket.Abort();
        }

        // A client may never acknowledge our close frame. Do not hold up host shutdown.
        try
        {
            await receiveTask.WaitAsync(NetworkConstants.WebSocketCloseTimeout);
        }
        catch (TimeoutException)
        {
            socket.Abort();
            ObserveFault(receiveTask);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task CloseOutputAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        using var timeout = new CancellationTokenSource(NetworkConstants.WebSocketCloseTimeout);
        try
        {
            await socket.CloseOutputAsync(status, description, timeout.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (InvalidOperationException) { /* The peer may have closed while we were stopping. */ }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(static completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task SendSnapshotsAsync(WebSocket socket, PongPeer peer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(NetworkConstants.BrowserSnapshotInterval);
        do
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(peer.Snapshot(), JsonOptions);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        while (socket.State == WebSocketState.Open && await timer.WaitForNextTickAsync(cancellationToken));
    }

    private static async Task ReceiveControlsAsync(WebSocket socket, PongPeer peer, Guid controllerId)
    {
        var chunk = new byte[MaxControlBytes];
        var message = new byte[MaxControlBytes];
        while (true)
        {
            var length = 0;
            var discard = false;
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk.AsMemory(), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;

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
    }
}
