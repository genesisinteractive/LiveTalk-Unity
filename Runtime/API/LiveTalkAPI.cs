using QwenTTS;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Video;
using Newtonsoft.Json;

namespace LiveTalk.API
{
    using Core;
    using Utils;

    #region Inference Queue

    /// <summary>
    /// Queue for serializing inference requests to prevent parallel model usage.
    /// Models like MuseTalk and the TTS talker cannot handle concurrent requests.
    /// </summary>
    internal class InferenceQueue
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly string _name;
        private int _queuedCount = 0;

        public InferenceQueue(string name)
        {
            _name = name;
        }

        /// <summary>
        /// Number of requests currently waiting in queue (including the one being processed)
        /// </summary>
        public int QueuedCount => _queuedCount;

        /// <summary>
        /// Acquire the queue lock. Call Release() when done.
        /// </summary>
        public async Task AcquireAsync()
        {
            Interlocked.Increment(ref _queuedCount);
            Logger.LogVerbose($"[{_name}Queue] Waiting for lock (queued: {_queuedCount})");
            await _semaphore.WaitAsync();
            Logger.LogVerbose($"[{_name}Queue] Lock acquired");
        }

        /// <summary>
        /// Release the queue lock.
        /// </summary>
        public void Release()
        {
            Interlocked.Decrement(ref _queuedCount);
            _semaphore.Release();
            Logger.LogVerbose($"[{_name}Queue] Lock released (remaining: {_queuedCount})");
        }
    }

    #endregion

    #region Public Data Types

    public enum LogLevel
    {
        VERBOSE,
        INFO,
        WARNING,
        ERROR,
    }

    public enum CreationMode
    {
        /// <summary>
        /// Only generate voice.
        /// </summary>
        VoiceOnly,
        /// <summary>
        /// Generate voice and single expression.
        /// </summary>
        SingleExpression,
        /// <summary>
        /// Generate voice and all expressions.
        /// </summary>
        AllExpressions,
    }

    public enum MemoryUsage
    {
        /// <summary>
        /// Not Recommended (Not enough extra quality trade-off with performance and memory usage)
        /// Requires all FP32 models to be packaged in StreamingAssets manually.
        /// </summary>
        Quality, 
        /// <summary>
        /// For desktop devices. Slower first use of any model.
        /// </summary>
        Performance, 
        /// <summary>
        /// Recommended for desktop devices. Prevent unnecessary model loading at startup. Default.
        /// </summary>
        Balanced, 
        /// <summary>
        /// For mobile devices (Not recommended for desktop)
        /// </summary>
        Optimal, 
    }

    /// <summary>
    /// Result of a voice preview generation.
    /// </summary>
    [Obsolete("Use LiveTalkAPI.DesignVoiceAsync, which returns a Voice whose Sample / SampleText are the preview take and is directly usable in CreateCharacter.")]
    public class VoicePreviewResult
    {
        /// <summary>The designed voice, when <see cref="Success"/>. Hand it to <see cref="LiveTalkAPI.CreateCharacter"/>.</summary>
        public Voice Voice { get; set; }

        /// <summary>
        /// Whether the generation was successful
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Error message if generation failed
        /// </summary>
        public string ErrorMessage { get; set; }
        
        /// <summary>
        /// The generated audio clip for playback
        /// </summary>
        public AudioClip AudioClip { get; set; }
        
        /// <summary>
        /// Path to the temporary voice folder containing saved voice data.
        /// Can be used later when creating a character with this voice.
        /// </summary>
        public string VoiceFolderPath { get; set; }
        
        /// <summary>
        /// The gender parameter used
        /// </summary>
        public string Gender { get; set; }
        
        /// <summary>
        /// The pitch parameter used
        /// </summary>
        public string Pitch { get; set; }
        
        /// <summary>
        /// The speed parameter used
        /// </summary>
        public string Speed { get; set; }
    }

    /// <summary>
    /// Class for all stream operations in the LiveTalk pipeline.
    /// Provides common functionality for frame streaming including queuing, cancellation, and synchronization.
    /// </summary>
    public class FrameStream
    {
        #region Properties

        /// <summary>
        /// Gets or sets the total number of frames expected to be processed through this stream.
        /// </summary>
        public int TotalExpectedFrames { get; set; }

        /// <summary>
        /// For lip-sync frames: the avatar frame (index into the expression's
        /// driving frames) the first frame of this stream was rendered onto;
        /// frame <c>i</c> is on avatar frame <c>StartFrameIndex + i</c>, wrapped.
        /// Set by the generator (the requested start) or by a frames-cache
        /// hit (the start the cached frames were rendered from). -1 when
        /// unknown or not applicable.
        /// </summary>
        public int StartFrameIndex { get; internal set; } = -1;

        /// <summary>
        /// For LivePortrait driving-frame generation: when non-null, the
        /// generator appends the final 63-float driving keypoint vector it
        /// rendered each frame from (post-stitching — exactly what
        /// <c>warping_spade</c> consumed). One entry per frame queued, in
        /// order. This is what an <see cref="API.Avatar"/> persists as
        /// <c>motion.bin</c> so a frame can later be re-rendered, blended or
        /// held without an extractor pass.
        /// </summary>
        internal List<float[]> Poses { get; set; }
        
        /// <summary>
        /// Gets a value indicating whether more frames are available for processing.
        /// This includes both queued frames and frames that are still being loaded.
        /// </summary>
        public bool HasMoreFrames => !Finished || !Queue.IsEmpty;

        /// <summary>
        /// Gets or sets a value indicating whether the stream processing is finished.
        /// </summary>
        internal bool Finished { get; set; } = false;

        /// <summary>
        /// The exception that stopped the producer, or null when it finished
        /// normally (or is still running). A faulted producer still sets
        /// <see cref="Finished"/> so consumers drain and exit; check this
        /// afterwards to tell a short stream from a complete one.
        /// </summary>
        public Exception Error { get; internal set; }

        /// <summary>
        /// Marks the stream finished with the given failure. Idempotent; the
        /// first error wins.
        /// </summary>
        internal void Fail(Exception error)
        {
            Error ??= error;
            Finished = true;
        }

        /// <summary>
        /// Gets the internal concurrent queue used for frame storage and retrieval.
        /// </summary>
        internal readonly ConcurrentQueue<Texture2D> Queue = new();

        /// <summary>
        /// Gets the cancellation token source for stream operations.
        /// </summary>
        internal CancellationTokenSource CancellationTokenSource { get; } = new();

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the FrameStream class with the specified total expected frames.
        /// </summary>
        /// <param name="totalExpectedFrames">The total number of frames expected to be processed</param>
        public FrameStream(int totalExpectedFrames)
        {
            TotalExpectedFrames = totalExpectedFrames;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Creates a yield instruction that waits until the next frame is available.
        /// The frame is then accessible through the FrameAwaiter.Texture property.
        /// </summary>
        /// <returns>A FrameAwaiter instance that can be used in Unity coroutines</returns>
        public FrameAwaiter WaitForNext() => new(Queue, () => Finished);

        /// <summary>
        /// Attempts to retrieve the next frame from the queue without blocking.
        /// </summary>
        /// <param name="texture">The retrieved texture, or null if no frame is available</param>
        /// <returns>True if a frame was successfully retrieved; false if the queue is empty</returns>
        public bool TryGetNext(out Texture2D texture) => Queue.TryDequeue(out texture);

        #endregion
    }

    /// <summary>
    /// Custom yield instruction for Unity coroutines that waits for and delivers texture frames.
    /// This class provides non-blocking frame retrieval with automatic Unity integration.
    /// </summary>
    public sealed class FrameAwaiter : CustomYieldInstruction
    {
        #region Private Fields

        private readonly ConcurrentQueue<Texture2D> _queue;
        private readonly Func<bool> _finished;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the texture that was retrieved from the queue, or null when the
        /// wait ended because the stream finished with nothing left to deliver.
        /// </summary>
        public Texture2D Texture { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the coroutine should continue waiting.
        /// Returns false when a frame is available, or when the stream has
        /// finished (or failed) with the queue empty — otherwise a consumer that
        /// was already waiting when the producer stopped would wait forever.
        /// </summary>
        public override bool keepWaiting
        {
            get
            {
                if (_queue.TryDequeue(out var texture))
                {
                    Texture = texture;
                    return false; // Stop waiting - caller resumes
                }
                if (_finished != null && _finished())
                {
                    Texture = null;
                    return false; // Nothing more will come
                }
                return true; // Keep waiting this frame
            }
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the FrameAwaiter class with the specified queue.
        /// </summary>
        /// <param name="queue">The concurrent queue to monitor for available frames</param>
        public FrameAwaiter(ConcurrentQueue<Texture2D> queue) : this(queue, null)
        {
        }

        /// <summary>
        /// Initializes a FrameAwaiter that also stops waiting once
        /// <paramref name="finished"/> reports the producer is done.
        /// </summary>
        public FrameAwaiter(ConcurrentQueue<Texture2D> queue, Func<bool> finished)
        {
            _queue = queue;
            _finished = finished;
        }

        #endregion
    }

    #endregion

    #region Main API Classes

    /// <summary>
    /// Integrated API that combines LivePortrait and MuseTalk for comprehensive talking head generation.
    /// Provides a unified interface for avatar animation with motion transfer and audio synchronization.
    /// 
    /// Model (2.0): three entities with independent lifetimes, all under
    /// <see cref="CharacterSaveLocation"/>.
    /// - <see cref="Avatar"/> (<c>avatars/&lt;id&gt;</c>): a portrait's driving frames and latents.
    ///   Id = content hash of the image and expression set; built once per portrait.
    /// - <see cref="Voice"/> (<c>voices/&lt;id&gt;</c>): a designed (GUID id) or cloned
    ///   (content-hash id) speaker with its sample take.
    /// - <see cref="Character"/> (<c>characters/&lt;id&gt;/character.json</c>): a name plus
    ///   references to one avatar and one voice. GUID id; instant to create;
    ///   <see cref="Character.ReplaceVoice"/> swaps the voice without touching the face.
    /// Pre-2.0 inline character folders at the root still load.
    ///   
    /// Workflow:
    /// 1. <see cref="CreateAvatarAsync"/>: LivePortrait animates the portrait, MuseTalk precomputes latents.
    /// 2. <see cref="DesignVoiceAsync"/> / <see cref="CloneVoiceAsync"/>: make a speaker.
    /// 3. <see cref="CreateCharacter"/>: compose the two; then <see cref="Character.SpeakAsync"/>.
    /// 
    /// This class orchestrates the complete pipeline for realistic talking head video generation.
    /// </summary>
    public class LiveTalkAPI : IDisposable
    {
        #region Private Fields
        private LivePortraitInference _livePortrait = null;
        private MuseTalkInference _museTalk = null;
        private LiveTalkConfig _config;
        private LiveTalkController _controller;
        private GameObject _liveTalkInstance;
        private bool _disposed = false;
        private bool _initialized = false;
        
        // Request queues to prevent parallel model usage
        private readonly InferenceQueue _voiceQueue = new("Voice");
        private readonly InferenceQueue _museTalkQueue = new("MuseTalk");

        #endregion

        #region Properties

        /// <summary>
        /// Gets the MuseTalk inference engine for internal operations.
        /// </summary>
        internal MuseTalkInference MuseTalk => _museTalk;

        /// <summary>
        /// Gets the LivePortrait inference engine for internal operations.
        /// </summary>
        internal LivePortraitInference LivePortrait => _livePortrait;

        /// <summary>
        /// Gets the GameObject that contains the LiveTalkAPI components.
        /// </summary>
        internal GameObject Object => _liveTalkInstance;

        /// <summary>
        /// Gets the LiveTalk configuration for internal operations.
        /// </summary>
        internal LiveTalkConfig Config => _config;

        /// <summary>
        /// Gets the LiveTalkController for coroutine management.
        /// </summary>
        internal LiveTalkController Controller => _controller;

        /// <summary>
        /// Gets the voice inference queue for serializing TTS requests.
        /// </summary>
        internal InferenceQueue VoiceQueue => _voiceQueue;

        /// <summary>
        /// Gets the MuseTalk inference queue for serializing animation requests.
        /// </summary>
        internal InferenceQueue MuseTalkQueue => _museTalkQueue;

        /// <summary>
        /// Generate lip-sync frames while the speech is still being synthesised,
        /// instead of after it. With this on, <see cref="Character.SpeakAsync"/>
        /// feeds each TTS chunk into an incremental Whisper feature extractor
        /// and runs the UNet on frames as soon as their audio window is final,
        /// so <see cref="CharacterPlayer"/> can start playing after the first
        /// ~0.5 s of audio rather than after the whole clip has been animated.
        /// The finished clip is still delivered to <c>onAudioReady</c> and the
        /// frames still go to the cache.
        ///
        /// <para>Trade-off: the mel normalisation reference
        /// (<c>power_to_db(ref=max)</c>) is captured from the first
        /// 0.5 s and held for the utterance. When the loudest moment comes
        /// later, features differ from the batch path by up to ~0.05 on
        /// [-1, 1] (measured; a fixed constant reference is 3–4x worse), and
        /// the encoder's global attention sees a shorter context. See the
        /// changelog for the measured frame difference. Off: the batch path,
        /// bit-identical to 1.x. Cache hits are unaffected either way.</para>
        ///
        /// <para><b>EXPERIMENTAL — default off.</b> Measured against the batch
        /// path on two utterances, streamed frames differ in the mouth band by
        /// a mean of 0.5–1.0/255 (max up to 4.8/255), and the residual is the
        /// Whisper encoder's global attention seeing a prefix instead of the
        /// full clip — it is not a margin, reference or alignment bug and does
        /// not close with more held-back context. First mouth movement drops
        /// from ~29 s to ~2.4 s on a 4 s line, but generation runs slower than
        /// real time on this hardware, so playback holds the last frame while
        /// it catches up. Turn on to trade fidelity and smoothness for latency.
        /// Behaviour and knobs may change without a major version bump.</para>
        /// </summary>
        public bool StreamLipSync { get; set; } = false;

        /// <summary>
        /// Audio held back, in seconds, beyond the exact 146 ms a frame's own
        /// feature window needs, before that frame is generated while
        /// streaming. The Whisper encoder's self-attention sees the whole
        /// window, so a frame encoded with more real audio after it lands
        /// closer to the batch result; the cost is that much more first-frame
        /// latency. Measured numbers for several values are in the changelog.
        /// Only used when <see cref="StreamLipSync"/> is on.
        /// </summary>
        public float StreamLipSyncContextSeconds { get; set; } = 0.5f;

        /// <summary>
        /// Root of everything LiveTalk saves. Layout:
        /// <c>avatars/&lt;avatarId&gt;/</c>, <c>voices/&lt;voiceId&gt;/</c>,
        /// <c>characters/&lt;characterId&gt;/character.json</c>, plus any
        /// pre-2.0 inline character folders at the root.
        /// </summary>
        public static string CharacterSaveLocation 
        { 
            get 
            {
                return Character.saveLocation;
            } 
            set 
            {
                Character.saveLocation = value;
            }
        }

        /// <summary>
        /// Gets the cache location path.
        /// </summary>
        public static string CacheLocation => LiveTalkCache.Path;

        /// <summary>
        /// Gets whether caching is enabled.
        /// </summary>
        public static bool IsCacheEnabled => LiveTalkCache.IsEnabled;

        /// <summary>
        /// Enable or disable caching at runtime.
        /// </summary>
        /// <param name="enabled">Whether to enable caching</param>
        public static void SetCacheEnabled(bool enabled) => LiveTalkCache.SetEnabled(enabled);

        /// <summary>
        /// Clear all cached speech and frames. With no argument, clears the
        /// cache <see cref="Initialize"/> configured (no-op before that). With
        /// an explicit <paramref name="cacheLocation"/> it works before
        /// <see cref="Initialize"/>, so a host can offer "clear cache" without
        /// first paying for model setup. Avatars, voices and characters are not
        /// cache and are untouched; see <see cref="DeleteAvatar"/> and friends.
        /// </summary>
        /// <param name="cacheLocation">The cache folder to empty, or null for the initialized one.</param>
        public static void ClearCache(string cacheLocation = null)
        {
            if (string.IsNullOrEmpty(cacheLocation))
                LiveTalkCache.Clear();
            else
                LiveTalkCache.Clear(cacheLocation);
        }

        /// <summary>
        /// Total bytes of cached speech and frames. With no argument, measures
        /// the cache <see cref="Initialize"/> configured (0 before that). With
        /// an explicit <paramref name="cacheLocation"/> it works before
        /// <see cref="Initialize"/>. Walks the folder; do not call every frame.
        /// </summary>
        /// <param name="cacheLocation">The cache folder to measure, or null for the initialized one.</param>
        public static long GetCacheSizeBytes(string cacheLocation = null) =>
            string.IsNullOrEmpty(cacheLocation) ? LiveTalkCache.GetSize() : LiveTalkCache.GetSize(cacheLocation);

        #endregion

        #region Constructor
        public static LiveTalkAPI Instance { get; private set; } = new();

        /// <summary>
        /// Initializes a new instance of the LiveTalkAPI class with the specified configuration and controller.
        /// </summary>
        /// <param name="logLevel">The logging level for the API (defaults to WARNING)</param>
        /// <param name="characterSaveLocation">The location to save the generated characters</param>
        /// <param name="parentModelPath">The parent path for model files (defaults to StreamingAssets if empty)</param>
        /// <param name="memoryUsage">The memory usage level for the API (defaults to Balanced)</param>
        /// <param name="cacheLocation">The location to cache generated content (null to disable caching)</param>
        /// <param name="enableCache">Whether to enable caching (defaults to true if location provided)</param>
        public void Initialize(
            LogLevel logLevel = LogLevel.INFO,
            string characterSaveLocation = "",
            string parentModelPath = "",
            MemoryUsage memoryUsage = MemoryUsage.Balanced,
            string cacheLocation = null,
            bool enableCache = true,
            string ttsModelRoot = null)
        {
            if (_initialized)
            {
                EnsureRuntimeHost();
                Logger.LogWarning("[LiveTalkAPI] Already initialized");
                return;
            }

            if (string.IsNullOrEmpty(parentModelPath))
            {
                parentModelPath = Application.streamingAssetsPath;
            }

            if (string.IsNullOrEmpty(characterSaveLocation))
            {
                characterSaveLocation = Path.Combine(Application.persistentDataPath, "Characters");
            }

            // Initialize cache
            if (string.IsNullOrEmpty(cacheLocation) && enableCache)
            {
                cacheLocation = Path.Combine(Application.persistentDataPath, "LiveTalkCache");
            }
            LiveTalkCache.Initialize(cacheLocation, enableCache);
            
            _config = new LiveTalkConfig(parentModelPath, logLevel, memoryUsage);
            Logger.LogLevel = _config.LogLevel;
            _livePortrait = new LivePortraitInference(_config);
            _museTalk = new MuseTalkInference(_config);

            var ttsLogLevel = _config.LogLevel switch
            {
                LogLevel.VERBOSE => QwenTTS.LogLevel.VERBOSE,
                LogLevel.INFO => QwenTTS.LogLevel.INFO,
                LogLevel.WARNING => QwenTTS.LogLevel.WARNING,
                LogLevel.ERROR => QwenTTS.LogLevel.ERROR,
                _ => QwenTTS.LogLevel.WARNING,
            };

            // Map LiveTalk MemoryUsage onto the TTS package's load policy
            var ttsMemoryUsage = memoryUsage switch
            {
                MemoryUsage.Performance => QwenTTS.MemoryUsage.Performance,
                MemoryUsage.Balanced => QwenTTS.MemoryUsage.Balanced,
                MemoryUsage.Optimal => QwenTTS.MemoryUsage.Optimal,
                MemoryUsage.Quality => QwenTTS.MemoryUsage.Performance,
                _ => QwenTTS.MemoryUsage.Balanced,
            };

            // TTS first so it owns OrtEnv: whichever side creates the
            // environment owns ONNX Runtime's logging sink, and this ordering
            // is deliberate rather than incidental.
            QwenTts.Initialize(new QwenTtsSettings
            {
                ModelRoot = ttsModelRoot,
                MemoryUsage = ttsMemoryUsage,
                LogLevel = ttsLogLevel,
            });
            // ORT at INFO emits an arena line per allocation — thousands per
            // session, which buries LiveTalk's own INFO lines. Only VERBOSE
            // opts into that; INFO drops ORT to WARNING.
            var ortLogLevel = _config.LogLevel switch
            {
                LogLevel.VERBOSE => LogLevel.VERBOSE,
                LogLevel.ERROR => LogLevel.ERROR,
                _ => LogLevel.WARNING,
            };
            ModelUtils.Initialize(ortLogLevel);

            Character.saveLocation = characterSaveLocation;
            LiveTalkStorage.SweepStaging();
            _liveTalkInstance = new GameObject("LiveTalkAPI");
            _controller = _liveTalkInstance.AddComponent<LiveTalkController>();
            _liveTalkInstance.AddComponent<VideoPlayer>();
            _initialized = true;
        }

        /// <summary>
        /// Recreates the coroutine host GameObject after Play mode tears it down
        /// while the API singleton is still initialized (editor, no domain reload).
        /// </summary>
        public void EnsureRuntimeHost()
        {
            if (!_initialized)
                return;
            if (_liveTalkInstance != null)
                return;

            _liveTalkInstance = new GameObject("LiveTalkAPI");
            _controller = _liveTalkInstance.AddComponent<LiveTalkController>();
            _liveTalkInstance.AddComponent<VideoPlayer>();
        }

        /// <summary>
        /// Drops the TTS engine's ONNX sessions and embedding tables. LiveTalk
        /// itself stays initialized.
        /// </summary>
        public void UnloadTts()
        {
            QwenTts.Unload();
            Logger.Log("[LiveTalkAPI] Unloaded TTS models");
        }

        /// <summary>
        /// Loads a TTS checkpoint now, off the main thread. Worth calling from
        /// a loading screen: the talker graphs are ~10 s each to open, and
        /// without this the first line pays all of it.
        /// </summary>
        public static Task WarmUpVoiceAsync(QwenCheckpoint checkpoint) =>
            QwenTts.WarmUpAsync(checkpoint);

        /// <summary>
        /// Releases one checkpoint (~7 GB resident) while keeping the other. Designing
        /// a voice and then speaking with a clone of it are different phases,
        /// so hosts should drop VoiceDesign once a take is locked.
        /// </summary>
        public static void EvictVoice(QwenCheckpoint checkpoint) => QwenTts.Evict(checkpoint);

        #endregion

        #region Public Methods - Model Loading

        /// <summary>
        /// Waits for all models to be loaded. Call this after Initialize() to ensure all models are ready.
        /// This is particularly useful when using MemoryUsage.Performance mode where models load at startup.
        /// </summary>
        /// <param name="onProgress">Optional callback for progress updates (modelName, progress 0-1)</param>
        /// <returns>A task that completes when all models are loaded</returns>
        /// <exception cref="InvalidOperationException">Thrown when API is not initialized</exception>
        public async Task WaitForAllModelsAsync(Action<string, float> onProgress = null)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("LiveTalkAPI not initialized. Call Initialize() first.");
            }

            Logger.Log("[LiveTalkAPI] Waiting for all models to load...");
            
            // Total: 3 model groups (LivePortrait, MuseTalk, voice)
            int totalGroups = 3;
            int currentGroup = 0;
            
            // Wait for LivePortrait models
            if (_livePortrait != null)
            {
                onProgress?.Invoke("LivePortrait Animation", (float)currentGroup / totalGroups);
                await _livePortrait.WaitForAllModelsAsync();
                currentGroup++;
                onProgress?.Invoke("LivePortrait Animation", (float)currentGroup / totalGroups);
            }
            else
            {
                currentGroup++;
            }
            
            // Wait for MuseTalk models
            if (_museTalk != null)
            {
                onProgress?.Invoke("MuseTalk Animation", (float)currentGroup / totalGroups);
                await _museTalk.WaitForAllModelsAsync();
                currentGroup++;
                onProgress?.Invoke("MuseTalk Animation", (float)currentGroup / totalGroups);
            }
            else
            {
                currentGroup++;
            }
            
            // TTS checkpoints are deliberately not warmed here: each is
            // ~7 GB resident and a caller waiting on the video models has not
            // asked for that. Use WarmUpVoiceAsync explicitly.
            onProgress?.Invoke("Voice Synthesis", (float)currentGroup / totalGroups);
            currentGroup++;
            onProgress?.Invoke("Voice Synthesis", (float)currentGroup / totalGroups);
            
            Logger.Log("[LiveTalkAPI] All models loaded successfully");
        }

        /// <summary>
        /// Gets whether all models have been loaded.
        /// </summary>
        public bool AreAllModelsLoaded
        {
            get
            {
                if (!_initialized) return false;
                return QwenTts.IsInitialized;
            }
        }

        /// <summary>True when a TTS checkpoint is resident in this process.</summary>
        public static bool VoiceModelsLoaded =>
            QwenTts.IsLoaded(QwenCheckpoint.VoiceDesign) || QwenTts.IsLoaded(QwenCheckpoint.Base);

        #endregion

        #region Public Methods - LivePortrait Animation

        /// <summary>
        /// Generates animated textures from a source image and a list of driving frames.
        /// This method processes all driving frames synchronously and provides streaming output.
        /// </summary>
        /// <param name="sourceImage">The source image containing the face to animate</param>
        /// <param name="drivingFrames">The list of driving frames that define the motion to transfer</param>
        /// <returns>An FrameStream for receiving generated animated frames</returns>
        /// <exception cref="ArgumentException">Thrown when source image or driving frames are null</exception>
        public FrameStream GenerateAnimatedTexturesAsync(Texture2D sourceImage, List<Texture2D> drivingFrames)
        {
            if (!_initialized)
            {
                throw new Exception("LiveTalkAPI not initialized. Call Initialize() first.");
            }
            ValidateAnimationInputs(sourceImage, drivingFrames);
            Logger.Log($"[LiveTalkAPI] Generating animated textures: {drivingFrames.Count} driving frames");
            
            var outputStream = new FrameStream(drivingFrames.Count);
            var inputStream = CreateInputStreamFromFrames(drivingFrames);
            inputStream.TotalExpectedFrames = drivingFrames.Count;
            
            _controller.StartCoroutine(LiveTalkController.Produce(
                _livePortrait.GenerateAsync(sourceImage, outputStream, inputStream), outputStream,
                "LiveTalkAPI.GenerateAnimatedTextures(frames)"));
            return outputStream;
        }

        /// <summary>
        /// Generates animated textures from a source image and a video player's frames.
        /// This method provides pipelined processing for efficient video-based animation.
        /// </summary>
        /// <param name="sourceImage">The source image containing the face to animate</param>
        /// <param name="videoPlayer">The video player containing the driving frames</param>
        /// <param name="maxFrames">The maximum number of frames to process (-1 for all frames)</param>
        /// <returns>An FrameStream for receiving generated animated frames</returns>
        /// <exception cref="ArgumentException">Thrown when source image or video player is null</exception>
        public FrameStream GenerateAnimatedTexturesAsync(Texture2D sourceImage, VideoPlayer videoPlayer, int maxFrames = -1)
            => GenerateAnimatedTexturesAsync(sourceImage, videoPlayer, poseSink: null, maxFrames);

        /// <summary>
        /// As <see cref="GenerateAnimatedTexturesAsync(Texture2D, VideoPlayer, int)"/>,
        /// additionally recording each frame's final driving pose into
        /// <paramref name="poseSink"/> (see <see cref="FrameStream.Poses"/>).
        /// </summary>
        internal FrameStream GenerateAnimatedTexturesAsync(
            Texture2D sourceImage, VideoPlayer videoPlayer, List<float[]> poseSink, int maxFrames = -1)
        {
            if (!_initialized)
            {
                throw new Exception("LiveTalkAPI not initialized. Call Initialize() first.");
            }
            ValidateAnimationInputs(sourceImage, videoPlayer);

            int frameCount = CalculateFrameCount(videoPlayer, maxFrames);
            Logger.Log($"[LiveTalkAPI] Generating animated textures: {frameCount} driving frames from video");

            var outputStream = new FrameStream(frameCount) { Poses = poseSink };
            _controller.LoadDrivingFrames(videoPlayer, maxFrames);
            _controller.StartCoroutine(LiveTalkController.Produce(
                _livePortrait.GenerateAsync(sourceImage, outputStream, _controller.DrivingFramesStream), outputStream,
                "LiveTalkAPI.GenerateAnimatedTextures(video)"));

            return outputStream;
        }

        /// <summary>
        /// Generates animated textures from a source image and driving frames loaded from a directory path.
        /// This method provides efficient file-based animation processing with streaming output.
        /// </summary>
        /// <param name="sourceImage">The source image containing the face to animate</param>
        /// <param name="drivingFramesPath">The path to the directory containing driving frame images</param>
        /// <param name="maxFrames">The maximum number of frames to process (-1 for all frames)</param>
        /// <returns>An FrameStream for receiving generated animated frames</returns>
        /// <exception cref="ArgumentException">Thrown when source image or path is invalid, or no frames are found</exception>
        public FrameStream GenerateAnimatedTexturesAsync(Texture2D sourceImage, string drivingFramesPath, int maxFrames = -1)
        {
            if (!_initialized)
            {
                throw new Exception("LiveTalkAPI not initialized. Call Initialize() first.");
            }
            ValidateAnimationInputs(sourceImage, drivingFramesPath);

            var frameFiles = GetFrameFiles(drivingFramesPath, maxFrames);
            Logger.Log($"[LiveTalkAPI] Generating animated textures: {frameFiles.Length} driving frames from directory");

            var outputStream = new FrameStream(frameFiles.Length);
            _controller.LoadDrivingFrames(frameFiles);
            _controller.StartCoroutine(LiveTalkController.Produce(
                _livePortrait.GenerateAsync(sourceImage, outputStream, _controller.DrivingFramesStream), outputStream,
                "LiveTalkAPI.GenerateAnimatedTextures(directory)"));

            return outputStream;
        }

        /// <summary>
        /// Renders full frames of <paramref name="sourceImage"/> from final
        /// LivePortrait driving poses (63 floats each) without running the
        /// motion extractor — the poses an avatar recorded per driving frame
        /// (<c>motion.bin</c>), or any interpolation / offset of them. This is
        /// the primitive behind expression blends and held peaks. Frames are
        /// delivered in order on the main thread; the caller owns them.
        /// Faults propagate to <paramref name="onError"/>; <paramref name="onComplete"/>
        /// fires once after the last frame.
        /// </summary>
        public IEnumerator RenderPosesAsync(
            Texture2D sourceImage,
            IReadOnlyList<float[]> poses,
            Action<int, Texture2D> onFrame,
            Action onComplete = null,
            Action<Exception> onError = null)
        {
            if (!_initialized)
                throw new Exception("LiveTalkAPI not initialized. Call Initialize() first.");
            if (sourceImage == null) throw new ArgumentNullException(nameof(sourceImage));
            if (poses == null) throw new ArgumentNullException(nameof(poses));

            bool failed = false;
            yield return TaskYield.Guard(
                _livePortrait.RenderPosesAsync(sourceImage, poses, onFrame),
                ex => { failed = true; onError?.Invoke(ex); },
                "LiveTalkAPI.RenderPoses");
            if (!failed)
                onComplete?.Invoke();
        }

        /// <summary>
        /// The frames of one of an avatar's expressions, in playback order,
        /// covering <paramref name="seconds"/> of playback at
        /// <see cref="Character.IdleFrameRate"/>. Frames cycle if the duration
        /// outlasts the expression.
        /// <para>
        /// Pass a negative <paramref name="seconds"/> for the expression's own
        /// length — exactly one cycle of its driving clip. That is the
        /// duration to ask for when the result has to **loop**: the clips are
        /// authored to close on themselves, so one whole cycle is seamless
        /// while any other length cuts mid-motion.
        /// </para>
        /// <para>
        /// Normally nothing is inferred. An avatar renders every expression
        /// when it is built, so this resolves to those stored PNGs and hands
        /// back their paths — the same files
        /// <see cref="CharacterPlayer"/> plays as its idle loop and a
        /// performance serves for stored ticks. Frames that are missing are
        /// re-rendered from the expression's recorded poses
        /// (<c>motion.bin</c>, avatars at <see cref="Avatar.Version"/> ≥ 2)
        /// into the cache, which is why this is async at all.
        /// </para>
        /// </summary>
        /// <param name="character">Owner of the avatar. Must be animatable.</param>
        /// <param name="expression">Index from <see cref="Avatar.ExpressionIndices"/>. 0 is idle.</param>
        /// <param name="seconds">Playback length; negative for one whole cycle.</param>
        /// <param name="onComplete">Frame paths in order, one per tick.</param>
        /// <param name="onError">Receives validation and render faults.</param>
        public IEnumerator RenderExpressionAsync(
            Character character,
            int expression,
            float seconds,
            Action<string[]> onComplete,
            Action<Exception> onError = null)
        {
            if (!_initialized)
                throw new Exception("LiveTalkAPI not initialized. Call Initialize() first.");

            if (character == null)
            {
                onError?.Invoke(new ArgumentNullException(nameof(character)));
                yield break;
            }
            var avatar = character.Avatar;
            if (avatar == null || !avatar.CanAnimate)
            {
                onError?.Invoke(new InvalidOperationException(
                    $"Character '{character.Name}' has no animatable avatar."));
                yield break;
            }
            if (!avatar.LoadedExpressions.TryGetValue(expression, out var data)
                || data == null || data.FrameCount <= 0)
            {
                onError?.Invoke(new ArgumentOutOfRangeException(
                    nameof(expression),
                    $"Avatar {avatar.Id} has no expression {expression} "
                    + $"({Avatar.GetExpressionName(expression)}). Available: "
                    + string.Join(", ", avatar.ExpressionIndices)));
                yield break;
            }

            int cycle = data.FrameCount;
            int ticks = seconds < 0f
                ? cycle
                : Mathf.Max(1, Mathf.RoundToInt(seconds * Avatar.DefaultFrameRate));
            string expressionFolder = avatar.ExpressionFolder(expression);

            var paths = new string[ticks];
            var missing = new List<int>();
            var missingSeen = new HashSet<int>();
            for (int tick = 0; tick < ticks; tick++)
            {
                int frame = tick % cycle;
                paths[tick] = Path.Combine(expressionFolder, $"{frame:D5}.png");
                if (!File.Exists(paths[tick]) && missingSeen.Add(frame))
                    missing.Add(frame);
            }

            if (missing.Count == 0)
            {
                Logger.Log($"[LiveTalkAPI] RenderExpression {avatar.Id} expression {expression}: "
                    + $"{ticks} tick(s) from stored frames, {cycle}-frame cycle"
                    + (seconds < 0f ? " (one whole loop)" : "") + ".");
                onComplete?.Invoke(paths);
                yield break;
            }

            if (data.Poses == null || data.Poses.Length < cycle)
            {
                onError?.Invoke(new InvalidOperationException(
                    $"Avatar {avatar.Id} expression {expression} is missing {missing.Count} "
                    + $"frame(s) and has no poses to re-render them from. The folder predates "
                    + $"v{Avatar.Version}; recreate the avatar."));
                yield break;
            }

            Logger.LogWarning($"[LiveTalkAPI] RenderExpression {avatar.Id} expression {expression}: "
                + $"{missing.Count} stored frame(s) absent; re-rendering them from motion.bin.");

            var poses = new List<float[]>(missing.Count);
            var rendered = new Dictionary<int, string>(missing.Count);
            for (int i = 0; i < missing.Count; i++)
            {
                int frame = missing[i];
                poses.Add(data.Poses[frame]);
                rendered[frame] = LiveTalkCache.GetFilePath(
                    $"{ExpressionFramePrefix}{avatar.Id}_{expression}_{frame:D5}", ".png");
            }

            Exception renderFail = null;
            yield return RenderPosesAsync(
                avatar.Image, poses,
                onFrame: (i, tex) =>
                {
                    File.WriteAllBytes(rendered[missing[i]], tex.EncodeToPNG());
                    if (tex != null) UnityEngine.Object.Destroy(tex);
                },
                onError: ex => renderFail = ex);

            if (renderFail != null)
            {
                onError?.Invoke(renderFail);
                yield break;
            }

            for (int tick = 0; tick < ticks; tick++)
            {
                int frame = tick % cycle;
                if (rendered.TryGetValue(frame, out var png))
                    paths[tick] = png;
            }
            onComplete?.Invoke(paths);
        }

        const string ExpressionFramePrefix = "expr_";

        #endregion

        #region Public Methods - Performances

        /// <summary>
        /// Renders a <see cref="Performance"/> — every utterance's audio, the
        /// expression track resolved to a pose per tick (stored frames free;
        /// blends and holds rendered from the avatars' poses), lip-sync over
        /// each spoken slice — into the cache once, keyed on the
        /// performance's fingerprint. A second call with the same cues,
        /// characters and voices returns the rendered folder immediately.
        /// Requires avatars built at <see cref="Avatar.Version"/> ≥ 2 (they
        /// carry <c>motion.bin</c>).
        /// </summary>
        /// <param name="onProgress">Stage label and 0–1 progress.</param>
        public IEnumerator RenderPerformanceAsync(
            Performance performance,
            Action<RenderedPerformance> onComplete,
            Action<Exception> onError,
            Action<string, float> onProgress = null)
        {
            if (!_initialized)
                throw new Exception("LiveTalkAPI not initialized. Call Initialize() first.");
            if (performance == null) throw new ArgumentNullException(nameof(performance));

            yield return TaskYield.Guard(
                PerformanceRenderer.RenderAsync(this, performance, onProgress, onComplete),
                ex =>
                {
                    if (onError != null) onError(ex);
                    else Logger.LogError("[LiveTalkAPI] RenderPerformance: " + ex.Message);
                },
                "LiveTalkAPI.RenderPerformance");
        }

        /// <summary>
        /// True when <see cref="RenderPerformanceAsync"/> has already written a
        /// complete folder for this performance (same cues, characters, voices).
        /// A host can skip a bake-preview and go straight to
        /// <see cref="CreatePerformancePlayer"/>.
        /// </summary>
        public bool IsPerformanceRendered(Performance performance)
        {
            if (performance == null) throw new ArgumentNullException(nameof(performance));
            if (!LiveTalkCache.IsInitialized) return false;
            string folder = PerformanceRenderer.FolderFor(performance.Fingerprint());
            return File.Exists(Path.Combine(folder, PerformanceRenderer.ManifestFileName));
        }

        /// <summary>
        /// A player for a rendered performance. The host owns the GameObject
        /// (<c>Destroy</c> it when done); it is parented like
        /// <see cref="CharacterPlayer"/>s.
        /// </summary>
        public PerformancePlayer CreatePerformancePlayer(RenderedPerformance performance, string name = null)
        {
            if (performance == null) throw new ArgumentNullException(nameof(performance));
            var go = new GameObject(name ?? ("PerformancePlayer_" + performance.Fingerprint[..Math.Min(8, performance.Fingerprint.Length)]));
            go.transform.SetParent(CharacterPlayer.ParentTransform, false);
            var player = go.AddComponent<PerformancePlayer>();
            player.Load(performance);
            return player;
        }

        #endregion

        #region Public Methods - MuseTalk Lip Synchronization

        /// <summary>
        /// Generates talking head video with lip synchronization using avatar frames and audio.
        /// This method combines facial animation with audio-driven lip movements for realistic speech synthesis.
        /// </summary>
        /// <param name="avatarTexture">The primary avatar texture to animate</param>
        /// <param name="talkingHeadFolderPath">The path to additional avatar frames for variation</param>
        /// <param name="audioClip">The audio clip to synchronize with the generated video</param>
        /// <returns>An FrameStream for receiving generated talking head frames</returns>
        /// <exception cref="ArgumentException">Thrown when avatar texture or audio clip is null</exception>
        /// <exception cref="InvalidOperationException">Thrown when the controller is not available</exception>
        public FrameStream GenerateTalkingHeadAsync(Texture2D avatarTexture, string talkingHeadFolderPath, AudioClip audioClip)
        {
            if (!_initialized)
            {
                throw new Exception("LiveTalkAPI not initialized. Call Initialize() first.");
            }
            ValidateControllerAvailability();
            ValidateTalkingHeadInputs(avatarTexture, audioClip);
            
            Logger.Log($"[LiveTalkAPI] Generating talking head: {audioClip.name} ({audioClip.length:F2}s)");
            
            var avatarTextures = LoadAvatarTextures(avatarTexture, talkingHeadFolderPath);
            int estimatedFrames = EstimateFrameCount(audioClip);
            
            var outputStream = new FrameStream(estimatedFrames);
            _controller.StartCoroutine(LiveTalkController.Produce(
                _museTalk.GenerateAsync(avatarTextures, audioClip, outputStream), outputStream,
                "LiveTalkAPI.GenerateTalkingHead"));
            
            return outputStream;
        }

        /// <summary>
        /// Generates talking head video using preloaded avatar data for optimized performance.
        /// This method bypasses avatar processing and directly generates frames from precomputed data.
        /// </summary>
        /// <param name="avatarData">The preloaded avatar data containing face regions and latent representations</param>
        /// <param name="audioClip">The audio clip to synchronize with the generated video</param>
        /// <returns>An FrameStream for receiving generated talking head frames</returns>
        /// <exception cref="ArgumentException">Thrown when audio clip is null</exception>
        /// <exception cref="InvalidOperationException">Thrown when the controller is not available</exception>
        internal FrameStream GenerateTalkingHeadWithPreloadedData(AvatarData avatarData, AudioClip audioClip, int startFrameIndex = 0)
        {
            ValidateControllerAvailability();
            ValidateTalkingHeadInputs(null, audioClip);
            
            Logger.Log($"[LiveTalkAPI] Generating talking head: {audioClip.name} ({audioClip.length:F2}s)");
            
            int estimatedFrames = EstimateFrameCount(audioClip);
            var outputStream = new FrameStream(estimatedFrames) { StartFrameIndex = startFrameIndex };
            
            _controller.StartCoroutine(LiveTalkController.Produce(
                _museTalk.GenerateWithPreloadedDataAsync(audioClip, avatarData, outputStream, startFrameIndex), outputStream,
                "LiveTalkAPI.GenerateTalkingHeadWithPreloadedData"));
            return outputStream;
        }

        /// <summary>
        /// Streaming counterpart of <see cref="GenerateTalkingHeadWithPreloadedData"/>:
        /// frames are produced as <paramref name="features"/> is fed audio, and
        /// the stream finishes when the extractor completes. The caller owns
        /// the extractor's lifetime (feed, complete or fail it) and the
        /// MuseTalk lease.
        /// </summary>
        internal FrameStream GenerateTalkingHeadIncremental(AvatarData avatarData, StreamingAudioFeatures features, int startFrameIndex = 0)
        {
            ValidateControllerAvailability();
            if (avatarData == null)
                throw new ArgumentNullException(nameof(avatarData));
            if (features == null)
                throw new ArgumentNullException(nameof(features));

            Logger.Log("[LiveTalkAPI] Generating talking head (streaming)");

            var outputStream = new FrameStream(0) { StartFrameIndex = startFrameIndex };
            _controller.StartCoroutine(LiveTalkController.Produce(
                _museTalk.GenerateFramesIncremental(avatarData, features, outputStream, startFrameIndex), outputStream,
                "LiveTalkAPI.GenerateTalkingHeadIncremental"));
            return outputStream;
        }

        #endregion

        #region Public Methods - Avatars

        /// <summary>
        /// Gets or creates the <see cref="Avatar"/> for a portrait: the driving
        /// frames, latents and face crops LivePortrait and MuseTalk need to
        /// animate it. The id is a content hash of the image and
        /// <paramref name="mode"/> (<see cref="HashUtils.GenerateAvatarId"/>), so
        /// if <c>&lt;saveLocation&gt;/avatars/&lt;id&gt;/</c> already exists
        /// and is complete it is loaded in seconds; otherwise the pipeline runs
        /// (minutes, hundreds of MB) and writes it. A run that fails removes
        /// its partial folder.
        /// </summary>
        /// <param name="image">Readable source portrait.</param>
        /// <param name="mode">
        /// Which expressions to generate. <see cref="CreationMode.VoiceOnly"/>
        /// stores the image only (usable as a thumbnail, not animatable).
        /// </param>
        /// <param name="onComplete">Receives the loaded avatar. Never called on failure.</param>
        /// <param name="onError">Receives the failure. Exactly one of the two callbacks fires.</param>
        public IEnumerator CreateAvatarAsync(
            Texture2D image,
            CreationMode mode,
            Action<Avatar> onComplete,
            Action<Exception> onError)
        {
            return TaskYield.Guard(CreateAvatarCore(image, mode, onComplete), onError, "LiveTalkAPI.CreateAvatarAsync");
        }

        private IEnumerator CreateAvatarCore(Texture2D image, CreationMode mode, Action<Avatar> onComplete)
        {
            RequireInitialized();
            Avatar avatar = null;
            yield return Avatar.CreateOrLoadCore(image, mode, a => avatar = a);
            onComplete?.Invoke(avatar);
        }

        /// <summary>
        /// Loads an existing avatar from <c>&lt;saveLocation&gt;/avatars/&lt;avatarId&gt;/</c>.
        /// Fails through <paramref name="onError"/> if the folder is missing or
        /// incomplete.
        /// </summary>
        public IEnumerator LoadAvatarAsync(
            string avatarId,
            Action<Avatar> onComplete,
            Action<Exception> onError)
        {
            return TaskYield.Guard(LoadAvatarCore(avatarId, onComplete), onError, "LiveTalkAPI.LoadAvatarAsync");
        }

        private IEnumerator LoadAvatarCore(string avatarId, Action<Avatar> onComplete)
        {
            RequireInitialized();
            if (string.IsNullOrEmpty(avatarId))
                throw new ArgumentException("Avatar ID cannot be null or empty.", nameof(avatarId));

            string folder = LiveTalkStorage.AvatarFolder(avatarId);
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Avatar not found: {avatarId} (expected at {folder})");
            if (!Avatar.IsComplete(folder, out string reason))
                throw new InvalidDataException($"Avatar {avatarId} at {folder} is incomplete ({reason}); recreate it with CreateAvatarAsync.");

            Avatar avatar = null;
            yield return Avatar.LoadCore(folder, avatarId, modeHint: null, isLegacy: false, a => avatar = a);
            onComplete?.Invoke(avatar);
        }

        /// <summary>
        /// Ids of every complete avatar under <c>&lt;saveLocation&gt;/avatars/</c>.
        /// </summary>
        public string[] GetAvailableAvatarIds()
        {
            if (!_initialized || !LiveTalkStorage.HasRoot)
                return Array.Empty<string>();
            return ListFolders(LiveTalkStorage.AvatarsRoot, dir => Avatar.IsComplete(dir, out _));
        }

        /// <summary>
        /// Deletes <c>avatars/&lt;avatarId&gt;/</c>. Refuses — throwing
        /// <see cref="InvalidOperationException"/> that names them — if any
        /// <c>characters/*/character.json</c> still references the avatar.
        /// Delete or re-point those characters first. Missing avatar: no-op.
        /// </summary>
        public void DeleteAvatar(string avatarId)
        {
            RequireInitialized();
            if (string.IsNullOrEmpty(avatarId))
                throw new ArgumentException("Avatar ID cannot be null or empty.", nameof(avatarId));

            var users = CharactersReferencing(f => f.avatarId == avatarId);
            if (users.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Avatar {avatarId} is still referenced by character(s) {string.Join(", ", users)}; delete or re-point them first.");
            }
            LiveTalkStorage.DeleteFolder(LiveTalkStorage.AvatarFolder(avatarId));
            Logger.Log($"[LiveTalkAPI] Deleted avatar {avatarId}");
        }

        #endregion

        #region Public Methods - Voices

        /// <summary>
        /// Designs a new speaker from a description and renders
        /// <paramref name="sampleText"/> as its <see cref="Voice.Sample"/>. Every
        /// call samples a new speaker and gets a new GUID id — this is the
        /// "roll the dice" operation; keep the <see cref="Voice"/> you like and
        /// <see cref="DeleteVoice"/> the rest. Saved to
        /// <c>&lt;saveLocation&gt;/voices/&lt;id&gt;/</c>. The rendered sample
        /// is at the engine's native rate so it can be handed straight to
        /// <see cref="CloneVoiceAsync"/> to lock the take.
        /// </summary>
        /// <param name="instruct">Optional free-text VoiceDesign notes, composed with the three enums.</param>
        /// <param name="sampleText">Text the voice speaks as its sample. Required.</param>
        public IEnumerator DesignVoiceAsync(
            Gender gender,
            Pitch pitch,
            Speed speed,
            string instruct,
            string sampleText,
            Action<Voice> onComplete,
            Action<Exception> onError)
        {
            return TaskYield.Guard(DesignVoiceCore(gender, pitch, speed, instruct, sampleText, onComplete), onError,
                "LiveTalkAPI.DesignVoiceAsync");
        }

        private IEnumerator DesignVoiceCore(
            Gender gender, Pitch pitch, Speed speed, string instruct, string sampleText, Action<Voice> onComplete)
        {
            RequireInitialized();
            Voice voice = null;
            yield return TaskYield.Wait(Voice.DesignAsync(this, gender, pitch, speed, instruct, sampleText),
                v => voice = v, "LiveTalkAPI.DesignVoiceAsync");
            onComplete?.Invoke(voice);
        }

        /// <summary>
        /// Clones a speaker from <paramref name="reference"/> (in-context when
        /// <paramref name="transcript"/> is given, which is what reproduces the
        /// speaker; x-vector only without it). The id is a content hash of the
        /// reference PCM and transcript (<see cref="HashUtils.GenerateClonedVoiceId"/>),
        /// so cloning the same take twice loads the existing
        /// <c>&lt;saveLocation&gt;/voices/&lt;id&gt;/</c> instead of re-running
        /// the encoders. The reference itself becomes <see cref="Voice.Sample"/>.
        /// Best at 24 kHz and at least a few seconds long.
        /// </summary>
        public IEnumerator CloneVoiceAsync(
            AudioClip reference,
            string transcript,
            Action<Voice> onComplete,
            Action<Exception> onError)
        {
            return TaskYield.Guard(CloneVoiceCore(reference, transcript, onComplete), onError,
                "LiveTalkAPI.CloneVoiceAsync");
        }

        private IEnumerator CloneVoiceCore(AudioClip reference, string transcript, Action<Voice> onComplete)
        {
            RequireInitialized();
            Voice voice = null;
            yield return TaskYield.Wait(Voice.CloneAsync(this, reference, transcript),
                v => voice = v, "LiveTalkAPI.CloneVoiceAsync");
            onComplete?.Invoke(voice);
        }

        /// <summary>
        /// Loads an existing voice from <c>&lt;saveLocation&gt;/voices/&lt;voiceId&gt;/</c>.
        /// Fails through <paramref name="onError"/> if the folder or its
        /// <c>voice.json</c> is missing, or the engine cannot restore it.
        /// </summary>
        public IEnumerator LoadVoiceAsync(
            string voiceId,
            Action<Voice> onComplete,
            Action<Exception> onError)
        {
            return TaskYield.Guard(LoadVoiceCore(voiceId, onComplete), onError, "LiveTalkAPI.LoadVoiceAsync");
        }

        private IEnumerator LoadVoiceCore(string voiceId, Action<Voice> onComplete)
        {
            RequireInitialized();
            if (string.IsNullOrEmpty(voiceId))
                throw new ArgumentException("Voice ID cannot be null or empty.", nameof(voiceId));

            string folder = LiveTalkStorage.VoiceFolder(voiceId);
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"Voice not found: {voiceId} (expected at {folder})");

            Voice voice = null;
            yield return TaskYield.Wait(Voice.LoadAsync(folder, voiceId, isLegacy: false, fallbackMeta: null),
                v => voice = v, "LiveTalkAPI.LoadVoiceAsync");
            onComplete?.Invoke(voice);
        }

        /// <summary>
        /// Ids of every complete voice under <c>&lt;saveLocation&gt;/voices/</c>.
        /// </summary>
        public string[] GetAvailableVoiceIds()
        {
            if (!_initialized || !LiveTalkStorage.HasRoot)
                return Array.Empty<string>();
            return ListFolders(LiveTalkStorage.VoicesRoot, Voice.IsComplete);
        }

        /// <summary>
        /// Deletes <c>voices/&lt;voiceId&gt;/</c>. Refuses — throwing
        /// <see cref="InvalidOperationException"/> that names them — if any
        /// <c>characters/*/character.json</c> still references the voice.
        /// <see cref="Character.ReplaceVoice"/> or delete those characters
        /// first. Missing voice: no-op.
        /// </summary>
        public void DeleteVoice(string voiceId)
        {
            RequireInitialized();
            if (string.IsNullOrEmpty(voiceId))
                throw new ArgumentException("Voice ID cannot be null or empty.", nameof(voiceId));

            var users = CharactersReferencing(f => f.voiceId == voiceId);
            if (users.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Voice {voiceId} is still referenced by character(s) {string.Join(", ", users)}; replace or delete them first.");
            }
            LiveTalkStorage.DeleteFolder(LiveTalkStorage.VoiceFolder(voiceId));
            Logger.Log($"[LiveTalkAPI] Deleted voice {voiceId}");
        }

        #endregion

        #region Public Methods - Characters

        /// <summary>
        /// Composes a loaded <see cref="Avatar"/> (or null for voice-only) and a
        /// loaded <see cref="Voice"/> into a new <see cref="Character"/>.
        /// Synchronous and instant: both halves already exist, so this only
        /// writes <c>&lt;saveLocation&gt;/characters/&lt;id&gt;/character.json</c>
        /// (<c>{ id, name, avatarId, voiceId, speechSampleRate, createdUtc }</c>)
        /// and returns a character that is loaded and ready to speak. The id is
        /// a fresh GUID. The avatar and voice folders are referenced, not
        /// copied, so any number of characters can share them.
        /// </summary>
        /// <exception cref="ArgumentNullException">No voice.</exception>
        /// <exception cref="ArgumentException">Empty name, or an avatar/voice that lives inline in a pre-2.0 folder.</exception>
        public Character CreateCharacter(string name, Avatar avatar, Voice voice)
        {
            RequireInitialized();
            return Character.CreateNew(name, avatar, voice);
        }

        /// <summary>
        /// Deletes <c>characters/&lt;characterId&gt;/</c>. The avatar and
        /// voice it referenced are left in place for other characters; use
        /// <see cref="DeleteAvatar"/> / <see cref="DeleteVoice"/> for those.
        /// Also deletes a pre-2.0 inline character folder (which takes its
        /// inline avatar and voice with it). Missing character: no-op.
        /// </summary>
        public void DeleteCharacter(string characterId)
        {
            RequireInitialized();
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("Character ID cannot be null or empty.", nameof(characterId));

            string path = Character.GetCharacterPath(characterId);
            if (path == null)
                return;
            LiveTalkStorage.DeleteFolder(path);
            Logger.Log($"[LiveTalkAPI] Deleted character {characterId} ({path})");
        }

        /// <summary>Character ids whose <c>character.json</c> satisfies <paramref name="predicate"/>.</summary>
        private List<string> CharactersReferencing(Func<CharacterFile, bool> predicate)
        {
            var users = new List<string>();
            string root = LiveTalkStorage.CharactersRoot;
            if (!Directory.Exists(root))
                return users;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string json = Path.Combine(dir, CharacterFile.FileName);
                if (!File.Exists(json))
                    continue;
                try
                {
                    var file = JsonConvert.DeserializeObject<CharacterFile>(File.ReadAllText(json));
                    if (file != null && predicate(file))
                        users.Add(string.IsNullOrEmpty(file.id) ? Path.GetFileName(dir) : file.id);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[LiveTalkAPI] Unreadable {json}: {ex.Message}");
                }
            }
            return users;
        }

        private static string[] ListFolders(string root, Func<string, bool> accept)
        {
            if (!Directory.Exists(root))
                return Array.Empty<string>();
            try
            {
                var ids = new List<string>();
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (accept(dir))
                        ids.Add(Path.GetFileName(dir));
                }
                return ids.ToArray();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LiveTalkAPI] Error listing {root}: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private void RequireInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("LiveTalkAPI not initialized. Call LiveTalkAPI.Initialize() first.");
        }

        #endregion

        #region Public Methods - Character Creation (legacy)

        /// <summary>
        /// Creates a new character with the specified parameters
        /// </summary>
        /// <param name="name">The name of the character</param>
        /// <param name="gender">The gender of the character</param>
        /// <param name="image">The image of the character</param>
        /// <param name="pitch">The pitch of the character</param>
        /// <param name="speed">The speed of the character</param>
        /// <param name="intro">The intro of the character</param>
        /// <param name="voicePromptPath">The path to the voice prompt</param>
        /// <param name="onComplete">Callback when character is successfully created</param>
        /// <param name="onError">Callback when an error occurs</param>
        [Obsolete("Use CreateAvatarAsync + DesignVoiceAsync/CloneVoiceAsync + CreateCharacter")]
        public IEnumerator CreateCharacterAsync(
            string name,
            Gender gender,
            Texture2D image,
            Pitch pitch,
            Speed speed,
            string intro,
            string voicePromptPath,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
#pragma warning disable CS0618 // forwards to the other obsolete overload
            return CreateCharacterAsync(name, gender, image, pitch, speed, intro, voicePromptPath, onComplete, onError, CreationMode.AllExpressions);
#pragma warning restore CS0618
        }

        /// <summary>
        /// Creates a new character with the specified parameters. Forwards to
        /// the 2.0 API: <see cref="CreateAvatarAsync"/> for
        /// <paramref name="image"/> (skipped when null), then
        /// <see cref="CloneVoiceAsync"/> when <paramref name="voicePromptPath"/>
        /// is given or <see cref="DesignVoiceAsync"/> otherwise (with
        /// <paramref name="intro"/> as the sample text), then
        /// <see cref="CreateCharacter"/>. The result is a 2.0 character under
        /// <c>characters/</c> referencing its avatar and voice; the id is a
        /// GUID, not a hash of the parameters.
        /// </summary>
        /// <param name="name">The name of the character</param>
        /// <param name="gender">The gender of the character</param>
        /// <param name="image">The image of the character</param>
        /// <param name="pitch">The pitch of the character</param>
        /// <param name="speed">The speed of the character</param>
        /// <param name="intro">The intro of the character</param>
        /// <param name="voicePromptPath">The path to the voice prompt</param>
        /// <param name="onComplete">Callback when character is successfully created</param>
        /// <param name="onError">Callback when an error occurs</param>
        /// <param name="creationMode">The creation mode to use</param>
        /// <param name="useBundle">Ignored. The 2.0 layout does not use macOS bundles; legacy bundles still load.</param>
        /// <param name="voiceInstruct">Optional VoiceDesign instruct notes</param>
        /// <param name="voiceCloneRefText">Transcript of the clone reference wav (ICL). Required for Base ICL clone.</param>
        /// <remarks>
        /// Exactly one of <paramref name="onComplete"/> / <paramref name="onError"/>
        /// fires. A failure anywhere in avatar generation or voice creation —
        /// a missing model file, a clone that could not be built, a voice
        /// folder that did not load — reaches <paramref name="onError"/>, and
        /// <paramref name="onComplete"/> is never called with a half-built
        /// character.
        /// </remarks>
        [Obsolete("Use CreateAvatarAsync + DesignVoiceAsync/CloneVoiceAsync + CreateCharacter")]
        public IEnumerator CreateCharacterAsync(
            string name,
            Gender gender,
            Texture2D image,
            Pitch pitch,
            Speed speed,
            string intro,
            string voicePromptPath,
            Action<Character> onComplete,
            Action<Exception> onError,
            CreationMode creationMode,
            bool useBundle = true,
            string voiceInstruct = null,
            string voiceCloneRefText = null)
        {
            return TaskYield.Guard(
                CreateCharacterCore(name, gender, image, pitch, speed, intro, voicePromptPath, onComplete, onError,
                    creationMode, useBundle, voiceInstruct, voiceCloneRefText),
                onError,
                "LiveTalkAPI.CreateCharacterAsync");
        }

        private IEnumerator CreateCharacterCore(
            string name,
            Gender gender,
            Texture2D image,
            Pitch pitch,
            Speed speed,
            string intro,
            string voicePromptPath,
            Action<Character> onComplete,
            Action<Exception> onError,
            CreationMode creationMode,
            bool useBundle,
            string voiceInstruct,
            string voiceCloneRefText)
        {
            RequireInitialized();

            // Unguarded core: a fault in any step propagates out of this
            // iterator to the Guard above, which routes it to onError.
            // onComplete is only reached when every step succeeded.
            Avatar avatar = null;
            if (image != null)
            {
                yield return Avatar.CreateOrLoadCore(image, creationMode, a => avatar = a);
            }

            Voice voice = null;
            if (!string.IsNullOrEmpty(voicePromptPath))
            {
                // Thrown, not logged-and-returned: a silent return here left
                // the character reported as created with no voice.
                AudioClip reference = null;
                yield return TaskYield.Wait(AudioFileIO.LoadClipAsync(voicePromptPath), c => reference = c,
                    $"LiveTalkAPI.CreateCharacterAsync load {voicePromptPath}");
                if (reference == null)
                    throw new FileNotFoundException(
                        $"Could not read the voice prompt (clone reference) at {voicePromptPath}", voicePromptPath);

                yield return TaskYield.Wait(Voice.CloneAsync(this, reference, voiceCloneRefText),
                    v => voice = v, "LiveTalkAPI.CreateCharacterAsync clone");
            }
            else
            {
                string sampleText = string.IsNullOrWhiteSpace(intro) ? "Hello, this is a test message" : intro;
                yield return TaskYield.Wait(Voice.DesignAsync(this, gender, pitch, speed, voiceInstruct, sampleText),
                    v => voice = v, "LiveTalkAPI.CreateCharacterAsync design");
            }

            var character = Character.CreateNew(name, avatar, voice);
            onComplete?.Invoke(character);
        }

        /// <summary>
        /// Load a character from a path
        /// </summary>
        /// <param name="characterPath">The path to the character</param>
        /// <param name="onComplete">Callback when character is successfully loaded</param>
        /// <param name="onError">Callback when an error occurs</param>
        public IEnumerator LoadCharacterAsyncFromPath(
            string characterPath,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            if (!_initialized)
            {
                onError?.Invoke(new Exception("LiveTalkAPI not initialized. Call Initialize() first."));
                yield break;
            }
            if (string.IsNullOrEmpty(characterPath))
            {
                onError?.Invoke(new ArgumentException("Character path cannot be null or empty."));
                yield break;
            }
            yield return Character.LoadCharacterAsyncFromPath(characterPath, onComplete, onError);
        }

        /// <summary>
        /// Load a character from the saveLocation using the character GUID
        /// </summary>
        /// <param name="characterId">The GUID/hash of the character to load</param>
        /// <param name="onComplete">Callback when character is successfully loaded</param>
        /// <param name="onError">Callback when an error occurs</param>
        public IEnumerator LoadCharacterAsyncFromId(
            string characterId,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            if (!_initialized)
            {
                onError?.Invoke(new Exception("LiveTalkAPI not initialized. Call Initialize() first."));
                yield break;
            }
            if (string.IsNullOrEmpty(characterId))
            {
                onError?.Invoke(new ArgumentException("Character ID cannot be null or empty."));
                yield break;
            }

            yield return Character.LoadCharacterAsyncFromId(characterId, onComplete, onError);
        }

        /// <summary>
        /// Load only character metadata (image + config JSON) without expressions/voice data by ID.
        /// Use this for thumbnail displays and lists. Call LoadCharacterAsyncFromId for full character.
        /// </summary>
        /// <param name="characterId">The GUID/hash of the character to load</param>
        /// <param name="onComplete">Callback with loaded character (only Image and config populated)</param>
        /// <param name="onError">Callback when an error occurs</param>
        public IEnumerator LoadCharacterMetadataAsync(
            string characterId,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            if (!_initialized)
            {
                onError?.Invoke(new Exception("LiveTalkAPI not initialized. Call Initialize() first."));
                yield break;
            }
            if (string.IsNullOrEmpty(characterId))
            {
                onError?.Invoke(new ArgumentException("Character ID cannot be null or empty."));
                yield break;
            }

            yield return Character.LoadCharacterMetadataAsync(characterId, onComplete, onError);
        }

        /// <summary>
        /// Load only character metadata (image + config JSON) without expressions/voice data from path.
        /// Use this for thumbnail displays and lists. Call LoadCharacterAsyncFromPath for full character.
        /// </summary>
        /// <param name="characterPath">The path to the character folder or bundle</param>
        /// <param name="onComplete">Callback with loaded character (only Image and config populated)</param>
        /// <param name="onError">Callback when an error occurs</param>
        public IEnumerator LoadCharacterMetadataFromPathAsync(
            string characterPath,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            if (!_initialized)
            {
                onError?.Invoke(new Exception("LiveTalkAPI not initialized. Call Initialize() first."));
                yield break;
            }
            if (string.IsNullOrEmpty(characterPath))
            {
                onError?.Invoke(new ArgumentException("Character path cannot be null or empty."));
                yield break;
            }

            yield return Character.LoadCharacterMetadataFromPathAsync(characterPath, onComplete, onError);
        }

        /// <summary>
        /// Get all available character IDs: every <c>characters/&lt;id&gt;/</c>
        /// with a <c>character.json</c>, plus any pre-2.0 inline character
        /// folder (<c>&lt;id&gt;</c> or <c>&lt;id&gt;.bundle</c>) at the root of
        /// the save location.
        /// </summary>
        /// <returns>Array of character ids</returns>
        public string[] GetAvailableCharacterIds()
        {
            if (!_initialized || !LiveTalkStorage.HasRoot)
            {
                return Array.Empty<string>();
            }

            try
            {
                string root = LiveTalkStorage.Root;
                if (!Directory.Exists(root))
                {
                    return Array.Empty<string>();
                }

                var characterIds = new List<string>();

                // 2.0 layout.
                string charactersRoot = LiveTalkStorage.CharactersRoot;
                if (Directory.Exists(charactersRoot))
                {
                    foreach (var dir in Directory.GetDirectories(charactersRoot))
                    {
                        if (File.Exists(Path.Combine(dir, CharacterFile.FileName)))
                            characterIds.Add(Path.GetFileName(dir));
                    }
                }

                // Legacy inline folders at the root. avatars/, voices/ and
                // characters/ hold no character.json of their own, so they fall
                // out naturally.
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!File.Exists(Path.Combine(dir, CharacterFile.FileName)))
                        continue;

                    // Remove .bundle extension if present to get the actual character ID
                    if (dirName.EndsWith(".bundle"))
                    {
                        dirName = dirName.Substring(0, dirName.Length - 7); // Remove ".bundle"
                    }

                    // Avoid duplicates (in case both folder and bundle exist for same character)
                    if (!characterIds.Contains(dirName))
                    {
                        characterIds.Add(dirName);
                    }
                }

                return characterIds.ToArray();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LiveTalkAPI] Error getting available character IDs: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Whether this platform can read macOS <c>.bundle</c> character
        /// folders. Only pre-2.0 characters use them; the 2.0 layout writes
        /// plain folders everywhere.
        /// </summary>
        /// <returns>True on macOS</returns>
        public static bool CanUseBundle()
        {
            return Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer;
        }

        #endregion

        #region Public Methods - Voice Preview

        /// <summary>Sample text a voice preview renders when the caller gives none.</summary>
        private const string DefaultPreviewText = "Hello, this is a short voice sample.";

        /// <summary>
        /// Generate a preview voice sample with the specified parameters.
        /// This is used for "rolling the dice" to preview different voices before committing.
        /// Implemented on <see cref="DesignVoiceAsync"/>: the preview is a real,
        /// saved <see cref="Voice"/> under <c>voices/</c> (returned as
        /// <c>VoicePreviewResult.Voice</c>; <c>VoiceFolderPath</c> is its folder),
        /// so a chosen preview can go straight into <see cref="CreateCharacter"/>
        /// and a rejected one is removed with <see cref="DeleteVoice"/>.
        /// </summary>
        /// <param name="gender">Voice gender ("male" or "female")</param>
        /// <param name="pitch">Voice pitch ("verylow", "low", "moderate", "high", "veryhigh")</param>
        /// <param name="speed">Voice speed ("verylow", "low", "moderate", "high", "veryhigh")</param>
        /// <param name="introText">Text to speak for the voice sample</param>
        /// <returns>VoicePreviewResult containing the AudioClip, the Voice and its folder path</returns>
        [Obsolete("Use DesignVoiceAsync, which returns a Voice directly.")]
        public async Task<VoicePreviewResult> GenerateVoicePreviewAsync(
            string gender,
            string pitch,
            string speed,
            string introText = DefaultPreviewText,
            string instruct = null)
        {
            RequireInitialized();

            if (string.IsNullOrEmpty(gender))
            {
                throw new ArgumentException("Gender parameter is required.");
            }

            if (string.IsNullOrEmpty(introText))
            {
                introText = DefaultPreviewText;
            }

            Logger.Log($"[LiveTalkAPI] Generating voice preview: {gender}/{pitch}/{speed}");

            try
            {
                var voice = await Voice.DesignAsync(
                    this, ParseGender(gender), ParsePitch(pitch), ParseSpeed(speed), instruct, introText);

                return new VoicePreviewResult
                {
                    Success = true,
                    Voice = voice,
                    AudioClip = voice.Sample,
                    VoiceFolderPath = voice.Folder,
                    Gender = gender.ToLower(),
                    Pitch = pitch?.ToLower() ?? "moderate",
                    Speed = speed?.ToLower() ?? "moderate"
                };
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LiveTalkAPI] Error generating voice preview: {ex.Message}");
                return new VoicePreviewResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        static Gender ParseGender(string value) =>
            string.Equals(value, "male", StringComparison.OrdinalIgnoreCase) ? Gender.Male : Gender.Female;

        static Pitch ParsePitch(string value) => (value ?? "").ToLowerInvariant() switch
        {
            "very_low" or "verylow" => Pitch.VeryLow,
            "low" => Pitch.Low,
            "high" => Pitch.High,
            "very_high" or "veryhigh" => Pitch.VeryHigh,
            _ => Pitch.Moderate,
        };

        static Speed ParseSpeed(string value) => (value ?? "").ToLowerInvariant() switch
        {
            "very_low" or "verylow" => Speed.VeryLow,
            "low" => Speed.Low,
            "high" => Speed.High,
            "very_high" or "veryhigh" => Speed.VeryHigh,
            _ => Speed.Moderate,
        };

        /// <summary>
        /// Clean up the pre-2.0 voice preview temp folder. Previews made by
        /// the current <see cref="GenerateVoicePreviewAsync"/> are saved voices;
        /// remove those with <see cref="DeleteVoice"/>.
        /// </summary>
        [Obsolete("Previews are saved voices now; remove unwanted ones with DeleteVoice.")]
        public void CleanupVoicePreviews()
        {
            string previewsFolder = Path.Combine(Application.temporaryCachePath, "VoicePreviews");
            if (Directory.Exists(previewsFolder))
            {
                try
                {
                    Directory.Delete(previewsFolder, true);
                    Logger.Log("[LiveTalkAPI] Cleaned up voice previews folder");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[LiveTalkAPI] Failed to cleanup voice previews: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Delete a specific voice preview folder by path. For a preview from
        /// the current <see cref="GenerateVoicePreviewAsync"/>, prefer
        /// <see cref="DeleteVoice"/> with <c>Voice.Id</c>, which refuses if a
        /// character still uses it.
        /// </summary>
        /// <param name="voiceFolderPath">Path to the voice folder to delete</param>
        [Obsolete("Use DeleteVoice(voiceId).")]
        public void DeleteVoicePreview(string voiceFolderPath)
        {
            if (string.IsNullOrEmpty(voiceFolderPath) || !Directory.Exists(voiceFolderPath))
            {
                return;
            }

            try
            {
                Directory.Delete(voiceFolderPath, true);
                Logger.LogVerbose($"[LiveTalkAPI] Deleted voice preview: {voiceFolderPath}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[LiveTalkAPI] Failed to delete voice preview: {ex.Message}");
            }
        }

        #endregion

        #region Private Methods - Input Validation

        /// <summary>
        /// Validates common animation inputs for source image and driving frames.
        /// </summary>
        /// <param name="sourceImage">The source image to validate</param>
        /// <param name="drivingFrames">The driving frames to validate</param>
        /// <exception cref="ArgumentException">Thrown when inputs are invalid</exception>
        private static void ValidateAnimationInputs(Texture2D sourceImage, List<Texture2D> drivingFrames)
        {
            if (sourceImage == null || drivingFrames == null)
                throw new ArgumentException("Invalid input: source image and driving frames are required");
        }

        /// <summary>
        /// Validates animation inputs for source image and video player.
        /// </summary>
        /// <param name="sourceImage">The source image to validate</param>
        /// <param name="videoPlayer">The video player to validate</param>
        /// <exception cref="ArgumentException">Thrown when inputs are invalid</exception>
        private static void ValidateAnimationInputs(Texture2D sourceImage, VideoPlayer videoPlayer)
        {
            if (sourceImage == null || videoPlayer == null)
                throw new ArgumentException("Invalid input: source image and video player are required");
        }

        /// <summary>
        /// Validates animation inputs for source image and driving frames path.
        /// </summary>
        /// <param name="sourceImage">The source image to validate</param>
        /// <param name="drivingFramesPath">The driving frames path to validate</param>
        /// <exception cref="ArgumentException">Thrown when inputs are invalid</exception>
        private static void ValidateAnimationInputs(Texture2D sourceImage, string drivingFramesPath)
        {
            if (sourceImage == null || string.IsNullOrEmpty(drivingFramesPath))
                throw new ArgumentException("Invalid input: source image and driving frames path are required");
        }

        /// <summary>
        /// Validates talking head inputs for avatar texture and audio clip.
        /// </summary>
        /// <param name="avatarTexture">The avatar texture to validate (can be null for preloaded data)</param>
        /// <param name="audioClip">The audio clip to validate</param>
        /// <exception cref="ArgumentException">Thrown when audio clip is null</exception>
        private static void ValidateTalkingHeadInputs(Texture2D avatarTexture, AudioClip audioClip)
        {
            if (audioClip == null)
                throw new ArgumentException("Audio clip is required");
        }

        /// <summary>
        /// Validates that the controller is available for streaming operations.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the controller is not available</exception>
        private void ValidateControllerAvailability()
        {
            if (_controller == null)
                throw new InvalidOperationException("Controller is required for streaming operations. Use constructor with LiveTalkController parameter.");
        }

        #endregion

        #region Private Methods - Helper Functions

        /// <summary>
        /// Creates an input stream from a list of driving frames.
        /// </summary>
        /// <param name="drivingFrames">The driving frames to populate the stream with</param>
        /// <returns>An InputStream populated with the driving frames</returns>
        private static FrameStream CreateInputStreamFromFrames(List<Texture2D> drivingFrames)
        {
            var inputStream = new FrameStream(drivingFrames.Count);
            foreach (var frame in drivingFrames)
            {
                inputStream.Queue.Enqueue(frame);
            }
            return inputStream;
        }

        /// <summary>
        /// Calculates the frame count based on video player settings and maximum frame limit.
        /// </summary>
        /// <param name="videoPlayer">The video player to get frame count from</param>
        /// <param name="maxFrames">The maximum number of frames to process</param>
        /// <returns>The calculated frame count</returns>
        private static int CalculateFrameCount(VideoPlayer videoPlayer, int maxFrames)
        {
            return maxFrames == -1 ? (int)videoPlayer.clip.frameCount : 
                   Mathf.Min(maxFrames, (int)videoPlayer.clip.frameCount);
        }

        /// <summary>
        /// Gets frame files from the specified directory path with optional frame limit.
        /// </summary>
        /// <param name="drivingFramesPath">The path to search for frame files</param>
        /// <param name="maxFrames">The maximum number of frames to retrieve</param>
        /// <returns>An array of frame file paths</returns>
        /// <exception cref="ArgumentException">Thrown when no frames are found</exception>
        private static string[] GetFrameFiles(string drivingFramesPath, int maxFrames)
        {
            var frameFiles = FileUtils.GetFrameFiles(drivingFramesPath, maxFrames);
            if (frameFiles.Length == 0)
            {
                throw new ArgumentException($"No driving frames found in path: {drivingFramesPath}");
            }
            return frameFiles;
        }

        /// <summary>
        /// Loads avatar textures from a primary texture and optional folder path.
        /// </summary>
        /// <param name="avatarTexture">The primary avatar texture</param>
        /// <param name="talkingHeadFolderPath">The optional folder path for additional textures</param>
        /// <returns>A list of avatar textures for processing</returns>
        private static List<Texture2D> LoadAvatarTextures(Texture2D avatarTexture, string talkingHeadFolderPath)
        {
            var avatarTextures = FileUtils.LoadFramesFromFolder(talkingHeadFolderPath);
            if (avatarTextures == null || avatarTextures.Count == 0)
            {
                avatarTexture = TextureUtils.ConvertTexture2DToRGB24(avatarTexture);
                avatarTextures = new List<Texture2D> { avatarTexture };
            }
            return avatarTextures;
        }

        /// <summary>
        /// Estimates the number of frames needed based on audio clip duration.
        /// </summary>
        /// <param name="audioClip">The audio clip to estimate frame count for</param>
        /// <returns>The estimated frame count based on 25 FPS</returns>
        private static int EstimateFrameCount(AudioClip audioClip)
        {
            return Mathf.CeilToInt(audioClip.length * 25f); // ~25 FPS estimate
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Releases all resources used by the LiveTalkAPI instance.
        /// Disposes of all inference engines and cleans up model utilities.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Releases the resources used by the LiveTalkAPI. There is no
        /// finalizer: everything this class owns (inference sessions, the
        /// coroutine host GameObject) must be released on the main thread,
        /// which a finalizer never runs on, and the singleton lives for the
        /// process anyway.
        /// </summary>
        /// <param name="disposing">True to release managed resources</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _livePortrait?.Dispose();
                    _museTalk?.Dispose();
                    ModelUtils.Dispose();
                }
                
                _disposed = true;
            }
        }

        #endregion
    }

    #endregion
}
