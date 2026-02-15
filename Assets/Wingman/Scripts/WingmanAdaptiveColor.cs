using UnityEngine;
using UnityEngine.Rendering;
using TMPro;

namespace Wingman
{
    /// <summary>
    /// Adaptively colors UI text and shader borders based on environment brightness.
    ///
    /// Samples Unity's spherical harmonics (light probes / ambient light) to estimate
    /// the luminance of the real-world environment behind the panel. In bright environments,
    /// text and borders turn dark (black); in dark environments, they turn light (white).
    /// This mimics the Minecraft crosshair inversion for always-visible UI.
    ///
    /// On Meta Quest 3 with passthrough, the passthrough layer writes ambient light data
    /// into the light estimation system (if enabled via OVR Scene / Lighting). Even without
    /// OVR light estimation, RenderSettings.ambientLight provides a reasonable fallback.
    ///
    /// Attach to the same GameObject as WingmanTranscriptionDisplay (e.g. Wingman Panel).
    /// Assign TMP references and the LiquidGlass material in the Inspector.
    /// </summary>
    public class WingmanAdaptiveColor : MonoBehaviour
    {
        [Header("Text References")]
        [Tooltip("TMP text elements to adaptively color.")]
        [SerializeField] TextMeshProUGUI[] m_TextElements;

        [Header("Material References")]
        [Tooltip("LiquidGlass material whose _BorderColor and _HighlightColor will be adapted.")]
        [SerializeField] Material m_LiquidGlassMaterial;

        [Header("Adaptation Settings")]
        [Tooltip("How often to re-sample environment luminance (seconds).")]
        [SerializeField] float m_SampleInterval = 0.5f;

        [Tooltip("Luminance threshold (0-1): below this = light text, above = dark text.")]
        [SerializeField] float m_LuminanceThreshold = 0.5f;

        [Tooltip("How quickly colors blend toward the target (higher = snappier, lower = smoother).")]
        [SerializeField] float m_LerpSpeed = 3f;

        [Tooltip("Alpha for text in adapted state.")]
        [SerializeField] float m_TextAlpha = 1f;

        [Tooltip("Alpha for border in adapted state.")]
        [SerializeField] float m_BorderAlpha = 0.45f;

        [Tooltip("Alpha for highlight/edge glow in adapted state.")]
        [SerializeField] float m_HighlightAlpha = 1f;

        [Header("Debug")]
        [SerializeField] bool m_DebugLog = false;

        // Internal
        float m_Timer;
        float m_CurrentLuminance;
        Color m_TargetTextColor;
        Color m_TargetBorderColor;
        Color m_TargetHighlightColor;
        Color m_CurrentTextColor;
        Color m_CurrentBorderColor;
        Color m_CurrentHighlightColor;
        bool m_Initialized;

        // Shader property IDs (cached for performance)
        static readonly int s_BorderColorID = Shader.PropertyToID("_BorderColor");
        static readonly int s_HighlightColorID = Shader.PropertyToID("_HighlightColor");

        void Start()
        {
            // Initialize with a sample
            m_CurrentLuminance = SampleEnvironmentLuminance();
            ComputeTargetColors(m_CurrentLuminance);

            m_CurrentTextColor = m_TargetTextColor;
            m_CurrentBorderColor = m_TargetBorderColor;
            m_CurrentHighlightColor = m_TargetHighlightColor;

            ApplyColors();
            m_Initialized = true;
        }

        void Update()
        {
            m_Timer += Time.deltaTime;

            if (m_Timer >= m_SampleInterval)
            {
                m_Timer = 0f;
                m_CurrentLuminance = SampleEnvironmentLuminance();
                ComputeTargetColors(m_CurrentLuminance);

                if (m_DebugLog)
                    Debug.Log($"[WingmanAdaptiveColor] Luminance={m_CurrentLuminance:F3} → text={(m_TargetTextColor.r < 0.5f ? "dark" : "light")}");
            }

            // Smooth lerp toward target colors
            m_CurrentTextColor = Color.Lerp(m_CurrentTextColor, m_TargetTextColor, Time.deltaTime * m_LerpSpeed);
            m_CurrentBorderColor = Color.Lerp(m_CurrentBorderColor, m_TargetBorderColor, Time.deltaTime * m_LerpSpeed);
            m_CurrentHighlightColor = Color.Lerp(m_CurrentHighlightColor, m_TargetHighlightColor, Time.deltaTime * m_LerpSpeed);

            ApplyColors();
        }

        // ================================================================
        // Luminance Sampling
        // ================================================================

        /// <summary>
        /// Estimate environment luminance from multiple light sources:
        /// 1. Spherical harmonics (light probes baked or runtime)
        /// 2. RenderSettings.ambientLight (fallback)
        /// 3. Main directional light intensity (additional signal)
        /// Returns 0-1 luminance estimate.
        /// </summary>
        float SampleEnvironmentLuminance()
        {
            float luminance = 0f;
            int sources = 0;

            // Source 1: Spherical Harmonics (most accurate for MR passthrough)
            // May fail if no light probes are baked — safe to skip
            try
            {
                SphericalHarmonicsL2 sh;
                LightProbes.GetInterpolatedProbe(transform.position, null, out sh);

                // Evaluate SH in a few directions and average
                Vector3[] dirs = new Vector3[]
                {
                    Vector3.forward,
                    Vector3.up,
                    Vector3.back,
                    Vector3.down,
                    Vector3.left,
                    Vector3.right
                };
                Color[] results = new Color[dirs.Length];
                sh.Evaluate(dirs, results);

                float shLum = 0f;
                for (int i = 0; i < results.Length; i++)
                {
                    shLum += ColorLuminance(results[i]);
                }
                shLum /= results.Length;

                if (shLum > 0.001f)
                {
                    luminance += shLum;
                    sources++;
                }
            }
            catch (System.Exception)
            {
                // No light probes available — skip this source
            }

            // Source 2: Ambient light (always available)
            Color ambient = RenderSettings.ambientLight;
            float ambientLum = ColorLuminance(ambient);
            if (ambientLum > 0.001f || RenderSettings.ambientMode == AmbientMode.Flat)
            {
                luminance += ambientLum;
                sources++;
            }

            // Source 3: Ambient intensity (for skybox / gradient modes)
            float ambientIntensity = RenderSettings.ambientIntensity;
            if (RenderSettings.ambientMode == AmbientMode.Skybox)
            {
                luminance += ambientIntensity * 0.5f; // scale down, skybox intensity can be > 1
                sources++;
            }

            if (sources == 0) return 0.5f; // neutral default
            return Mathf.Clamp01(luminance / sources);
        }

        /// <summary>
        /// Standard luminance calculation (ITU-R BT.709).
        /// </summary>
        static float ColorLuminance(Color c)
        {
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        // ================================================================
        // Color Computation
        // ================================================================

        /// <summary>
        /// Compute target colors based on luminance.
        /// Bright environment → dark (black) text/border.
        /// Dark environment → light (white) text/border.
        /// Uses smooth interpolation around the threshold for a gradual transition.
        /// </summary>
        void ComputeTargetColors(float luminance)
        {
            // Smooth step around threshold for gradual dark↔light transition
            // Maps luminance to a 0-1 "darkness factor":
            //   0 = environment is dark → use white
            //   1 = environment is bright → use black
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                m_LuminanceThreshold - 0.15f,
                m_LuminanceThreshold + 0.15f,
                luminance));

            // Invert: t=1 means bright environment → use black text
            float textBrightness = 1f - t;
            float borderBrightness = 1f - t;

            m_TargetTextColor = new Color(textBrightness, textBrightness, textBrightness, m_TextAlpha);
            m_TargetBorderColor = new Color(borderBrightness, borderBrightness, borderBrightness, m_BorderAlpha);
            m_TargetHighlightColor = new Color(borderBrightness, borderBrightness, borderBrightness, m_HighlightAlpha);
        }

        // ================================================================
        // Color Application
        // ================================================================

        void ApplyColors()
        {
            // Apply to TMP texts
            if (m_TextElements != null)
            {
                for (int i = 0; i < m_TextElements.Length; i++)
                {
                    if (m_TextElements[i] != null)
                        m_TextElements[i].color = m_CurrentTextColor;
                }
            }

            // Apply to LiquidGlass material
            if (m_LiquidGlassMaterial != null)
            {
                m_LiquidGlassMaterial.SetColor(s_BorderColorID, m_CurrentBorderColor);
                m_LiquidGlassMaterial.SetColor(s_HighlightColorID, m_CurrentHighlightColor);
            }
        }
    }
}
