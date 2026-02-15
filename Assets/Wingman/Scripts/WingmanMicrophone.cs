using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Wingman
{
    /// <summary>
    /// Captures microphone audio with Voice Activity Detection (VAD).
    ///
    /// Audio format: 16-bit signed PCM, mono, 16kHz — matching ElevenLabs Scribe V2.
    /// Uses Unity's Microphone API with a rolling AudioClip buffer.
    ///
    /// VAD behavior:
    ///   - Continuously monitors mic RMS level (converted to dB)
    ///   - When dB exceeds threshold → begins accumulating a speech segment
    ///   - When dB drops below threshold for holdover duration → ends the segment
    ///   - Fires OnAudioChunkReady with each chunk during speech (for streaming)
    ///   - Fires OnSpeechSegmentComplete with the full accumulated PCM when speech ends
    ///
    /// On Meta Quest 3, Microphone.devices[0] is the built-in headset mic.
    /// android.permission.RECORD_AUDIO must be declared in the manifest
    /// (handled by Meta XR SDK if voice features are enabled).
    /// </summary>
    public class WingmanMicrophone : MonoBehaviour
    {
        [Header("Audio Settings")]
        [Tooltip("Target sample rate for STT (ElevenLabs expects 16kHz).")]
        [SerializeField] int m_SampleRate = 16000;

        [Tooltip("Size of the rolling AudioClip buffer in seconds.")]
        [SerializeField] int m_BufferLengthSeconds = 10;

        [Tooltip("How often to read and analyze audio chunks (seconds).")]
        [SerializeField] float m_ChunkInterval = 0.1f;

        [Header("Voice Activity Detection")]
        [Tooltip("RMS dB threshold to trigger speech detection. Typical: -30 to -20 dB.")]
        [SerializeField] float m_VadThresholdDb = -26f;

        [Tooltip("Seconds to keep recording after volume drops below threshold.")]
        [SerializeField] float m_VadHoldoverSeconds = 0.8f;

        [Tooltip("Minimum speech segment duration in seconds to send (filters short noise bursts).")]
        [SerializeField] float m_MinSegmentDuration = 0.3f;

        [Tooltip("Maximum speech segment duration in seconds (force-flush to avoid memory buildup).")]
        [SerializeField] float m_MaxSegmentDuration = 30f;

        [Header("Debug")]
        [Tooltip("Log VAD state transitions and dB levels.")]
        [SerializeField] bool m_DebugVAD = true;

        [Tooltip("Debug display for on-screen logging.")]
        WingmanDebugDisplay m_DebugDisplay;

        // --- Events ---

        /// <summary>Fired with each PCM16 chunk during active speech (for real-time streaming).</summary>
        public event Action<byte[]> OnAudioChunkReady;

        /// <summary>Fired when a complete speech segment ends. Contains all accumulated PCM16 bytes.</summary>
        public event Action<byte[]> OnSpeechSegmentComplete;

        /// <summary>Fired when voice activity state changes. True = speech detected, False = silence.</summary>
        public event Action<bool> OnVoiceActivityChanged;

        /// <summary>Fired when recording state changes (mic started/stopped).</summary>
        public event Action<bool> OnRecordingStateChanged;

        /// <summary>Current dB level (updated every chunk interval). Useful for UI level meters.</summary>
        public float CurrentDbLevel { get; private set; } = -100f;

        /// <summary>Whether speech is currently detected.</summary>
        public bool IsSpeechActive { get; private set; }

        /// <summary>Whether the mic is currently recording.</summary>
        public bool IsRecording => m_IsRecording;

        // --- Internal state ---
        AudioClip m_MicClip;
        string m_DeviceName;
        int m_LastReadPosition;
        bool m_IsRecording;
        Coroutine m_CaptureCoroutine;

        // VAD state
        float m_SilenceTimer; // time since voice dropped below threshold
        List<byte[]> m_SegmentChunks; // accumulated PCM chunks for current speech segment
        int m_SegmentByteCount; // total bytes in current segment
        float m_SegmentStartTime; // when current speech segment began

        // ================================================================
        // Public API
        // ================================================================

        void Start()
        {
            m_DebugDisplay = FindAnyObjectByType<WingmanDebugDisplay>(FindObjectsInactive.Include);
        }

        /// <summary>Start capturing microphone audio.</summary>
        public void StartRecording()
        {
            if (m_IsRecording)
            {
                Debug.LogWarning("[WingmanMic] Already recording");
                return;
            }

            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                StartCoroutine(RequestPermissionAndStart());
                return;
            }

            BeginCapture();
        }

        /// <summary>Stop capturing microphone audio.</summary>
        public void StopRecording()
        {
            if (!m_IsRecording) return;

            m_IsRecording = false;

            if (m_CaptureCoroutine != null)
            {
                StopCoroutine(m_CaptureCoroutine);
                m_CaptureCoroutine = null;
            }

            // Flush any in-progress speech segment
            FlushSegment();

            if (Microphone.IsRecording(m_DeviceName))
                Microphone.End(m_DeviceName);

            m_MicClip = null;
            m_LastReadPosition = 0;

            if (IsSpeechActive)
            {
                IsSpeechActive = false;
                OnVoiceActivityChanged?.Invoke(false);
            }

            Debug.Log("[WingmanMic] Recording stopped");
            OnRecordingStateChanged?.Invoke(false);
        }

        void OnDestroy()
        {
            StopRecording();
        }

        // ================================================================
        // Internal — Permission & Capture Start
        // ================================================================

        IEnumerator RequestPermissionAndStart()
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);

            if (Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                BeginCapture();
            }
            else
            {
                Debug.LogError("[WingmanMic] Microphone permission denied");
            }
        }

        void BeginCapture()
        {
            if (Microphone.devices.Length == 0)
            {
                MicDebug("No microphone devices found!");
                Debug.LogError("[WingmanMic] No microphone devices found");
                return;
            }

            m_DeviceName = Microphone.devices[0];
            MicDebug($"Device: {m_DeviceName}");
            MicDebug($"All devices ({Microphone.devices.Length}): {string.Join(", ", Microphone.devices)}");

            int minFreq, maxFreq;
            Microphone.GetDeviceCaps(m_DeviceName, out minFreq, out maxFreq);

            int actualRate = m_SampleRate;
            if (maxFreq > 0 && m_SampleRate > maxFreq)
                actualRate = maxFreq;
            if (minFreq > 0 && m_SampleRate < minFreq)
                actualRate = minFreq;

            MicDebug($"Rate: req={m_SampleRate} actual={actualRate} caps=[{minFreq}-{maxFreq}]");

            m_MicClip = Microphone.Start(m_DeviceName, true, m_BufferLengthSeconds, actualRate);

            if (m_MicClip == null)
            {
                MicDebug("Microphone.Start FAILED (null clip)");
                Debug.LogError("[WingmanMic] Failed to start microphone");
                return;
            }

            // Wait for recording to actually begin
            int timeout = 100;
            while (!(Microphone.GetPosition(m_DeviceName) > 0) && timeout > 0)
            {
                timeout--;
            }

            MicDebug($"Mic started (waited {100 - timeout} cycles). Clip: {m_MicClip.samples} samples, {m_MicClip.frequency}Hz, {m_MicClip.channels}ch");

            m_LastReadPosition = 0;
            m_IsRecording = true;
            m_SilenceTimer = 0f;
            m_SegmentChunks = new List<byte[]>();
            m_SegmentByteCount = 0;
            IsSpeechActive = false;

            MicDebug($"VAD: threshold={m_VadThresholdDb}dB holdover={m_VadHoldoverSeconds}s minSeg={m_MinSegmentDuration}s");
            OnRecordingStateChanged?.Invoke(true);

            m_CaptureCoroutine = StartCoroutine(CaptureLoop());
            MicDebug("CaptureLoop coroutine started");
        }

        // ================================================================
        // Internal — Capture Loop with VAD
        // ================================================================

        IEnumerator CaptureLoop()
        {
            var wait = new WaitForSeconds(m_ChunkInterval);
            int chunkCount = 0;

            while (m_IsRecording)
            {
                yield return wait;

                if (!Microphone.IsRecording(m_DeviceName))
                {
                    MicDebug("Mic stopped unexpectedly!");
                    Debug.LogWarning("[WingmanMic] Microphone stopped unexpectedly");
                    StopRecording();
                    yield break;
                }

                int currentPosition = Microphone.GetPosition(m_DeviceName);
                if (currentPosition == m_LastReadPosition)
                    continue;

                // Calculate samples to read (handling circular buffer wrap)
                int clipSamples = m_MicClip.samples;
                int samplesToRead;

                if (currentPosition > m_LastReadPosition)
                {
                    samplesToRead = currentPosition - m_LastReadPosition;
                }
                else
                {
                    samplesToRead = (clipSamples - m_LastReadPosition) + currentPosition;
                }

                if (samplesToRead <= 0)
                    continue;

                // Read float samples from the AudioClip
                float[] samples = new float[samplesToRead];
                m_MicClip.GetData(samples, m_LastReadPosition);
                m_LastReadPosition = currentPosition;

                // Compute RMS and dB for VAD
                float rms = ComputeRMS(samples);
                float db = RMSToDb(rms);
                CurrentDbLevel = db;

                bool voiceDetected = db > m_VadThresholdDb;

                chunkCount++;

                // Log dB level every ~1 second (every 10 chunks) to debug display
                if (chunkCount % 10 == 0)
                {
                    MicDebug($"dB={db:F1} thr={m_VadThresholdDb} speech={IsSpeechActive} voice={voiceDetected}");
                }

                if (m_DebugVAD && Time.frameCount % 30 == 0)
                {
                    Debug.Log($"[WingmanMic] dB={db:F1} threshold={m_VadThresholdDb} speech={IsSpeechActive} voiceNow={voiceDetected}");
                }

                // --- VAD State Machine ---
                if (voiceDetected)
                {
                    m_SilenceTimer = 0f;

                    if (!IsSpeechActive)
                    {
                        // Speech onset
                        IsSpeechActive = true;
                        m_SegmentStartTime = Time.time;
                        m_SegmentChunks = new List<byte[]>();
                        m_SegmentByteCount = 0;

                        MicDebug($">>> SPEECH START (dB={db:F1})");

                        if (m_DebugVAD)
                            Debug.Log($"[WingmanMic] VAD: Speech started (dB={db:F1})");

                        OnVoiceActivityChanged?.Invoke(true);
                    }

                    // Accumulate and stream this chunk
                    byte[] pcm16 = FloatToPCM16(samples);
                    m_SegmentChunks.Add(pcm16);
                    m_SegmentByteCount += pcm16.Length;
                    OnAudioChunkReady?.Invoke(pcm16);

                    // Check max segment duration (force flush)
                    if (Time.time - m_SegmentStartTime > m_MaxSegmentDuration)
                    {
                        if (m_DebugVAD)
                            Debug.Log("[WingmanMic] VAD: Max duration reached, flushing segment");

                        FlushSegment();
                        // Stay in speech-active state — new segment starts immediately
                        m_SegmentStartTime = Time.time;
                        m_SegmentChunks = new List<byte[]>();
                        m_SegmentByteCount = 0;
                    }
                }
                else
                {
                    if (IsSpeechActive)
                    {
                        // Voice dropped — still accumulate during holdover
                        byte[] pcm16 = FloatToPCM16(samples);
                        m_SegmentChunks.Add(pcm16);
                        m_SegmentByteCount += pcm16.Length;
                        OnAudioChunkReady?.Invoke(pcm16);

                        m_SilenceTimer += m_ChunkInterval;

                        if (m_SilenceTimer >= m_VadHoldoverSeconds)
                        {
                            // Holdover expired — speech segment complete
                            float segDur = Time.time - m_SegmentStartTime;
                            MicDebug($"<<< SPEECH END ({segDur:F1}s, {m_SegmentByteCount}B)");

                            if (m_DebugVAD)
                                Debug.Log($"[WingmanMic] VAD: Speech ended after {segDur:F1}s");

                            FlushSegment();

                            IsSpeechActive = false;
                            OnVoiceActivityChanged?.Invoke(false);
                        }
                    }
                    // If not in speech-active state and no voice, do nothing (ignore silence)
                }
            }
        }

        // ================================================================
        // Internal — Segment Management
        // ================================================================

        /// <summary>
        /// Flush the accumulated speech segment if it meets minimum duration.
        /// </summary>
        void FlushSegment()
        {
            if (m_SegmentChunks == null || m_SegmentChunks.Count == 0)
                return;

            float segmentDuration = (float)m_SegmentByteCount / (m_SampleRate * 2); // PCM16 = 2 bytes per sample

            if (segmentDuration < m_MinSegmentDuration)
            {
                MicDebug($"Discarded short segment ({segmentDuration:F2}s)");
                if (m_DebugVAD)
                    Debug.Log($"[WingmanMic] VAD: Discarding short segment ({segmentDuration:F2}s < {m_MinSegmentDuration}s)");

                m_SegmentChunks.Clear();
                m_SegmentByteCount = 0;
                return;
            }

            // Concatenate all chunks into a single byte array
            byte[] fullSegment = new byte[m_SegmentByteCount];
            int offset = 0;
            for (int i = 0; i < m_SegmentChunks.Count; i++)
            {
                Buffer.BlockCopy(m_SegmentChunks[i], 0, fullSegment, offset, m_SegmentChunks[i].Length);
                offset += m_SegmentChunks[i].Length;
            }

            MicDebug($"Flushing segment: {segmentDuration:F1}s, {m_SegmentByteCount}B, {m_SegmentChunks.Count} chunks");
            Debug.Log($"[WingmanMic] Speech segment complete: {segmentDuration:F1}s, {m_SegmentByteCount} bytes");

            int listenerCount = OnSpeechSegmentComplete != null ? OnSpeechSegmentComplete.GetInvocationList().Length : 0;
            MicDebug($"OnSpeechSegmentComplete listeners: {listenerCount}");

            OnSpeechSegmentComplete?.Invoke(fullSegment);

            m_SegmentChunks.Clear();
            m_SegmentByteCount = 0;
        }

        // ================================================================
        // Internal — Audio Math
        // ================================================================

        /// <summary>Compute root-mean-square of audio samples.</summary>
        static float ComputeRMS(float[] samples)
        {
            float sum = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return Mathf.Sqrt(sum / samples.Length);
        }

        /// <summary>Convert RMS amplitude to decibels.</summary>
        static float RMSToDb(float rms)
        {
            if (rms <= 0f) return -100f;
            return 20f * Mathf.Log10(rms);
        }

        /// <summary>Convert Unity float audio samples [-1, 1] to 16-bit signed PCM bytes (little-endian).</summary>
        static byte[] FloatToPCM16(float[] samples)
        {
            byte[] bytes = new byte[samples.Length * 2];

            for (int i = 0; i < samples.Length; i++)
            {
                float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                short pcmValue = (short)(clamped * 32767f);

                bytes[i * 2] = (byte)(pcmValue & 0xFF);
                bytes[i * 2 + 1] = (byte)((pcmValue >> 8) & 0xFF);
            }

            return bytes;
        }

        // ================================================================
        // Debug helper
        // ================================================================

        void MicDebug(string msg)
        {
            if (m_DebugDisplay != null)
                m_DebugDisplay.Log($"MIC: {msg}");
        }
    }
}
