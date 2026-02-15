using UnityEngine;
using TMPro;

namespace Wingman
{
    /// <summary>
    /// Dedicated suggestions display panel for Gemini coaching output.
    ///
    /// Unlike WingmanTranscriptionDisplay (which accumulates lines), this panel
    /// REPLACES its content entirely each time new suggestions arrive.
    /// Only the latest set of suggestions is ever visible — no scrolling history.
    ///
    /// Layout:
    ///   - Status text (top strip): shows topic, engagement score, tone
    ///   - Suggestions text (main area): numbered list of suggestions
    ///
    /// Wire to WingmanManager via m_SuggestionsDisplay.
    /// </summary>
    public class WingmanSuggestionsDisplay : MonoBehaviour
    {
        [Header("UI References")]
        [Tooltip("TMP text for the status line (topic + engagement score).")]
        [SerializeField] TextMeshProUGUI m_StatusText;

        [Tooltip("TMP text for the suggestion content (main area).")]
        [SerializeField] TextMeshProUGUI m_SuggestionsText;

        [Header("Colors")]
        [SerializeField] Color m_StatusColor = new Color(1f, 1f, 1f, 0.7f);
        [SerializeField] Color m_SuggestionColor = new Color(1f, 1f, 1f, 0.95f);
        [SerializeField] Color m_ConfidenceHighColor = new Color(0.6f, 1f, 0.7f, 0.95f);
        [SerializeField] Color m_ConfidenceMedColor = new Color(1f, 1f, 0.6f, 0.9f);
        [SerializeField] Color m_ConfidenceLowColor = new Color(1f, 0.8f, 0.6f, 0.8f);
        [SerializeField] Color m_StyleTagColor = new Color(0.7f, 0.85f, 1f, 0.6f);
        [SerializeField] Color m_IdleColor = new Color(1f, 1f, 1f, 0.35f);

        [Header("Settings")]
        [Tooltip("Show style tags next to each suggestion.")]
        [SerializeField] bool m_ShowStyleTags = true;

        void Awake()
        {
            if (m_StatusText != null)
            {
                m_StatusText.text = "Suggestions";
                m_StatusText.color = m_IdleColor;
            }

            if (m_SuggestionsText != null)
            {
                m_SuggestionsText.text = "<i>Waiting for conversation...</i>";
                m_SuggestionsText.color = m_IdleColor;
            }
        }

        // ================================================================
        // Public API — called by WingmanManager
        // ================================================================

        /// <summary>
        /// Display a new set of suggestions, REPLACING whatever was shown before.
        /// This is the core difference from the transcription display — no accumulation.
        /// </summary>
        public void OnSuggestions(SuggestionPayload payload)
        {
            if (payload == null || payload.suggestions == null || payload.suggestions.Length == 0)
            {
                if (m_SuggestionsText != null)
                {
                    m_SuggestionsText.text = "<i>No suggestions</i>";
                    m_SuggestionsText.color = m_IdleColor;
                }
                return;
            }

            // Update status line
            if (m_StatusText != null)
            {
                string topic = string.IsNullOrEmpty(payload.dominantTopic) ? "general" : payload.dominantTopic;
                string engagement = EngagementLabel(payload.engagementEstimate);
                string statusHex = ColorUtility.ToHtmlStringRGBA(m_StatusColor);
                m_StatusText.text = $"<color=#{statusHex}>{topic}  |  {engagement}</color>";
                m_StatusText.color = Color.white; // let rich text handle color
            }

            // Build suggestion list — REPLACE entirely
            if (m_SuggestionsText != null)
            {
                var sb = new System.Text.StringBuilder();

                for (int i = 0; i < payload.suggestions.Length; i++)
                {
                    var s = payload.suggestions[i];
                    if (string.IsNullOrEmpty(s.text)) continue;

                    if (sb.Length > 0) sb.Append('\n');

                    // Pick color based on confidence
                    Color lineColor = s.confidence >= 0.75f ? m_ConfidenceHighColor
                                    : s.confidence >= 0.5f  ? m_ConfidenceMedColor
                                    : m_ConfidenceLowColor;
                    string hex = ColorUtility.ToHtmlStringRGBA(lineColor);

                    // Number + suggestion text
                    sb.Append($"<color=#{hex}>{i + 1}.  {s.text}</color>");

                    // Optional style tag
                    if (m_ShowStyleTags)
                    {
                        string tagHex = ColorUtility.ToHtmlStringRGBA(m_StyleTagColor);
                        sb.Append($"  <color=#{tagHex}><size=70%>{s.style}</size></color>");
                    }
                }

                m_SuggestionsText.text = sb.ToString();
                m_SuggestionsText.color = Color.white; // let rich text handle colors
            }
        }

        /// <summary>
        /// Show an error on the suggestions panel.
        /// </summary>
        public void OnError(string error)
        {
            if (m_SuggestionsText != null)
            {
                string hex = ColorUtility.ToHtmlStringRGBA(new Color(1f, 0.5f, 0.5f, 0.8f));
                m_SuggestionsText.text = $"<color=#{hex}>{error}</color>";
            }
        }

        /// <summary>Clear the display.</summary>
        public void Clear()
        {
            if (m_StatusText != null)
            {
                m_StatusText.text = "Suggestions";
                m_StatusText.color = m_IdleColor;
            }

            if (m_SuggestionsText != null)
            {
                m_SuggestionsText.text = "<i>Waiting for conversation...</i>";
                m_SuggestionsText.color = m_IdleColor;
            }
        }

        // ================================================================
        // Internal
        // ================================================================

        static string EngagementLabel(float score)
        {
            if (score >= 0.8f) return "engaged";
            if (score >= 0.6f) return "interested";
            if (score >= 0.4f) return "neutral";
            if (score >= 0.2f) return "distracted";
            return "checked out";
        }
    }
}
