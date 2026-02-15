using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using LazyFollow = UnityEngine.XR.Interaction.Toolkit.UI.LazyFollow;

namespace Wingman
{
    /// <summary>
    /// Core HUD controller for the Wingman suggestion system.
    /// Manages up to three suggestion cards, an engagement gauge,
    /// and a conversation timer. Positioned in peripheral vision using LazyFollow.
    ///
    /// Follows the same world-space Canvas pattern as the existing Coaching UI:
    ///   - Small-scale Canvas (0.00075) on UI layer
    ///   - TrackedDeviceGraphicRaycaster for XR input
    ///   - LazyFollow for gentle gaze tracking
    ///
    /// Scene hierarchy expected:
    ///   Wingman HUD (this + LazyFollow)
    ///     └─ WingmanCanvas (Canvas, CanvasScaler, GraphicRaycaster, TrackedDeviceGraphicRaycaster)
    ///         ├─ SuggestionsPanel (VerticalLayoutGroup)
    ///         │   ├─ SuggestionCard_0 (WingmanSuggestionCard)
    ///         │   ├─ SuggestionCard_1 (WingmanSuggestionCard)
    ///         │   └─ SuggestionCard_2 (WingmanSuggestionCard)
    ///         ├─ EngagementGauge (Slider or filled Image)
    ///         └─ ConversationTimer (TextMeshProUGUI)
    /// </summary>
    public class WingmanHUD : MonoBehaviour
    {
        [Header("Suggestion Cards")]
        [SerializeField] WingmanSuggestionCard[] m_SuggestionCards = new WingmanSuggestionCard[3];

        [Header("Engagement Gauge")]
        [SerializeField] Image m_EngagementFill;
        [SerializeField] TextMeshProUGUI m_EngagementLabel;

        [Header("Conversation Timer")]
        [SerializeField] TextMeshProUGUI m_TimerText;

        [Header("Status")]
        [SerializeField] TextMeshProUGUI m_StatusText;
        [SerializeField] Image m_ConnectionIndicator;

        [Header("Follow Behavior")]
        [SerializeField] LazyFollow m_LazyFollow;

        [Header("Settings")]
        [Tooltip("Maximum number of suggestion cards to display simultaneously.")]
        [SerializeField] int m_MaxVisibleCards = 3;

        [Tooltip("Seconds between timer display updates.")]
        [SerializeField] float m_TimerUpdateInterval = 1f;

        /// <summary>Fired when user accepts a suggestion (for upstream handling like TTS request).</summary>
        public event Action<SuggestionData> OnSuggestionAccepted;

        /// <summary>Fired when user requests whisper playback for a suggestion.</summary>
        public event Action<SuggestionData> OnWhisperRequested;

        bool m_SessionActive;
        float m_SessionStartTime;
        float m_CurrentEngagement;
        Coroutine m_TimerCoroutine;

        // Colors for connection status
        static readonly Color k_ConnectedColor = new Color(0.3f, 0.85f, 0.45f, 1f);
        static readonly Color k_DisconnectedColor = new Color(0.85f, 0.3f, 0.3f, 1f);
        static readonly Color k_ConnectingColor = new Color(0.95f, 0.75f, 0.25f, 1f);

        void Awake()
        {
            // Wire up card events
            foreach (var card in m_SuggestionCards)
            {
                if (card == null) continue;
                card.OnAccepted += HandleSuggestionAccepted;
                card.OnWhisperRequested += HandleWhisperRequested;
                card.OnDismissed += HandleCardDismissed;
            }

            // Start hidden
            SetConnectionStatus(ConnectionStatus.Disconnected);
            ClearAllCards();

            if (m_TimerText != null)
                m_TimerText.text = "00:00";

            if (m_EngagementFill != null)
                m_EngagementFill.fillAmount = 0f;
        }

        void OnDestroy()
        {
            foreach (var card in m_SuggestionCards)
            {
                if (card == null) continue;
                card.OnAccepted -= HandleSuggestionAccepted;
                card.OnWhisperRequested -= HandleWhisperRequested;
                card.OnDismissed -= HandleCardDismissed;
            }
        }

        #region Public API

        /// <summary>
        /// Starts a Wingman session: begins timer, shows HUD, enables follow.
        /// </summary>
        public void StartSession()
        {
            m_SessionActive = true;
            m_SessionStartTime = Time.time;

            if (m_LazyFollow != null)
                m_LazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.Follow;

            SetConnectionStatus(ConnectionStatus.Connecting);
            SetStatusText("Listening...");

            if (m_TimerCoroutine != null)
                StopCoroutine(m_TimerCoroutine);
            m_TimerCoroutine = StartCoroutine(UpdateTimerCoroutine());
        }

        /// <summary>
        /// Ends the current session: stops timer, clears cards, updates status.
        /// </summary>
        public void EndSession()
        {
            m_SessionActive = false;

            if (m_TimerCoroutine != null)
            {
                StopCoroutine(m_TimerCoroutine);
                m_TimerCoroutine = null;
            }

            ClearAllCards();
            SetConnectionStatus(ConnectionStatus.Disconnected);
            SetStatusText("Session ended");
        }

        /// <summary>
        /// Processes a full suggestion payload from the backend.
        /// Updates cards, engagement gauge, and topic display.
        /// </summary>
        public void DisplaySuggestions(SuggestionPayload payload)
        {
            if (payload == null || payload.suggestions == null) return;

            int count = Mathf.Min(payload.suggestions.Length, m_MaxVisibleCards);

            // Show cards with data
            for (int i = 0; i < m_SuggestionCards.Length; i++)
            {
                if (m_SuggestionCards[i] == null) continue;

                if (i < count)
                    m_SuggestionCards[i].Show(payload.suggestions[i]);
                else
                    m_SuggestionCards[i].Hide();
            }

            // Update engagement gauge
            SetEngagement(payload.engagementEstimate);

            // Update status with topic
            if (!string.IsNullOrEmpty(payload.dominantTopic))
                SetStatusText(payload.dominantTopic);
        }

        /// <summary>
        /// Updates the engagement gauge (0-1 range).
        /// </summary>
        public void SetEngagement(float value)
        {
            m_CurrentEngagement = Mathf.Clamp01(value);

            if (m_EngagementFill != null)
                m_EngagementFill.fillAmount = m_CurrentEngagement;

            if (m_EngagementLabel != null)
                m_EngagementLabel.text = $"{Mathf.RoundToInt(m_CurrentEngagement * 100)}%";
        }

        /// <summary>
        /// Updates the connection status indicator.
        /// </summary>
        public void SetConnectionStatus(ConnectionStatus status)
        {
            if (m_ConnectionIndicator == null) return;

            switch (status)
            {
                case ConnectionStatus.Connected:
                    m_ConnectionIndicator.color = k_ConnectedColor;
                    break;
                case ConnectionStatus.Connecting:
                    m_ConnectionIndicator.color = k_ConnectingColor;
                    break;
                case ConnectionStatus.Disconnected:
                    m_ConnectionIndicator.color = k_DisconnectedColor;
                    break;
            }
        }

        /// <summary>
        /// Updates the HUD status text line (topic, state, etc.).
        /// </summary>
        public void SetStatusText(string text)
        {
            if (m_StatusText != null)
                m_StatusText.text = text;
        }

        /// <summary>
        /// Toggles the HUD visibility (e.g. from hand menu).
        /// </summary>
        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
        }

        /// <summary>
        /// For testing: display hardcoded sample suggestions.
        /// </summary>
        public void ShowTestSuggestions()
        {
            var payload = new SuggestionPayload
            {
                suggestions = new[]
                {
                    new SuggestionData("That sounds really interesting!", SuggestionStyle.Casual, 0.92f, "engagement"),
                    new SuggestionData("What inspired you to try that?", SuggestionStyle.Thoughtful, 0.85f, "question"),
                    new SuggestionData("I'd love to hear more about it", SuggestionStyle.Empathetic, 0.78f, "follow-up"),
                },
                engagementEstimate = 0.72f,
                dominantTopic = "personal interests"
            };

            DisplaySuggestions(payload);
        }

        #endregion

        #region Private Methods

        void ClearAllCards()
        {
            foreach (var card in m_SuggestionCards)
            {
                if (card != null)
                    card.HideImmediate();
            }
        }

        void HandleSuggestionAccepted(SuggestionData data)
        {
            OnSuggestionAccepted?.Invoke(data);
        }

        void HandleWhisperRequested(SuggestionData data)
        {
            OnWhisperRequested?.Invoke(data);
        }

        void HandleCardDismissed(WingmanSuggestionCard card)
        {
            // Card handles its own hide animation.
            // Could shift remaining cards up here if desired.
        }

        IEnumerator UpdateTimerCoroutine()
        {
            var wait = new WaitForSeconds(m_TimerUpdateInterval);
            while (m_SessionActive)
            {
                float elapsed = Time.time - m_SessionStartTime;
                int minutes = Mathf.FloorToInt(elapsed / 60f);
                int seconds = Mathf.FloorToInt(elapsed % 60f);

                if (m_TimerText != null)
                    m_TimerText.text = $"{minutes:00}:{seconds:00}";

                yield return wait;
            }
        }

        #endregion
    }

    public enum ConnectionStatus
    {
        Disconnected,
        Connecting,
        Connected
    }
}
