using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Wingman
{
    /// <summary>
    /// WebSocket client for communicating with the Wingman backend.
    /// Uses System.Net.WebSockets.ClientWebSocket (available on Quest/Android with IL2CPP).
    ///
    /// Handles connection lifecycle, JSON message parsing, and binary audio frames.
    /// All events are dispatched on the Unity main thread via a message queue
    /// processed in Update().
    ///
    /// Protocol:
    ///   - Text frames: JSON messages (suggestions, transcripts, telemetry, errors)
    ///   - Binary frames: PCM audio from Quest mic (outbound) or MP3 TTS audio (inbound)
    /// </summary>
    public class WingmanWebSocket : MonoBehaviour
    {
        [Header("Connection")]
        [Tooltip("Backend WebSocket base URL (without /ws/ path).")]
        [SerializeField] string m_ServerUrl = "ws://localhost:8000";

        [Header("Reconnection")]
        [SerializeField] float m_ReconnectDelay = 2f;
        [SerializeField] int m_MaxReconnectAttempts = 5;
        [SerializeField] float m_PingInterval = 15f;

        [Header("Buffer")]
        [Tooltip("Receive buffer size in bytes (must accommodate largest TTS audio frame).")]
        [SerializeField] int m_ReceiveBufferSize = 1024 * 256; // 256 KB

        // --- Events (dispatched on main thread) ---
        public event Action<SuggestionPayload> OnSuggestionsReceived;
        public event Action<string, string, bool> OnTranscriptReceived; // text, speaker, isPartial
        public event Action<string> OnTherapyResponseReceived; // therapy text
        public event Action<byte[]> OnTTSAudioReceived;
        public event Action<float> OnSilenceAlert;
        public event Action<JObject> OnTelemetryReceived;
        public event Action<ConnectionStatus> OnConnectionStatusChanged;
        public event Action<string> OnError;

        ClientWebSocket m_ClientWs;
        CancellationTokenSource m_Cts;
        string m_SessionId;
        int m_ReconnectAttempts;
        float m_LastPingTime;
        bool m_IntentionalDisconnect;
        ConnectionStatus m_CurrentStatus = ConnectionStatus.Disconnected;

        // Main-thread dispatch queue
        readonly ConcurrentQueue<Action> m_MainThreadQueue = new ConcurrentQueue<Action>();

        public bool IsConnected => m_ClientWs?.State == WebSocketState.Open;
        public string SessionId => m_SessionId;
        public ConnectionStatus CurrentStatus => m_CurrentStatus;

        // ================================================================
        // Public API
        // ================================================================

        /// <summary>Connect to the backend WebSocket for the given session.</summary>
        public async Task Connect(string sessionId)
        {
            m_SessionId = sessionId;
            m_IntentionalDisconnect = false;
            m_ReconnectAttempts = 0;
            await ConnectInternal();
        }

        /// <summary>Gracefully disconnect from the backend.</summary>
        public async Task Disconnect()
        {
            m_IntentionalDisconnect = true;
            await CloseSocket();
        }

        /// <summary>Send raw PCM audio bytes to the backend for STT processing.</summary>
        public async Task SendAudio(byte[] pcmData)
        {
            if (m_ClientWs?.State != WebSocketState.Open) return;

            try
            {
                await m_ClientWs.SendAsync(
                    new ArraySegment<byte>(pcmData),
                    WebSocketMessageType.Binary,
                    true,
                    m_Cts.Token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WingmanWS] SendAudio error: {ex.Message}");
            }
        }

        /// <summary>Send a manual transcript (if doing client-side STT).</summary>
        public Task SendTranscript(string text, string speaker = "user", bool isPartial = true)
        {
            return SendJson(new
            {
                type = "transcript",
                text,
                speaker,
                is_partial = isPartial
            });
        }

        /// <summary>Explicitly request suggestions for a given transcript.</summary>
        public Task RequestSuggestions(string transcript = null)
        {
            var msg = new JObject { ["type"] = "request_suggestions" };
            if (!string.IsNullOrEmpty(transcript))
                msg["transcript"] = transcript;
            return SendJson(msg);
        }

        /// <summary>Request TTS audio for specific text.</summary>
        public Task RequestTTS(string text)
        {
            return SendJson(new { type = "request_tts", text });
        }

        // ================================================================
        // Unity lifecycle
        // ================================================================

        void Update()
        {
            // Dispatch queued callbacks on main thread
            while (m_MainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError($"[WingmanWS] Main thread dispatch error: {ex}"); }
            }

            // Periodic ping
            if (IsConnected && Time.time - m_LastPingTime > m_PingInterval)
            {
                m_LastPingTime = Time.time;
                _ = SendJson(new { type = "ping" });
            }
        }

        void OnDestroy()
        {
            m_IntentionalDisconnect = true;
            m_Cts?.Cancel();
            m_ClientWs?.Dispose();
        }

        // ================================================================
        // Internal — connection
        // ================================================================

        async Task ConnectInternal()
        {
            EnqueueMainThread(() => SetStatus(ConnectionStatus.Connecting));

            string url = $"{m_ServerUrl.TrimEnd('/')}/ws/{m_SessionId}";
            Debug.Log($"[WingmanWS] Connecting to {url}");

            m_Cts?.Cancel();
            m_ClientWs?.Dispose();
            m_Cts = new CancellationTokenSource();
            m_ClientWs = new ClientWebSocket();

            try
            {
                await m_ClientWs.ConnectAsync(new Uri(url), m_Cts.Token);

                Debug.Log("[WingmanWS] Connected");
                m_ReconnectAttempts = 0;

                EnqueueMainThread(() =>
                {
                    m_LastPingTime = Time.time;
                    SetStatus(ConnectionStatus.Connected);
                });

                // Start receive loop on background thread
                _ = ReceiveLoop();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WingmanWS] Connection failed: {ex.Message}");
                EnqueueMainThread(() =>
                {
                    SetStatus(ConnectionStatus.Disconnected);
                    OnError?.Invoke($"Connection failed: {ex.Message}");
                });

                if (!m_IntentionalDisconnect)
                    _ = ScheduleReconnect();
            }
        }

        async Task CloseSocket()
        {
            if (m_ClientWs?.State == WebSocketState.Open)
            {
                try
                {
                    // Send end_session before closing
                    await SendJson(new { type = "end_session" });
                    await m_ClientWs.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
                }
                catch { }
            }

            m_Cts?.Cancel();
            m_ClientWs?.Dispose();
            m_ClientWs = null;

            EnqueueMainThread(() => SetStatus(ConnectionStatus.Disconnected));
        }

        // ================================================================
        // Internal — receive loop
        // ================================================================

        async Task ReceiveLoop()
        {
            var buffer = new byte[m_ReceiveBufferSize];
            var token = m_Cts.Token;

            try
            {
                while (m_ClientWs?.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    // Accumulate fragments into a single message
                    using (var ms = new System.IO.MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await m_ClientWs.ReceiveAsync(
                                new ArraySegment<byte>(buffer), token);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                Debug.Log("[WingmanWS] Server closed connection");
                                EnqueueMainThread(() => SetStatus(ConnectionStatus.Disconnected));
                                if (!m_IntentionalDisconnect)
                                    _ = ScheduleReconnect();
                                return;
                            }

                            ms.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        byte[] messageBytes = ms.ToArray();

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string text = Encoding.UTF8.GetString(messageBytes);
                            EnqueueMainThread(() => HandleJsonMessage(text));
                        }
                        else if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            // Binary = TTS audio
                            EnqueueMainThread(() => OnTTSAudioReceived?.Invoke(messageBytes));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on disconnect
            }
            catch (WebSocketException ex)
            {
                Debug.LogWarning($"[WingmanWS] WebSocket error: {ex.Message}");
                EnqueueMainThread(() =>
                {
                    SetStatus(ConnectionStatus.Disconnected);
                    OnError?.Invoke(ex.Message);
                });

                if (!m_IntentionalDisconnect)
                    _ = ScheduleReconnect();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WingmanWS] Receive loop error: {ex}");
                EnqueueMainThread(() => SetStatus(ConnectionStatus.Disconnected));

                if (!m_IntentionalDisconnect)
                    _ = ScheduleReconnect();
            }
        }

        // ================================================================
        // Internal — message handling
        // ================================================================

        void HandleJsonMessage(string json)
        {
            try
            {
                var msg = JObject.Parse(json);
                string msgType = msg.Value<string>("type") ?? "";

                switch (msgType)
                {
                    case "suggestions":
                        HandleSuggestions(msg);
                        break;

                    case "transcript":
                    case "transcript_ack":
                        HandleTranscript(msg);
                        break;

                    case "therapy_response":
                        string therapyText = msg.Value<string>("text") ?? "";
                        OnTherapyResponseReceived?.Invoke(therapyText);
                        break;

                    case "silence_alert":
                        float seconds = msg.Value<float>("seconds");
                        OnSilenceAlert?.Invoke(seconds);
                        break;

                    case "telemetry":
                        OnTelemetryReceived?.Invoke(msg);
                        break;

                    case "tts_meta":
                        Debug.Log($"[WingmanWS] TTS meta: cached={msg.Value<bool>("cached")} latency={msg.Value<float>("tts_latency_ms")}ms");
                        break;

                    case "session_ended":
                        Debug.Log("[WingmanWS] Session ended by server");
                        m_IntentionalDisconnect = true;
                        break;

                    case "pong":
                        break;

                    case "error":
                        string detail = msg.Value<string>("detail") ?? "Unknown error";
                        Debug.LogWarning($"[WingmanWS] Server error: {detail}");
                        OnError?.Invoke(detail);
                        break;

                    default:
                        Debug.LogWarning($"[WingmanWS] Unknown message type: {msgType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WingmanWS] JSON parse error: {ex.Message}");
            }
        }

        void HandleSuggestions(JObject msg)
        {
            try
            {
                var suggestionsArray = msg["suggestions"] as JArray;
                if (suggestionsArray == null) return;

                var suggestions = new SuggestionData[suggestionsArray.Count];
                for (int i = 0; i < suggestionsArray.Count; i++)
                {
                    var s = suggestionsArray[i];
                    string styleStr = s.Value<string>("style") ?? "casual";
                    SuggestionStyle style = ParseStyle(styleStr);
                    float confidence = s.Value<float>("confidence");
                    string text = s.Value<string>("text") ?? "";

                    suggestions[i] = new SuggestionData(text, style, confidence);
                }

                var payload = new SuggestionPayload
                {
                    suggestions = suggestions,
                    engagementEstimate = msg.Value<float>("engagement_score"),
                    dominantTopic = msg.Value<string>("topic") ?? ""
                };

                OnSuggestionsReceived?.Invoke(payload);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WingmanWS] Failed to parse suggestions: {ex.Message}");
            }
        }

        void HandleTranscript(JObject msg)
        {
            string text = msg.Value<string>("text") ?? "";
            string speaker = msg.Value<string>("speaker") ?? "unknown";
            bool isPartial = msg.Value<bool>("is_partial");

            OnTranscriptReceived?.Invoke(text, speaker, isPartial);
        }

        static SuggestionStyle ParseStyle(string style)
        {
            switch (style.ToLowerInvariant())
            {
                case "casual": return SuggestionStyle.Casual;
                case "thoughtful": return SuggestionStyle.Thoughtful;
                case "flirty": return SuggestionStyle.Flirty;
                case "empathetic": return SuggestionStyle.Empathetic;
                case "humorous": return SuggestionStyle.Humorous;
                default: return SuggestionStyle.Casual;
            }
        }

        // ================================================================
        // Internal — send
        // ================================================================

        async Task SendJson(object data)
        {
            if (m_ClientWs?.State != WebSocketState.Open) return;

            try
            {
                string json;
                if (data is JObject jobj)
                    json = jobj.ToString(Formatting.None);
                else
                    json = JsonConvert.SerializeObject(data);

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await m_ClientWs.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    m_Cts.Token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WingmanWS] SendJson error: {ex.Message}");
            }
        }

        // ================================================================
        // Internal — reconnection
        // ================================================================

        async Task ScheduleReconnect()
        {
            if (m_ReconnectAttempts >= m_MaxReconnectAttempts)
            {
                Debug.LogError($"[WingmanWS] Max reconnect attempts ({m_MaxReconnectAttempts}) reached");
                EnqueueMainThread(() => OnError?.Invoke("Connection lost — max reconnect attempts reached"));
                return;
            }

            m_ReconnectAttempts++;
            float delay = m_ReconnectDelay * Mathf.Pow(2, m_ReconnectAttempts - 1);
            Debug.Log($"[WingmanWS] Reconnecting in {delay:F1}s (attempt {m_ReconnectAttempts}/{m_MaxReconnectAttempts})");

            await Task.Delay((int)(delay * 1000));

            if (!m_IntentionalDisconnect)
                await ConnectInternal();
        }

        // ================================================================
        // Internal — helpers
        // ================================================================

        void SetStatus(ConnectionStatus status)
        {
            if (m_CurrentStatus == status) return;
            m_CurrentStatus = status;
            OnConnectionStatusChanged?.Invoke(status);
        }

        void EnqueueMainThread(Action action)
        {
            m_MainThreadQueue.Enqueue(action);
        }
    }
}
