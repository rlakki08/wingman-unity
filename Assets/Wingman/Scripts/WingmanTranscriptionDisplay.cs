using System.Collections;
using UnityEngine;
using TMPro;

namespace Wingman
{
    /// <summary>
    /// Teleprompter-style transcription display for the Wingman Panel.
    ///
    /// Text is bottom-aligned: the newest transcription always appears at the bottom
    /// of the panel, and older lines push upward. Any text that overflows the top
    /// edge of the panel is clipped (TMP overflow = Truncate).
    ///
    /// This mimics live captioning / subtitles: you always read the bottom,
    /// and old content scrolls off the top.
    ///
    /// Uses two TMP text fields:
    ///   - Status text (m_StatusText): small top strip showing state
    ///   - Transcript text (m_TranscriptText): bottom-aligned main area
    ///
    /// Wire to WingmanManager for transcription events.
    /// </summary>
    public class WingmanTranscriptionDisplay : MonoBehaviour
    {
        [Header("UI References")]
        [Tooltip("TMP text for the current status line (small, top of panel).")]
        [SerializeField] TextMeshProUGUI m_StatusText;

        [Tooltip("TMP text for the main transcription content (bottom-aligned).")]
        [SerializeField] TextMeshProUGUI m_TranscriptText;

        [Header("Teleprompter Settings")]
        [Tooltip("Maximum number of recent lines to keep in the buffer. " +
                 "Older lines are discarded. TMP overflow clipping handles the visual cutoff.")]
        [SerializeField] int m_MaxBufferLines = 12;

        [Header("Colors")]
        [Tooltip("Color for the newest transcript line.")]
        [SerializeField] Color m_NewestColor = new Color(1f, 1f, 1f, 1f);

        [Tooltip("Color for older transcript lines (progressively faded).")]
        [SerializeField] Color m_OldestColor = new Color(1f, 1f, 1f, 0.25f);

        [Tooltip("Color for partial (in-progress) transcript text.")]
        [SerializeField] Color m_PartialColor = new Color(1f, 1f, 1f, 0.45f);

        [Tooltip("Color for status line when active.")]
        [SerializeField] Color m_StatusActiveColor = new Color(1f, 1f, 1f, 0.9f);

        [Tooltip("Color for status line when idle.")]
        [SerializeField] Color m_StatusIdleColor = new Color(1f, 1f, 1f, 0.4f);

        [Header("Labels")]
        [SerializeField] string m_UserPrefix = "You: ";
        [SerializeField] string m_OtherPrefix = "Them: ";
        [SerializeField] string m_AIPrefix = "AI: ";
        [SerializeField] string m_SuggestionPrefix = ">> ";

        [Header("AI Speaker Color")]
        [Tooltip("Color for AI/assistant speaker (therapy mode).")]
        [SerializeField] Color m_AIColor = new Color(0.5f, 0.9f, 1f, 1f); // Cyan

        [Header("Suggestion Colors")]
        [Tooltip("Color for Gemini suggestion lines displayed in the stream.")]
        [SerializeField] Color m_SuggestionColor = new Color(0.6f, 1f, 0.7f, 0.95f);

        // Internal state
        string[] m_Lines;
        int m_LineCount;
        bool m_IsRecording;
        string m_CurrentPartial;

        void Awake()
        {
            m_Lines = new string[m_MaxBufferLines];
            m_LineCount = 0;
            m_CurrentPartial = null;

            if (m_StatusText != null)
            {
                m_StatusText.text = "Listening...";
                m_StatusText.color = m_StatusIdleColor;
            }

            if (m_TranscriptText != null)
            {
                m_TranscriptText.text = "";
                m_TranscriptText.alpha = 1f;
            }
        }

        // ================================================================
        // Public API — called by WingmanManager
        // ================================================================

        /// <summary>
        /// Called when voice activity changes.
        /// </summary>
        public void OnVoiceActivity(bool isSpeaking)
        {
            m_IsRecording = isSpeaking;
            if (m_StatusText == null) return;

            if (isSpeaking)
            {
                m_StatusText.text = "Recording...";
                m_StatusText.color = m_StatusActiveColor;
            }
            else
            {
                m_StatusText.text = "Listening...";
                m_StatusText.color = m_StatusIdleColor;
            }
        }

        /// <summary>
        /// Called when a transcript arrives.
        /// isPartial=true: interim preview (shown dimmed at bottom, not committed)
        /// isPartial=false: finalized line (committed to history, pushed to bottom)
        /// speaker="system": status-only message
        /// </summary>
        public void OnTranscript(string text, string speaker, bool isPartial)
        {
            if (string.IsNullOrEmpty(text)) return;

            // System messages go to status line only
            if (speaker == "system")
            {
                if (m_StatusText != null)
                {
                    m_StatusText.text = text;
                    m_StatusText.color = m_StatusIdleColor;
                }
                return;
            }

            // Determine prefix based on speaker
            string prefix;
            if (speaker == "user" || speaker == "self")
                prefix = m_UserPrefix;
            else if (speaker == "ai" || speaker == "assistant")
                prefix = m_AIPrefix;
            else
                prefix = m_OtherPrefix;

            if (isPartial)
            {
                m_CurrentPartial = prefix + text;

                if (m_StatusText != null)
                {
                    m_StatusText.text = "Transcribing...";
                    m_StatusText.color = m_StatusIdleColor;
                }
            }
            else
            {
                // Finalized: commit to history buffer
                string finalLine;
                
                // Apply color for AI messages
                if (speaker == "ai" || speaker == "assistant")
                {
                    string aiHex = ColorUtility.ToHtmlStringRGBA(m_AIColor);
                    finalLine = $"<color=#{aiHex}>{prefix}{text}</color>";
                }
                else
                {
                    finalLine = prefix + text;
                }
                
                PushLine(finalLine);
                m_CurrentPartial = null;

                if (m_StatusText != null)
                {
                    m_StatusText.text = m_IsRecording ? "Recording..." : "Listening...";
                    m_StatusText.color = m_IsRecording ? m_StatusActiveColor : m_StatusIdleColor;
                }
            }

            // Rebuild the display
            RebuildDisplay();
        }

        /// <summary>
        /// Called when the mic recording state changes.
        /// </summary>
        public void OnRecordingStateChanged(bool isRecording)
        {
            if (!isRecording && m_StatusText != null)
            {
                m_StatusText.text = "Mic stopped";
                m_StatusText.color = m_StatusIdleColor;
            }
        }

        /// <summary>
        /// Called when connection status changes.
        /// </summary>
        public void OnConnectionStatusChanged(ConnectionStatus status)
        {
            if (m_StatusText == null) return;

            switch (status)
            {
                case ConnectionStatus.Connecting:
                    m_StatusText.text = "Connecting...";
                    m_StatusText.color = m_StatusIdleColor;
                    break;
                case ConnectionStatus.Connected:
                    m_StatusText.text = "Connected. Listening...";
                    m_StatusText.color = m_StatusActiveColor;
                    break;
                case ConnectionStatus.Disconnected:
                    m_StatusText.text = "Disconnected";
                    m_StatusText.color = m_StatusIdleColor;
                    break;
            }
        }

        /// <summary>Clear all transcript history.</summary>
        public void ClearTranscript()
        {
            m_LineCount = 0;
            m_CurrentPartial = null;
            if (m_TranscriptText != null)
                m_TranscriptText.text = "";
            if (m_StatusText != null)
            {
                m_StatusText.text = "Listening...";
                m_StatusText.color = m_StatusIdleColor;
            }
        }

        /// <summary>
        /// Display Gemini coaching suggestions inline in the transcript stream.
        /// Each suggestion appears as a colored line so the user sees them
        /// alongside their speech in real time.
        /// </summary>
        public void OnSuggestions(SuggestionPayload payload)
        {
            if (payload.suggestions == null || payload.suggestions.Length == 0) return;

            string hex = ColorUtility.ToHtmlStringRGBA(m_SuggestionColor);

            for (int i = 0; i < payload.suggestions.Length; i++)
            {
                string line = $"<color=#{hex}>{m_SuggestionPrefix}{payload.suggestions[i].text}</color>";
                PushLine(line);
            }

            RebuildDisplay();
        }

        /// <summary>
        /// Display a Gemini error inline in the transcript stream.
        /// </summary>
        public void OnGeminiError(string error)
        {
            if (string.IsNullOrEmpty(error)) return;
            string hex = ColorUtility.ToHtmlStringRGBA(new Color(1f, 0.5f, 0.5f, 0.8f));
            PushLine($"<color=#{hex}>[Gemini: {error}]</color>");
            RebuildDisplay();
        }

        // ================================================================
        // Internal — line buffer (ring buffer, newest at end)
        // ================================================================

        void PushLine(string line)
        {
            if (m_LineCount < m_MaxBufferLines)
            {
                m_Lines[m_LineCount] = line;
                m_LineCount++;
            }
            else
            {
                // Shift everything up, drop oldest (index 0)
                for (int i = 0; i < m_MaxBufferLines - 1; i++)
                    m_Lines[i] = m_Lines[i + 1];
                m_Lines[m_MaxBufferLines - 1] = line;
            }
        }

        // ================================================================
        // Internal — display rebuild
        // ================================================================

        /// <summary>
        /// Rebuild the TMP text content. Lines are ordered top-to-bottom with
        /// NEWEST at top, oldest at bottom. TMP is top-aligned with Truncate
        /// overflow, so the newest lines are always visible at the top and
        /// oldest lines get clipped at the bottom when the text overflows.
        ///
        /// Each line gets a color based on age: newest = bright, oldest = faded.
        /// A partial transcript (if present) appears first (topmost), in italic + dim.
        /// </summary>
        void RebuildDisplay()
        {
            if (m_TranscriptText == null) return;

            int totalLines = m_LineCount + (m_CurrentPartial != null ? 1 : 0);
            if (totalLines == 0)
            {
                m_TranscriptText.text = "";
                return;
            }

            var sb = new System.Text.StringBuilder();

            // Partial transcript at the very top (dimmed, italic) — always visible
            if (m_CurrentPartial != null)
            {
                string partialHex = ColorUtility.ToHtmlStringRGBA(m_PartialColor);
                sb.Append($"<color=#{partialHex}><i>{m_CurrentPartial}</i></color>");
            }

            // Committed lines: newest first (index m_LineCount-1) → oldest last (index 0)
            for (int i = m_LineCount - 1; i >= 0; i--)
            {
                if (sb.Length > 0) sb.Append('\n');

                // Fade factor: 1.0 for newest (i == m_LineCount-1), 0.0 for oldest (i == 0)
                float t = m_LineCount > 1 ? (float)i / (m_LineCount - 1) : 1f;
                Color lineColor = Color.Lerp(m_OldestColor, m_NewestColor, t);
                string hex = ColorUtility.ToHtmlStringRGBA(lineColor);

                sb.Append($"<color=#{hex}>{m_Lines[i]}</color>");
            }

            m_TranscriptText.text = sb.ToString();
        }
    }
}
