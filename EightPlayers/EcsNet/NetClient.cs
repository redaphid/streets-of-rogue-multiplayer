using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EightPlayers.EcsNet
{
    public enum NetState
    {
        Disconnected,
        Connecting,
        Connected,
    }

    // WebSocket client for the sor-ecs-net worker. All socket IO runs on the
    // thread pool; Unity code talks to it only through thread-safe queues and
    // the State property. Reconnect policy lives in EcsNetManager.
    public sealed class NetClient : IDisposable
    {
        private readonly ConcurrentQueue<string> _received = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();
        private readonly SemaphoreSlim _outboundSignal = new SemaphoreSlim(0);

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private volatile NetState _state = NetState.Disconnected;
        private volatile string _lastError = "";

        public NetState State => _state;
        public string LastError => _lastError;

        public void Connect(string url)
        {
            if (_state != NetState.Disconnected)
                return;
            _state = NetState.Connecting;
            _lastError = "";
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => RunAsync(url, token));
        }

        public void Send(string frame)
        {
            if (_state != NetState.Connected)
                return;
            _outbound.Enqueue(frame);
            _outboundSignal.Release();
        }

        public bool TryReceive(out string frame) => _received.TryDequeue(out frame);

        public void Disconnect()
        {
            _cts?.Cancel();
            _state = NetState.Disconnected;
        }

        public void Dispose() => Disconnect();

        private async Task RunAsync(string url, CancellationToken token)
        {
            try
            {
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri(url), token).ConfigureAwait(false);
                _state = NetState.Connected;

                var send = SendLoopAsync(token);
                var receive = ReceiveLoopAsync(token);
                await Task.WhenAny(send, receive).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _lastError = ex.GetBaseException().Message;
            }
            finally
            {
                try { _socket?.Dispose(); } catch { }
                _socket = null;
                while (_outbound.TryDequeue(out _)) { }
                _state = NetState.Disconnected;
            }
        }

        private async Task SendLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await _outboundSignal.WaitAsync(token).ConfigureAwait(false);
                if (!_outbound.TryDequeue(out var frame))
                    continue;
                var bytes = Encoding.UTF8.GetBytes(frame);
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token)
                    .ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            using (var assembly = new MemoryStream())
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    assembly.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;
                    if (result.MessageType == WebSocketMessageType.Text)
                        _received.Enqueue(Encoding.UTF8.GetString(assembly.GetBuffer(), 0, (int)assembly.Length));
                    assembly.SetLength(0);
                }
            }
        }
    }
}
