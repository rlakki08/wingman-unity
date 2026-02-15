using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using TMPro;

public static class ApplyLiquidGlass
{
    // ================================================================
    // Helper: Apply LiquidGlass material to a panel's WingmanCardRoot Image
    // ================================================================

    /// <summary>
    /// Finds the WingmanCardRoot/Image on the given panel and applies the
    /// LiquidGlass material so it's see-through (not an opaque black wall).
    /// </summary>
    static void ApplyLiquidGlassToPanel(GameObject panel)
    {
        if (panel == null) return;

        Material mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Wingman/Materials/LiquidGlass.mat");
        if (mat == null)
        {
            Debug.LogWarning("[Wingman] LiquidGlass.mat not found — panel background may be opaque");
            return;
        }

        Transform cardRoot = panel.transform.Find("WingmanCardRoot");
        if (cardRoot == null) return;

        Image img = cardRoot.GetComponent<Image>();
        if (img == null) return;

        img.sprite = null;
        img.type = Image.Type.Simple;
        img.material = mat;
        img.color = Color.white;

        EditorUtility.SetDirty(img);
        EditorUtility.SetDirty(cardRoot.gameObject);
    }

    [MenuItem("Wingman/Apply Liquid Glass Material")]
    public static void Apply()
    {
        // Load the material
        Material mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Wingman/Materials/LiquidGlass.mat");
        if (mat == null)
        {
            Debug.LogError("[Wingman] LiquidGlass.mat not found at Assets/Wingman/Materials/LiquidGlass.mat");
            return;
        }

        // Find WingmanCardRoot
        GameObject cardRoot = GameObject.Find("Wingman Panel/WingmanCardRoot");
        if (cardRoot == null)
        {
            Debug.LogError("[Wingman] WingmanCardRoot not found under Wingman Panel");
            return;
        }

        Image img = cardRoot.GetComponent<Image>();
        if (img == null)
        {
            Debug.LogError("[Wingman] No Image component on WingmanCardRoot");
            return;
        }

        // Clear the broken sprite reference and set to Simple type
        img.sprite = null;
        img.type = Image.Type.Simple;

        // Assign the Liquid Glass material
        img.material = mat;

        // Set the image color to white (shader handles tinting)
        img.color = Color.white;

        EditorUtility.SetDirty(img);
        EditorUtility.SetDirty(cardRoot);

        Debug.Log("[Wingman] LiquidGlass material applied to WingmanCardRoot Image. Sprite cleared, type set to Simple.");
    }

    [MenuItem("Wingman/Setup Adaptive Color")]
    public static void SetupAdaptiveColor()
    {
        // Find Wingman Panel
        GameObject wingmanPanel = GameObject.Find("Wingman Panel");
        if (wingmanPanel == null)
        {
            Debug.LogError("[Wingman] Wingman Panel not found in scene");
            return;
        }

        // Add or get WingmanAdaptiveColor component
        var adaptiveColor = wingmanPanel.GetComponent<Wingman.WingmanAdaptiveColor>();
        if (adaptiveColor == null)
        {
            adaptiveColor = wingmanPanel.AddComponent<Wingman.WingmanAdaptiveColor>();
            Debug.Log("[Wingman] Added WingmanAdaptiveColor component to Wingman Panel");
        }

        // Find text elements
        Transform contentTransform = wingmanPanel.transform.Find("WingmanCardRoot/Wingman Content");
        if (contentTransform == null)
        {
            Debug.LogError("[Wingman] Wingman Content not found under WingmanCardRoot");
            return;
        }

        var textElements = new System.Collections.Generic.List<TextMeshProUGUI>();

        // Modal Text
        Transform modalTextTransform = contentTransform.Find("Modal Text");
        if (modalTextTransform != null)
        {
            var tmp = modalTextTransform.GetComponent<TextMeshProUGUI>();
            if (tmp != null) textElements.Add(tmp);
        }

        // Transcript Text
        Transform transcriptTextTransform = contentTransform.Find("Transcript Text");
        if (transcriptTextTransform != null)
        {
            var tmp = transcriptTextTransform.GetComponent<TextMeshProUGUI>();
            if (tmp != null) textElements.Add(tmp);
        }

        // Use SerializedObject to set private serialized fields
        var so = new SerializedObject(adaptiveColor);

        // Set m_TextElements
        var textProp = so.FindProperty("m_TextElements");
        textProp.arraySize = textElements.Count;
        for (int i = 0; i < textElements.Count; i++)
        {
            textProp.GetArrayElementAtIndex(i).objectReferenceValue = textElements[i];
        }

        // Set m_LiquidGlassMaterial
        Material mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Wingman/Materials/LiquidGlass.mat");
        var matProp = so.FindProperty("m_LiquidGlassMaterial");
        matProp.objectReferenceValue = mat;

        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(adaptiveColor);
        EditorUtility.SetDirty(wingmanPanel);

        Debug.Log($"[Wingman] Adaptive Color setup complete: {textElements.Count} text elements, material={(mat != null ? "assigned" : "MISSING")}");
    }

    [MenuItem("Wingman/Setup Direct STT")]
    public static void SetupDirectSTT()
    {
        // Find Wingman Manager
        GameObject managerObj = GameObject.Find("Wingman Manager");
        if (managerObj == null)
        {
            Debug.LogError("[Wingman] Wingman Manager not found in scene");
            return;
        }

        // Add ElevenLabsSTT if not present
        var stt = managerObj.GetComponent<Wingman.ElevenLabsSTT>();
        if (stt == null)
        {
            stt = managerObj.AddComponent<Wingman.ElevenLabsSTT>();
            Debug.Log("[Wingman] Added ElevenLabsSTT component to Wingman Manager");
        }
        else
        {
            Debug.Log("[Wingman] ElevenLabsSTT already on Wingman Manager");
        }

        // Wire it into WingmanManager's m_STT field
        var manager = managerObj.GetComponent<Wingman.WingmanManager>();
        if (manager != null)
        {
            var so = new SerializedObject(manager);
            var sttProp = so.FindProperty("m_STT");
            sttProp.objectReferenceValue = stt;

            // Ensure m_DirectMode is true
            var directModeProp = so.FindProperty("m_DirectMode");
            directModeProp.boolValue = true;

            // Ensure m_AutoStart is true for testing
            var autoStartProp = so.FindProperty("m_AutoStart");
            autoStartProp.boolValue = true;

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(manager);
            Debug.Log("[Wingman] WingmanManager wired: m_STT assigned, DirectMode=true, AutoStart=true");
        }

        EditorUtility.SetDirty(managerObj);
        Debug.Log("[Wingman] Direct STT setup complete. Run 'Wingman > Setup Direct STT' after domain reload if needed.");
    }

    [MenuItem("Wingman/Setup Debug Panel")]
    public static void SetupDebugPanel()
    {
        // Check if Debug Panel already exists
        GameObject existing = GameObject.Find("Debug Panel");
        if (existing != null)
        {
            Debug.LogWarning("[Wingman] Debug Panel already exists. Delete it first if you want to recreate.");
            Selection.activeGameObject = existing;
            return;
        }

        // Find the Wingman Panel to duplicate
        GameObject wingmanPanel = GameObject.Find("Wingman Panel");
        if (wingmanPanel == null)
        {
            Debug.LogError("[Wingman] Wingman Panel not found — cannot duplicate for Debug Panel");
            return;
        }

        // Duplicate
        GameObject debugPanel = Object.Instantiate(wingmanPanel, wingmanPanel.transform.parent);
        debugPanel.name = "Debug Panel";

        // Apply LiquidGlass so it's see-through (not an opaque black wall)
        ApplyLiquidGlassToPanel(debugPanel);

        // --- Configure LazyFollow: offset to the LEFT of the user ---
        var lazyFollow = debugPanel.GetComponent<LazyFollow>();
        if (lazyFollow != null)
        {
            var lfSO = new SerializedObject(lazyFollow);

            // targetOffset = (-0.3, 0, 0.6) — 0.3m LEFT, 0.6m forward
            var offsetProp = lfSO.FindProperty("m_TargetOffset");
            offsetProp.vector3Value = new Vector3(-0.3f, 0f, 0.6f);

            lfSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(lazyFollow);
        }

        // --- Remove components that belong to the Wingman Panel, not the debug panel ---
        var transcriptionDisplay = debugPanel.GetComponent<Wingman.WingmanTranscriptionDisplay>();
        if (transcriptionDisplay != null) Object.DestroyImmediate(transcriptionDisplay);

        var adaptiveColor = debugPanel.GetComponent<Wingman.WingmanAdaptiveColor>();
        if (adaptiveColor != null) Object.DestroyImmediate(adaptiveColor);

        // --- Add WingmanDebugDisplay component ---
        var debugDisplay = debugPanel.AddComponent<Wingman.WingmanDebugDisplay>();

        // --- Configure text elements ---
        // Use Modal Text as status header, Transcript Text as debug stream (same layout as transcription)
        Transform contentTransform = debugPanel.transform.Find("WingmanCardRoot/Wingman Content");
        TextMeshProUGUI debugTMP = null;

        if (contentTransform != null)
        {
            // Configure Modal Text as a small status header
            Transform modalTextTransform = contentTransform.Find("Modal Text");
            if (modalTextTransform != null)
            {
                var modalTMP = modalTextTransform.GetComponent<TextMeshProUGUI>();
                if (modalTMP != null)
                {
                    modalTMP.text = "Debug Log";
                    modalTMP.fontSize = 16f;
                    modalTMP.alignment = TextAlignmentOptions.TopLeft;
                    modalTMP.overflowMode = TextOverflowModes.Ellipsis;
                    EditorUtility.SetDirty(modalTMP);
                }

                RectTransform modalRT = modalTextTransform.GetComponent<RectTransform>();
                if (modalRT != null)
                {
                    modalRT.anchorMin = new Vector2(0, 1);
                    modalRT.anchorMax = new Vector2(1, 1);
                    modalRT.pivot = new Vector2(0.5f, 1);
                    modalRT.anchoredPosition = new Vector2(0, -10);
                    modalRT.sizeDelta = new Vector2(-30, 36);
                    EditorUtility.SetDirty(modalRT);
                }
            }

            // Use Transcript Text as the debug stream area (keep active, full area)
            Transform transcriptTextTransform = contentTransform.Find("Transcript Text");
            if (transcriptTextTransform != null)
            {
                transcriptTextTransform.gameObject.SetActive(true);

                debugTMP = transcriptTextTransform.GetComponent<TextMeshProUGUI>();
                if (debugTMP != null)
                {
                    debugTMP.text = "Waiting for events...";
                    debugTMP.fontSize = 18f;
                    debugTMP.alignment = TextAlignmentOptions.TopLeft;
                    debugTMP.enableWordWrapping = true;
                    debugTMP.overflowMode = TextOverflowModes.Truncate;
                    debugTMP.richText = true;
                    EditorUtility.SetDirty(debugTMP);
                }

                RectTransform transcriptRT = transcriptTextTransform.GetComponent<RectTransform>();
                if (transcriptRT != null)
                {
                    transcriptRT.anchorMin = new Vector2(0, 0);
                    transcriptRT.anchorMax = new Vector2(1, 1);
                    transcriptRT.pivot = new Vector2(0.5f, 0.5f);
                    transcriptRT.offsetMin = new Vector2(15, 15);
                    transcriptRT.offsetMax = new Vector2(-15, -50);
                    EditorUtility.SetDirty(transcriptRT);
                }
            }
        }

        // Wire the TMP reference into WingmanDebugDisplay
        if (debugDisplay != null && debugTMP != null)
        {
            var ddSO = new SerializedObject(debugDisplay);
            var textProp = ddSO.FindProperty("m_DebugText");
            textProp.objectReferenceValue = debugTMP;
            ddSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(debugDisplay);
        }

        // --- Wire the debug display into WingmanManager ---
        GameObject managerObj = GameObject.Find("Wingman Manager");
        if (managerObj != null)
        {
            var manager = managerObj.GetComponent<Wingman.WingmanManager>();
            if (manager != null)
            {
                var mgrSO = new SerializedObject(manager);
                var debugProp = mgrSO.FindProperty("m_DebugDisplay");
                debugProp.objectReferenceValue = debugDisplay;
                mgrSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(manager);
            }
        }

        EditorUtility.SetDirty(debugPanel);
        Selection.activeGameObject = debugPanel;

        Debug.Log("[Wingman] Debug Panel created with LiquidGlass (see-through). Position: LEFT of user (-0.3, 0, 0.6). " +
                  $"TMP ref: {(debugTMP != null ? "assigned" : "MISSING")}");
    }

    [MenuItem("Wingman/Setup On-Device Gemini")]
    public static void SetupOnDeviceGemini()
    {
        // Find Wingman Manager
        GameObject managerObj = GameObject.Find("Wingman Manager");
        if (managerObj == null)
        {
            Debug.LogError("[Wingman] Wingman Manager not found in scene");
            return;
        }

        // Add GeminiClient if not present
        var geminiClient = managerObj.GetComponent<Wingman.GeminiClient>();
        if (geminiClient == null)
        {
            geminiClient = managerObj.AddComponent<Wingman.GeminiClient>();
            Debug.Log("[Wingman] Added GeminiClient component to Wingman Manager");
        }
        else
        {
            Debug.Log("[Wingman] GeminiClient already on Wingman Manager");
        }

        // Add GeminiSuggestionService if not present
        var geminiService = managerObj.GetComponent<Wingman.GeminiSuggestionService>();
        if (geminiService == null)
        {
            geminiService = managerObj.AddComponent<Wingman.GeminiSuggestionService>();
            Debug.Log("[Wingman] Added GeminiSuggestionService component to Wingman Manager");
        }
        else
        {
            Debug.Log("[Wingman] GeminiSuggestionService already on Wingman Manager");
        }

        // Wire GeminiClient into GeminiSuggestionService
        var serviceSO = new SerializedObject(geminiService);
        var clientProp = serviceSO.FindProperty("m_GeminiClient");
        clientProp.objectReferenceValue = geminiClient;
        serviceSO.ApplyModifiedProperties();
        EditorUtility.SetDirty(geminiService);

        // Wire GeminiSuggestionService into WingmanManager and enable on-device mode
        var manager = managerObj.GetComponent<Wingman.WingmanManager>();
        if (manager != null)
        {
            var mgrSO = new SerializedObject(manager);

            // Set m_GeminiService
            var geminiProp = mgrSO.FindProperty("m_GeminiService");
            geminiProp.objectReferenceValue = geminiService;

            // Enable on-device Gemini
            var onDeviceProp = mgrSO.FindProperty("m_OnDeviceGemini");
            onDeviceProp.boolValue = true;

            // Disable legacy backend forwarding
            var forwardProp = mgrSO.FindProperty("m_ForwardToBackend");
            forwardProp.boolValue = false;

            // Ensure direct mode is on
            var directModeProp = mgrSO.FindProperty("m_DirectMode");
            directModeProp.boolValue = true;

            // Ensure auto-start is on
            var autoStartProp = mgrSO.FindProperty("m_AutoStart");
            autoStartProp.boolValue = true;

            mgrSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(manager);

            Debug.Log("[Wingman] WingmanManager wired: GeminiService assigned, OnDeviceGemini=true, " +
                      "ForwardToBackend=false, DirectMode=true, AutoStart=true");
        }

        EditorUtility.SetDirty(managerObj);
        Selection.activeGameObject = managerObj;

        Debug.Log("[Wingman] On-Device Gemini setup complete! Pipeline: " +
                  "Mic → ElevenLabs STT → Gemini 3 Pro Preview → Suggestions HUD. No backend needed.");
    }

    [MenuItem("Wingman/Resize Transcription Panel")]
    public static void ResizeTranscriptionPanel()
    {
        // Find Wingman Panel
        GameObject wingmanPanel = GameObject.Find("Wingman Panel");
        if (wingmanPanel == null)
        {
            Debug.LogError("[Wingman] Wingman Panel not found");
            return;
        }

        // --- Resize WingmanCardRoot ---
        // Currently SizeDelta = (300, 0). Height of 0 means the card has no bounding box
        // for clipping. Increase to a larger panel: 420 x 520.
        Transform cardRoot = wingmanPanel.transform.Find("WingmanCardRoot");
        if (cardRoot == null)
        {
            Debug.LogError("[Wingman] WingmanCardRoot not found");
            return;
        }

        RectTransform cardRootRT = cardRoot.GetComponent<RectTransform>();
        if (cardRootRT != null)
        {
            cardRootRT.sizeDelta = new Vector2(420, 520);
            EditorUtility.SetDirty(cardRootRT);
            Debug.Log($"[Wingman] WingmanCardRoot resized to 420x520 (was {cardRootRT.sizeDelta})");
        }

        // Also resize the BoxCollider to match (for grab interaction)
        var boxCol = wingmanPanel.GetComponent<BoxCollider>();
        if (boxCol != null)
        {
            // BoxCollider size is in world-space of the panel. WingmanCardRoot scale = 0.00075.
            // 420 * 0.00075 = 0.315, 520 * 0.00075 = 0.39
            boxCol.size = new Vector3(0.315f, 0.39f, 0.01f);
            boxCol.center = Vector3.zero;
            EditorUtility.SetDirty(boxCol);
        }

        // --- Resize Wingman Content to match ---
        Transform content = cardRoot.Find("Wingman Content");
        if (content != null)
        {
            RectTransform contentRT = content.GetComponent<RectTransform>();
            if (contentRT != null)
            {
                contentRT.sizeDelta = new Vector2(420, 520);
                EditorUtility.SetDirty(contentRT);
            }
        }

        // --- Configure Modal Text (status line) — small strip at top ---
        Transform modalText = content != null ? content.Find("Modal Text") : null;
        if (modalText != null)
        {
            RectTransform modalRT = modalText.GetComponent<RectTransform>();
            if (modalRT != null)
            {
                // Anchor to top, stretch width
                modalRT.anchorMin = new Vector2(0, 1);
                modalRT.anchorMax = new Vector2(1, 1);
                modalRT.pivot = new Vector2(0.5f, 1);
                modalRT.anchoredPosition = new Vector2(0, -10); // 10px from top
                modalRT.sizeDelta = new Vector2(-30, 40); // inset 15px each side, 40px tall
                EditorUtility.SetDirty(modalRT);
            }

            var modalTMP = modalText.GetComponent<TextMeshProUGUI>();
            if (modalTMP != null)
            {
                modalTMP.fontSize = 18;
                modalTMP.alignment = TextAlignmentOptions.TopLeft;
                modalTMP.overflowMode = TextOverflowModes.Ellipsis;
                modalTMP.enableWordWrapping = true;
                EditorUtility.SetDirty(modalTMP);
            }
        }

        // --- Configure Transcript Text — fills remaining space, bottom-aligned ---
        Transform transcriptText = content != null ? content.Find("Transcript Text") : null;
        if (transcriptText != null)
        {
            // Make sure it's active
            transcriptText.gameObject.SetActive(true);

            RectTransform transcriptRT = transcriptText.GetComponent<RectTransform>();
            if (transcriptRT != null)
            {
                // Anchor: stretch both axes, but leave 55px gap at top for status line
                transcriptRT.anchorMin = new Vector2(0, 0);
                transcriptRT.anchorMax = new Vector2(1, 1);
                transcriptRT.pivot = new Vector2(0.5f, 0);
                transcriptRT.offsetMin = new Vector2(15, 15);   // left=15, bottom=15
                transcriptRT.offsetMax = new Vector2(-15, -55);  // right=-15, top=-55
                EditorUtility.SetDirty(transcriptRT);
            }

            var transcriptTMP = transcriptText.GetComponent<TextMeshProUGUI>();
            if (transcriptTMP != null)
            {
                transcriptTMP.fontSize = 28;
                transcriptTMP.alignment = TextAlignmentOptions.TopLeft;
                transcriptTMP.overflowMode = TextOverflowModes.Truncate;
                transcriptTMP.enableWordWrapping = true;
                transcriptTMP.richText = true;
                EditorUtility.SetDirty(transcriptTMP);
            }
        }

        EditorUtility.SetDirty(wingmanPanel);
        Selection.activeGameObject = wingmanPanel;

        Debug.Log("[Wingman] Transcription panel resized: CardRoot=420x520, Content=420x520, " +
                  "Modal Text=top strip 40px, Transcript Text=fills rest top-aligned (newest-first), fontSize=28. " +
                  "Overflow=Truncate (clips oldest text at bottom).");
    }

    [MenuItem("Wingman/Setup Gemini Display Panel")]
    public static void SetupGeminiDisplayPanel()
    {
        // Check if Gemini Display Panel already exists
        GameObject existing = GameObject.Find("Gemini Display Panel");
        if (existing != null)
        {
            Debug.LogWarning("[Wingman] Gemini Display Panel already exists. Delete it first if you want to recreate.");
            Selection.activeGameObject = existing;
            return;
        }

        // Find the Wingman Panel to duplicate
        GameObject wingmanPanel = GameObject.Find("Wingman Panel");
        if (wingmanPanel == null)
        {
            Debug.LogError("[Wingman] Wingman Panel not found — cannot duplicate for Gemini Display Panel");
            return;
        }

        // Duplicate
        GameObject geminiPanel = Object.Instantiate(wingmanPanel, wingmanPanel.transform.parent);
        geminiPanel.name = "Gemini Display Panel";

        // Apply LiquidGlass so it's see-through (not an opaque black wall)
        ApplyLiquidGlassToPanel(geminiPanel);

        // --- Configure LazyFollow: position ABOVE the user, centered ---
        var lazyFollow = geminiPanel.GetComponent<LazyFollow>();
        if (lazyFollow != null)
        {
            var lfSO = new SerializedObject(lazyFollow);
            // targetOffset = (0, 0.35, 0.6) — above center, 0.6m forward
            var offsetProp = lfSO.FindProperty("m_TargetOffset");
            offsetProp.vector3Value = new Vector3(0f, 0.35f, 0.6f);
            lfSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(lazyFollow);
        }

        // --- Remove Wingman-specific components ---
        var transcriptionDisplay = geminiPanel.GetComponent<Wingman.WingmanTranscriptionDisplay>();
        if (transcriptionDisplay != null) Object.DestroyImmediate(transcriptionDisplay);

        var adaptiveColor = geminiPanel.GetComponent<Wingman.WingmanAdaptiveColor>();
        if (adaptiveColor != null) Object.DestroyImmediate(adaptiveColor);

        // --- Resize to WIDE format (700x520) ---
        Transform cardRoot = geminiPanel.transform.Find("WingmanCardRoot");
        if (cardRoot != null)
        {
            RectTransform cardRootRT = cardRoot.GetComponent<RectTransform>();
            if (cardRootRT != null)
            {
                cardRootRT.sizeDelta = new Vector2(700, 520);
                EditorUtility.SetDirty(cardRootRT);
            }

            // Resize content to match
            Transform content = cardRoot.Find("Wingman Content");
            if (content != null)
            {
                RectTransform contentRT = content.GetComponent<RectTransform>();
                if (contentRT != null)
                {
                    contentRT.sizeDelta = new Vector2(700, 520);
                    EditorUtility.SetDirty(contentRT);
                }
            }
        }

        // Update BoxCollider to match wider size
        // 700 * 0.00075 = 0.525, 520 * 0.00075 = 0.39
        var boxCol = geminiPanel.GetComponent<BoxCollider>();
        if (boxCol != null)
        {
            boxCol.size = new Vector3(0.525f, 0.39f, 0.01f);
            boxCol.center = Vector3.zero;
            EditorUtility.SetDirty(boxCol);
        }

        // --- Configure text elements ---
        Transform contentTransform = geminiPanel.transform.Find("WingmanCardRoot/Wingman Content");
        TextMeshProUGUI statusTMP = null;
        TextMeshProUGUI outputTMP = null;

        if (contentTransform != null)
        {
            // Configure Modal Text as status line
            Transform modalTextTransform = contentTransform.Find("Modal Text");
            if (modalTextTransform != null)
            {
                statusTMP = modalTextTransform.GetComponent<TextMeshProUGUI>();
                if (statusTMP != null)
                {
                    statusTMP.text = "Gemini Monitor - Waiting...";
                    statusTMP.fontSize = 16f;
                    statusTMP.alignment = TextAlignmentOptions.TopLeft;
                    statusTMP.enableWordWrapping = true;
                    statusTMP.overflowMode = TextOverflowModes.Ellipsis;
                    EditorUtility.SetDirty(statusTMP);
                }

                // Set up anchoring for status
                RectTransform modalRT = modalTextTransform.GetComponent<RectTransform>();
                if (modalRT != null)
                {
                    modalRT.anchorMin = new Vector2(0, 1);
                    modalRT.anchorMax = new Vector2(1, 1);
                    modalRT.pivot = new Vector2(0.5f, 1);
                    modalRT.anchoredPosition = new Vector2(0, -10);
                    modalRT.sizeDelta = new Vector2(-30, 36);
                    EditorUtility.SetDirty(modalRT);
                }
            }

            // Configure Transcript Text as Gemini output area
            Transform transcriptTextTransform = contentTransform.Find("Transcript Text");
            if (transcriptTextTransform != null)
            {
                transcriptTextTransform.gameObject.SetActive(true);

                outputTMP = transcriptTextTransform.GetComponent<TextMeshProUGUI>();
                if (outputTMP != null)
                {
                    outputTMP.text = "";
                    outputTMP.fontSize = 18f;    // smaller for JSON readability
                    outputTMP.alignment = TextAlignmentOptions.TopLeft;
                    outputTMP.enableWordWrapping = true;
                    outputTMP.overflowMode = TextOverflowModes.Truncate;
                    outputTMP.richText = true;
                    EditorUtility.SetDirty(outputTMP);
                }

                // Anchor to fill remaining space
                RectTransform transcriptRT = transcriptTextTransform.GetComponent<RectTransform>();
                if (transcriptRT != null)
                {
                    transcriptRT.anchorMin = new Vector2(0, 0);
                    transcriptRT.anchorMax = new Vector2(1, 1);
                    transcriptRT.pivot = new Vector2(0.5f, 0.5f);
                    transcriptRT.offsetMin = new Vector2(15, 15);
                    transcriptRT.offsetMax = new Vector2(-15, -50);
                    EditorUtility.SetDirty(transcriptRT);
                }
            }
        }

        // --- Add WingmanGeminiDisplay component ---
        var geminiDisplay = geminiPanel.AddComponent<Wingman.WingmanGeminiDisplay>();

        // Wire TMP references
        if (geminiDisplay != null)
        {
            var gdSO = new SerializedObject(geminiDisplay);

            if (statusTMP != null)
            {
                var statusProp = gdSO.FindProperty("m_StatusText");
                statusProp.objectReferenceValue = statusTMP;
            }

            if (outputTMP != null)
            {
                var outputProp = gdSO.FindProperty("m_OutputText");
                outputProp.objectReferenceValue = outputTMP;
            }

            // Wire GeminiClient and GeminiSuggestionService
            GameObject managerObj = GameObject.Find("Wingman Manager");
            if (managerObj != null)
            {
                var geminiClient = managerObj.GetComponent<Wingman.GeminiClient>();
                if (geminiClient != null)
                {
                    var clientProp = gdSO.FindProperty("m_GeminiClient");
                    clientProp.objectReferenceValue = geminiClient;
                }

                var geminiService = managerObj.GetComponent<Wingman.GeminiSuggestionService>();
                if (geminiService != null)
                {
                    var serviceProp = gdSO.FindProperty("m_GeminiService");
                    serviceProp.objectReferenceValue = geminiService;
                }
            }

            gdSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(geminiDisplay);
        }

        EditorUtility.SetDirty(geminiPanel);
        Selection.activeGameObject = geminiPanel;

        Debug.Log("[Wingman] Gemini Display Panel created: WIDE (700x520), position ABOVE user (0, 0.35, 0.6). " +
                  $"Status TMP: {(statusTMP != null ? "assigned" : "MISSING")}, " +
                  $"Output TMP: {(outputTMP != null ? "assigned" : "MISSING")}. " +
                  "Auto-wires to GeminiClient + GeminiSuggestionService events.");
    }

    // ================================================================
    // Disable Coaching UI (the black rectangle blocking passthrough)
    // ================================================================

    [MenuItem("Wingman/Disable Coaching UI")]
    public static void DisableCoachingUI()
    {
        // Find by name — this is the legacy Coaching UI that renders as an opaque black box
        GameObject coachingUI = GameObject.Find("Coaching UI");
        if (coachingUI == null)
        {
            // It might already be disabled — search inactive objects
            var allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
            foreach (var t in allTransforms)
            {
                if (t.name == "Coaching UI" && t.parent != null && t.parent.name == "UI")
                {
                    coachingUI = t.gameObject;
                    break;
                }
            }
        }

        if (coachingUI == null)
        {
            Debug.LogWarning("[Wingman] Coaching UI not found in scene — may already be removed.");
            return;
        }

        coachingUI.SetActive(false);
        EditorUtility.SetDirty(coachingUI);

        // Also disable the old Wingman HUD if it's still active
        GameObject wingmanHUD = GameObject.Find("Wingman HUD");
        if (wingmanHUD != null)
        {
            wingmanHUD.SetActive(false);
            EditorUtility.SetDirty(wingmanHUD);
            Debug.Log("[Wingman] Wingman HUD also disabled.");
        }

        Debug.Log("[Wingman] Coaching UI DISABLED — black rectangle removed from passthrough view. " +
                  "The GameObject is preserved but inactive.");
    }

    // ================================================================
    // Setup Suggestions Panel (dedicated panel for Gemini coaching output)
    // ================================================================

    [MenuItem("Wingman/Setup Suggestions Panel")]
    public static void SetupSuggestionsPanel()
    {
        // Check if Suggestions Panel already exists
        GameObject existing = GameObject.Find("Suggestions Panel");
        if (existing != null)
        {
            Debug.LogWarning("[Wingman] Suggestions Panel already exists. Delete it first if you want to recreate.");
            Selection.activeGameObject = existing;
            return;
        }

        // Find the Wingman Panel to duplicate
        GameObject wingmanPanel = GameObject.Find("Wingman Panel");
        if (wingmanPanel == null)
        {
            Debug.LogError("[Wingman] Wingman Panel not found — cannot duplicate for Suggestions Panel");
            return;
        }

        // Duplicate
        GameObject suggestionsPanel = Object.Instantiate(wingmanPanel, wingmanPanel.transform.parent);
        suggestionsPanel.name = "Suggestions Panel";

        // Apply LiquidGlass so it's see-through (CRITICAL: prevents opaque black wall)
        ApplyLiquidGlassToPanel(suggestionsPanel);

        // --- Configure LazyFollow: position BELOW transcription panel (right side) ---
        var lazyFollow = suggestionsPanel.GetComponent<LazyFollow>();
        if (lazyFollow != null)
        {
            var lfSO = new SerializedObject(lazyFollow);
            // targetOffset = (0.3, -0.25, 0.6) — aligned right with transcription, 0.25m below
            var offsetProp = lfSO.FindProperty("m_TargetOffset");
            offsetProp.vector3Value = new Vector3(0.3f, -0.25f, 0.6f);
            lfSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(lazyFollow);
        }

        // --- Remove Wingman-specific components that don't belong ---
        var transcriptionDisplay = suggestionsPanel.GetComponent<Wingman.WingmanTranscriptionDisplay>();
        if (transcriptionDisplay != null) Object.DestroyImmediate(transcriptionDisplay);

        var adaptiveColor = suggestionsPanel.GetComponent<Wingman.WingmanAdaptiveColor>();
        if (adaptiveColor != null) Object.DestroyImmediate(adaptiveColor);

        // Also remove any existing debug display that may have been duplicated
        var debugDisplay = suggestionsPanel.GetComponent<Wingman.WingmanDebugDisplay>();
        if (debugDisplay != null) Object.DestroyImmediate(debugDisplay);

        // --- Configure text elements ---
        Transform contentTransform = suggestionsPanel.transform.Find("WingmanCardRoot/Wingman Content");
        TextMeshProUGUI statusTMP = null;
        TextMeshProUGUI suggestionsTMP = null;

        if (contentTransform != null)
        {
            // Configure Modal Text as status header (topic + engagement)
            Transform modalTextTransform = contentTransform.Find("Modal Text");
            if (modalTextTransform != null)
            {
                statusTMP = modalTextTransform.GetComponent<TextMeshProUGUI>();
                if (statusTMP != null)
                {
                    statusTMP.text = "Suggestions";
                    statusTMP.fontSize = 18f;
                    statusTMP.alignment = TextAlignmentOptions.TopLeft;
                    statusTMP.enableWordWrapping = true;
                    statusTMP.overflowMode = TextOverflowModes.Ellipsis;
                    EditorUtility.SetDirty(statusTMP);
                }

                RectTransform modalRT = modalTextTransform.GetComponent<RectTransform>();
                if (modalRT != null)
                {
                    modalRT.anchorMin = new Vector2(0, 1);
                    modalRT.anchorMax = new Vector2(1, 1);
                    modalRT.pivot = new Vector2(0.5f, 1);
                    modalRT.anchoredPosition = new Vector2(0, -10);
                    modalRT.sizeDelta = new Vector2(-30, 36);
                    EditorUtility.SetDirty(modalRT);
                }
            }

            // Configure Transcript Text as main suggestion content
            Transform transcriptTextTransform = contentTransform.Find("Transcript Text");
            if (transcriptTextTransform != null)
            {
                transcriptTextTransform.gameObject.SetActive(true);

                suggestionsTMP = transcriptTextTransform.GetComponent<TextMeshProUGUI>();
                if (suggestionsTMP != null)
                {
                    suggestionsTMP.text = "<i>Waiting for conversation...</i>";
                    suggestionsTMP.fontSize = 26f;
                    suggestionsTMP.alignment = TextAlignmentOptions.TopLeft;
                    suggestionsTMP.enableWordWrapping = true;
                    suggestionsTMP.overflowMode = TextOverflowModes.Truncate;
                    suggestionsTMP.richText = true;
                    // More line spacing for readability of numbered suggestions
                    suggestionsTMP.lineSpacing = 8f;
                    EditorUtility.SetDirty(suggestionsTMP);
                }

                RectTransform transcriptRT = transcriptTextTransform.GetComponent<RectTransform>();
                if (transcriptRT != null)
                {
                    transcriptRT.anchorMin = new Vector2(0, 0);
                    transcriptRT.anchorMax = new Vector2(1, 1);
                    transcriptRT.pivot = new Vector2(0.5f, 0.5f);
                    transcriptRT.offsetMin = new Vector2(15, 15);
                    transcriptRT.offsetMax = new Vector2(-15, -50);
                    EditorUtility.SetDirty(transcriptRT);
                }
            }
        }

        // --- Add WingmanSuggestionsDisplay component ---
        var suggestionsDisplay = suggestionsPanel.AddComponent<Wingman.WingmanSuggestionsDisplay>();

        // Wire TMP references
        if (suggestionsDisplay != null)
        {
            var sdSO = new SerializedObject(suggestionsDisplay);

            if (statusTMP != null)
            {
                var statusProp = sdSO.FindProperty("m_StatusText");
                statusProp.objectReferenceValue = statusTMP;
            }

            if (suggestionsTMP != null)
            {
                var textProp = sdSO.FindProperty("m_SuggestionsText");
                textProp.objectReferenceValue = suggestionsTMP;
            }

            sdSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(suggestionsDisplay);
        }

        // --- Wire into WingmanManager ---
        GameObject managerObj = GameObject.Find("Wingman Manager");
        if (managerObj != null)
        {
            var manager = managerObj.GetComponent<Wingman.WingmanManager>();
            if (manager != null)
            {
                var mgrSO = new SerializedObject(manager);
                var sugProp = mgrSO.FindProperty("m_SuggestionsDisplay");
                sugProp.objectReferenceValue = suggestionsDisplay;
                mgrSO.ApplyModifiedProperties();
                EditorUtility.SetDirty(manager);
            }
        }

        EditorUtility.SetDirty(suggestionsPanel);
        Selection.activeGameObject = suggestionsPanel;

        Debug.Log("[Wingman] Suggestions Panel created with LiquidGlass (see-through). " +
                  $"Position: BELOW transcription (0.3, -0.25, 0.6). " +
                  $"Status TMP: {(statusTMP != null ? "assigned" : "MISSING")}, " +
                  $"Suggestions TMP: {(suggestionsTMP != null ? "assigned" : "MISSING")}. " +
                  "Content REPLACES on each Gemini response (no accumulation).");
    }

    // ================================================================
    // Clean Up Template — permanently disable MR Template objects
    // ================================================================

    [MenuItem("Wingman/Clean Up Template")]
    public static void CleanUpTemplate()
    {
        int cleaned = 0;

        // --- 1. Disable template GameObjects ---
        string[] templateNames = new[]
        {
            "Environment",                              // Skybox mesh
            "Hand Menu Setup MR Template Variant",      // Template hand menu
            "Coaching UI",                              // Tutorial panel
            "Spatial Panel Manipulator",                // Template interactable
            "Tap Tooltip",                              // Template tooltip
            "Wingman HUD",                              // Legacy HUD (opaque background)
        };

        // GameObject.Find only finds active objects, so also search inactive
        var allTransforms = Resources.FindObjectsOfTypeAll<Transform>();

        foreach (var name in templateNames)
        {
            // Try active first
            GameObject obj = GameObject.Find(name);
            if (obj == null)
            {
                // Search inactive
                foreach (var t in allTransforms)
                {
                    if (t.name == name && t.gameObject.scene.isLoaded)
                    {
                        obj = t.gameObject;
                        break;
                    }
                }
            }

            if (obj != null && obj.activeSelf)
            {
                obj.SetActive(false);
                EditorUtility.SetDirty(obj);
                Debug.Log($"[Wingman] Disabled: {name}");
                cleaned++;
            }
        }

        // --- 2. Disable GoalManager component (on MR Interaction Setup) ---
        // It calls TogglePassthrough(false) on Start which kills passthrough.
        var allBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
        string[] templateComponentNames = new[]
        {
            "GoalManager",
            "FadeMaterial",
            "SpawnedObjectsManager",
        };

        foreach (var mb in allBehaviours)
        {
            if (mb == null || !mb.gameObject.scene.isLoaded) continue;
            var typeName = mb.GetType().Name;
            foreach (var target in templateComponentNames)
            {
                if (typeName == target && mb.enabled)
                {
                    mb.enabled = false;
                    EditorUtility.SetDirty(mb);
                    EditorUtility.SetDirty(mb.gameObject);
                    Debug.Log($"[Wingman] Disabled component: {typeName} on {mb.gameObject.name}");
                    cleaned++;
                    break;
                }
            }
        }

        // --- 3. Force ARCameraManager enabled (passthrough) ---
        var arCamManagers = Resources.FindObjectsOfTypeAll<UnityEngine.XR.ARFoundation.ARCameraManager>();
        foreach (var arCam in arCamManagers)
        {
            if (arCam != null && arCam.gameObject.scene.isLoaded && !arCam.enabled)
            {
                arCam.enabled = true;
                EditorUtility.SetDirty(arCam);
                Debug.Log($"[Wingman] Enabled ARCameraManager on {arCam.gameObject.name} — passthrough ON");
                cleaned++;
            }
        }

        if (cleaned == 0)
            Debug.Log("[Wingman] Clean Up Template: nothing to clean — already done.");
        else
            Debug.Log($"[Wingman] Clean Up Template: {cleaned} items cleaned. Save the scene to persist.");
    }
}
