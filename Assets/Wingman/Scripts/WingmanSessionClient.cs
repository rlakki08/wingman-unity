using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Wingman
{
    /// <summary>
    /// Handles REST API calls to the Wingman backend for session lifecycle.
    ///
    /// Flow:
    ///   1. POST /api/sessions/ → creates a session, returns { session_id, websocket_url }
    ///   2. Pass session_id to WingmanWebSocket.Connect()
    ///   3. POST /api/sessions/{id}/end → ends the session
    ///
    /// Authentication: sends Authorization: Bearer {token} header.
    /// When auth is not configured on the backend, it accepts "anonymous" users.
    /// </summary>
    public class WingmanSessionClient : MonoBehaviour
    {
        [Header("Backend")]
        [Tooltip("HTTP base URL of the Wingman backend (e.g. http://localhost:8000).")]
        [SerializeField] string m_BackendUrl = "http://localhost:8000";

        [Header("Session Preferences")]
        [SerializeField] bool m_PrivacyOptIn = false;
        [SerializeField] string m_SuggestionStyle = "balanced";
        [SerializeField] bool m_TTSEnabled = true;
        [SerializeField] string m_Mode = "dating"; // "dating" or "therapy"

        /// <summary>Fired when a session is successfully created.</summary>
        public event Action<string, string> OnSessionCreated; // sessionId, websocketUrl

        /// <summary>Fired when session creation fails.</summary>
        public event Action<string> OnSessionError; // error message

        string m_AuthToken;
        string m_CurrentSessionId;

        public string CurrentSessionId => m_CurrentSessionId;
        public string BackendUrl => m_BackendUrl;

        /// <summary>
        /// Set the auth token for API calls. Pass null/empty for anonymous mode.
        /// </summary>
        public void SetAuthToken(string token)
        {
            m_AuthToken = token;
        }

        /// <summary>
        /// Set the coaching mode ("dating" or "therapy").
        /// </summary>
        public void SetMode(string mode)
        {
            m_Mode = mode;
        }

        /// <summary>
        /// Create a new coaching session via the REST API.
        /// </summary>
        public async void CreateSession()
        {
            string url = $"{m_BackendUrl.TrimEnd('/')}/api/sessions/";

            var body = new
            {
                privacy_opt_in = m_PrivacyOptIn,
                suggestion_style = m_SuggestionStyle,
                tts_enabled = m_TTSEnabled,
                mode = m_Mode
            };

            string json = JsonConvert.SerializeObject(body);
            Debug.Log($"[WingmanSession] Creating session at {url}");

            try
            {
                using (var request = new UnityWebRequest(url, "POST"))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                    request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");

                    if (!string.IsNullOrEmpty(m_AuthToken))
                        request.SetRequestHeader("Authorization", $"Bearer {m_AuthToken}");
                    else
                        request.SetRequestHeader("Authorization", "Bearer anonymous");

                    var operation = request.SendWebRequest();

                    // Wait for completion
                    while (!operation.isDone)
                        await Task.Yield();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        string error = $"Session creation failed: {request.error} ({request.responseCode})";
                        if (request.downloadHandler?.text != null)
                            error += $" — {request.downloadHandler.text}";

                        Debug.LogError($"[WingmanSession] {error}");
                        OnSessionError?.Invoke(error);
                        return;
                    }

                    string response = request.downloadHandler.text;
                    var data = JObject.Parse(response);

                    m_CurrentSessionId = data.Value<string>("session_id");
                    string wsUrl = data.Value<string>("websocket_url");

                    Debug.Log($"[WingmanSession] Session created: {m_CurrentSessionId}");
                    OnSessionCreated?.Invoke(m_CurrentSessionId, wsUrl);
                }
            }
            catch (Exception ex)
            {
                string error = $"Session creation exception: {ex.Message}";
                Debug.LogError($"[WingmanSession] {error}");
                OnSessionError?.Invoke(error);
            }
        }

        /// <summary>
        /// End the current session via the REST API.
        /// </summary>
        public async void EndSession()
        {
            if (string.IsNullOrEmpty(m_CurrentSessionId))
            {
                Debug.LogWarning("[WingmanSession] No active session to end");
                return;
            }

            string url = $"{m_BackendUrl.TrimEnd('/')}/api/sessions/{m_CurrentSessionId}/end";

            try
            {
                using (var request = new UnityWebRequest(url, "POST"))
                {
                    request.downloadHandler = new DownloadHandlerBuffer();

                    if (!string.IsNullOrEmpty(m_AuthToken))
                        request.SetRequestHeader("Authorization", $"Bearer {m_AuthToken}");
                    else
                        request.SetRequestHeader("Authorization", "Bearer anonymous");

                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                        await Task.Yield();

                    if (request.result == UnityWebRequest.Result.Success)
                        Debug.Log($"[WingmanSession] Session ended: {m_CurrentSessionId}");
                    else
                        Debug.LogWarning($"[WingmanSession] End session failed: {request.error}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WingmanSession] End session error: {ex.Message}");
            }

            m_CurrentSessionId = null;
        }
    }
}
