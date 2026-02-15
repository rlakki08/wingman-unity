using UnityEngine;
using TMPro;

namespace Wingman
{
    /// <summary>
    /// Debug display for Gemini LLM input/output.
    ///
    /// Shows a live stream of:
    ///   - Outgoing prompts sent to Gemini (transcript context)
    ///   - Raw JSON responses from Gemini
    ///   - Parsed suggestion text
    ///   - Errors and status
    ///
    /// Designed as a wide panel (copy of the Transcription Modal) so the
    /// full JSON output is readable. Newest entries appear at the top
    /// with oldest scrolling off the bottom.
    ///
    /// Auto-wires to GeminiClient and GeminiSuggestionService events.
    /// </summary>
    public class WingmanGeminiDisplay : MonoBehaviour
    {
        [Header("UI References")]
        [Tooltip("TMP text for the status line (top strip).")]
        [SerializeField] TextMeshProUGUI m_StatusText;

        [Tooltip("TMP text for the main Gemini output stream.")]
        [SerializeField] TextMeshProUGUI m_OutputText;

        [Header("Dependencies")]
        [Tooltip("GeminiClient to subscribe to raw responses.")]
        [SerializeField] GeminiClient m_GeminiClient;

        [Tooltip("GeminiSuggestionService to subscribe to parsed suggestions.")]
        [SerializeField] GeminiSuggestionService m_GeminiService;

        [Header("Settings")]
        [Tooltip("Maximum log entries to keep.")]
        [SerializeField] int m_MaxEntries = 20;

        [Tooltip("Maximum characters per entry (truncates long JSON).")]
        [SerializeField] int m_MaxEntryLength = 400;

        [Header("Colors")]
        [SerializeField] Color m_RequestColor = new Color(0.6f, 0.8f, 1f, 0.9f);
        [SerializeField] Color m_ResponseColor = new Color(0.6f, 1f, 0.6f, 0.9f);
        [SerializeField] Color m_ErrorColor = new Color(1f, 0.5f, 0.5f, 0.9f);
        [SerializeField] Color m_SuggestionColor = new Color(1f, 1f, 0.6f, 0.9f);
        [SerializeField] Color m_OldEntryColor = new Color(1f, 1f, 1f, 0.3f);

        // Internal state
        string[] m_Entries;
        int m_EntryCount;
        float m_StartTime;
        int m_RequestCount;
        int m_ResponseCount;
        int m_ErrorCount;

        void Awake()
        {
            m_Entries = new string[m_MaxEntries];
            m_EntryCount = 0;
            m_StartTime = Time.realtimeSinceStartup;

            if (m_StatusText != null)
            {
                m_StatusText.text = "Gemini Monitor - Waiting...";
                m_StatusText.color = new Color(1f, 1f, 1f, 0.5f);
            }

            if (m_OutputText != null)
                m_OutputText.text = "";
        }

        void OnEnable()
        {
            // Auto-find dependencies if not assigned
            if (m_GeminiClient == null)
                m_GeminiClient = FindAnyObjectByType<GeminiClient>(FindObjectsInactive.Include);
            if (m_GeminiService == null)
                m_GeminiService = FindAnyObjectByType<GeminiSuggestionService>(FindObjectsInactive.Include);

            Subscribe();
        }

        void OnDisable()
        {
            Unsubscribe();
        }

        // ================================================================
        // Event Subscriptions
        // ================================================================

        void Subscribe()
        {
            if (m_GeminiClient != null)
            {
                m_GeminiClient.OnRequestSent += OnRequestSent;
                m_GeminiClient.OnRawResponse += OnRawResponse;
            }

            if (m_GeminiService != null)
            {
                m_GeminiService.OnSuggestionsReady += OnSuggestionsReady;
                m_GeminiService.OnError += OnServiceError;
                m_GeminiService.OnSilenceDetected += OnSilenceDetected;
            }
        }

        void Unsubscribe()
        {
            if (m_GeminiClient != null)
            {
                m_GeminiClient.OnRequestSent -= OnRequestSent;
                m_GeminiClient.OnRawResponse -= OnRawResponse;
            }

            if (m_GeminiService != null)
            {
                m_GeminiService.OnSuggestionsReady -= OnSuggestionsReady;
                m_GeminiService.OnError -= OnServiceError;
                m_GeminiService.OnSilenceDetected -= OnSilenceDetected;
            }
        }

        // ================================================================
        // Event Handlers
        // ================================================================

        void OnRequestSent(int requestId, string userMessage)
        {
            m_RequestCount++;
            string truncated = Truncate(userMessage, m_MaxEntryLength);
            string hex = ColorUtility.ToHtmlStringRGBA(m_RequestColor);
            PushEntry($"<color=#{hex}>[REQ #{requestId}] {truncated}</color>");
            UpdateStatus();
        }

        void OnRawResponse(string rawBody, string extractedText, string error)
        {
            if (error != null)
            {
                m_ErrorCount++;
                string hex = ColorUtility.ToHtmlStringRGBA(m_ErrorColor);
                string truncErr = Truncate(error, m_MaxEntryLength);
                PushEntry($"<color=#{hex}>[ERR] {truncErr}</color>");

                if (!string.IsNullOrEmpty(rawBody))
                {
                    string truncBody = Truncate(rawBody, m_MaxEntryLength);
                    PushEntry($"<color=#{hex}>[RAW] {truncBody}</color>");
                }
            }
            else
            {
                m_ResponseCount++;
                string hex = ColorUtility.ToHtmlStringRGBA(m_ResponseColor);

                // Show extracted text (the parsed JSON from Gemini)
                if (!string.IsNullOrEmpty(extractedText))
                {
                    string truncText = Truncate(extractedText, m_MaxEntryLength);
                    PushEntry($"<color=#{hex}>[RESP] {truncText}</color>");
                }
            }

            UpdateStatus();
        }

        void OnSuggestionsReady(SuggestionPayload payload)
        {
            if (payload.suggestions == null || payload.suggestions.Length == 0) return;

            string hex = ColorUtility.ToHtmlStringRGBA(m_SuggestionColor);
            var sb = new System.Text.StringBuilder();
            sb.Append($"<color=#{hex}>[SUGGESTIONS] topic={payload.dominantTopic} engagement={payload.engagementEstimate:F2}");
            for (int i = 0; i < payload.suggestions.Length; i++)
            {
                sb.Append($"\n  {i + 1}. \"{payload.suggestions[i].text}\" ({payload.suggestions[i].style})");
            }
            sb.Append("</color>");
            PushEntry(sb.ToString());
        }

        void OnServiceError(string error)
        {
            m_ErrorCount++;
            string hex = ColorUtility.ToHtmlStringRGBA(m_ErrorColor);
            PushEntry($"<color=#{hex}>[SVC ERR] {Truncate(error, m_MaxEntryLength)}</color>");
            UpdateStatus();
        }

        void OnSilenceDetected(float seconds)
        {
            string hex = ColorUtility.ToHtmlStringRGBA(m_SuggestionColor);
            PushEntry($"<color=#{hex}>[SILENCE] {seconds:F1}s — proactive suggestions triggered</color>");
        }

        // ================================================================
        // Public API
        // ================================================================

        /// <summary>Clear all entries.</summary>
        public void Clear()
        {
            m_EntryCount = 0;
            m_RequestCount = 0;
            m_ResponseCount = 0;
            m_ErrorCount = 0;
            if (m_OutputText != null) m_OutputText.text = "";
            UpdateStatus();
        }

        // ================================================================
        // Internal
        // ================================================================

        void PushEntry(string entry)
        {
            float elapsed = Time.realtimeSinceStartup - m_StartTime;
            string timestamped = $"[{elapsed:F1}s] {entry}";

            if (m_EntryCount < m_MaxEntries)
            {
                m_Entries[m_EntryCount] = timestamped;
                m_EntryCount++;
            }
            else
            {
                // Shift up, drop oldest
                for (int i = 0; i < m_MaxEntries - 1; i++)
                    m_Entries[i] = m_Entries[i + 1];
                m_Entries[m_MaxEntries - 1] = timestamped;
            }

            RebuildDisplay();
        }

        void RebuildDisplay()
        {
            if (m_OutputText == null) return;

            var sb = new System.Text.StringBuilder();

            // Newest first (top) — same pattern as transcription display fix
            for (int i = m_EntryCount - 1; i >= 0; i--)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(m_Entries[i]);
            }

            m_OutputText.text = sb.ToString();
        }

        void UpdateStatus()
        {
            if (m_StatusText == null) return;
            m_StatusText.text = $"Gemini Monitor | Req: {m_RequestCount} | Resp: {m_ResponseCount} | Err: {m_ErrorCount}";
            m_StatusText.color = m_ErrorCount > 0
                ? new Color(1f, 0.7f, 0.7f, 0.9f)
                : new Color(0.7f, 1f, 0.7f, 0.9f);
        }

        static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            // Replace newlines with spaces for compact display
            text = text.Replace('\n', ' ').Replace('\r', ' ');
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "...";
        }
    }
}
