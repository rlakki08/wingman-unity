using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Wingman
{
    /// <summary>
    /// Mode toggle button component for switching between Dating Coach and Therapy modes.
    /// Attach to a Button GameObject in your UI.
    /// 
    /// The button displays the current mode and allows the user to toggle between modes.
    /// Updates WingmanManager when clicked.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class WingmanModeToggle : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("WingmanManager to control. Auto-finds if not assigned.")]
        [SerializeField] WingmanManager m_Manager;

        [Tooltip("Text component to update with current mode. Auto-finds child if not assigned.")]
        [SerializeField] TextMeshProUGUI m_ModeText;

        [Header("Labels")]
        [SerializeField] string m_DatingLabel = "Dating Coach";
        [SerializeField] string m_TherapyLabel = "Therapy Mode";

        [Header("Colors")]
        [SerializeField] Color m_DatingColor = new Color(0.4f, 0.9f, 0.5f, 1f); // Green
        [SerializeField] Color m_TherapyColor = new Color(0.5f, 0.7f, 1f, 1f);  // Blue

        Button m_Button;
        string m_CurrentMode = "dating";

        void Awake()
        {
            // Auto-find manager if not assigned
            if (m_Manager == null)
                m_Manager = FindAnyObjectByType<WingmanManager>();

            // Auto-find text if not assigned
            if (m_ModeText == null)
                m_ModeText = GetComponentInChildren<TextMeshProUGUI>();

            // Get button component
            m_Button = GetComponent<Button>();
            if (m_Button != null)
                m_Button.onClick.AddListener(OnButtonClicked);

            UpdateButtonText();
        }

        void OnButtonClicked()
        {
            // Toggle mode
            m_CurrentMode = (m_CurrentMode == "dating") ? "therapy" : "dating";

            // Update manager
            if (m_Manager != null)
                m_Manager.SwitchMode(m_CurrentMode);

            // Update UI
            UpdateButtonText();

            Debug.Log($"[WingmanModeToggle] Mode switched to: {m_CurrentMode}");
        }

        void UpdateButtonText()
        {
            if (m_ModeText != null)
            {
                string label = (m_CurrentMode == "dating") ? m_DatingLabel : m_TherapyLabel;
                Color color = (m_CurrentMode == "dating") ? m_DatingColor : m_TherapyColor;
                
                m_ModeText.text = $"Mode: {label}";
                m_ModeText.color = color;
            }
        }

        /// <summary>
        /// Manually set the current mode (updates UI, does not trigger manager).
        /// </summary>
        public void SetMode(string mode)
        {
            m_CurrentMode = mode;
            UpdateButtonText();
        }
    }
}
