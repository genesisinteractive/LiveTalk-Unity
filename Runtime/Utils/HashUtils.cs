using System;
using System.Text;
using UnityEngine;

namespace LiveTalk.Utils
{
    /// <summary>
    /// Sophisticated hashing utilities for LiveTalk character and speech caching.
    /// Provides cryptographic-quality hashing for unique identification of:
    /// - Text content for speech caching
    /// - Character identity for voice generation
    /// - Texture content for avatar identification
    /// - Combined voice hashes for global caching
    /// </summary>
    internal static class HashUtils
    {
        // FNV-1a constants for 64-bit hashing
        private const ulong FNV_OFFSET_BASIS_64 = 14695981039346656037UL;
        private const ulong FNV_PRIME_64 = 1099511628211UL;
        
        // FNV-1a constants for 32-bit hashing  
        private const uint FNV_OFFSET_BASIS_32 = 0x811C9DC5;
        private const uint FNV_PRIME_32 = 0x01000193;

        /// <summary>
        /// Generates a consistent MD5 hash for text content.
        /// Used for speech caching to identify identical text across sessions.
        /// </summary>
        /// <param name="text">The text to hash</param>
        /// <returns>32-character lowercase hex string, or "empty" if text is null/empty</returns>
        public static string GenerateTextHash(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "empty";
                
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(text);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                
                // Convert to hexadecimal string
                var sb = new StringBuilder(32);
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Identity of an <see cref="API.Avatar"/>: a content hash of the
        /// source image plus the expression set it was built with.
        ///
        /// Driving frames are a pure function of the source image and the set
        /// of expressions asked for — the voice has no bearing on them — so the
        /// same portrait always maps to the same avatar folder, and a second
        /// request for it loads instead of spending minutes in LivePortrait
        /// again. This hashes the encoded image bytes rather than going through
        /// <see cref="GenerateTextureHash"/>, which samples a 32x32 subset:
        /// good enough to tell avatars apart, too collision-prone to decide
        /// whether to skip the whole bake.
        /// </summary>
        /// <param name="imageBytes">Encoded (PNG) bytes of the source image</param>
        /// <param name="expressionsSignature">
        /// Identifies which expressions were generated. A single-expression
        /// avatar must not satisfy a request for the full set.
        /// </param>
        /// <returns>
        /// <c>&lt;md5 of image, 32 hex&gt;_&lt;8 hex of the signature&gt;</c>, or
        /// null when there is nothing to key on.
        /// </returns>
        public static string GenerateAvatarId(byte[] imageBytes, string expressionsSignature)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                return null;

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hashBytes = md5.ComputeHash(imageBytes);
                var sb = new StringBuilder(48);
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                sb.Append('_');
                sb.Append(GenerateTextHash(expressionsSignature ?? "").Substring(0, 8));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Identity of a cloned <see cref="API.Voice"/>: a content hash of the
        /// reference recording and its transcript. Locking the same take twice
        /// yields the same id, so the clone is built once and reloaded after.
        /// </summary>
        /// <param name="pcm">Reference samples, as handed to the engine</param>
        /// <param name="sampleRate">Rate of <paramref name="pcm"/></param>
        /// <param name="transcript">Transcript of the reference; may be empty for an x-vector-only clone</param>
        /// <returns>32-character lowercase hex string, or null when there is no audio</returns>
        public static string GenerateClonedVoiceId(float[] pcm, int sampleRate, string transcript)
        {
            if (pcm == null || pcm.Length == 0)
                return null;

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var pcmBytes = new byte[pcm.Length * sizeof(float)];
                Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
                md5.TransformBlock(pcmBytes, 0, pcmBytes.Length, null, 0);

                var rateBytes = BitConverter.GetBytes(sampleRate);
                md5.TransformBlock(rateBytes, 0, rateBytes.Length, null, 0);

                var textBytes = Encoding.UTF8.GetBytes("|" + (transcript ?? ""));
                md5.TransformFinalBlock(textBytes, 0, textBytes.Length);

                var sb = new StringBuilder(32);
                foreach (byte b in md5.Hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Generates a fast content-based hash for a texture using dimensions, format, and pixel sampling.
        /// This method provides good uniqueness for texture comparison while being much faster than hashing all pixels.
        /// Samples a subset of pixels (32x32 maximum) and uses every 10th pixel for efficient hash generation.
        /// </summary>
        /// <param name="texture">The texture to hash</param>
        /// <returns>16-character hex string representing the texture content</returns>
        public static string GenerateTextureHash(Texture2D texture)
        {
            if (texture == null)
                return "0000000000000000";
            
            ulong hash = FNV_OFFSET_BASIS_64;
            
            // Hash texture properties
            hash = HashInt(hash, texture.width);
            hash = HashInt(hash, texture.height);
            hash = HashInt(hash, (int)texture.format);
            int maxSampleSize = 256;
            
            // Sample pixels for content-based hashing (faster than full texture)
            try
            {
                int sampleWidth = Math.Min(maxSampleSize, texture.width);
                int sampleHeight = Math.Min(maxSampleSize, texture.height);
                var pixels = texture.GetPixels(0, 0, sampleWidth, sampleHeight);
                
                // Sample every 10th pixel for efficiency
                for (int i = 0; i < pixels.Length; i += 10)
                {
                    var pixel = pixels[i];
                    hash = HashInt(hash, (int)(pixel.r * 255));
                    hash = HashInt(hash, (int)(pixel.g * 255));
                    hash = HashInt(hash, (int)(pixel.b * 255));
                    hash = HashInt(hash, (int)(pixel.a * 255));
                }
            }
            catch
            {
                // If we can't read pixels (e.g., compressed texture), use only dimensions
                hash = HashString(hash, "unreadable");
            }
            
            return hash.ToString("x16");
        }

        /// <summary>
        /// Generates a unique hash for a character based on all identifying properties.
        /// Combines name, gender, pitch, speed, intro text, and optional image.
        /// </summary>
        /// <param name="name">Character name</param>
        /// <param name="gender">Character gender</param>
        /// <param name="pitch">Voice pitch setting</param>
        /// <param name="speed">Voice speed setting</param>
        /// <param name="intro">Intro/reference text for voice generation</param>
        /// <param name="image">Optional character image texture</param>
        /// <returns>16-character hex string uniquely identifying this character configuration</returns>
        public static string GenerateCharacterHash(
            string name, 
            string gender, 
            string pitch, 
            string speed, 
            string intro = null,
            Texture2D image = null,
            string instruct = null)
        {
            ulong hash = FNV_OFFSET_BASIS_64;
            
            // Hash all character properties
            hash = HashString(hash, name ?? "unnamed");
            hash = HashString(hash, gender ?? "unknown");
            hash = HashString(hash, pitch ?? "moderate");
            hash = HashString(hash, speed ?? "moderate");
            
            // Include intro text if provided (affects voice generation)
            if (!string.IsNullOrEmpty(intro))
            {
                hash = HashString(hash, intro);
            }

            if (!string.IsNullOrEmpty(instruct))
            {
                hash = HashString(hash, instruct);
            }
            
            // Include image hash if provided
            if (image != null)
            {
                string imageHash = GenerateTextureHash(image);
                hash = HashString(hash, imageHash);
            }
            
            return hash.ToString("x16");
        }

        /// <summary>
        /// Generates a sophisticated character identity hash.
        /// Combines character id with voice parameters for unique voice identification.
        /// </summary>
        /// <param name="characterId">The character's unique identifier (folder name/GUID)</param>
        /// <param name="name">Character name</param>
        /// <param name="gender">Voice gender parameter</param>
        /// <param name="pitch">Voice pitch parameter</param>
        /// <param name="speed">Voice speed parameter</param>
        /// <returns>16-character hex string representing the character voice identity</returns>
        public static string GenerateCharacterVoiceHash(
            string characterId, 
            string name = null, 
            string gender = null, 
            string pitch = null, 
            string speed = null)
        {
            // Use 64-bit FNV-1a for better distribution
            ulong hash = FNV_OFFSET_BASIS_64;
            
            // Primary identity from characterId
            hash = HashString(hash, characterId ?? "unknown");
            
            // Add voice parameters if provided (for voice style identification)
            if (!string.IsNullOrEmpty(name))
                hash = HashString(hash, name);
            if (!string.IsNullOrEmpty(gender))
                hash = HashString(hash, gender);
            if (!string.IsNullOrEmpty(pitch))
                hash = HashString(hash, pitch);
            if (!string.IsNullOrEmpty(speed))
                hash = HashString(hash, speed);
            
            return hash.ToString("x16");
        }

        /// <summary>
        /// Creates a global voice hash that uniquely identifies a specific speech output.
        /// Combines text content hash with the voice identity for global caching.
        /// </summary>
        /// <param name="textHash">The MD5 hash of the text content</param>
        /// <param name="voiceId">The <see cref="API.Voice.Id"/> that speaks it</param>
        /// <returns>24-character hex string for global voice cache lookup</returns>
        public static string CreateGlobalVoiceHash(string textHash, string voiceId)
        {
            if (string.IsNullOrEmpty(textHash) || string.IsNullOrEmpty(voiceId))
                return null;
            
            // Use 64-bit hash for combining
            ulong combined = FNV_OFFSET_BASIS_64;
            
            // Mix text hash bytes
            combined = HashString(combined, textHash);
            
            // Mix voice id
            combined = HashString(combined, voiceId);
            
            // Salt. v2: keyed on the voice id rather than the character id,
            // so entries written by the character-keyed v1 layout never match.
            combined = HashString(combined, "voice_cache_v2");
            
            // Return as 24-char hex (16 for main hash + 8 for collision resistance)
            string mainHash = combined.ToString("x16");
            uint collisionResistance = (uint)(combined >> 32) ^ (uint)combined;
            return mainHash + collisionResistance.ToString("x8");
        }

        /// <summary>
        /// Creates a global voice hash from raw text and voice id.
        /// Convenience method that handles text hashing internally.
        /// </summary>
        /// <param name="text">The raw text content</param>
        /// <param name="voiceId">The <see cref="API.Voice.Id"/> that speaks it</param>
        /// <returns>24-character hex string for global voice cache lookup</returns>
        public static string CreateGlobalVoiceHashFromText(string text, string voiceId)
        {
            string textHash = GenerateTextHash(text);
            return CreateGlobalVoiceHash(textHash, voiceId);
        }

        /// <summary>
        /// Cache key for the speech audio of one utterance:
        /// <c>hash(voiceId, text)</c>. The character id is deliberately not
        /// part of it — two characters sharing a voice share the audio, and a
        /// character whose voice was replaced stops matching the old takes.
        /// </summary>
        /// <param name="voiceId">The <see cref="API.Voice.Id"/> speaking</param>
        /// <param name="text">The text to be spoken</param>
        /// <returns>Unique cache key for this specific speech, or null when either input is empty</returns>
        public static string GenerateSpeechCacheKey(string voiceId, string text)
        {
            string textHash = GenerateTextHash(text);
            return CreateGlobalVoiceHash(textHash, voiceId);
        }

        /// <summary>
        /// Cache key for the lip-sync frames of one utterance:
        /// <c>hash(voiceId, text, avatarId, expressionIndex)</c>. Frames depend
        /// on the audio (voice + text), on the face (avatar) and on which
        /// expression's driving frames they were rendered over — the same line
        /// at expression 0 and expression 3 are different clips.
        /// </summary>
        /// <param name="voiceId">The <see cref="API.Voice.Id"/> speaking</param>
        /// <param name="text">The text to be spoken</param>
        /// <param name="avatarId">The <see cref="API.Avatar.Id"/> being animated</param>
        /// <param name="expressionIndex">Expression the frames were generated for</param>
        /// <returns>Unique cache key, or null when any id or the text is empty</returns>
        public static string GenerateFramesCacheKey(
            string voiceId, string text, string avatarId, int expressionIndex)
        {
            if (string.IsNullOrEmpty(voiceId) || string.IsNullOrEmpty(text) || string.IsNullOrEmpty(avatarId))
                return null;

            ulong combined = FNV_OFFSET_BASIS_64;
            combined = HashString(combined, GenerateTextHash(text));
            combined = HashString(combined, voiceId);
            combined = HashString(combined, avatarId);
            combined = HashInt(combined, expressionIndex);
            // Salt. v2: carries the avatar id and the expression index, which
            // the v1 layout (speech key + "_frames") did not.
            combined = HashString(combined, "frames_cache_v2");

            string mainHash = combined.ToString("x16");
            uint collisionResistance = (uint)(combined >> 32) ^ (uint)combined;
            return mainHash + collisionResistance.ToString("x8");
        }

        /// <summary>
        /// Cache key for one performance lip-sync slice:
        /// <c>hash(voiceId, text, avatarId, planSliceHash, wavBytes)</c>.
        /// Same words over the same sequence of base faces (and the same
        /// wav) reuse the mouths; the same words over a re-timed expression
        /// track miss. Distinct from <see cref="GenerateFramesCacheKey"/>
        /// (chat utterances keyed on a single expression index).
        /// </summary>
        public static string GeneratePerformanceMouthKey(
            string voiceId, string text, string avatarId, string planSliceHash, long wavBytes)
        {
            if (string.IsNullOrEmpty(voiceId) || string.IsNullOrEmpty(text)
                || string.IsNullOrEmpty(avatarId) || string.IsNullOrEmpty(planSliceHash))
                return null;

            ulong combined = FNV_OFFSET_BASIS_64;
            combined = HashString(combined, GenerateTextHash(text));
            combined = HashString(combined, voiceId);
            combined = HashString(combined, avatarId);
            combined = HashString(combined, planSliceHash);
            combined = HashString(combined, wavBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            combined = HashString(combined, "perf_mouth_v1");

            string mainHash = combined.ToString("x16");
            uint collisionResistance = (uint)(combined >> 32) ^ (uint)combined;
            return mainHash + collisionResistance.ToString("x8");
        }

        /// <summary>
        /// Mixes multiple hash strings into a single deterministic hash.
        /// Uses FNV-1a algorithm for consistent results.
        /// </summary>
        /// <param name="hashes">Array of hash strings in hex format</param>
        /// <returns>Combined hash as 16-character hex string</returns>
        public static string MixHashes(params string[] hashes)
        {
            if (hashes == null || hashes.Length == 0)
                return "0000000000000000";
            
            ulong combined = FNV_OFFSET_BASIS_64;
            
            foreach (string hash in hashes)
            {
                if (!string.IsNullOrEmpty(hash))
                {
                    combined = HashString(combined, hash);
                }
            }
            
            return combined.ToString("x16");
        }

        #region Private Helper Methods

        /// <summary>
        /// Hash a string into the running 64-bit hash value using FNV-1a
        /// </summary>
        private static ulong HashString(ulong hash, string str)
        {
            if (string.IsNullOrEmpty(str))
                return hash;
                
            byte[] bytes = Encoding.UTF8.GetBytes(str);
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= FNV_PRIME_64;
            }
            return hash;
        }

        /// <summary>
        /// Hash an integer into the running 64-bit hash value
        /// </summary>
        private static ulong HashInt(ulong hash, int value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= FNV_PRIME_64;
            }
            return hash;
        }

        #endregion
    }
}

