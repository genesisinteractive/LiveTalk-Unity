using System;
using System.IO;
using System.Threading.Tasks;
using QwenTTS.Audio;
using UnityEngine;

namespace LiveTalk.Utils
{
    /// <summary>
    /// Reading and writing wavs on disk. Thin wrapper over the TTS package's
    /// WAV codec, which parses the header rather than assuming a rate — a
    /// 24 kHz clip read as 16 kHz plays 1.5x slow, and that is exactly the
    /// signal a voice clone is derived from.
    /// </summary>
    internal static class AudioFileIO
    {
        /// <summary>
        /// Reads a PCM wav into a clip at the rate the file declares. Returns
        /// null when the file is absent or not a readable wav.
        /// </summary>
        public static async Task<AudioClip> LoadClipAsync(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Logger.LogError($"[AudioFileIO] Audio file not found: {path}");
                return null;
            }

            try
            {
                byte[] bytes = await Task.Run(() => File.ReadAllBytes(path));
                // Decoding is pure, but AudioClip.Create is main-thread only,
                // so it happens after the await rather than inside Task.Run.
                var clip = WavCodec.ToAudioClip(bytes, Path.GetFileNameWithoutExtension(path));
                if (clip == null)
                    Logger.LogError($"[AudioFileIO] Could not decode {path}");
                return clip;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[AudioFileIO] Error loading {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Writes a clip as 16-bit mono PCM at its own sample rate.</summary>
        public static async Task SaveClipAsync(AudioClip clip, string path)
        {
            if (clip == null)
                throw new ArgumentNullException(nameof(clip));

            byte[] bytes = EncodeWav(clip);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await Task.Run(() => File.WriteAllBytes(path, bytes));
        }

        /// <summary>
        /// Identity of a take: MD5 of the 16-bit mono wav this clip encodes
        /// to, the same bytes <see cref="SaveClipAsync"/> would write.
        /// </summary>
        public static string ContentHash(AudioClip clip)
        {
            if (clip == null) return null;
            return HashUtils.GenerateAudioContentHash(EncodeWav(clip));
        }

        static byte[] EncodeWav(AudioClip clip)
        {
            var samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);
            return WavCodec.Encode(QwenAudio.ToMono(samples, clip.channels), clip.frequency);
        }
    }
}
