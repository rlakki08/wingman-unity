using System;

namespace Wingman
{
    /// <summary>
    /// Visual style categories for conversation suggestions.
    /// Maps to distinct icon/color treatments on the HUD cards.
    /// </summary>
    public enum SuggestionStyle
    {
        Casual,
        Thoughtful,
        Flirty,
        Empathetic,
        Humorous
    }

    /// <summary>
    /// Immutable data payload for a single conversation suggestion
    /// delivered from the backend to the HUD.
    /// </summary>
    [Serializable]
    public class SuggestionData
    {
        /// <summary>Short suggestion text (3-10 words for HUD readability).</summary>
        public string text;

        /// <summary>Visual/tonal style for this suggestion.</summary>
        public SuggestionStyle style;

        /// <summary>Backend confidence score, 0-1 range.</summary>
        public float confidence;

        /// <summary>Optional topic label from the LLM (e.g. "small talk", "question").</summary>
        public string topic;

        public SuggestionData() { }

        public SuggestionData(string text, SuggestionStyle style, float confidence, string topic = "")
        {
            this.text = text;
            this.style = style;
            this.confidence = UnityEngine.Mathf.Clamp01(confidence);
            this.topic = topic;
        }
    }

    /// <summary>
    /// Batch payload matching the backend suggestion response structure.
    /// Contains multiple suggestions plus session-level metadata.
    /// </summary>
    [Serializable]
    public class SuggestionPayload
    {
        public SuggestionData[] suggestions;
        public float engagementEstimate;
        public string dominantTopic;

        public SuggestionPayload()
        {
            suggestions = Array.Empty<SuggestionData>();
            engagementEstimate = 0f;
            dominantTopic = "";
        }
    }
}
