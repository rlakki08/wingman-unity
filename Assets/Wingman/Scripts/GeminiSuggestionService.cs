using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Wingman
{
    /// <summary>
    /// On-device conversation coaching engine powered by Gemini.
    /// Replaces the entire backend pipeline (llm.py + websocket.py + cache.py + session_manager.py).
    ///
    /// Manages:
    ///   - Transcript context buffer (rolling window of recent turns)
    ///   - Heuristic short-circuits for common intents (greetings, questions, farewells, etc.)
    ///   - TTL-based suggestion cache (avoids duplicate LLM calls for similar transcripts)
    ///   - System prompt and structured JSON output parsing
    ///   - Fallback suggestions when LLM is unavailable or fails
    ///   - Silence detection for proactive conversation starters
    ///
    /// Attach to the Wingman Manager GameObject alongside GeminiClient.
    ///
    /// Usage:
    ///   1. Call AddTranscript() whenever a finalized transcript arrives from STT.
    ///   2. The service automatically triggers Gemini and fires OnSuggestionsReady.
    ///   3. Subscribe to OnSuggestionsReady to display suggestions on the HUD.
    /// </summary>
    public class GeminiSuggestionService : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] GeminiClient m_GeminiClient;

        [Header("Context Buffer")]
        [Tooltip("Maximum number of transcript turns to keep in the context window.")]
        [SerializeField] int m_MaxContextTurns = 6;

        [Tooltip("Minimum characters in context before triggering LLM (avoids wasting calls on tiny fragments).")]
        [SerializeField] int m_MinContextLength = 10;

        [Header("Cache")]
        [Tooltip("TTL for cached suggestions in seconds. Identical transcripts reuse cached results.")]
        [SerializeField] float m_CacheTTLSeconds = 300f;

        [Tooltip("Maximum number of cached suggestion entries.")]
        [SerializeField] int m_MaxCacheSize = 128;

        [Header("Silence Detection")]
        [Tooltip("Seconds of silence before proactively suggesting conversation starters.")]
        [SerializeField] float m_SilenceThresholdSeconds = 8f;

        [Tooltip("Enable proactive silence alerts and suggestions.")]
        [SerializeField] bool m_SilenceDetectionEnabled = true;

        [Header("Debug")]
        [SerializeField] bool m_DebugLog = true;

        WingmanDebugDisplay m_DebugDisplay;

        // --- Events ---

        /// <summary>Fired when new coaching suggestions are ready for the HUD.</summary>
        public event Action<SuggestionPayload> OnSuggestionsReady;

        /// <summary>Fired when an awkward silence is detected.</summary>
        public event Action<float> OnSilenceDetected;

        /// <summary>Fired when the service encounters an error.</summary>
        public event Action<string> OnError;

        // --- Internal state ---
        readonly List<TranscriptTurn> m_TranscriptHistory = new List<TranscriptTurn>();
        readonly Dictionary<string, CachedSuggestion> m_SuggestionCache = new Dictionary<string, CachedSuggestion>();
        bool m_IsProcessing;
        float m_LastSpeechTime;
        bool m_SilenceAlertSent;
        int m_TotalSuggestions;

        // --- System prompt (rewritten for substantive, context-specific coaching) ---
        const string k_SystemPrompt =
@"You are Wingman, a real-time social coach running inside AR glasses during a live face-to-face conversation. You receive a rolling transcript. Lines prefixed ""user:"" are the wearer (your client). Other lines are the person they're talking to.

YOUR JOB: Give the user SPECIFIC things to say that reference what was ACTUALLY discussed. Never generic filler.

=== RULES ===

1. REFERENCE SPECIFICS from the transcript. If they mentioned ""Tokyo"", your suggestion must contain the word ""Tokyo"" or something specific about it. If they said ""nursing"", reference nursing.

2. SUGGEST COMPLETE SAYABLE PHRASES (3-12 words). The user should be able to speak your suggestion almost verbatim.

3. When the other person shares something, suggest a SPECIFIC follow-up about THAT thing:
   - They mention a trip → ask about a specific aspect (food, highlight, how long)
   - They mention their job → ask what their day actually looks like
   - They mention a hobby → ask how they got into it or what they love about it
   - They share a problem → suggest acknowledging it specifically, then a helpful angle

4. When the user is ASKED a question, suggest ACTUAL ANSWERS — not stalling phrases like ""great question"" or ""let me think."" Give them content to say.

5. When a topic is dying (short replies, long pauses), suggest a PIVOT to a related but fresh angle. Don't just repeat the dead topic.

6. Match the energy: bar chat = casual/fun, deep conversation = thoughtful, flirting = playful/warm.

=== EXAMPLES ===

TRANSCRIPT: ""other: Yeah I just got back from two weeks in Japan""
GOOD: [""What was the best thing you ate there?"", ""Two weeks — did you do Tokyo and Kyoto or stay in one spot?"", ""I've always wanted to go — what surprised you most?""]
BAD: [""That's so cool"", ""Tell me more about your trip"", ""Wow, two weeks""]

TRANSCRIPT: ""other: I'm actually a pediatric nurse at Children's Hospital""
GOOD: [""Pediatric — what age group do you work with mostly?"", ""What made you choose peds over other specialties?"", ""That must be intense — what keeps you going?""]
BAD: [""That's amazing work"", ""Tell me more about nursing"", ""How interesting""]

TRANSCRIPT: ""other: Do you have any pets?""
GOOD: [""Yeah I have a golden retriever named Max"", ""No but I've been thinking about getting a cat"", ""I grew up with dogs — do you?""]
BAD: [""That's a great question"", ""Let me think about that"", ""What about you?""]

TRANSCRIPT: ""user: I'm in software engineering""  ""other: Oh nice, what kind of stuff do you build?""
GOOD: [""Mostly backend APIs — the invisible plumbing"", ""I work on mobile apps actually"", ""Right now I'm building an AR project""]
BAD: [""Oh you know, tech stuff"", ""It's complicated"", ""Various things""]

TRANSCRIPT: ""other: Yeah the commute is killing me, it's like 90 minutes each way""
GOOD: [""Ninety minutes — do you at least get a seat?"", ""Have you thought about asking to go hybrid?"", ""What do you do to survive it — podcasts?""]
BAD: [""That sounds rough"", ""I can relate"", ""Commutes are the worst""]

=== OUTPUT FORMAT (JSON only) ===
{
  ""suggestions"": [
    {""text"": ""actual phrase to say"", ""style"": ""casual|thoughtful|flirty|empathetic|humorous"", ""confidence"": 0.0-1.0}
  ],
  ""topic"": ""one-word conversation topic"",
  ""engagement_score"": 0.0-1.0,
  ""tone"": ""neutral|positive|negative|excited|tense""
}

Return 3-4 suggestions. engagement_score = how engaged the OTHER person seems (0=checked out, 1=locked in). confidence = how well the suggestion fits this exact moment.";

        // --- Heuristic data ---
        static readonly string[] k_GreetingWords = { "hi", "hello", "hey", "howdy", "greetings", "good morning", "good evening", "good afternoon", "what's up", "sup" };
        static readonly string[] k_FarewellWords = { "bye", "goodbye", "see you", "take care", "gotta go", "nice meeting", "later", "good night" };
        static readonly string[] k_SmallTalkWords = { "weather", "how's your day", "what do you do", "where are you from", "nice day" };

        // --- Fallback suggestions ---
        static readonly SuggestionPayload k_FallbackPayload = new SuggestionPayload
        {
            suggestions = new[]
            {
                new SuggestionData("Tell me more about that", SuggestionStyle.Casual, 0.6f),
                new SuggestionData("That's really interesting", SuggestionStyle.Thoughtful, 0.5f),
                new SuggestionData("I totally agree", SuggestionStyle.Empathetic, 0.5f),
            },
            engagementEstimate = 0.5f,
            dominantTopic = "general"
        };

        static readonly SuggestionPayload k_GreetingPayload = new SuggestionPayload
        {
            suggestions = new[]
            {
                new SuggestionData("Hey, great to see you!", SuggestionStyle.Casual, 0.9f, "greeting"),
                new SuggestionData("Hi there, how are you?", SuggestionStyle.Casual, 0.85f, "greeting"),
                new SuggestionData("Good to meet you", SuggestionStyle.Thoughtful, 0.8f, "greeting"),
            },
            engagementEstimate = 0.7f,
            dominantTopic = "greeting"
        };

        static readonly SuggestionPayload k_QuestionPayload = new SuggestionPayload
        {
            suggestions = new[]
            {
                new SuggestionData("That's a great question", SuggestionStyle.Thoughtful, 0.8f, "question"),
                new SuggestionData("Let me think about that", SuggestionStyle.Thoughtful, 0.7f, "question"),
                new SuggestionData("Honestly, I'd say...", SuggestionStyle.Casual, 0.7f, "question"),
            },
            engagementEstimate = 0.6f,
            dominantTopic = "question"
        };

        static readonly SuggestionPayload k_FarewellPayload = new SuggestionPayload
        {
            suggestions = new[]
            {
                new SuggestionData("It was great talking!", SuggestionStyle.Casual, 0.85f, "farewell"),
                new SuggestionData("Let's do this again soon", SuggestionStyle.Empathetic, 0.8f, "farewell"),
                new SuggestionData("Take care, see you around", SuggestionStyle.Casual, 0.75f, "farewell"),
            },
            engagementEstimate = 0.5f,
            dominantTopic = "farewell"
        };

        static readonly SuggestionPayload k_SilencePayload = new SuggestionPayload
        {
            suggestions = new[]
            {
                new SuggestionData("So, what are you into?", SuggestionStyle.Casual, 0.7f, "starter"),
                new SuggestionData("Done anything fun lately?", SuggestionStyle.Casual, 0.65f, "starter"),
                new SuggestionData("What's been on your mind?", SuggestionStyle.Thoughtful, 0.6f, "starter"),
            },
            engagementEstimate = 0.3f,
            dominantTopic = "silence"
        };

        // ================================================================
        // Unity Lifecycle
        // ================================================================

        void Awake()
        {
            if (m_GeminiClient == null)
                m_GeminiClient = GetComponent<GeminiClient>();
        }

        void Start()
        {
            m_DebugDisplay = FindAnyObjectByType<WingmanDebugDisplay>(FindObjectsInactive.Include);
            m_LastSpeechTime = Time.time;
            ServiceDebug("GeminiSuggestionService initialized");
        }

        void Update()
        {
            // Silence detection
            if (m_SilenceDetectionEnabled && !m_IsProcessing && !m_SilenceAlertSent)
            {
                float silenceDuration = Time.time - m_LastSpeechTime;
                if (silenceDuration >= m_SilenceThresholdSeconds && m_TranscriptHistory.Count > 0)
                {
                    m_SilenceAlertSent = true;
                    ServiceDebug($"Silence detected: {silenceDuration:F1}s");
                    OnSilenceDetected?.Invoke(silenceDuration);

                    // Proactively suggest conversation starters
                    OnSuggestionsReady?.Invoke(k_SilencePayload);
                }
            }
        }

        // ================================================================
        // Public API
        // ================================================================

        /// <summary>
        /// Add a finalized transcript turn and trigger coaching suggestion generation.
        /// Call this every time ElevenLabs STT produces a finalized result.
        /// </summary>
        /// <param name="text">The transcribed text.</param>
        /// <param name="speaker">Who spoke: "user" or "other".</param>
        public void AddTranscript(string text, string speaker)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            m_LastSpeechTime = Time.time;
            m_SilenceAlertSent = false;

            // Add to history
            m_TranscriptHistory.Add(new TranscriptTurn
            {
                speaker = speaker,
                text = text.Trim(),
                timestamp = Time.time
            });

            // Trim history to max turns
            while (m_TranscriptHistory.Count > m_MaxContextTurns * 2)
                m_TranscriptHistory.RemoveAt(0);

            ServiceDebug($"Transcript added [{speaker}]: \"{text}\" (history: {m_TranscriptHistory.Count} turns)");

            // Trigger suggestion generation
            GenerateSuggestions();
        }

        /// <summary>
        /// Notify the service that speech activity started (resets silence timer).
        /// </summary>
        public void OnSpeechActivity()
        {
            m_LastSpeechTime = Time.time;
            m_SilenceAlertSent = false;
        }

        /// <summary>
        /// Clear all transcript history and cache.
        /// </summary>
        public void Reset()
        {
            m_TranscriptHistory.Clear();
            m_SuggestionCache.Clear();
            m_IsProcessing = false;
            m_SilenceAlertSent = false;
            m_LastSpeechTime = Time.time;
        }

        /// <summary>Current number of turns in the context buffer.</summary>
        public int ContextTurnCount => m_TranscriptHistory.Count;

        /// <summary>Total suggestion payloads generated this session.</summary>
        public int TotalSuggestions => m_TotalSuggestions;

        // ================================================================
        // Internal — Suggestion Generation Pipeline
        // ================================================================

        void GenerateSuggestions()
        {
            if (m_IsProcessing)
            {
                ServiceDebug("Already processing, skipping");
                return;
            }

            // Build context string from recent turns
            string context = BuildContextString();
            if (context.Length < m_MinContextLength)
            {
                ServiceDebug($"Context too short ({context.Length} chars), skipping LLM");
                return;
            }

            // Check cache first
            string cacheKey = NormalizeForCache(context);
            if (TryGetCached(cacheKey, out var cached))
            {
                ServiceDebug("Cache hit — returning cached suggestions");
                m_TotalSuggestions++;
                OnSuggestionsReady?.Invoke(cached);
                return;
            }

            // Check heuristics (instant, no LLM needed)
            string lastText = m_TranscriptHistory.Count > 0
                ? m_TranscriptHistory[m_TranscriptHistory.Count - 1].text
                : "";

            var heuristic = DetectHeuristic(lastText);
            if (heuristic != null)
            {
                ServiceDebug($"Heuristic match — topic: {heuristic.dominantTopic}");
                CacheSuggestion(cacheKey, heuristic);
                m_TotalSuggestions++;
                OnSuggestionsReady?.Invoke(heuristic);
                return;
            }

            // No cache, no heuristic — call Gemini
            if (m_GeminiClient == null || !m_GeminiClient.IsConfigured)
            {
                ServiceDebug("Gemini not available — returning fallback");
                m_TotalSuggestions++;
                OnSuggestionsReady?.Invoke(k_FallbackPayload);
                return;
            }

            m_IsProcessing = true;
            string userMessage = $"Recent transcript:\n{context}";

            ServiceDebug($"Calling Gemini with {context.Length} chars of context...");

            m_GeminiClient.GenerateContent(userMessage, k_SystemPrompt, (responseJson, error) =>
            {
                m_IsProcessing = false;

                if (error != null)
                {
                    ServiceDebug($"Gemini error: {error}");
                    OnError?.Invoke(error);
                    m_TotalSuggestions++;
                    OnSuggestionsReady?.Invoke(k_FallbackPayload);
                    return;
                }

                // Parse the JSON response into a SuggestionPayload
                var payload = ParseGeminiResponse(responseJson);
                CacheSuggestion(cacheKey, payload);
                m_TotalSuggestions++;
                ServiceDebug($"Gemini returned {payload.suggestions.Length} suggestions (topic: {payload.dominantTopic})");
                OnSuggestionsReady?.Invoke(payload);
            });
        }

        // ================================================================
        // Internal — Context Building
        // ================================================================

        string BuildContextString()
        {
            // Take the last N non-partial turns
            int start = Mathf.Max(0, m_TranscriptHistory.Count - m_MaxContextTurns);
            var sb = new System.Text.StringBuilder();

            for (int i = start; i < m_TranscriptHistory.Count; i++)
            {
                var turn = m_TranscriptHistory[i];
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(turn.speaker);
                sb.Append(": ");
                sb.Append(turn.text);
            }

            return sb.ToString();
        }

        // ================================================================
        // Internal — Heuristic Detection
        // ================================================================

        SuggestionPayload DetectHeuristic(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string lower = text.Trim().ToLowerInvariant();

            // Only fire heuristics on SHORT utterances (< 5 words).
            // Longer sentences deserve a real Gemini call for contextual suggestions.
            // e.g. "hey" → heuristic greeting, but "hey what's the deal with..." → Gemini.
            int wordCount = lower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount > 4) return null;

            // Check greetings (short only)
            for (int i = 0; i < k_GreetingWords.Length; i++)
            {
                if (lower.StartsWith(k_GreetingWords[i]))
                    return k_GreetingPayload;
            }

            // Check farewells (StartsWith, not Contains — "later we should..." is NOT a farewell)
            for (int i = 0; i < k_FarewellWords.Length; i++)
            {
                if (lower.StartsWith(k_FarewellWords[i]))
                    return k_FarewellPayload;
            }

            // Check questions (short questions only, e.g. "what?" "really?" "you sure?")
            if (lower.TrimEnd().EndsWith("?"))
                return k_QuestionPayload;

            return null;
        }

        // ================================================================
        // Internal — Response Parsing
        // ================================================================

        SuggestionPayload ParseGeminiResponse(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson))
            {
                ServiceDebug("Empty Gemini response — returning fallback");
                return k_FallbackPayload;
            }

            try
            {
                var json = JObject.Parse(rawJson);

                // Parse suggestions array
                var suggestionsArray = json["suggestions"] as JArray;
                if (suggestionsArray == null || suggestionsArray.Count == 0)
                {
                    ServiceDebug("No suggestions in Gemini response — returning fallback");
                    return k_FallbackPayload;
                }

                var suggestions = new List<SuggestionData>();
                foreach (var s in suggestionsArray)
                {
                    string text = s.Value<string>("text") ?? "";
                    string styleStr = s.Value<string>("style") ?? "casual";
                    float confidence = s.Value<float>("confidence");

                    // Validate suggestion length (3-10 words)
                    int wordCount = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    if (wordCount < 1 || wordCount > 15) continue; // lenient filter, skip clearly broken ones
                    if (text.Length > 80) continue; // skip absurdly long suggestions

                    SuggestionStyle style = ParseStyle(styleStr);
                    confidence = Mathf.Clamp01(confidence);

                    suggestions.Add(new SuggestionData(text, style, confidence));
                }

                if (suggestions.Count == 0)
                {
                    ServiceDebug("All suggestions filtered out — returning fallback");
                    return k_FallbackPayload;
                }

                // Clamp to 4 max
                if (suggestions.Count > 4)
                    suggestions.RemoveRange(4, suggestions.Count - 4);

                string topic = json.Value<string>("topic") ?? "general";
                float engagement = Mathf.Clamp01(json.Value<float>("engagement_score"));
                string tone = json.Value<string>("tone") ?? "neutral";

                return new SuggestionPayload
                {
                    suggestions = suggestions.ToArray(),
                    engagementEstimate = engagement,
                    dominantTopic = topic
                };
            }
            catch (JsonReaderException ex)
            {
                ServiceDebug($"JSON parse error: {ex.Message}");
                return k_FallbackPayload;
            }
            catch (Exception ex)
            {
                ServiceDebug($"Parse error: {ex.Message}");
                return k_FallbackPayload;
            }
        }

        static SuggestionStyle ParseStyle(string style)
        {
            switch (style.ToLowerInvariant())
            {
                case "casual": return SuggestionStyle.Casual;
                case "thoughtful": return SuggestionStyle.Thoughtful;
                case "flirty": return SuggestionStyle.Flirty;
                case "empathetic": return SuggestionStyle.Empathetic;
                case "humorous": return SuggestionStyle.Humorous;
                case "professional": return SuggestionStyle.Thoughtful; // map professional → thoughtful
                default: return SuggestionStyle.Casual;
            }
        }

        // ================================================================
        // Internal — Cache
        // ================================================================

        string NormalizeForCache(string text)
        {
            // Same normalization as backend cache.py: lowercase, collapse whitespace
            return System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant().Trim(), @"\s+", " ");
        }

        bool TryGetCached(string key, out SuggestionPayload payload)
        {
            payload = null;
            if (m_SuggestionCache.TryGetValue(key, out var entry))
            {
                if (Time.time - entry.timestamp < m_CacheTTLSeconds)
                {
                    payload = entry.payload;
                    return true;
                }
                else
                {
                    // Expired
                    m_SuggestionCache.Remove(key);
                }
            }
            return false;
        }

        void CacheSuggestion(string key, SuggestionPayload payload)
        {
            // Evict oldest entries if cache is full
            if (m_SuggestionCache.Count >= m_MaxCacheSize)
            {
                // Simple eviction: clear half the cache
                // (In production you'd use LRU, but this is fine for on-device)
                m_SuggestionCache.Clear();
                ServiceDebug("Cache evicted (full)");
            }

            m_SuggestionCache[key] = new CachedSuggestion
            {
                payload = payload,
                timestamp = Time.time
            };
        }

        // ================================================================
        // Debug
        // ================================================================

        void ServiceDebug(string msg)
        {
            if (m_DebugLog)
                Debug.Log($"[GeminiSuggestionService] {msg}");
            if (m_DebugDisplay != null)
                m_DebugDisplay.Log($"LLM: {msg}");
        }

        // ================================================================
        // Internal Types
        // ================================================================

        [Serializable]
        struct TranscriptTurn
        {
            public string speaker;
            public string text;
            public float timestamp;
        }

        struct CachedSuggestion
        {
            public SuggestionPayload payload;
            public float timestamp;
        }
    }
}
