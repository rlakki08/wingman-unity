using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Wingman
{
    /// <summary>
    /// Direct REST client for Google Gemini, running entirely on-device.
    /// Replaces the backend's llm.py — no server required.
    ///
    /// Uses the Gemini REST API v1beta with JSON-mode output:
    ///   POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}
    ///
    /// Thread-safe: all callbacks are dispatched on the Unity main thread via coroutines.
    ///
    /// Usage:
    ///   1. Attach to the Wingman Manager GameObject.
    ///   2. Set your Gemini API key in the Inspector (or leave the default).
    ///   3. Call GenerateContent(userMessage, systemInstruction, callback).
    ///   4. The callback receives (string rawJson, string error).
    /// </summary>
    public class GeminiClient : MonoBehaviour
    {
        [Header("Gemini API")]
        [Tooltip("Google Gemini API key.")]
        [SerializeField] string m_ApiKey = "AIzaSyCmJb7kk51zDVAcHi1k6NHVuGXHzugg9uM";

        [Tooltip("Gemini model ID.")]
        [SerializeField] string m_Model = "gemini-3-pro-preview";

        [Header("Generation Config")]
        [Tooltip("Sampling temperature (0.0 - 2.0).")]
        [SerializeField] float m_Temperature = 0.7f;

        [Tooltip("Top-p nucleus sampling threshold.")]
        [SerializeField] float m_TopP = 0.95f;

        [Tooltip("Maximum output tokens.")]
        [SerializeField] int m_MaxOutputTokens = 512;

        [Header("Settings")]
        [Tooltip("Request timeout in seconds.")]
        [SerializeField] int m_TimeoutSeconds = 30;

        [Tooltip("Maximum concurrent requests.")]
        [SerializeField] int m_MaxConcurrentRequests = 2;

        [Header("Retry")]
        [Tooltip("Maximum number of retry attempts on transient failure (0 = no retry).")]
        [SerializeField] int m_MaxRetries = 2;

        [Tooltip("Initial backoff delay in seconds (doubles each retry).")]
        [SerializeField] float m_InitialBackoffSeconds = 1.5f;

        [Tooltip("HTTP status codes that are retryable (e.g. 429, 500, 502, 503).")]
        static readonly int[] k_RetryableStatusCodes = { 429, 500, 502, 503, 504, 0 }; // 0 = network error / timeout

        [Header("Debug")]
        [SerializeField] bool m_DebugLog = true;

        WingmanDebugDisplay m_DebugDisplay;

        int m_ActiveRequests;
        int m_TotalRequests;

        /// <summary>Fired when a raw Gemini response or error is received. (rawBody, extractedText, error)</summary>
        public event Action<string, string, string> OnRawResponse;

        /// <summary>Fired when a Gemini request is about to be sent. (requestId, userMessage)</summary>
        public event Action<int, string> OnRequestSent;

        /// <summary>Number of in-flight Gemini requests.</summary>
        public int ActiveRequests => m_ActiveRequests;

        /// <summary>Whether we can accept another request without exceeding concurrency limit.</summary>
        public bool CanAcceptRequest => m_ActiveRequests < m_MaxConcurrentRequests;

        /// <summary>Whether the API key is configured.</summary>
        public bool IsConfigured => !string.IsNullOrEmpty(m_ApiKey);

        void Start()
        {
            m_DebugDisplay = FindAnyObjectByType<WingmanDebugDisplay>(FindObjectsInactive.Include);
            GeminiDebug($"GeminiClient init. Model={m_Model} Key={(!string.IsNullOrEmpty(m_ApiKey) ? m_ApiKey.Substring(0, 8) + "..." : "NOT SET")}");
        }

        // ================================================================
        // Public API
        // ================================================================

        /// <summary>
        /// Send a prompt to Gemini and receive the raw JSON text response.
        /// The response is constrained to application/json via responseMimeType.
        /// </summary>
        /// <param name="userMessage">The user message (transcript context).</param>
        /// <param name="systemInstruction">System prompt to guide the model.</param>
        /// <param name="callback">Called with (responseText, error). On success error is null.</param>
        public void GenerateContent(string userMessage, string systemInstruction, Action<string, string> callback)
        {
            if (string.IsNullOrEmpty(m_ApiKey))
            {
                callback?.Invoke(null, "Gemini API key not configured");
                return;
            }

            if (!CanAcceptRequest)
            {
                GeminiDebug($"Too many requests ({m_ActiveRequests}/{m_MaxConcurrentRequests}), dropping");
                callback?.Invoke(null, "Too many concurrent Gemini requests");
                return;
            }

            StartCoroutine(GenerateContentCoroutine(userMessage, systemInstruction, callback));
        }

        // ================================================================
        // Internal — HTTP Request
        // ================================================================

        IEnumerator GenerateContentCoroutine(string userMessage, string systemInstruction, Action<string, string> callback)
        {
            m_ActiveRequests++;
            m_TotalRequests++;
            int requestId = m_TotalRequests;

            // Build the Gemini REST API URL
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{m_Model}:generateContent?key={m_ApiKey}";

            // Build request body
            var requestBody = new JObject
            {
                ["system_instruction"] = new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = systemInstruction }
                    }
                },
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["parts"] = new JArray
                        {
                            new JObject { ["text"] = userMessage }
                        }
                    }
                },
                ["generationConfig"] = new JObject
                {
                    ["temperature"] = m_Temperature,
                    ["topP"] = m_TopP,
                    ["maxOutputTokens"] = m_MaxOutputTokens,
                    ["responseMimeType"] = "application/json"
                }
            };

            string jsonBody = requestBody.ToString(Formatting.None);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

            GeminiDebug($"#{requestId} POST Gemini ({bodyBytes.Length}B)...");
            OnRequestSent?.Invoke(requestId, userMessage);

            // --- Retry loop with exponential backoff ---
            int attempt = 0;
            int maxAttempts = 1 + Mathf.Max(0, m_MaxRetries); // 1 initial + N retries

            while (attempt < maxAttempts)
            {
                if (attempt > 0)
                {
                    float backoff = m_InitialBackoffSeconds * Mathf.Pow(2f, attempt - 1);
                    GeminiDebug($"#{requestId} Retry {attempt}/{m_MaxRetries} after {backoff:F1}s backoff...");
                    yield return new WaitForSeconds(backoff);
                }

                float startTime = Time.realtimeSinceStartup;

                using (var request = new UnityWebRequest(url, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = m_TimeoutSeconds;

                    yield return request.SendWebRequest();

                    float elapsed = Time.realtimeSinceStartup - startTime;
                    long httpCode = request.responseCode;

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        string responseBody = request.downloadHandler.text;
                        GeminiDebug($"#{requestId} OK ({elapsed:F1}s, {responseBody.Length} chars, attempt {attempt + 1})");

                        // Parse the Gemini response to extract the text content
                        try
                        {
                            var responseJson = JObject.Parse(responseBody);
                            var candidates = responseJson["candidates"] as JArray;
                            if (candidates != null && candidates.Count > 0)
                            {
                                var firstCandidate = candidates[0];
                                var parts = firstCandidate?["content"]?["parts"] as JArray;
                                if (parts != null && parts.Count > 0)
                                {
                                    string text = parts[0]?.Value<string>("text") ?? "";
                                    GeminiDebug($"#{requestId} Extracted {text.Length} chars");
                                    OnRawResponse?.Invoke(responseBody, text, null);
                                    callback?.Invoke(text, null);
                                    m_ActiveRequests--;
                                    yield break; // Success — exit retry loop
                                }
                                else
                                {
                                    string err = "No content parts in Gemini response";
                                    GeminiDebug($"#{requestId} No parts in response");
                                    OnRawResponse?.Invoke(responseBody, null, err);
                                    callback?.Invoke(null, err);
                                    m_ActiveRequests--;
                                    yield break; // Parse issue, not retryable
                                }
                            }
                            else
                            {
                                // Check for safety blocking
                                var promptFeedback = responseJson["promptFeedback"];
                                string blockReason = promptFeedback?.Value<string>("blockReason") ?? "Unknown";
                                string err = $"Gemini blocked: {blockReason}";
                                GeminiDebug($"#{requestId} Blocked: {blockReason}");
                                OnRawResponse?.Invoke(responseBody, null, err);
                                callback?.Invoke(null, err);
                                m_ActiveRequests--;
                                yield break; // Content blocked, not retryable
                            }
                        }
                        catch (Exception ex)
                        {
                            string err = $"Response parse error: {ex.Message}";
                            GeminiDebug($"#{requestId} Parse error: {ex.Message}");
                            OnRawResponse?.Invoke(responseBody, null, err);
                            callback?.Invoke(null, err);
                            m_ActiveRequests--;
                            yield break; // Parse error, not retryable
                        }
                    }
                    else
                    {
                        // HTTP failure — check if retryable
                        string error = $"HTTP {httpCode}: {request.error}";
                        string body = request.downloadHandler?.text ?? "";

                        bool isRetryable = IsRetryableStatusCode(httpCode);
                        bool hasRetriesLeft = attempt + 1 < maxAttempts;

                        if (isRetryable && hasRetriesLeft)
                        {
                            // Will retry — log but don't fire callback yet
                            GeminiDebug($"#{requestId} FAILED ({elapsed:F1}s): {error} — will retry");
                            attempt++;
                            continue;
                        }

                        // Final failure — no more retries
                        GeminiDebug($"#{requestId} FAILED ({elapsed:F1}s): {error} (attempt {attempt + 1}/{maxAttempts}, giving up)");

                        // Try to extract error detail from response body
                        string fullErr;
                        if (!string.IsNullOrEmpty(body))
                        {
                            try
                            {
                                var errorJson = JObject.Parse(body);
                                var errorObj = errorJson["error"];
                                string message = errorObj?.Value<string>("message") ?? body;
                                fullErr = $"{error} - {message}";
                            }
                            catch
                            {
                                fullErr = $"{error} - {body}";
                            }
                        }
                        else
                        {
                            fullErr = error;
                        }

                        OnRawResponse?.Invoke(body, null, fullErr);
                        callback?.Invoke(null, fullErr);
                        m_ActiveRequests--;
                        yield break;
                    }
                }

                attempt++;
            }

            // Should not reach here, but safety net
            m_ActiveRequests--;
        }

        static bool IsRetryableStatusCode(long code)
        {
            for (int i = 0; i < k_RetryableStatusCodes.Length; i++)
            {
                if (k_RetryableStatusCodes[i] == code) return true;
            }
            return false;
        }

        // ================================================================
        // Debug
        // ================================================================

        void GeminiDebug(string msg)
        {
            if (m_DebugLog)
                Debug.Log($"[GeminiClient] {msg}");
            if (m_DebugDisplay != null)
                m_DebugDisplay.Log($"Gemini: {msg}");
        }
    }
}
