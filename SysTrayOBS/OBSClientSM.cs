using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SysTrayOBS
{
    public enum ObsClientState
    {
        Disconnected,
        Connecting,
        HelloReceived,
        Authenticating,
        Identifying,
        Ready,
        Reconnecting,
        Stopping
    }

    public sealed class OBSClientSM : IAsyncDisposable
    {
        private readonly string _uri;
        private readonly string _password;

        private ClientWebSocket _socket = new();
        private CancellationTokenSource? _cts;

        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _flushLock = new(1, 1);

        private readonly ConcurrentQueue<Func<Task>> _readyQueue = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();

        private volatile bool _isStreaming;
        private ObsClientState _state = ObsClientState.Disconnected;
        private readonly BehaviorSubject<ObsClientState> _stateChanged = new(ObsClientState.Disconnected);
        public IObservable<ObsClientState> StateChanged => _stateChanged.AsObservable();
        private readonly Subject<bool> _streamStateChanged = new();
        public IObservable<bool> StreamStateChanged => _streamStateChanged.AsObservable();
        public ObsClientState State => _state;
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
        private Task? _connectionTask;
        private readonly SemaphoreSlim _startStopLock = new(1, 1);

        public OBSClientSM(string uri, string password)
        {
            _uri = uri;
            _password = password;

            StateChanged.Subscribe(async (state) =>
            {
                Console.WriteLine($"[OBS] State changed to: {state}");
                if (state == ObsClientState.Ready) {
                    var result = await RunOrQueueAsync<bool>(async () =>
                    {
                        var response = await SendRequestAsync("GetStreamStatus");

                        if (!response.TryGetProperty("responseData", out var data) ||
                            !data.TryGetProperty("outputActive", out var active))
                        {
                            return false;
                        }

                        return active.GetBoolean();
                    });
                    _isStreaming = result;
                    _streamStateChanged.OnNext(result);
                }
            });
        }

        #region Lifecycle

        public async Task StartAsync()
        {
            await _startStopLock.WaitAsync();
            try
            {
                if (_connectionTask != null)
                    return;

                _connectionTask = Task.Run(ConnectionSupervisorAsync);
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        private async Task ConnectionSupervisorAsync()
        {
            while (true)
            {
                TransitionTo(ObsClientState.Connecting);

                _cts = new CancellationTokenSource();
                _socket = new ClientWebSocket();

                try
                {
                    await _socket.ConnectAsync(new Uri(_uri), _cts.Token);
                    TransitionTo(ObsClientState.Identifying);

                    // Run receive loop until it exits
                    await ReceiveLoop();
                }
                catch (OperationCanceledException)
                {
                    break; // Stop requested
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OBS] Connection error: {ex.Message}");
                }

                if (_state == ObsClientState.Stopping)
                    break;

                TransitionTo(ObsClientState.Reconnecting);
                CleanupPendingRequests(new Exception("Disconnected"));

                await Task.Delay(ReconnectDelay);
            }

            TransitionTo(ObsClientState.Disconnected);
        }
        public async Task StopAsync()
        {
            await _startStopLock.WaitAsync();
            try
            {
                if (_connectionTask == null)
                    return;

                TransitionTo(ObsClientState.Stopping);
                _cts?.Cancel();

                await _connectionTask;
                _connectionTask = null;
            }
            finally
            {
                _startStopLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _socket.Dispose();
            _cts?.Dispose();
            _sendLock.Dispose();
            _flushLock.Dispose();
            _streamStateChanged.Dispose();
            _stateChanged.Dispose();
        }

        #endregion

        #region State Machine

        private void TransitionTo(ObsClientState newState)
        {
            if (_state == newState)
                return;

            _state = newState;
            Console.WriteLine($"[OBS] → {_state}");
            _stateChanged.OnNext(_state);

            if (_state == ObsClientState.Ready)
                _ = FlushReadyQueueAsync();
        }

        #endregion

        #region Receive Loop

        private async Task ReceiveLoop()
        {
            var buffer = new byte[8192];

            try
            {
                while (!_cts!.IsCancellationRequested &&
                       _socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(buffer, _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var root = JsonDocument.Parse(json).RootElement;

                    await HandleMessage(root);
                }
            }
            catch (OperationCanceledException) { }        
        }

        #endregion

        #region Message Handling

        private async Task HandleMessage(JsonElement root)
        {
            int op = root.GetProperty("op").GetInt32();

            switch (op)
            {
                case 0: // Hello
                    TransitionTo(ObsClientState.HelloReceived);
                    await HandleHello(root);
                    break;

                case 2: // Identified
                    TransitionTo(ObsClientState.Ready);
                    //_authTcs?.TrySetResult(true);
                    break;

                case 5: // Event
                    await HandleEvent(root);
                    break;

                case 7: // Request response
                    ResolveRequest(root);
                    break;
            }
        }

        private async Task HandleHello(JsonElement root)
        {
            bool authRequired =
                root.GetProperty("d").TryGetProperty("authentication", out var auth);

            TransitionTo(authRequired
                ? ObsClientState.Authenticating
                : ObsClientState.Identifying);

            await SendAsync(new
            {
                op = 1,
                d = new
                {
                    rpcVersion = 1,
                    authentication = authRequired
                        ? ComputeAuth(
                            _password,
                            auth.GetProperty("salt").GetString()!,
                            auth.GetProperty("challenge").GetString()!)
                        : null
                }
            });
        }

        private async Task HandleEvent(JsonElement root)
        {
            string type = root.GetProperty("d").GetProperty("eventType").GetString()!;
            if (type == "StreamStateChanged")
            {
                bool active = root.GetProperty("d")
                                  .GetProperty("eventData")
                                  .GetProperty("outputActive")
                                  .GetBoolean();                
                _isStreaming = active;
                _streamStateChanged.OnNext(active);
            }
        }

        #endregion

        #region Sending / Requests

        private async Task SendAsync(object payload)
        {
            if (_state is ObsClientState.Reconnecting or ObsClientState.Disconnected)
                throw new InvalidOperationException("Not connected");

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(_cts!.Token);
            try
            {
                await _socket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    true,
                    _cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task<JsonElement> SendRequestAsync(
            string requestType,
            object? data = null,
            int timeoutMs = 5000)
        {
            if (_state != ObsClientState.Ready)
                throw new InvalidOperationException("OBS not ready");

            string id = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _pendingRequests[id] = tcs;

            await SendAsync(new
            {
                op = 6,
                d = new
                {
                    requestType,
                    requestId = id,
                    requestData = data ?? new { }
                }
            });

            using var timeout = new CancellationTokenSource(timeoutMs);
            using var reg = timeout.Token.Register(() =>
                tcs.TrySetException(new TimeoutException(requestType)));

            return await tcs.Task;
        }

        private void ResolveRequest(JsonElement root)
        {
            string id = root.GetProperty("d").GetProperty("requestId").GetString()!;
            if (_pendingRequests.TryRemove(id, out var tcs))
                tcs.TrySetResult(root.GetProperty("d"));
        }

        private void CleanupPendingRequests(Exception ex)
        {
            foreach (var kv in _pendingRequests)
                if (_pendingRequests.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(ex);
        }

        #endregion

        #region Ready Queue

        //private Task RunOrQueueAsync(Func<Task> action)
        //{
        //    if (_state == ObsClientState.Ready)
        //        return action();

        //    _readyQueue.Enqueue(action);
        //    return Task.CompletedTask;
        //}

        private Task<T> RunOrQueueAsync<T>(Func<Task<T>> action)
        {
            if (_state == ObsClientState.Ready)
                return action();

            var tcs = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _readyQueue.Enqueue(async () =>
            {
                try
                {
                    var result = await action();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        private async Task FlushReadyQueueAsync()
        {
            if (_state != ObsClientState.Ready)
                return;

            await _flushLock.WaitAsync();
            try
            {
                while (_state == ObsClientState.Ready &&
                       _readyQueue.TryDequeue(out var action))
                {
                    try
                    {
                        await action();
                    }
                    catch
                    {
                        _readyQueue.Enqueue(action);
                        break;
                    }
                }
            }
            finally
            {
                _flushLock.Release();
            }
        }

        public async Task<string?> GetCurrentSceneAsync()
        {
            try
            {
                var response = await SendRequestAsync("GetCurrentProgramScene");

                // Check requestStatus
                if (response.TryGetProperty("requestStatus", out var status) &&
                    status.TryGetProperty("result", out var result) &&
                    result.GetBoolean() == false)
                {
                    string comment = status.TryGetProperty("comment", out var c) ? c.GetString()! : "Unknown error";
                    Console.WriteLine($"Failed to get current scene: {comment}");
                    return null;
                }

                // Extract current scene name
                if (response.TryGetProperty("responseData", out var data) &&
                    data.TryGetProperty("currentProgramSceneName", out var sceneNameProp))
                {
                    return sceneNameProp.GetString();
                }

                Console.WriteLine("Could not find currentProgramSceneName in response.");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting current scene: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> SetCurrentSceneAsync(string sceneName)
        {
            try
            {
                var response = await SendRequestAsync("SetCurrentProgramScene", new { sceneName });

                // Check request status
                if (response.TryGetProperty("requestStatus", out var status) &&
                    status.TryGetProperty("result", out var result) &&
                    result.GetBoolean() == false)
                {
                    string comment = status.TryGetProperty("comment", out var c) ? c.GetString()! : "Unknown error";
                    Console.WriteLine($"Failed to set scene '{sceneName}': {comment}");
                    return false;
                }

                Console.WriteLine($"Switched to scene '{sceneName}'");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error switching scene: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Public API

        public async Task StartStreamingAsync()
        {
            await RunOrQueueAsync(async () => await SendRequestAsync("StartStream"));
        }

        public async Task StopStreamingAsync() =>
            await RunOrQueueAsync(async () => await SendRequestAsync("StopStream"));

        public async Task ToggleScenes(params string[] sceneNames)
        {
            await RunOrQueueAsync(async () => await ActionToggleScenes(sceneNames));
        }

        public async Task ToggleSceneItems(string sceneName, params string[] sceneNames)
        {
            await RunOrQueueAsync(async () => await ActionToggleSceneItems(sceneName, sceneNames));
        }

        async Task<bool> ActionToggleScenes(params string[] sceneNames)
        {
            var currentScene = await GetCurrentSceneAsync();
            var pos = sceneNames.IndexOf(currentScene);
            var nextScene = pos == -1 ? sceneNames[0] : sceneNames[(pos + 1) % sceneNames.Length];            
            return await SetCurrentSceneAsync(nextScene);
        }

        async Task<bool> ActionToggleSceneItems(string sceneName, params string[] sourceNames)
        {
            var sceneItems = await GetSceneItems(sceneName);
            var matchedItems = sceneItems.Value
            .EnumerateArray()
            .Where(item =>
            {
                string name = item.GetProperty("sourceName").GetString()!;
                return sourceNames.Contains(name, StringComparer.OrdinalIgnoreCase);
            })
            .ToArray();

            var scenesFound = from sceneItem in sceneItems.Value.EnumerateArray()
                     where sourceNames.Contains(sceneItem.GetProperty("sourceName").GetString()!)
                     select new Tuple<string, bool>(sceneItem.GetProperty("sourceName").GetString()!, sceneItem.GetProperty("sceneItemEnabled").GetBoolean()!);

            var toogleValue = scenesFound.Any(x => x.Item2 == true);

            foreach (var item in scenesFound)
            {
                await SetSceneItemEnabledAsync(sceneName, item.Item1, !toogleValue);
            }
            return true;
        }

        public async Task<HashSet<string>> GetAllSceneNames()
        {
            var response = await SendRequestAsync("GetSceneList");

            return response
                .GetProperty("responseData").GetProperty("scenes").EnumerateArray()
                .Select(scene => scene.GetProperty("sceneName").GetString()!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public async Task<bool> ValidateScenesExistAsync(params string[] sceneNames)
        {
            if (sceneNames == null || sceneNames.Length == 0)
                return false;

            var existingScenes = await GetAllSceneNames();

            return sceneNames.All(existingScenes.Contains);
        }

        public async Task<bool> ValidateSceneItemsExistAsync(string sceneName, params string[] sourceNames)
        {
            if (sourceNames == null || sourceNames.Length == 0)
                return false;
            var sceneItems = await GetSceneItems(sceneName);
            if (sceneItems == null)
                return false;
            var existingSourceNames = sceneItems.Value
                .EnumerateArray()
                .Select(item => item.GetProperty("sourceName").GetString()!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return sourceNames.All(existingSourceNames.Contains);
        }

        async Task<JsonElement?> GetSceneItem(string sceneName, string sourceName)
        {
            var response = await SendRequestAsync("GetSceneItemList", new { sceneName });

            foreach (var item in response.GetProperty("responseData").GetProperty("sceneItems").EnumerateArray())
            {
                if (item.GetProperty("sourceName").GetString()!.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }
        async Task<bool> SetSceneItemEnabledAsync(string sceneName, string sourceName, bool enabled)
        {
            var sceneItem = await GetSceneItem(sceneName, sourceName);
            if (sceneItem == null)
            {
                Console.WriteLine($"Scene item '{sourceName}' not found in '{sceneName}'");
                return false;
            }

            int sceneItemId = sceneItem.Value.GetProperty("sceneItemId").GetInt32();
            await SendRequestAsync("SetSceneItemEnabled", new
            {
                sceneName,
                sceneItemId,
                sceneItemEnabled = enabled
            });

            Console.WriteLine($"Scene item '{sourceName}' in '{sceneName}' set to {(enabled ? "enabled" : "disabled")}");
            return true;
        }

        async Task<JsonElement?> GetSceneItems(string sceneName)
        {
            var response = await SendRequestAsync("GetSceneItemList", new { sceneName });
            if (response.TryGetProperty("requestStatus", out var status))
            {
                if (status.GetProperty("result").GetBoolean() != true)
                {
                    Console.WriteLine($"Failed to get scene items for '{sceneName}': {status.GetProperty("comment").GetString()}");
                    return null;
                }
            }
            return response.GetProperty("responseData").GetProperty("sceneItems");
        }

        public async Task ToggleStreamAsync()
        {
            await RunOrQueueAsync(async () =>
            {
                if (_isStreaming)
                    return await SendRequestAsync("StopStream");
                else
                    return await SendRequestAsync("StartStream");
            });
        }

        #endregion

        #region Auth Helper

        private static string ComputeAuth(string password, string salt, string challenge)
        {
            using var sha = SHA256.Create();

            var hash1 = sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt));
            var hash2 = sha.ComputeHash(
                Encoding.UTF8.GetBytes(Convert.ToBase64String(hash1) + challenge));

            return Convert.ToBase64String(hash2);
        }

        #endregion
    }
}
