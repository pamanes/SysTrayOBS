using System;
using System.Reactive.Subjects;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace SysTrayOBS
{
    public class OBSClient
    {
        private ClientWebSocket _socket = new();
        private bool _isStreaming = false;
        private CancellationTokenSource _cts = new();
        private readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
        private TaskCompletionSource<bool>? _authTcs;
        private ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests
            = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();
        // Track streaming state changes
        private readonly Subject<bool> _streamStateChanged = new Subject<bool>();
        public IObservable<bool> StreamStateChanged => _streamStateChanged;
        private string _uri, _password;
        private bool _isConnected;

        public OBSClient(string uri, string password)
        {
            this._uri = uri;
            this._password = password;
        }

        public async Task Init()
        {
            await ConnectAsync(_uri, _password);
            // Wait until authentication or no-auth
            bool authenticated = await _authTcs!.Task;
            if (!authenticated)
            {
                Console.WriteLine("Authentication failed");
                return;
            }
            _isStreaming = await GetCurrentStreamingStateAsync();
        }

        #region Connection & Receive Loop

        public async Task ConnectAsync(string uri, string password)
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (_socket != null && _socket.State == WebSocketState.Open)
                        return;
                    _socket = new ClientWebSocket();
                    await _socket.ConnectAsync(new Uri(uri), _cts.Token);
                    _isConnected = true;

                    Console.WriteLine("Connected. Starting receive loop...");

                    // Prepare auth TCS
                    _authTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    // Start receive loop
                    _ = Task.Run(() => ReceiveLoop(password));

                    break; // connected successfully
                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    Console.WriteLine($"Connection failed: {ex.Message}. Retrying in {ReconnectDelay.TotalSeconds}s...");
                    await Task.Delay(ReconnectDelay);
                }
            }
        }
        public async Task DisconnectAsync()
        {
            _cts.Cancel();

            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);

            _isConnected = false;
        }

        private async Task ReceiveLoop(string password)
        {
            var buffer = new byte[8192];

            try
            {
                while (_socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _cts.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Console.WriteLine("OBS closed the connection.");
                            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
                            _isConnected = false;
                            break;
                        }

                        ms.Write(buffer, 0, result.Count);

                    } while (!result.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);
                    using var reader = new StreamReader(ms, Encoding.UTF8);
                    string jsonText = await reader.ReadToEndAsync();

                    if (string.IsNullOrWhiteSpace(jsonText))
                        continue;

                    try
                    {
                        var root = JsonDocument.Parse(jsonText).RootElement;
                        await HandleMessage(root, password);
                    }
                    catch (JsonException jex)
                    {
                        Console.WriteLine($"JSON parse error: {jex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Receive loop error: {ex.Message}");
            }

            _isConnected = false;

            if (!_cts.IsCancellationRequested)
            {
                Console.WriteLine("Disconnected. Reconnecting...");
                await ConnectAsync("ws://192.168.1.175:4455", password);
            }
        }

        #endregion

        #region Message Handling

        private async Task HandleMessage(JsonElement root, string password)
        {
            int op = root.GetProperty("op").GetInt32();

            switch (op)
            {
                case 0: // Hello
                    bool authRequired = root.GetProperty("d").TryGetProperty("authentication", out var auth);
                    if (authRequired)
                    {
                        string? authResp = ComputeAuth(
                            password!,
                            auth.GetProperty("salt").GetString()!,
                            auth.GetProperty("challenge").GetString()!
                        );

                        await SendAsync(new
                        {
                            op = 1,
                            d = new
                            {
                                rpcVersion = 1,
                                authentication = authResp
                            }
                        });
                    }
                    else
                    {
                        //still handshake but no auth needed
                        await SendAsync(new
                        {
                            op = 1,
                            d = new
                            {
                                rpcVersion = 1
                            }
                        });

                    }
                    break;

                case 2: // Identified
                    bool failed = root.GetProperty("d").TryGetProperty("authenticationFailed", out var authFailed) &&
                                  authFailed.GetBoolean();
                    _authTcs?.TrySetResult(!failed);
                    break;

                case 5: // Event
                    await HandleEvent(root);
                    break;

                case 7: // Request response
                    string requestId = root.GetProperty("d").GetProperty("requestId").GetString()!;
                    if (_pendingRequests.TryRemove(requestId, out var tcs))
                        tcs.SetResult(root.GetProperty("d"));
                    break;
            }
        }

        private async Task HandleEvent(JsonElement root)
        {
            string eventType = root.GetProperty("d").GetProperty("eventType").GetString()!;

            if (eventType == "StreamStateChanged")
            {
                bool streaming = root.GetProperty("d").GetProperty("eventData")
                    .GetProperty("outputActive").GetBoolean();
                _isStreaming = streaming;
                Console.WriteLine(streaming ? "✅ Streaming started" : "⏹ Streaming stopped");
                _streamStateChanged.OnNext(streaming);
            }
        }

        #endregion

        #region Sending Requests

        private async Task SendAsync(object payload)
        {
            string json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }

        private async Task<JsonElement> SendRequestAsync(string requestType, object? requestData = null, int timeoutMs = 5000)
        {
            string requestId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            var payload = new
            {
                op = 6,
                d = new
                {
                    requestType,
                    requestId,
                    requestData = requestData ?? new { }
                }
            };

            await SendAsync(payload);

            // Wait with timeout
            var delayTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(tcs.Task, delayTask);

            if (completed == delayTask)
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw new TimeoutException($"Request {requestType} timed out after {timeoutMs} ms");
            }

            return await tcs.Task;
        }

        #endregion

        #region Streaming Control

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

        public async Task ToggleStream()
        {
            if(_isStreaming)
            {
                await StopStreamingAsync();
            }
            else
            {
                await StartStreamingAsync();
            }
        }

        public async Task ToggleSceneItems(string sceneName, params string[] sourceNames)
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

            var ok = from sceneItem in sceneItems.Value.EnumerateArray()
                     where sourceNames.Contains(sceneItem.GetProperty("sourceName").GetString()!)
                     select new Tuple<string, bool>(sceneItem.GetProperty("sourceName").GetString()!, sceneItem.GetProperty("sceneItemEnabled").GetBoolean()!);

            var toogleValue = ok.Any(x => x.Item2 == true);

            foreach (var item in ok)
            {
                await SetSceneItemEnabledAsync(sceneName, item.Item1, !toogleValue);
            }
        }
        public async Task ToggleScenes(params string[] sceneNames)
        {
            var currentScene = await GetCurrentSceneAsync();
            var pos = sceneNames.IndexOf(currentScene);
            var nextScene = sceneNames[(pos + 1) % sceneNames.Length];
            await SetCurrentSceneAsync(nextScene);
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

        private async Task<bool> GetCurrentStreamingStateAsync()
        {
            var result = false;
            var response = await SendRequestAsync("GetStreamStatus", new { outputName = "stream" });
            if (!response.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("outputActive", out var active))
                return false;

            result = active.GetBoolean();
            _streamStateChanged.OnNext(result);
            return result;
        }

        public async Task StartStreamingAsync()
        {
            if (_isStreaming)
            {
                Console.WriteLine("Streaming already running.");
                return;
            }

            await SendRequestAsync("StartStream");
            Console.WriteLine("StartStream request sent, waiting for StreamStateChanged...");
        }

        public async Task StopStreamingAsync()
        {
            if (!_isStreaming)
            {
                Console.WriteLine("Streaming not running.");
                return;
            }

            await SendRequestAsync("StopStream");
            Console.WriteLine("StopStream request sent, waiting for StreamStateChanged...");
        }

        #endregion

        #region Scene Control

        private async Task<JsonElement?> GetSceneItem(string sceneName, string sourceName)
        {
            var response = await SendRequestAsync("GetSceneItemList", new { sceneName });
            //var ok = response.GetProperty("responseData").GetProperty("sceneItems");
            //ok.EnumerateArray();

            foreach (var item in response.GetProperty("responseData").GetProperty("sceneItems").EnumerateArray())
            {
                if (item.GetProperty("sourceName").GetString()!.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }

        private async Task<JsonElement?> GetSceneItems(string sceneName)
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

        private async Task<bool> SetSceneItemEnabledAsync(string sceneName, string sourceName, bool enabled)
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

        #endregion

        #region OBS Auth Helper

        private string ComputeAuth(string password, string salt, string challenge)
        {
            // OBS WebSocket v5 auth: base64(SHA256(base64(SHA256(password + salt)) + challenge))
            using var sha256 = System.Security.Cryptography.SHA256.Create();

            byte[] passwordBytes = Encoding.UTF8.GetBytes(password + salt);
            byte[] hash1 = sha256.ComputeHash(passwordBytes);
            string base64Hash1 = Convert.ToBase64String(hash1);

            byte[] hash2Input = Encoding.UTF8.GetBytes(base64Hash1 + challenge);
            byte[] hash2 = sha256.ComputeHash(hash2Input);

            return Convert.ToBase64String(hash2);
        }

        #endregion
    }
}
