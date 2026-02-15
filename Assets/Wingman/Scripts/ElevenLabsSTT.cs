using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace Wingman
{
    /// <summary>
    /// Direct ElevenLabs Scribe V2 speech-to-text client.
    ///
    /// Sends completed PCM16 speech segments to the ElevenLabs REST API
    /// and returns transcription results. No backend server required.
    ///
    /// API: POST https://api.elevenlabs.io/v1/speech-to-text
    /// Format: multipart/form-data with model_id, file, file_format, language_code
    ///
    /// Usage:
    ///   1. Attach to a GameObject (e.g. Wingman Manager)
    ///   2. Call TranscribeSegment(pcmBytes) when a speech segment is ready
    ///   3. Subscribe to OnTranscriptionResult for final text
    ///   4. Subscribe to OnTranscriptionError for failures
    /// </summary>
    public class ElevenLabsSTT : MonoBehaviour
    {
        [Header("ElevenLabs API")]
        [Tooltip("ElevenLabs API key (xi-api-key header).")]
        [SerializeField] string m_ApiKey = "sk_fa45860c9582630a7c5193b4f832820cd741f7a13ded55eb";

        [Tooltip("STT model ID. scribe_v2 is recommended for accuracy.")]
        [SerializeField] string m_ModelId = "scribe_v2";

        [Tooltip("Language code for transcription (ISO 639-1).")]
        [SerializeField] string m_LanguageCode = "en";

        [Header("Settings")]
        [Tooltip("Maximum concurrent transcription requests.")]
        [SerializeField] int m_MaxConcurrentRequests = 3;

        [Tooltip("Request timeout in seconds.")]
        [SerializeField] int m_TimeoutSeconds = 30;

        [Header("Debug")]
        [SerializeField] bool m_DebugLog = true;

        WingmanDebugDisplay m_DebugDisplay;

        // --- Events ---

        /// <summary>
        /// Fired when transcription completes successfully.
        /// Parameters: transcribed text, language code detected, duration of audio in seconds.
        /// </summary>
        public event Action<string, string, float> OnTranscriptionResult;

        /// <summary>
        /// Fired when transcription fails.
        /// Parameter: error message.
        /// </summary>
        public event Action<string> OnTranscriptionError;

        /// <summary>
        /// Fired when a request starts or finishes, with current active request count.
        /// </summary>
        public event Action<int> OnActiveRequestCountChanged;

        // --- State ---
        const string k_ApiUrl = "https://api.elevenlabs.io/v1/speech-to-text";
        int m_ActiveRequests;
        int m_TotalRequests;
        int m_SuccessfulRequests;
        int m_FailedRequests;

        /// <summary>Number of currently in-flight transcription requests.</summary>
        public int ActiveRequests => m_ActiveRequests;

        /// <summary>Whether the client is ready to accept new requests.</summary>
        public bool CanAcceptRequest => m_ActiveRequests < m_MaxConcurrentRequests;

        void Start()
        {
            m_DebugDisplay = FindAnyObjectByType<WingmanDebugDisplay>(FindObjectsInactive.Include);
            STTDebug($"ElevenLabsSTT init. Key={m_ApiKey.Substring(0, 8)}... Model={m_ModelId} Lang={m_LanguageCode}");
        }

        // ================================================================
        // Public API
        // ================================================================

        /// <summary>
        /// Send a completed PCM16 speech segment to ElevenLabs for transcription.
        /// The result arrives asynchronously via OnTranscriptionResult.
        /// </summary>
        /// <param name="pcm16Bytes">Raw PCM16 audio bytes (16kHz, mono, signed 16-bit LE).</param>
        public void TranscribeSegment(byte[] pcm16Bytes)
        {
            if (pcm16Bytes == null || pcm16Bytes.Length == 0)
            {
                STTDebug("Empty segment, skipping");
                Debug.LogWarning("[ElevenLabsSTT] Empty audio segment, skipping");
                return;
            }

            if (!CanAcceptRequest)
            {
                STTDebug($"Too many requests ({m_ActiveRequests}/{m_MaxConcurrentRequests}), dropping");
                Debug.LogWarning($"[ElevenLabsSTT] Too many concurrent requests ({m_ActiveRequests}/{m_MaxConcurrentRequests}), dropping segment");
                return;
            }

            float durationSec = pcm16Bytes.Length / (16000f * 2f); // 16kHz, 2 bytes per sample
            STTDebug($"TranscribeSegment: {durationSec:F1}s, {pcm16Bytes.Length}B (req#{m_TotalRequests + 1})");

            if (m_DebugLog)
                Debug.Log($"[ElevenLabsSTT] Sending segment: {durationSec:F1}s, {pcm16Bytes.Length} bytes (request #{m_TotalRequests + 1})");

            StartCoroutine(TranscribeCoroutine(pcm16Bytes, durationSec));
        }

        // ================================================================
        // Internal — HTTP Request
        // ================================================================

        IEnumerator TranscribeCoroutine(byte[] pcmBytes, float audioDuration)
        {
            m_ActiveRequests++;
            m_TotalRequests++;
            int requestId = m_TotalRequests;
            OnActiveRequestCountChanged?.Invoke(m_ActiveRequests);

            float startTime = Time.realtimeSinceStartup;

            STTDebug($"#{requestId} Building form data...");

            // Build multipart form data
            var form = new WWWForm();
            form.AddField("model_id", m_ModelId);
            form.AddField("language_code", m_LanguageCode);

            // Add raw PCM bytes as a file upload
            form.AddBinaryData("file", pcmBytes, "audio.pcm", "audio/x-raw");

            // file_format tells ElevenLabs how to decode the raw bytes
            form.AddField("file_format", "pcm_s16le_16");

            STTDebug($"#{requestId} POST {k_ApiUrl} ({pcmBytes.Length}B)...");

            using (var request = UnityWebRequest.Post(k_ApiUrl, form))
            {
                // Set auth header
                request.SetRequestHeader("xi-api-key", m_ApiKey);

                request.timeout = m_TimeoutSeconds;

                yield return request.SendWebRequest();

                float elapsed = Time.realtimeSinceStartup - startTime;

                STTDebug($"#{requestId} Response: HTTP {request.responseCode} ({elapsed:F1}s)");

                if (request.result == UnityWebRequest.Result.Success)
                {
                    m_SuccessfulRequests++;
                    string responseBody = request.downloadHandler.text;

                    STTDebug($"#{requestId} Body: {(responseBody.Length > 120 ? responseBody.Substring(0, 120) + "..." : responseBody)}");

                    if (m_DebugLog)
                        Debug.Log($"[ElevenLabsSTT] Response #{requestId} ({elapsed:F1}s): {responseBody}");

                    // Parse response JSON
                    try
                    {
                        var json = JObject.Parse(responseBody);
                        string text = json.Value<string>("text") ?? "";
                        string detectedLang = json.Value<string>("language_code") ?? m_LanguageCode;

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            STTDebug($"#{requestId} TEXT: \"{text}\"");

                            if (m_DebugLog)
                                Debug.Log($"[ElevenLabsSTT] Transcript #{requestId}: \"{text}\" (lang={detectedLang}, latency={elapsed:F1}s)");

                            OnTranscriptionResult?.Invoke(text.Trim(), detectedLang, audioDuration);
                        }
                        else
                        {
                            STTDebug($"#{requestId} (empty/silence)");
                            if (m_DebugLog)
                                Debug.Log($"[ElevenLabsSTT] Transcript #{requestId}: (empty/silence)");
                        }
                    }
                    catch (Exception ex)
                    {
                        STTDebug($"#{requestId} JSON PARSE ERR: {ex.Message}");
                        Debug.LogError($"[ElevenLabsSTT] JSON parse error: {ex.Message}\nResponse: {responseBody}");
                        OnTranscriptionError?.Invoke($"JSON parse error: {ex.Message}");
                        m_FailedRequests++;
                    }
                }
                else
                {
                    m_FailedRequests++;
                    string error = $"HTTP {request.responseCode}: {request.error}";
                    string body = request.downloadHandler?.text ?? "";

                    STTDebug($"#{requestId} FAILED: {error}");
                    if (!string.IsNullOrEmpty(body))
                        STTDebug($"#{requestId} ErrBody: {(body.Length > 120 ? body.Substring(0, 120) + "..." : body)}");

                    Debug.LogError($"[ElevenLabsSTT] Request #{requestId} failed ({elapsed:F1}s): {error}\n{body}");

                    // Try to parse error detail from response body
                    if (!string.IsNullOrEmpty(body))
                    {
                        try
                        {
                            var errorJson = JObject.Parse(body);
                            string detail = errorJson.Value<string>("detail") ?? body;
                            OnTranscriptionError?.Invoke($"{error} — {detail}");
                        }
                        catch
                        {
                            OnTranscriptionError?.Invoke(error);
                        }
                    }
                    else
                    {
                        OnTranscriptionError?.Invoke(error);
                    }
                }
            }

            m_ActiveRequests--;
            OnActiveRequestCountChanged?.Invoke(m_ActiveRequests);
        }

        // ================================================================
        // Debug
        // ================================================================

        /// <summary>Get a summary string of STT client stats.</summary>
        public string GetStats()
        {
            return $"Requests: {m_TotalRequests} total, {m_SuccessfulRequests} ok, {m_FailedRequests} failed, {m_ActiveRequests} active";
        }

        void STTDebug(string msg)
        {
            if (m_DebugDisplay != null)
                m_DebugDisplay.Log($"STT: {msg}");
        }
    }
}
