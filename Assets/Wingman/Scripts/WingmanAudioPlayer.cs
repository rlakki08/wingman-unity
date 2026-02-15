using System;
using System.Collections;
using UnityEngine;

namespace Wingman
{
    /// <summary>
    /// Plays TTS audio received from the backend through the headset speakers.
    ///
    /// The backend sends MP3 audio bytes over the WebSocket binary channel.
    /// Since Unity doesn't natively decode MP3 at runtime on all platforms,
    /// we request WAV/PCM from the backend when possible, or use
    /// UnityWebRequest for MP3 decoding.
    ///
    /// For the Quest 3S earpiece/speaker output, this uses a standard AudioSource.
    /// The audio is played at low volume as a "whisper" coaching prompt that
    /// only the wearer can hear.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class WingmanAudioPlayer : MonoBehaviour
    {
        [Header("Playback Settings")]
        [Tooltip("Volume for TTS whisper playback (0-1).")]
        [SerializeField, Range(0f, 1f)] float m_WhisperVolume = 0.4f;

        [Tooltip("Volume for notification sounds (0-1).")]
        [SerializeField, Range(0f, 1f)] float m_NotificationVolume = 0.3f;

        [Header("Audio Clips")]
        [Tooltip("Short chime when new suggestions arrive.")]
        [SerializeField] AudioClip m_SuggestionChime;

        [Tooltip("Subtle alert for silence detection.")]
        [SerializeField] AudioClip m_SilenceAlert;

        [Tooltip("Connection established sound.")]
        [SerializeField] AudioClip m_ConnectedSound;

        /// <summary>Fired when TTS playback finishes.</summary>
        public event Action OnPlaybackComplete;

        AudioSource m_AudioSource;
        bool m_IsPlaying;

        public bool IsPlaying => m_IsPlaying;

        void Awake()
        {
            m_AudioSource = GetComponent<AudioSource>();
            m_AudioSource.playOnAwake = false;
            m_AudioSource.spatialBlend = 0f; // 2D audio — direct to headphones
            m_AudioSource.volume = m_WhisperVolume;
        }

        /// <summary>
        /// Play TTS audio bytes received from the backend.
        /// Expects raw PCM16 mono 24kHz data (ElevenLabs output format when
        /// requested as pcm). Falls back to attempting MP3 decode if PCM fails.
        /// </summary>
        public void PlayTTSAudio(byte[] audioBytes)
        {
            if (audioBytes == null || audioBytes.Length == 0)
            {
                Debug.LogWarning("[WingmanAudio] Empty audio data received");
                return;
            }

            // Stop any current playback
            StopPlayback();

            // Try to detect audio format and play
            // MP3 files start with 0xFF 0xFB or "ID3"
            if (audioBytes.Length > 3 &&
                ((audioBytes[0] == 0xFF && (audioBytes[1] & 0xE0) == 0xE0) ||
                 (audioBytes[0] == 0x49 && audioBytes[1] == 0x44 && audioBytes[2] == 0x33)))
            {
                // MP3 data — use WAV conversion workaround
                PlayMP3ViaFile(audioBytes);
            }
            else
            {
                // Assume raw PCM16 mono 24kHz (ElevenLabs default PCM output)
                PlayPCMAudio(audioBytes, 24000);
            }
        }

        /// <summary>
        /// Play a notification sound effect.
        /// </summary>
        public void PlaySuggestionChime()
        {
            PlayNotification(m_SuggestionChime);
        }

        /// <summary>
        /// Play silence alert sound.
        /// </summary>
        public void PlaySilenceAlert()
        {
            PlayNotification(m_SilenceAlert);
        }

        /// <summary>
        /// Play connection established sound.
        /// </summary>
        public void PlayConnectedSound()
        {
            PlayNotification(m_ConnectedSound);
        }

        /// <summary>
        /// Stop any current playback.
        /// </summary>
        public void StopPlayback()
        {
            m_AudioSource.Stop();
            m_IsPlaying = false;
        }

        // ----------------------------------------------------------------
        // Internal
        // ----------------------------------------------------------------

        void PlayPCMAudio(byte[] pcmBytes, int sampleRate)
        {
            int sampleCount = pcmBytes.Length / 2; // 16-bit = 2 bytes per sample
            AudioClip clip = AudioClip.Create("TTS_Playback", sampleCount, 1, sampleRate, false);

            // Convert PCM16 bytes to float samples
            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short pcmValue = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
                samples[i] = pcmValue / 32768f;
            }

            clip.SetData(samples, 0);
            m_AudioSource.volume = m_WhisperVolume;
            m_AudioSource.clip = clip;
            m_AudioSource.Play();
            m_IsPlaying = true;

            StartCoroutine(WaitForPlaybackEnd(clip.length));
        }

        void PlayMP3ViaFile(byte[] mp3Bytes)
        {
            // Write MP3 to a temporary file and load via UnityWebRequest
            // This is the most reliable cross-platform MP3 decode path
            StartCoroutine(LoadMP3Coroutine(mp3Bytes));
        }

        IEnumerator LoadMP3Coroutine(byte[] mp3Bytes)
        {
            // Write to persistent data path
            string tempPath = System.IO.Path.Combine(Application.temporaryCachePath, "wingman_tts.mp3");
            System.IO.File.WriteAllBytes(tempPath, mp3Bytes);

            string fileUrl = "file://" + tempPath;

            using (var www = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
                fileUrl, AudioType.MPEG))
            {
                yield return www.SendWebRequest();

                if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[WingmanAudio] Failed to decode MP3: {www.error}");
                    yield break;
                }

                AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(www);
                if (clip == null || clip.length <= 0)
                {
                    Debug.LogError("[WingmanAudio] Decoded MP3 clip is empty");
                    yield break;
                }

                m_AudioSource.volume = m_WhisperVolume;
                m_AudioSource.clip = clip;
                m_AudioSource.Play();
                m_IsPlaying = true;

                yield return new WaitForSeconds(clip.length);
                m_IsPlaying = false;
                OnPlaybackComplete?.Invoke();
            }

            // Clean up temp file
            try { System.IO.File.Delete(tempPath); } catch { }
        }

        void PlayNotification(AudioClip clip)
        {
            if (clip == null) return;
            m_AudioSource.PlayOneShot(clip, m_NotificationVolume);
        }

        IEnumerator WaitForPlaybackEnd(float duration)
        {
            yield return new WaitForSeconds(duration);
            m_IsPlaying = false;
            OnPlaybackComplete?.Invoke();
        }
    }
}
