using UnityEngine;
using TMPro;

namespace Wingman
{
    /// <summary>
    /// On-screen debug log for the Wingman pipeline.
    ///
    /// Shows timestamped log lines on a separate HUD panel so you can see
    /// exactly what's happening at each step:
    ///   - Manager state changes
    ///   - Mic start/stop, device info
    ///   - VAD triggers (speech start/end, dB levels)
    ///   - Speech segment dispatch (size, duration)
    ///   - ElevenLabs API requests (sent, response, errors)
    ///   - Transcription results
    ///
    /// Uses a single TMP text field with a scrolling line buffer.
    /// Attach to the Debug Panel in the scene and assign the TMP reference.
    /// WingmanManager will auto-find this via FindAnyObjectByType.
    /// </summary>
    public class WingmanDebugDisplay : MonoBehaviour
    {
        [Header("UI Reference")]
        [Tooltip("TMP text element for the debug log output.")]
        [SerializeField] TextMeshProUGUI m_DebugText;

        [Header("Settings")]
        [Tooltip("Maximum number of log lines to keep on screen.")]
        [SerializeField] int m_MaxLines = 30;

        [Tooltip("Also forward log lines to Debug.Log for adb logcat.")]
        [SerializeField] bool m_MirrorToConsole = true;

        // Internal line buffer
        string[] m_Lines;
        int m_LineCount;
        float m_StartTime;

        void Awake()
        {
            m_Lines = new string[m_MaxLines];
            m_LineCount = 0;
            m_StartTime = Time.realtimeSinceStartup;

            if (m_DebugText != null)
                m_DebugText.text = "";

            Log("DebugDisplay initialized");
        }

        // ================================================================
        // Public API
        // ================================================================

        /// <summary>
        /// Add a timestamped line to the debug display.
        /// </summary>
        public void Log(string message)
        {
            float elapsed = Time.realtimeSinceStartup - m_StartTime;
            string line = $"[{elapsed:F1}s] {message}";

            if (m_MirrorToConsole)
                Debug.Log($"[WingmanDebug] {message}");

            AppendLine(line);
        }

        /// <summary>
        /// Add an error line (prefixed with ERR).
        /// </summary>
        public void LogError(string message)
        {
            float elapsed = Time.realtimeSinceStartup - m_StartTime;
            string line = $"[{elapsed:F1}s] ERR: {message}";

            if (m_MirrorToConsole)
                Debug.LogError($"[WingmanDebug] {message}");

            AppendLine(line);
        }

        /// <summary>
        /// Clear the debug display.
        /// </summary>
        public void Clear()
        {
            m_LineCount = 0;
            if (m_DebugText != null)
                m_DebugText.text = "";
        }

        // ================================================================
        // Internal
        // ================================================================

        void AppendLine(string line)
        {
            if (m_LineCount < m_MaxLines)
            {
                m_Lines[m_LineCount] = line;
                m_LineCount++;
            }
            else
            {
                // Shift up, drop oldest
                for (int i = 0; i < m_MaxLines - 1; i++)
                    m_Lines[i] = m_Lines[i + 1];
                m_Lines[m_MaxLines - 1] = line;
            }

            RebuildText();
        }

        void RebuildText()
        {
            if (m_DebugText == null) return;

            var sb = new System.Text.StringBuilder();

            // Newest first (top) — newest entry always visible,
            // oldest scrolls off the bottom and gets clipped by TMP Truncate
            for (int i = m_LineCount - 1; i >= 0; i--)
            {
                if (i < m_LineCount - 1) sb.Append('\n');
                sb.Append(m_Lines[i]);
            }

            m_DebugText.text = sb.ToString();
        }
    }
}
