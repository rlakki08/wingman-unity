using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Newtonsoft.Json.Linq;

namespace Wingman
{
    /// <summary>
    /// Main orchestrator for the Wingman real-time conversation coach system.
    ///
    /// BACKEND MODE (ONLY SUPPORTED MODE):
    ///   Quest Mic → WebSocket → Backend FastAPI → ElevenLabs STT → Gemini/OpenRouter LLM → Quest HUD
    ///   
    /// The Quest only handles:
    ///   - Microphone audio capture
    ///   - WebSocket communication
    ///   - HUD display of suggestions
    ///
    /// The Backend handles:
    ///   - Speech-to-text (ElevenLabs)
    ///   - LLM processing (Gemini with OpenRouter fallback)
    ///   - Conversation history and context
    ///   - Text-to-speech (ElevenLabs) for therapy mode
    ///
    /// Attach this to a persistent GameObject in the scene (e.g. "Wingman Manager").
    /// Assign all subsystem references in the Inspector.
    /// </summary>
    public class WingmanManager : MonoBehaviour
    {
        [Header("Subsystems - Backend Mode Only")]
        [SerializeField] WingmanSessionClient m_SessionClient;
        [SerializeField] WingmanWebSocket m_WebSocket;
        [SerializeField] WingmanMicrophone m_Microphone;
        [SerializeField] WingmanAudioPlayer m_AudioPlayer;
        [SerializeField] WingmanHUD m_HUD;
        [SerializeField] WingmanTranscriptionDisplay m_TranscriptionDisplay;
        [SerializeField] WingmanDebugDisplay m_DebugDisplay;
        [SerializeField] WingmanSuggestionsDisplay m_SuggestionsDisplay;

        [Header("Settings")]
        [Tooltip("Automatically start coaching when the scene loads.")]
        [SerializeField] bool m_AutoStart = false;

        [Tooltip("Show telemetry debug info on screen.")]
        [SerializeField] bool m_ShowTelemetry = false;

        [Tooltip("Coaching mode: dating (default) or therapy.")]
        [SerializeField] string m_CoachingMode = "dating";

        /// <summary>Current state of the coaching session.</summary>
        public CoachingState State { get; private set; } = CoachingState.Idle;

        /// <summary>Fired when coaching state changes.</summary>
        public event Action<CoachingState> OnStateChanged;

        // Telemetry
        float m_LastLLMLatency;
        float m_LastTTSLatency;
        float m_LastRoundtrip;
        int m_SuggestionsReceived;

        void Awake()
        {
            // Auto-wire sibling components if not assigned in Inspector
            if (m_SessionClient == null) m_SessionClient = GetComponent<WingmanSessionClient>();
            if (m_WebSocket == null) m_WebSocket = GetComponent<WingmanWebSocket>();
            if (m_Microphone == null) m_Microphone = GetComponent<WingmanMicrophone>();
            if (m_AudioPlayer == null) m_AudioPlayer = GetComponent<WingmanAudioPlayer>();
            if (m_HUD == null) m_HUD = FindAnyObjectByType<WingmanHUD>(FindObjectsInactive.Include);
            if (m_TranscriptionDisplay == null) m_TranscriptionDisplay = FindAnyObjectByType<WingmanTranscriptionDisplay>(FindObjectsInactive.Include);
            if (m_DebugDisplay == null) m_DebugDisplay = FindAnyObjectByType<WingmanDebugDisplay>(FindObjectsInactive.Include);
            if (m_SuggestionsDisplay == null) m_SuggestionsDisplay = FindAnyObjectByType<WingmanSuggestionsDisplay>(FindObjectsInactive.Include);

            // Disable template components BEFORE their Start() methods run.
            // GoalManager.Start() calls TogglePassthrough(false) which kills passthrough.
            NeutralizeTemplateComponents();
        }

        void Start()
        {
            // Force passthrough on and disable template clutter
            EnsurePassthrough();
            DisableTemplateObjects();

            // Verify backend connection settings
            if (m_SessionClient == null)
            {
                Debug.LogError("[WingmanManager] ❌ WingmanSessionClient is missing! Cannot connect to backend.");
            }
            else
            {
                Debug.Log($"[WingmanManager] ✓ BACKEND MODE ONLY - Backend URL: {m_SessionClient.BackendUrl}");
            }

            if (m_WebSocket == null)
            {
                Debug.LogError("[WingmanManager] ❌ WingmanWebSocket is missing! Cannot connect to backend.");
            }

            if (m_Microphone == null)
            {
                Debug.LogError("[WingmanManager] ❌ WingmanMicrophone is missing! Cannot capture audio.");
            }

            if (m_AutoStart)
                StartCoaching();
        }

        void OnDestroy()
        {
            if (State != CoachingState.Idle)
                StopCoaching();
        }

        // ================================================================
        // Template cleanup & passthrough
        // ================================================================

        /// <summary>
        /// Disable template MonoBehaviours in Awake, BEFORE their Start() methods
        /// can execute. GoalManager.Start() calls TogglePassthrough(false) which
        /// turns off passthrough. FadeMaterial fades the skybox back in when
        /// passthrough is toggled off.
        /// </summary>
        void NeutralizeTemplateComponents()
        {
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                var typeName = mb.GetType().Name;
                if (typeName == "GoalManager" ||
                    typeName == "FadeMaterial" ||
                    typeName == "SpawnedObjectsManager" ||
                    typeName == "OcclusionManager")
                {
                    mb.enabled = false;
                    Debug.Log($"[WingmanManager] Disabled template component: {typeName} on {mb.gameObject.name}");
                }
            }
        }

        /// <summary>
        /// Force passthrough to always be active.
        /// Enables ARCameraManager (= passthrough on Quest) and sets the camera
        /// background to fully transparent as a fallback.
        /// </summary>
        void EnsurePassthrough()
        {
            // Enable ARCameraManager — this IS passthrough on Quest/ARFoundation
            var arCam = FindAnyObjectByType<ARCameraManager>(FindObjectsInactive.Include);
            if (arCam != null)
            {
                arCam.enabled = true;
                DebugLog("ARCameraManager enabled — passthrough forced ON");
            }
            else
            {
                DebugLog("WARNING: ARCameraManager not found — passthrough may not work");
            }

            // Set camera to transparent background as belt-and-suspenders
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.clear;
                DebugLog("Camera background set to transparent");
            }
        }

        /// <summary>
        /// Disable template GameObjects that aren't needed for Wingman.
        /// Keeps the XR Rig (MR Interaction Setup), Permissions Manager, and Lighting.
        /// </summary>
        void DisableTemplateObjects()
        {
            string[] templateNames = new[]
            {
                "Environment",                              // Skybox mesh — blocks passthrough
                "Hand Menu Setup MR Template Variant",      // Template hand menu with coaching controls
                "Coaching UI",                              // Tutorial panel (may already be disabled)
                "Spatial Panel Manipulator",                // Template interactable (may already be disabled)
                "Tap Tooltip",                              // Template tooltip (may already be disabled)
            };

            foreach (var objName in templateNames)
            {
                var obj = GameObject.Find(objName);
                if (obj != null)
                {
                    obj.SetActive(false);
                    DebugLog($"Disabled template object: {objName}");
                }
            }
        }

        // ================================================================
        // Public API
        // ================================================================

        /// <summary>
        /// Begin a coaching session.
        /// BACKEND MODE ONLY: Creates session via REST → connects WebSocket → starts mic.
        /// </summary>
        public void StartCoaching()
        {
            if (State != CoachingState.Idle)
            {
                Debug.LogWarning("[WingmanManager] Already in a coaching session");
                return;
            }

            Debug.Log("[WingmanManager] Starting BACKEND MODE coaching session...");

            // Verify required components
            if (m_SessionClient == null)
            {
                Debug.LogError("[WingmanManager] ❌ Cannot start: WingmanSessionClient is missing!");
                SetState(CoachingState.Error);
                return;
            }

            if (m_WebSocket == null)
            {
                Debug.LogError("[WingmanManager] ❌ Cannot start: WingmanWebSocket is missing!");
                SetState(CoachingState.Error);
                return;
            }

            if (m_Microphone == null)
            {
                Debug.LogError("[WingmanManager] ❌ Cannot start: WingmanMicrophone is missing!");
                SetState(CoachingState.Error);
                return;
            }

            // Subscribe to events
            SubscribeAll();

            SetState(CoachingState.Connecting);
            
            // Step 1: Create session via REST API
            Debug.Log($"[WingmanManager] Creating session at {m_SessionClient.BackendUrl}");
            m_SessionClient.CreateSession();
        }

        /// <summary>
        /// End the coaching session and clean up all subsystems.
        /// </summary>
        public async void StopCoaching()
        {
            if (State == CoachingState.Idle) return;

            SetState(CoachingState.Stopping);

            Debug.Log("[WingmanManager] Stopping coaching session...");

            // Stop mic first
            if (m_Microphone != null)
                m_Microphone.StopRecording();

            // Stop audio
            if (m_AudioPlayer != null)
                m_AudioPlayer.StopPlayback();

            // Disconnect WebSocket (sends end_session message to backend)
            if (m_WebSocket != null)
                await m_WebSocket.Disconnect();

            // End session via REST API
            if (m_SessionClient != null)
                m_SessionClient.EndSession();

            // Clear suggestions display
            if (m_SuggestionsDisplay != null)
                m_SuggestionsDisplay.Clear();

            UnsubscribeAll();
            SetState(CoachingState.Idle);

            Debug.Log("[WingmanManager] Coaching session ended");
        }

        /// <summary>
        /// Toggle coaching on/off (for button binding).
        /// </summary>
        public void ToggleCoaching()
        {
            if (State == CoachingState.Idle)
                StartCoaching();
            else
                StopCoaching();
        }

        // ================================================================
        // Backend Event Handlers
        // ================================================================

        void OnSessionCreated(string sessionId, string wsUrl)
        {
            Debug.Log($"[WingmanManager] ✓ Session created: {sessionId}");
            Debug.Log($"[WingmanManager] Connecting to WebSocket: {wsUrl}");
            
            // Step 2: Connect WebSocket to start receiving suggestions
            _ = m_WebSocket.Connect(sessionId);
        }

        void OnSessionError(string error)
        {
            Debug.LogError($"[WingmanManager] Session error: {error}");
            SetState(CoachingState.Error);

            if (m_HUD != null)
            {
                m_HUD.SetStatusText("Connection failed");
                m_HUD.SetConnectionStatus(ConnectionStatus.Disconnected);
            }
        }

        void OnWebSocketStatusChanged(ConnectionStatus status)
        {
            if (m_HUD != null)
                m_HUD.SetConnectionStatus(status);

            if (m_TranscriptionDisplay != null)
                m_TranscriptionDisplay.OnConnectionStatusChanged(status);

            if (status == ConnectionStatus.Connected)
            {
                SetState(CoachingState.Active);

                if (m_Microphone != null)
                    m_Microphone.StartRecording();

                if (m_HUD != null)
                {
                    m_HUD.SetVisible(true);
                    m_HUD.StartSession();
                }

                if (m_AudioPlayer != null)
                    m_AudioPlayer.PlayConnectedSound();

                Debug.Log("[WingmanManager] Coaching active — listening");
            }
            else if (status == ConnectionStatus.Disconnected && State == CoachingState.Active)
            {
                if (m_HUD != null)
                    m_HUD.SetStatusText("Reconnecting...");
            }
        }

        void OnWebSocketError(string error)
        {
            Debug.LogWarning($"[WingmanManager] WebSocket error: {error}");
        }

        void OnMicAudioChunk(byte[] pcmData)
        {
            // Stream mic audio to backend for STT processing
            if (m_WebSocket != null && m_WebSocket.IsConnected)
            {
                _ = m_WebSocket.SendAudio(pcmData);
            }
            else if (m_WebSocket != null)
            {
                Debug.LogWarning("[WingmanManager] ⚠️ Cannot send audio: WebSocket not connected!");
            }
        }

        void OnSuggestionsReceived(SuggestionPayload payload)
        {
            // Backend suggestions (legacy path)
            m_SuggestionsReceived++;

            if (m_HUD != null)
                m_HUD.DisplaySuggestions(payload);

            if (m_AudioPlayer != null)
                m_AudioPlayer.PlaySuggestionChime();

            Debug.Log($"[WingmanManager] Backend suggestions: {payload.suggestions?.Length ?? 0} (total: {m_SuggestionsReceived})");
        }

        void OnTranscriptReceived(string text, string speaker, bool isPartial)
        {
            if (m_TranscriptionDisplay != null)
                m_TranscriptionDisplay.OnTranscript(text, speaker, isPartial);

            if (!isPartial)
                Debug.Log($"[WingmanManager] Transcript [{speaker}]: {text}");
        }

        void OnTTSAudioReceived(byte[] audioBytes)
        {
            if (m_AudioPlayer != null)
                m_AudioPlayer.PlayTTSAudio(audioBytes);
        }

        void OnSilenceAlert(float seconds)
        {
            // Backend silence alert (legacy path)
            Debug.Log($"[WingmanManager] Silence detected: {seconds:F1}s");

            if (m_HUD != null)
                m_HUD.SetStatusText("Awkward silence...");

            if (m_AudioPlayer != null)
                m_AudioPlayer.PlaySilenceAlert();
        }

        void OnTelemetryReceived(JObject telemetry)
        {
            m_LastLLMLatency = telemetry.Value<float>("llm_latency_ms");
            m_LastTTSLatency = telemetry.Value<float>("tts_latency_ms");
            m_LastRoundtrip = telemetry.Value<float>("roundtrip_ms");

            if (m_ShowTelemetry)
                Debug.Log($"[WingmanManager] Telemetry: LLM={m_LastLLMLatency:F0}ms TTS={m_LastTTSLatency:F0}ms RT={m_LastRoundtrip:F0}ms");
        }

        void OnTherapyResponseReceived(string text)
        {
            Debug.Log($"[WingmanManager] Therapy response: {text}");

            // Display on transcription panel
            if (m_TranscriptionDisplay != null)
                m_TranscriptionDisplay.OnTranscript(text, "ai", false);

            // Play chime
            if (m_AudioPlayer != null)
                m_AudioPlayer.PlaySuggestionChime();
        }

        // ================================================================
        // Mode Switching (Voice Commands)
        // ================================================================

        bool CheckVoiceCommands(string text)
        {
            string lower = text.ToLower().Trim();

            // Therapy mode activation
            if (lower.Contains("switch to therapy") ||
                lower.Contains("therapy mode") ||
                lower.Contains("enable therapy") ||
                lower.Contains("activate therapy") ||
                lower.Contains("start therapy"))
            {
                if (m_CoachingMode != "therapy")
                {
                    Debug.Log("[WingmanManager] Voice command: Switching to THERAPY mode");
                    if (m_TranscriptionDisplay != null)
                        m_TranscriptionDisplay.OnTranscript("Switching to Therapy Mode...", "system", false);
                    SwitchMode("therapy");
                    if (m_AudioPlayer != null)
                        m_AudioPlayer.PlaySuggestionChime();
                }
                return true;
            }

            // Dating mode activation
            if (lower.Contains("switch to dating") ||
                lower.Contains("dating mode") ||
                lower.Contains("coach mode") ||
                lower.Contains("normal mode") ||
                lower.Contains("back to normal"))
            {
                if (m_CoachingMode != "dating")
                {
                    Debug.Log("[WingmanManager] Voice command: Switching to DATING mode");
                    if (m_TranscriptionDisplay != null)
                        m_TranscriptionDisplay.OnTranscript("Switching to Dating Coach Mode...", "system", false);
                    SwitchMode("dating");
                    if (m_AudioPlayer != null)
                        m_AudioPlayer.PlaySuggestionChime();
                }
                return true;
            }

            return false;
        }

        public async void SwitchMode(string newMode)
        {
            if (newMode == m_CoachingMode) return;

            bool wasActive = (State == CoachingState.Active);

            // Stop current session
            if (wasActive)
            {
                StopCoaching();
                await System.Threading.Tasks.Task.Delay(500);
            }

            // Update mode
            m_CoachingMode = newMode;
            if (m_SessionClient != null)
            {
                m_SessionClient.SetMode(newMode);
            }

            // Update UI
            UpdateUIForMode(newMode);

            // Restart if was active
            if (wasActive)
            {
                await System.Threading.Tasks.Task.Delay(500);
                StartCoaching();
            }
        }

        void UpdateUIForMode(string mode)
        {
            if (m_SuggestionsDisplay != null)
            {
                if (mode == "therapy")
                {
                    // Hide suggestions in therapy mode
                    m_SuggestionsDisplay.gameObject.SetActive(false);
                }
                else
                {
                    // Show suggestions in dating mode
                    m_SuggestionsDisplay.gameObject.SetActive(true);
                }
            }

            // Ensure transcription display is always visible
            if (m_TranscriptionDisplay != null)
            {
                m_TranscriptionDisplay.gameObject.SetActive(true);
            }
        }

        void OnSuggestionAccepted(SuggestionData data)
        {
            Debug.Log($"[WingmanManager] Suggestion accepted: \"{data.text}\"");

            // Request TTS from backend
            if (m_WebSocket != null && m_WebSocket.IsConnected)
                _ = m_WebSocket.RequestTTS(data.text);
        }

        void OnWhisperRequested(SuggestionData data)
        {
            Debug.Log($"[WingmanManager] Whisper requested: \"{data.text}\"");

            // Request TTS from backend
            if (m_WebSocket != null && m_WebSocket.IsConnected)
                _ = m_WebSocket.RequestTTS(data.text);
        }

        void OnVoiceActivityChanged(bool isSpeaking)
        {
            DebugLog($"VAD: {(isSpeaking ? "SPEECH START" : "SPEECH END")}");

            if (m_TranscriptionDisplay != null)
                m_TranscriptionDisplay.OnVoiceActivity(isSpeaking);
        }

        void OnMicRecordingStateChanged(bool isRecording)
        {
            DebugLog($"Mic recording: {(isRecording ? "STARTED" : "STOPPED")}");

            if (m_TranscriptionDisplay != null)
                m_TranscriptionDisplay.OnRecordingStateChanged(isRecording);
        }

        // ================================================================
        // Subscription management
        // ================================================================

        void SubscribeAll()
        {
            // Session client (legacy backend mode)
            if (m_SessionClient != null)
            {
                m_SessionClient.OnSessionCreated += OnSessionCreated;
                m_SessionClient.OnSessionError += OnSessionError;
            }

            // WebSocket (legacy backend mode)
            if (m_WebSocket != null)
            {
                m_WebSocket.OnConnectionStatusChanged += OnWebSocketStatusChanged;
                m_WebSocket.OnSuggestionsReceived += OnSuggestionsReceived;
                m_WebSocket.OnTranscriptReceived += OnTranscriptReceived;
                m_WebSocket.OnTherapyResponseReceived += OnTherapyResponseReceived;
                m_WebSocket.OnTTSAudioReceived += OnTTSAudioReceived;
                m_WebSocket.OnSilenceAlert += OnSilenceAlert;
                m_WebSocket.OnTelemetryReceived += OnTelemetryReceived;
                m_WebSocket.OnError += OnWebSocketError;
            }

            // Microphone
            if (m_Microphone != null)
            {
                m_Microphone.OnAudioChunkReady += OnMicAudioChunk;
                m_Microphone.OnVoiceActivityChanged += OnVoiceActivityChanged;
                m_Microphone.OnRecordingStateChanged += OnMicRecordingStateChanged;
            }

            // HUD
            if (m_HUD != null)
            {
                m_HUD.OnSuggestionAccepted += OnSuggestionAccepted;
                m_HUD.OnWhisperRequested += OnWhisperRequested;
            }
        }

        void UnsubscribeAll()
        {
            if (m_SessionClient != null)
            {
                m_SessionClient.OnSessionCreated -= OnSessionCreated;
                m_SessionClient.OnSessionError -= OnSessionError;
            }

            if (m_WebSocket != null)
            {
                m_WebSocket.OnConnectionStatusChanged -= OnWebSocketStatusChanged;
                m_WebSocket.OnSuggestionsReceived -= OnSuggestionsReceived;
                m_WebSocket.OnTranscriptReceived -= OnTranscriptReceived;
                m_WebSocket.OnTherapyResponseReceived -= OnTherapyResponseReceived;
                m_WebSocket.OnTTSAudioReceived -= OnTTSAudioReceived;
                m_WebSocket.OnSilenceAlert -= OnSilenceAlert;
                m_WebSocket.OnTelemetryReceived -= OnTelemetryReceived;
                m_WebSocket.OnError -= OnWebSocketError;
            }

            if (m_Microphone != null)
            {
                m_Microphone.OnAudioChunkReady -= OnMicAudioChunk;
                m_Microphone.OnVoiceActivityChanged -= OnVoiceActivityChanged;
                m_Microphone.OnRecordingStateChanged -= OnMicRecordingStateChanged;
            }

            if (m_HUD != null)
            {
                m_HUD.OnSuggestionAccepted -= OnSuggestionAccepted;
                m_HUD.OnWhisperRequested -= OnWhisperRequested;
            }
        }

        // ================================================================
        // State management
        // ================================================================

        void SetState(CoachingState newState)
        {
            if (State == newState) return;
            Debug.Log($"[WingmanManager] State: {State} → {newState}");
            DebugLog($"State: {State} → {newState}");
            State = newState;
            OnStateChanged?.Invoke(newState);
        }

        // ================================================================
        // Debug display helpers
        // ================================================================

        void DebugLog(string msg)
        {
            if (m_DebugDisplay != null)
                m_DebugDisplay.Log(msg);
            else
                Debug.Log($"[WingmanManager] {msg}");
        }

        void DebugLogError(string msg)
        {
            if (m_DebugDisplay != null)
                m_DebugDisplay.LogError(msg);
            else
                Debug.LogError($"[WingmanManager] {msg}");
        }
    }

    public enum CoachingState
    {
        Idle,
        Connecting,
        Active,
        Stopping,
        Error
    }
}
