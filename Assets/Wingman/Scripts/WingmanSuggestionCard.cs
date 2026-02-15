using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Wingman
{
    /// <summary>
    /// Controls a single suggestion card on the Wingman HUD.
    /// Handles display, fade animation, and user interaction callbacks
    /// (air-tap to accept, long-press/dwell to request whisper, swipe to dismiss).
    /// 
    /// Attach to a UI GameObject that has:
    ///   - A child TextMeshProUGUI for the suggestion text
    ///   - A child Image for the style icon
    ///   - A child Slider or Image (filled) for the confidence indicator
    ///   - A CanvasGroup on this GameObject for fade transitions
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class WingmanSuggestionCard : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] TextMeshProUGUI m_SuggestionText;
        [SerializeField] Image m_StyleIcon;
        [SerializeField] Image m_ConfidenceFill;
        [SerializeField] Image m_BackgroundImage;

        [Header("Style Icons (indexed by SuggestionStyle enum)")]
        [SerializeField] Sprite[] m_StyleSprites;

        [Header("Style Colors (indexed by SuggestionStyle enum)")]
        [SerializeField] Color[] m_StyleAccentColors = new Color[]
        {
            new Color(0.35f, 0.75f, 0.95f, 1f),   // Casual - light blue
            new Color(0.55f, 0.45f, 0.85f, 1f),   // Thoughtful - purple
            new Color(0.95f, 0.45f, 0.55f, 1f),   // Flirty - pink/red
            new Color(0.45f, 0.85f, 0.65f, 1f),   // Empathetic - green
            new Color(0.95f, 0.75f, 0.25f, 1f),   // Humorous - yellow/gold
        };

        [Header("Animation")]
        [SerializeField] float m_FadeDuration = 0.25f;

        /// <summary>Fired when user air-taps to accept this suggestion.</summary>
        public event Action<SuggestionData> OnAccepted;

        /// <summary>Fired when user long-presses/dwells to request whisper playback.</summary>
        public event Action<SuggestionData> OnWhisperRequested;

        /// <summary>Fired when user swipes to dismiss.</summary>
        public event Action<WingmanSuggestionCard> OnDismissed;

        SuggestionData m_CurrentData;
        CanvasGroup m_CanvasGroup;
        Coroutine m_FadeCoroutine;
        bool m_IsVisible;

        void Awake()
        {
            m_CanvasGroup = GetComponent<CanvasGroup>();
            m_CanvasGroup.alpha = 0f;
            m_CanvasGroup.interactable = false;
            m_CanvasGroup.blocksRaycasts = false;
            m_IsVisible = false;
        }

        /// <summary>
        /// Populates the card with new suggestion data and fades it in.
        /// </summary>
        public void Show(SuggestionData data)
        {
            m_CurrentData = data;

            // Set text
            if (m_SuggestionText != null)
                m_SuggestionText.text = data.text;

            // Set style icon
            int styleIndex = (int)data.style;
            if (m_StyleIcon != null && m_StyleSprites != null && styleIndex < m_StyleSprites.Length)
                m_StyleIcon.sprite = m_StyleSprites[styleIndex];

            // Set accent color
            if (styleIndex < m_StyleAccentColors.Length)
            {
                Color accent = m_StyleAccentColors[styleIndex];
                if (m_StyleIcon != null)
                    m_StyleIcon.color = accent;
                if (m_BackgroundImage != null)
                {
                    Color bg = accent;
                    bg.a = 0.15f;
                    m_BackgroundImage.color = bg;
                }
            }

            // Set confidence fill
            if (m_ConfidenceFill != null)
                m_ConfidenceFill.fillAmount = data.confidence;

            gameObject.SetActive(true);
            FadeTo(1f);
        }

        /// <summary>
        /// Fades the card out and deactivates it.
        /// </summary>
        public void Hide()
        {
            FadeTo(0f, () =>
            {
                m_CanvasGroup.interactable = false;
                m_CanvasGroup.blocksRaycasts = false;
                m_IsVisible = false;
            });
        }

        /// <summary>
        /// Immediately hides without animation.
        /// </summary>
        public void HideImmediate()
        {
            if (m_FadeCoroutine != null)
                StopCoroutine(m_FadeCoroutine);

            m_CanvasGroup.alpha = 0f;
            m_CanvasGroup.interactable = false;
            m_CanvasGroup.blocksRaycasts = false;
            m_IsVisible = false;
        }

        /// <summary>Called by UI Button OnClick or XR poke — accept this suggestion.</summary>
        public void HandleAccept()
        {
            if (m_CurrentData != null)
                OnAccepted?.Invoke(m_CurrentData);
        }

        /// <summary>Called by long-press/dwell detection — request whisper playback.</summary>
        public void HandleWhisperRequest()
        {
            if (m_CurrentData != null)
                OnWhisperRequested?.Invoke(m_CurrentData);
        }

        /// <summary>Called by swipe gesture or dismiss button — dismiss this card.</summary>
        public void HandleDismiss()
        {
            Hide();
            OnDismissed?.Invoke(this);
        }

        public bool IsVisible => m_IsVisible;
        public SuggestionData CurrentData => m_CurrentData;

        void FadeTo(float target, Action onComplete = null)
        {
            if (m_FadeCoroutine != null)
                StopCoroutine(m_FadeCoroutine);
            m_FadeCoroutine = StartCoroutine(FadeCoroutine(target, onComplete));
        }

        IEnumerator FadeCoroutine(float target, Action onComplete)
        {
            float start = m_CanvasGroup.alpha;
            float elapsed = 0f;

            while (elapsed < m_FadeDuration)
            {
                elapsed += Time.deltaTime;
                m_CanvasGroup.alpha = Mathf.Lerp(start, target, elapsed / m_FadeDuration);
                yield return null;
            }

            m_CanvasGroup.alpha = target;

            if (target > 0.5f)
            {
                m_CanvasGroup.interactable = true;
                m_CanvasGroup.blocksRaycasts = true;
                m_IsVisible = true;
            }

            onComplete?.Invoke();
            m_FadeCoroutine = null;
        }
    }
}
