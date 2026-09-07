using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using QwenTTS;
using Newtonsoft.Json;

namespace LiveTalk.API
{
    using Core;
    using Utils;
    public enum Gender
    {
        Male,
        Female
    }

    public enum Pitch
    {
        VeryLow,
        Low,
        Moderate,
        High,
        VeryHigh
    }

    public enum Speed
    {
        VeryLow,
        Low,
        Moderate,
        High,
        VeryHigh
    }

    /// <summary>
    /// <c>character.json</c>. The 2.0 layout is references only:
    /// <c>{ id, name, avatarId, voiceId, speechSampleRate, createdUtc }</c>.
    /// The pre-2.0 layout had <c>{ name, gender, pitch, speed, intro,
    /// voiceInstruct }</c> with the avatar and voice inline beside it; those
    /// fields are kept here so a legacy file still parses and can be told
    /// apart (<see cref="IsLegacy"/>). Newtonsoft only; not a Unity-serialized type.
    /// </summary>
    internal class CharacterFile
    {
        public string id;
        public string name;
        public string avatarId;
        public string voiceId;
        public int? speechSampleRate;
        public DateTime? createdUtc;
        public string version;

        // Pre-2.0 inline layout.
        public Gender? gender;
        public Pitch? pitch;
        public Speed? speed;
        public string intro;
        public string voiceInstruct;

        public const string FileName = "character.json";

        [JsonIgnore]
        public bool IsLegacy => string.IsNullOrEmpty(id) && string.IsNullOrEmpty(avatarId) && string.IsNullOrEmpty(voiceId);
    }

    /// <summary>
    /// A named composition of an <see cref="Avatar"/> (the face) and a
    /// <see cref="Voice"/> (the speaker). Creating one is instant — both halves
    /// already exist — and swapping the voice is a one-line write.
    ///
    /// <para><b>Identity.</b> <see cref="Id"/> is a GUID assigned by
    /// <see cref="LiveTalkAPI.CreateCharacter"/> and stable for the
    /// character's life, whatever its voice or name becomes.</para>
    ///
    /// <para><b>Storage.</b>
    /// <c>&lt;saveLocation&gt;/characters/&lt;Id&gt;/character.json</c> holding
    /// <c>{ id, name, avatarId, voiceId, speechSampleRate, createdUtc }</c>.
    /// The avatar and voice folders are referenced by id, never copied, so
    /// several characters share one avatar and deleting a character leaves
    /// both halves for others.</para>
    ///
    /// <para><b>Legacy.</b> Pre-2.0 characters lived in
    /// <c>&lt;saveLocation&gt;/&lt;id&gt;[.bundle]/</c> with the avatar and
    /// voice inline. <see cref="LiveTalkAPI.LoadCharacterAsyncFromId"/> still
    /// loads those in place (<see cref="IsLegacy"/>); they cannot have their
    /// voice replaced and their halves cannot be shared. Recreate them through
    /// the 2.0 API to migrate.</para>
    /// </summary>
    public sealed class Character
    {
        /// <summary>GUID assigned at creation; also the folder name under <c>characters/</c>. For a legacy character, its folder name.</summary>
        public string Id { get; private set; }

        /// <summary>Same as <see cref="Id"/>. Read-only; a character's id never changes.</summary>
        public string CharacterId => Id;

        /// <summary>Display name. Not part of the identity.</summary>
        public string Name { get; private set; }

        /// <summary>The face, or null for a voice-only character.</summary>
        public Avatar Avatar { get; private set; }

        /// <summary>The speaker. Never null once <see cref="IsDataLoaded"/>.</summary>
        public Voice Voice { get; private set; }

        /// <summary>
        /// The portrait: <see cref="Avatar"/>'s image once loaded, or just the
        /// image file after <see cref="LiveTalkAPI.LoadCharacterMetadataAsync"/>.
        /// </summary>
        public Texture2D Image { get; private set; }

        /// <summary>
        /// Sample rate for generated speech, or 0 for the TTS model's native rate.
        /// 16 kHz for a character with an animatable avatar because that is what
        /// the lip-sync stack consumes; native for a voice-only character, which
        /// matters when the clip is going to be a clone reference: the speaker
        /// encoder reads mel up to 12 kHz, so a 16 kHz round trip throws away the
        /// top of the band the speaker is identified by. Persisted in
        /// <c>character.json</c>.
        /// </summary>
        public int SpeechSampleRate { get; set; } = 16000;

        /// <summary>True once <see cref="Avatar"/> (if any) and <see cref="Voice"/> are loaded and the character can speak.</summary>
        public bool IsDataLoaded { get; private set; }

        /// <summary>True for a pre-2.0 inline folder. See the class remarks.</summary>
        public bool IsLegacy { get; private set; }

        /// <summary>When the character was created; <see cref="DateTime.MinValue"/> for a legacy file.</summary>
        public DateTime CreatedUtc { get; private set; }

        [Obsolete("Use Voice.Gender.")]
        public Gender Gender => Voice != null ? Voice.Gender : _legacyFile?.gender ?? default;
        [Obsolete("Use Voice.Pitch.")]
        public Pitch Pitch => Voice != null ? Voice.Pitch : _legacyFile?.pitch ?? default;
        [Obsolete("Use Voice.Speed.")]
        public Speed Speed => Voice != null ? Voice.Speed : _legacyFile?.speed ?? default;
        [Obsolete("Use Voice.SampleText.")]
        public string Intro => Voice != null ? Voice.SampleText : _legacyFile?.intro;
        [Obsolete("Use Voice.Instruct.")]
        public string VoiceInstruct => Voice != null ? Voice.Instruct : _legacyFile?.voiceInstruct;
        [Obsolete("Use Voice.SampleText.")]
        public string VoiceCloneRefText => Voice != null && Voice.Kind == VoiceKind.Cloned ? Voice.SampleText : null;
        [Obsolete("Use Voice.Sample.")]
        public AudioClip VoicePromptClip => Voice?.Sample;

        internal static string saveLocation
        {
            get => LiveTalkStorage.Root;
            set => LiveTalkStorage.Root = value;
        }

        /// <summary><c>characters/&lt;Id&gt;</c>, or the legacy inline folder.</summary>
        internal string CharacterFolder { get; private set; }

        /// <summary>Where the idle frames are (expression 0), or null without an animatable avatar.</summary>
        internal string IdleFramesFolder => Avatar != null && Avatar.CanAnimate ? Avatar.ExpressionFolder(0) : null;

        /// <summary>The rate the idle frames play at.</summary>
        public float IdleFrameRate => Avatar.DefaultFrameRate;

        // Ids read from character.json before the halves are loaded
        // (metadata load), and the legacy file for the inline path.
        private string _avatarId;
        private string _voiceId;
        private CharacterFile _legacyFile;

        // CharacterPlayer for animation and playback
        private CharacterPlayer _characterPlayer;

        /// <summary>
        /// The player for this character, created on first access once the
        /// data is loaded. Null before that. <see cref="DestroyPlayer"/> tears
        /// it down; the next access makes a new one.
        /// </summary>
        public CharacterPlayer CharacterPlayer
        {
            get
            {
                if (_characterPlayer == null && IsDataLoaded)
                {
                    CreateCharacterPlayer();
                }
                return _characterPlayer;
            }
        }

        private Character(string id, string name)
        {
            Id = id;
            Name = name;
        }

        #region Create / persist

        /// <summary>
        /// Composes a loaded avatar and voice into a new character and writes
        /// its <c>character.json</c>. Synchronous: nothing is processed.
        /// </summary>
        internal static Character CreateNew(string name, Avatar avatar, Voice voice)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Character name cannot be null or empty.", nameof(name));
            if (voice == null)
                throw new ArgumentNullException(nameof(voice), "A character needs a voice. Design or clone one first.");
            if (voice.Loaded == null)
                throw new ArgumentException("The voice is not loaded.", nameof(voice));
            if (voice.IsLegacy)
                throw new ArgumentException(
                    "This voice lives inline in a pre-2.0 character folder and cannot be referenced by a new character. " +
                    "Clone or design a voice through the 2.0 API instead.", nameof(voice));
            if (avatar != null && avatar.IsLegacy)
                throw new ArgumentException(
                    "This avatar lives inline in a pre-2.0 character folder and cannot be referenced by a new character. " +
                    "Create it through CreateAvatarAsync instead.", nameof(avatar));

            var character = new Character(Guid.NewGuid().ToString("N"), name)
            {
                Avatar = avatar,
                Voice = voice,
                Image = avatar?.Image,
                CreatedUtc = DateTime.UtcNow,
                // Nothing lip-syncs a voice-only character, so hand back the
                // TTS model's own rate instead of the 16 kHz lip-sync rate.
                SpeechSampleRate = avatar != null && avatar.CanAnimate ? 16000 : 0,
            };
            character._avatarId = avatar?.Id;
            character._voiceId = voice.Id;
            character.CharacterFolder = LiveTalkStorage.CharacterFolder(character.Id);
            character.WriteFile();
            character.IsDataLoaded = true;

            Logger.Log($"[Character] Created character '{name}' ({character.Id}): avatar={avatar?.Id ?? "none"}, voice={voice.Id}");
            return character;
        }

        /// <summary>
        /// Points this character at a different voice. Rewrites
        /// <c>character.json</c>, and if a player exists, drops any speech
        /// still queued or in flight in the old voice so the next
        /// <see cref="CharacterPlayer.QueueSpeech"/> speaks in the new one.
        /// The avatar, id and name are untouched; nothing is reprocessed.
        /// </summary>
        /// <exception cref="ArgumentNullException">No voice.</exception>
        /// <exception cref="ArgumentException">The voice is not loaded, or lives inline in a pre-2.0 folder.</exception>
        /// <exception cref="InvalidOperationException">This is a pre-2.0 inline character; recreate it through <see cref="LiveTalkAPI.CreateCharacter"/>.</exception>
        public void ReplaceVoice(Voice voice)
        {
            if (voice == null)
                throw new ArgumentNullException(nameof(voice));
            if (voice.Loaded == null)
                throw new ArgumentException("The voice is not loaded.", nameof(voice));
            if (voice.IsLegacy)
                throw new ArgumentException(
                    "This voice lives inline in a pre-2.0 character folder and cannot be referenced.", nameof(voice));
            if (IsLegacy)
                throw new InvalidOperationException(
                    $"Character '{Name}' is a pre-2.0 inline character; its voice cannot be replaced in place. " +
                    "Create a new character from its avatar and the new voice instead.");
            if (!IsDataLoaded)
                throw new InvalidOperationException(
                    $"Character '{Name}' is not loaded. Load it fully before replacing its voice.");

            var previous = Voice;
            Voice = voice;
            _voiceId = voice.Id;
            WriteFile();

            // Anything generated in the old voice must not play as the new one.
            if (_characterPlayer != null)
                _characterPlayer.OnVoiceReplaced();

            Logger.Log($"[Character] '{Name}' voice replaced: {previous?.Id ?? "none"} → {voice.Id}");
        }

        /// <summary>Writes <c>character.json</c> for a 2.0 character. Synchronous; the file is a few hundred bytes.</summary>
        private void WriteFile()
        {
            var file = new CharacterFile
            {
                id = Id,
                name = Name,
                avatarId = _avatarId,
                voiceId = _voiceId,
                speechSampleRate = SpeechSampleRate,
                createdUtc = CreatedUtc,
                version = "2.0",
            };
            Directory.CreateDirectory(CharacterFolder);
            File.WriteAllText(Path.Combine(CharacterFolder, CharacterFile.FileName),
                JsonConvert.SerializeObject(file, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        #endregion

        #region Player

        /// <summary>
        /// Creates and initializes the CharacterPlayer for this character
        /// </summary>
        private void CreateCharacterPlayer()
        {
            if (_characterPlayer != null || !IsDataLoaded)
                return;
            
            // Create GameObject for CharacterPlayer
            var playerObject = new GameObject($"CharacterPlayer_{Name}");
            playerObject.transform.SetParent(CharacterPlayer.ParentTransform);
            
            // Add CharacterPlayer component
            _characterPlayer = playerObject.AddComponent<CharacterPlayer>();
            
            // Assign this character to the player
            _characterPlayer.AssignCharacter(this);
            
            Logger.Log($"[Character] Created CharacterPlayer for {Name}");
        }

        /// <summary>
        /// Stops and destroys the <see cref="CharacterPlayer"/> GameObject, if
        /// one was created. Safe to call when there is none, or when the host
        /// already destroyed it. The next <see cref="CharacterPlayer"/> access
        /// creates a fresh one. Hosts that rebuild characters should call this
        /// so idle players and their AudioSources do not accumulate.
        /// </summary>
        public void DestroyPlayer()
        {
            var player = _characterPlayer;
            _characterPlayer = null;
            if (player == null) // Unity null: also true for an already-destroyed component
                return;

            player.Stop();
            var go = player.gameObject;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(go);
            else
                UnityEngine.Object.DestroyImmediate(go);
            Logger.Log($"[Character] Destroyed CharacterPlayer for {Name}");
        }

        #endregion

        #region Load

        /// <remarks>
        /// Exactly one of <paramref name="onComplete"/> / <paramref name="onError"/>
        /// fires. A character whose avatar or voice is missing or fails to load
        /// is reported through <paramref name="onError"/>, naming the missing
        /// half, rather than handed back half-loaded.
        /// </remarks>
        public static IEnumerator LoadCharacterAsyncFromPath(
            string characterPath,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            return TaskYield.Guard(LoadCharacterFromPathCore(characterPath, onComplete), onError,
                "Character.LoadCharacterAsyncFromPath");
        }

        private static IEnumerator LoadCharacterFromPathCore(string characterPath, Action<Character> onComplete)
        {
            if (string.IsNullOrEmpty(characterPath))
                throw new ArgumentException("Character path cannot be null or empty.");
            Logger.Log($"[Character] Loading character from: {characterPath}");

            var start = System.Diagnostics.Stopwatch.StartNew();
            Character character = null;
            yield return ReadFileCore(characterPath, c => character = c);
            yield return character.LoadData();

            Logger.LogVerbose($"[Character] Character '{character.Name}' loaded in {start.Elapsed.TotalMilliseconds:F0}ms");
            onComplete?.Invoke(character);
        }

        public static IEnumerator LoadCharacterAsyncFromId(
            string characterId,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            if (string.IsNullOrEmpty(characterId))
            {
                onError?.Invoke(new ArgumentException("Character ID cannot be null or empty."));
                yield break;
            }

            string characterPath = GetCharacterPath(characterId);
            if (characterPath == null)
            {
                onError?.Invoke(new DirectoryNotFoundException(
                    $"Character not found: {characterId} (checked characters/{characterId}, and the legacy {characterId}.bundle / {characterId} folders)"));
                yield break;
            }

            yield return LoadCharacterAsyncFromPath(characterPath, onComplete, onError);
        }

        /// <summary>
        /// Load only character metadata (name + image) without expressions/voice by ID.
        /// This is a lightweight load for thumbnails and lists.
        /// </summary>
        public static IEnumerator LoadCharacterMetadataAsync(
            string characterId,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            if (string.IsNullOrEmpty(characterId))
            {
                onError?.Invoke(new ArgumentException("Character ID cannot be null or empty."));
                yield break;
            }

            string characterPath = GetCharacterPath(characterId);
            if (characterPath == null)
            {
                onError?.Invoke(new DirectoryNotFoundException($"Character not found: {characterId}"));
                yield break;
            }

            yield return LoadCharacterMetadataFromPathAsync(characterPath, onComplete, onError);
        }

        /// <summary>
        /// Load only character metadata (name + image) without expressions/voice from path.
        /// This is a lightweight load for thumbnails and lists.
        /// </summary>
        public static IEnumerator LoadCharacterMetadataFromPathAsync(
            string characterPath,
            Action<Character> onComplete,
            Action<Exception> onError)
        {
            return TaskYield.Guard(LoadCharacterMetadataFromPathCore(characterPath, onComplete), onError,
                "Character.LoadCharacterMetadataFromPathAsync");
        }

        private static IEnumerator LoadCharacterMetadataFromPathCore(string characterPath, Action<Character> onComplete)
        {
            if (string.IsNullOrEmpty(characterPath))
                throw new ArgumentException("Character path cannot be null or empty.");

            Character character = null;
            yield return ReadFileCore(characterPath, c => character = c);

            // Image only — no frames, no voice.
            string imagePath = character.IsLegacy
                ? Path.Combine(characterPath, Avatar.ImageFileName)
                : string.IsNullOrEmpty(character._avatarId)
                    ? null
                    : Path.Combine(LiveTalkStorage.AvatarFolder(character._avatarId), Avatar.ImageFileName);
            if (imagePath != null && File.Exists(imagePath))
            {
                byte[] imageBytes = null;
                yield return TaskYield.Wait(File.ReadAllBytesAsync(imagePath), b => imageBytes = b,
                    $"Character.LoadMetadata read {imagePath}");
                var texture = new Texture2D(2, 2);
                if (texture.LoadImage(imageBytes))
                    character.Image = texture;
                else
                    UnityEngine.Object.DestroyImmediate(texture);
            }

            Logger.Log($"[Character] Loaded metadata for {character.Name}");
            onComplete?.Invoke(character);
        }

        /// <summary>
        /// Reads <c>character.json</c> into an unloaded <see cref="Character"/>:
        /// id, name, folder, referenced ids (or the legacy file). Throws when
        /// the file is missing or unreadable.
        /// </summary>
        private static IEnumerator ReadFileCore(string characterFolder, Action<Character> onRead)
        {
            string configPath = Path.Combine(characterFolder, CharacterFile.FileName);
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"Character config file not found: {configPath}", configPath);

            string json = null;
            yield return TaskYield.Wait(File.ReadAllTextAsync(configPath), t => json = t,
                $"Character.ReadFile {configPath}");

            CharacterFile file;
            try
            {
                file = JsonConvert.DeserializeObject<CharacterFile>(json)
                       ?? throw new InvalidDataException("empty document");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to parse {configPath}: {ex.Message}", ex);
            }

            string folderId = Path.GetFileNameWithoutExtension(characterFolder);
            string id = string.IsNullOrEmpty(file.id) ? folderId : file.id;
            var character = new Character(id, string.IsNullOrEmpty(file.name) ? folderId : file.name)
            {
                CharacterFolder = characterFolder,
                IsLegacy = file.IsLegacy,
                CreatedUtc = file.createdUtc ?? DateTime.MinValue,
            };
            if (file.IsLegacy)
            {
                character._legacyFile = file;
                Logger.Log($"[Character] '{character.Name}' is a pre-2.0 inline character folder ({characterFolder}); " +
                           "loading in place. Recreate it through CreateCharacter to migrate.");
            }
            else
            {
                character._avatarId = file.avatarId;
                character._voiceId = file.voiceId;
                if (file.speechSampleRate.HasValue)
                    character.SpeechSampleRate = file.speechSampleRate.Value;
                if (string.IsNullOrEmpty(file.voiceId))
                    throw new InvalidDataException($"{configPath} references no voice; the character cannot speak.");
            }
            onRead(character);
        }

        /// <summary>
        /// Loads the avatar (if any) and the voice this character references,
        /// or the inline halves of a legacy folder. Throws — so the guarded
        /// caller's onError fires — naming whichever half is missing, rather
        /// than marking a character loaded without it.
        /// </summary>
        internal IEnumerator LoadData()
        {
            if (IsDataLoaded)
                yield break;
            if (string.IsNullOrEmpty(CharacterFolder))
                throw new InvalidOperationException($"Character '{Name}' has no folder to load from.");

            Logger.LogVerbose($"[Character] Loading character data for {Name}");

            if (IsLegacy)
            {
                yield return LoadLegacyInline();
            }
            else
            {
                yield return LoadReferenced();
            }

            if (Voice == null || Voice.Loaded == null)
            {
                // Both loaders throw on every failure they can see, so this is
                // the belt to that brace: IsDataLoaded must never be true for
                // a character that cannot speak.
                throw new InvalidOperationException(
                    $"Voice for character '{Name}' did not load; the character is not usable. See the earlier error for the cause.");
            }

            Image = Avatar?.Image ?? Image;
            IsDataLoaded = true;
            Logger.LogVerbose($"[Character] Character data loaded successfully for {Name}");
        }

        private IEnumerator LoadReferenced()
        {
            // Check both halves before loading either, so one error can name
            // everything that is missing.
            string avatarFolder = string.IsNullOrEmpty(_avatarId) ? null : LiveTalkStorage.AvatarFolder(_avatarId);
            string voiceFolder = LiveTalkStorage.VoiceFolder(_voiceId);
            string missing = null;
            if (avatarFolder != null && !Directory.Exists(avatarFolder))
                missing = $"avatar {_avatarId} (expected at {avatarFolder})";
            if (!Directory.Exists(voiceFolder))
                missing = (missing == null ? "" : missing + " and ") + $"voice {_voiceId} (expected at {voiceFolder})";
            if (missing != null)
            {
                throw new DirectoryNotFoundException(
                    $"Character '{Name}' ({Id}) references {missing}, which is missing. " +
                    "It was deleted, or the save location moved without it.");
            }

            if (avatarFolder != null)
            {
                Avatar avatar = null;
                yield return Avatar.LoadCore(avatarFolder, _avatarId, modeHint: null, isLegacy: false, a => avatar = a);
                Avatar = avatar;
            }

            Voice voice = null;
            yield return TaskYield.Wait(Voice.LoadAsync(voiceFolder, _voiceId, isLegacy: false, fallbackMeta: null),
                v => voice = v, $"Character.LoadVoice {voiceFolder}");
            Voice = voice;
        }

        private IEnumerator LoadLegacyInline()
        {
            // The avatar half is inline: image.png + drivingFrames/ beside the
            // json. A voice-only legacy character has neither.
            bool hasImage = File.Exists(Path.Combine(CharacterFolder, Avatar.ImageFileName));
            bool hasFrames = Directory.Exists(Path.Combine(CharacterFolder, Avatar.DrivingFramesFolderName));
            if (hasImage || hasFrames)
            {
                Avatar avatar = null;
                yield return Avatar.LoadCore(CharacterFolder, Id, modeHint: null, isLegacy: true, a => avatar = a);
                Avatar = avatar;
            }

            string voiceFolder = Path.Combine(CharacterFolder, "voice");
            if (!Directory.Exists(voiceFolder))
            {
                throw new DirectoryNotFoundException(
                    $"No voice folder for character '{Name}': {voiceFolder}. " +
                    "The character was never given a voice, or its creation failed part-way.");
            }

            // Voice.LoadAsync corrects the kind (and the text) from the engine
            // manifest when the folder turns out to hold a clone.
            var fallback = new VoiceMeta
            {
                id = Id,
                kind = VoiceKind.Designed,
                sampleText = _legacyFile?.intro,
                gender = _legacyFile?.gender ?? default,
                pitch = _legacyFile?.pitch ?? default,
                speed = _legacyFile?.speed ?? default,
                instruct = _legacyFile?.voiceInstruct,
            };
            Voice voice = null;
            yield return TaskYield.Wait(Voice.LoadAsync(voiceFolder, Id, isLegacy: true, fallbackMeta: fallback),
                v => voice = v, $"Character.LoadVoice {voiceFolder}");
            Voice = voice;

            // Legacy files did not persist the rate; apply the creation-time rule.
            SpeechSampleRate = Avatar != null && Avatar.CanAnimate ? 16000 : 0;
        }

        /// <summary>
        /// Get the full path to a character by ID: the 2.0 <c>characters/</c>
        /// folder first, then the legacy <c>.bundle</c> and plain folders.
        /// </summary>
        /// <returns>The full path to the character folder/bundle, or null if not found</returns>
        internal static string GetCharacterPath(string characterId)
        {
            if (!LiveTalkStorage.HasRoot || string.IsNullOrEmpty(characterId))
                return null;

            string root = LiveTalkStorage.Root;
            string[] candidates =
            {
                Path.Combine(root, LiveTalkStorage.CharactersFolderName, characterId),
                Path.Combine(root, $"{characterId}.bundle"),
                Path.Combine(root, characterId),
            };
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, CharacterFile.FileName)))
                    return candidate;
            }
            return null;
        }

        #endregion

        #region Speak

        /// <summary>
        /// Generate speech asynchronously using coroutines with optional caching.
        /// Speech audio is automatically cached using the global Cache if enabled.
        /// Provides two callbacks: one when audio is ready, another when animation completes.
        /// Uses queuing to prevent parallel model usage.
        /// </summary>
        /// <param name="text">Text to speak</param>
        /// <param name="expressionIndex">Expression to use, -1 for voice only</param>
        /// <param name="onAudioReady">Callback when audio generation is complete. Called with (FrameStream, AudioClip). 
        /// FrameStream will receive frames as they're generated. Caller can schedule next SpeakAsync here.</param>
        /// <param name="onAnimationComplete">Callback when animation generation is complete. Called with the final FrameStream.
        /// For voice-only mode (expressionIndex=-1), this is called immediately after onAudioReady.</param>
        /// <param name="onError">Callback when an error occurs</param>
        /// <param name="onSpeechChunk">
        /// Optional. Receives speech as it is generated, rather than only when
        /// the line is finished: generation runs slightly faster than playback,
        /// so the first chunk arrives in about a second. Each call carries only
        /// samples not reported before, so appending them in order reproduces
        /// the utterance. Delivered on the main thread.
        ///
        /// Ignored for a cache hit, since there is nothing to stream — the
        /// whole clip is already on disk and arrives via onAudioReady.
        /// </param>
        /// <param name="onStreamStarted">Optional. Fires once when streamed lip-sync begins delivering audio and frames (see <see cref="LiveTalkAPI.StreamLipSync"/>).</param>
        /// <param name="startFrameIndex">
        /// The avatar frame (index into the expression's driving frames) the
        /// first lip-sync frame is rendered onto; later frames walk on from it,
        /// wrapping. A player passes the frame its idle
        /// loop will be on when the line starts so the head does not jump; 0
        /// is the clip's first frame. The <see cref="FrameStream"/> handed to
        /// <paramref name="onAudioReady"/> reports the start actually used in
        /// <see cref="FrameStream.StartFrameIndex"/> — a frames-cache hit
        /// replays from the start it was rendered with, not the one requested.
        /// </param>
        /// <param name="useCache">
        /// Governs the <em>audio</em> cache only. When true (the default), a
        /// matching wav is served instead of synthesising. When false, this
        /// call always generates a new take and overwrites that wav so the
        /// next default call hits it. Lip-sync frames are keyed on that wav's
        /// content hash as well as voice, text, avatar and expression: a new
        /// take misses automatically. <paramref name="expressionIndex"/> -1
        /// never runs MuseTalk. Independent of
        /// <see cref="LiveTalkAPI.SetCacheEnabled"/>, which is a global
        /// on/off rather than a per-utterance skip.
        /// </param>
        /// <returns>Coroutine for audio generation</returns>
        /// <remarks>
        /// Audio is cached on <c>(Voice.Id, text)</c> and frames on
        /// <c>(Voice.Id, text, Avatar.Id, expressionIndex, wavHash)</c>, so a
        /// replaced voice never replays old takes, the same line at two
        /// expressions never shares frames, and a re-rolled wav never replays
        /// mouths generated against the previous take. The start frame is
        /// recorded inside the frames entry rather than keyed on, so a line
        /// has one entry whatever the idle loop was doing when it was first
        /// spoken. Pass <paramref name="useCache"/> false to roll a new wav
        /// of the same line without turning the whole cache off;
        /// <paramref name="expressionIndex"/> -1 still does not run MuseTalk.
        ///
        /// Every failure reaches <paramref name="onError"/>: a faulted speech
        /// synthesis, a lip-sync model that failed to load, a driving-frame
        /// cache that could not be read. Failures after audio was handed to
        /// <paramref name="onAudioReady"/> still fire <paramref name="onError"/>,
        /// and the <see cref="FrameStream"/> is marked finished with its
        /// <see cref="FrameStream.Error"/> set, so a consumer draining it exits.
        /// <paramref name="onAnimationComplete"/> is not called for a failed
        /// animation.
        /// </remarks>
        public IEnumerator SpeakAsync(
            string text, 
            int expressionIndex = 0,
            Action<FrameStream, AudioClip> onAudioReady = null,
            Action<FrameStream> onAnimationComplete = null,
            Action<Exception> onError = null,
            Action<float[], int> onSpeechChunk = null,
            Action<SpeechStream> onStreamStarted = null,
            int startFrameIndex = 0,
            bool useCache = true)
        {
            int start = Math.Max(0, startFrameIndex);
            return SpeakAsync(text, expressionIndex, onAudioReady, onAnimationComplete, onError, onSpeechChunk,
                onStreamStarted, _ => start, useCache);
        }

        /// <summary>
        /// As <see cref="SpeakAsync(string, int, Action{FrameStream, AudioClip}, Action{FrameStream}, Action{Exception}, Action{float[], int}, Action{SpeechStream}, int, bool)"/>,
        /// but the start frame is asked for at the moment lip-sync generation
        /// begins — after synthesis on the batch path, at the first audio chunk
        /// when streaming — rather than when the line is queued. The argument
        /// is the number of frames the line will have, or -1 when not yet
        /// known (streaming). A player whose idle loop keeps running while the
        /// line is prepared can predict where that loop will be when playback
        /// starts far more accurately from here than from the call site.
        /// </summary>
        internal IEnumerator SpeakAsync(
            string text,
            int expressionIndex,
            Action<FrameStream, AudioClip> onAudioReady,
            Action<FrameStream> onAnimationComplete,
            Action<Exception> onError,
            Action<float[], int> onSpeechChunk,
            Action<SpeechStream> onStreamStarted,
            Func<int, int> startFrameIndexProvider,
            bool useCache = true)
        {
            return TaskYield.Guard(
                SpeakCore(text, expressionIndex, onAudioReady, onAnimationComplete, onError, onSpeechChunk, onStreamStarted,
                    startFrameIndexProvider, useCache),
                onError,
                "Character.SpeakAsync");
        }

        /// <summary>
        /// Wraps a synthesis failure handed to the streaming animation so its
        /// guard can tell "the audio producer already reported this" from a
        /// fault of its own, and not report it a second time.
        /// </summary>
        private sealed class UpstreamFailure : Exception
        {
            public UpstreamFailure(Exception inner) : base(inner?.Message ?? "Speech synthesis failed.", inner) { }
        }

        /// <summary>Seconds of audio per TTS codec frame, which is the unit <see cref="SpeechOptions.FirstChunkFrames"/> counts in.</summary>
        private const float TtsCodecFrameSeconds = 0.08f;

        /// <summary>
        /// Audio the first streamed chunk carries past the hold-back, so the
        /// first encoder run already yields ~5 frames (0.2 s at 25 fps) instead
        /// of one. Later chunks double from this size (see
        /// <see cref="SpeechOptions.MaxChunkFrames"/>), so a longer first chunk
        /// also means fewer, larger encoder runs.
        /// </summary>
        private const float FirstChunkFramesLeadSeconds = 0.2f;

        static string ResolveFramesCacheKey(
            string voiceId, string text, string avatarId, int expressionIndex,
            AudioClip clip, string wavPath)
        {
            string audioHash = HashUtils.GenerateAudioContentHash(wavPath)
                ?? AudioFileIO.ContentHash(clip);
            return HashUtils.GenerateFramesCacheKey(voiceId, text, avatarId, expressionIndex, audioHash);
        }

        private IEnumerator SpeakCore(
            string text,
            int expressionIndex,
            Action<FrameStream, AudioClip> onAudioReady,
            Action<FrameStream> onAnimationComplete,
            Action<Exception> onError,
            Action<float[], int> onSpeechChunk,
            Action<SpeechStream> onStreamStarted,
            Func<int, int> startFrameIndexProvider,
            bool useCache)
        {
            var start = System.Diagnostics.Stopwatch.StartNew();
            if (!IsDataLoaded)
            {
                onError?.Invoke(new InvalidOperationException(
                    "Character data not loaded. Use LiveTalkAPI.LoadCharacterAsyncFromId() or CreateCharacter() first, " +
                    "and check that call's onError — a character whose load failed stays unloaded."));
                yield break;
            }

            if (string.IsNullOrEmpty(text))
            {
                onError?.Invoke(new ArgumentException("Text cannot be null or empty."));
                yield break;
            }

            if (expressionIndex != -1)
            {
                if (Avatar == null || !Avatar.CanAnimate)
                {
                    onError?.Invoke(new ArgumentException(
                        $"Character '{Name}' has no animatable avatar; use expressionIndex -1 for voice only."));
                    yield break;
                }
                if (!Avatar.LoadedExpressions.ContainsKey(expressionIndex))
                {
                    onError?.Invoke(new ArgumentException($"Expression index {expressionIndex} not available. Available expressions: {string.Join(", ", Avatar.LoadedExpressions.Keys)}"));
                    yield break;
                }
            }

            var voice = Voice?.Loaded;
            if (voice == null)
            {
                onError?.Invoke(new InvalidOperationException(
                    "Character voice not loaded — the voice folder is missing or the voice failed to design/clone/load; " +
                    "see the earlier error from character creation or load."));
                yield break;
            }

            var liveTalkAPI = LiveTalkAPI.Instance;
            if (liveTalkAPI == null)
            {
                onError?.Invoke(new InvalidOperationException("LiveTalkAPI not initialized. Call LiveTalkAPI.Initialize() first."));
                yield break;
            }

            Logger.LogVerbose($"[Character] {Name} speaking async: \"{text}\" with expression {expressionIndex}");

            AudioClip audioClip = null;
            string cacheKey = null;
            string cachedWavPath = null;
            string framesCacheKey = null;
            bool audioFromCache = false;
            bool framesCached = false;
            string cachedFramesFolder = null;
            int cachedFrameCount = 0;

            // Audio cache. useCache false skips the read so this call
            // synthesises a new take; the write after generation still
            // overwrites the wav. Frames are looked up only after we have
            // that wav — they key on its content hash.
            if (LiveTalkCache.IsEnabled && !string.IsNullOrEmpty(Voice.Id))
            {
                cacheKey = HashUtils.GenerateSpeechCacheKey(Voice.Id, text);
                if (useCache)
                {
                    var (exists, cachedPath) = LiveTalkCache.CheckExists(cacheKey);

                    if (exists)
                    {
                        Logger.LogVerbose($"[Character] Loading cached audio for: {text[..Math.Min(30, text.Length)]}...");
                        var loadTask = AudioFileIO.LoadClipAsync(cachedPath);
                        yield return new WaitUntil(() => loadTask.IsCompleted);

                        // A cache hit that cannot be read is not fatal — the line
                        // is regenerated below — but it is not silent either.
                        if (loadTask.IsFaulted)
                        {
                            Logger.LogWarning($"[Character] Cached audio unreadable, regenerating: {cachedPath}: " +
                                loadTask.Exception?.GetBaseException().Message);
                        }
                        else if (loadTask.Result != null)
                        {
                            audioClip = loadTask.Result;
                            audioFromCache = true;
                            cachedWavPath = cachedPath;
                        }
                    }
                }
                else
                {
                    Logger.LogVerbose($"[Character] Skipping audio cache read for: {text[..Math.Min(30, text.Length)]}...");
                }
            }

            // The animation stream and the streaming state. When lip-sync is
            // streamed, the animation starts before the audio is finished and
            // the stream object exists before synthesis begins.
            FrameStream outputStream = null;
            SpeechStream speechStream = null;
            bool streamed = false;

            // Generate new audio if not cached (with queuing)
            if (audioClip == null)
            {
                // Stream lip-sync only while synthesising a line that will
                // actually be animated from scratch. Cached frames need the
                // wav hash, which exists only after audio is ready, so a
                // generate path cannot be a frames hit. Voice-only has
                // nothing to animate.
                streamed = expressionIndex != -1 && liveTalkAPI.StreamLipSync;

                // Acquire voice queue lock. The lease is released in the
                // finally below on every exit: success, a fault rethrown by
                // the bridge, or the host stopping this coroutine.
                yield return TaskYield.Wait(liveTalkAPI.VoiceQueue.AcquireAsync(), "Character.VoiceQueue.Acquire");

                try
                {
                    var options = new SpeechOptions { SampleRate = SpeechSampleRate };

                    StreamingAudioFeatures features = null;
                    if (streamed)
                    {
                        // Chunks arrive at the requested rate (or the engine's
                        // native one when 0), which is what the stream reports.
                        int streamRate = SpeechSampleRate > 0 ? SpeechSampleRate : QwenTts.NativeSampleRate;
                        outputStream = new FrameStream(0);
                        speechStream = new SpeechStream(outputStream, streamRate);
                        features = liveTalkAPI.MuseTalk.CreateStreamingFeatures(
                            extraContextSeconds: liveTalkAPI.StreamLipSyncContextSeconds);

                        // A consumer that stops mid-line abandons the frames still
                        // being generated for it, so the engine is free for the
                        // next line rather than finishing work nobody will see.
                        var cancelTarget = features;
                        speechStream.CancelGeneration = () =>
                            cancelTarget.Fail(new UpstreamFailure(new OperationCanceledException("Lip-sync generation cancelled by the consumer.")));

                        // Size the first chunk so the mel reference can be captured
                        // from it (warm-up) and it already yields a few frames past
                        // the hold-back, rather than the first frames waiting for
                        // the second, larger chunk.
                        float firstChunkSeconds = Mathf.Max(features.WarmupSeconds,
                            StreamingAudioFeatures.MarginSeconds + features.ExtraContextSeconds + FirstChunkFramesLeadSeconds);
                        int firstChunkFrames = Mathf.CeilToInt(firstChunkSeconds / TtsCodecFrameSeconds);
                        options.FirstChunkFrames = Mathf.Max(options.FirstChunkFrames, firstChunkFrames);

                        // The animation starts now and waits for audio. Guarded
                        // like the batch animation; a synthesis fault is relayed
                        // to it as UpstreamFailure and not reported twice.
                        var expression = Avatar.LoadedExpressions[expressionIndex];
                        var animationStream = outputStream;
                        var extractor = features;
                        liveTalkAPI.Controller.StartCoroutine(TaskYield.Guard(
                            GenerateAnimationWithQueue(liveTalkAPI, expression.Data,
                                start => liveTalkAPI.GenerateTalkingHeadIncremental(expression.Data, extractor, start),
                                startFrameIndexProvider, expectedFrames: -1,
                                animationStream, framesCacheKey: null, onAnimationComplete, extractor),
                            ex =>
                            {
                                animationStream.Fail(ex);
                                if (ex is not UpstreamFailure)
                                    onError?.Invoke(ex);
                            },
                            "Character.GenerateAnimation(streaming)"));
                    }

                    // Progress<T> captures the SynchronizationContext it is
                    // built on. This coroutine runs on the main thread, so the
                    // engine's worker-thread reports arrive back here rather
                    // than on the thread that generated them — which matters,
                    // because a host will want to hand these to an AudioSource.
                    Progress<SpeechChunk> chunkRelay = null;
                    if (onSpeechChunk != null || streamed)
                    {
                        var stream = speechStream;
                        var extractor = features;
                        bool started = false;
                        chunkRelay = new Progress<SpeechChunk>(c =>
                        {
                            if (stream != null && !stream.AudioFinished)
                            {
                                stream.Append(c.Pcm);
                                extractor.Append(c.Pcm, c.SampleRate);
                                if (!started)
                                {
                                    started = true;
                                    onStreamStarted?.Invoke(stream);
                                }
                            }
                            onSpeechChunk?.Invoke(c.Pcm, c.SampleRate);
                        });
                    }

                    var audioTask = chunkRelay == null
                        ? voice.SpeakAsync(text, options)
                        : voice.SpeakStreamAsync(text, chunkRelay, options);

                    // Completion of the streamed audio is tied to the task, not
                    // to this coroutine: a host may stop the coroutine while
                    // synthesis is mid-flight, and the animation holding the
                    // MuseTalk lease must still be told the audio has ended.
                    if (streamed)
                        CompleteStreamWhenSynthesised(audioTask, speechStream, features, onStreamStarted);

                    // A faulted synthesis rethrows here, unwinds through the
                    // finally (releasing the lease) and reaches onError via
                    // the Guard in SpeakAsync.
                    SpeechResult speech = default;
                    yield return TaskYield.Wait(audioTask, r => speech = r, "Character.SpeakAsync (TTS)");

                    // ToAudioClip has to happen here: it is a main-thread API.
                    audioClip = speech.ToAudioClip($"{Name}_speech");
                }
                finally
                {
                    // Release voice queue lock
                    liveTalkAPI.VoiceQueue.Release();
                }
                
                // Save audio to cache. Animated calls wait so the wav is on
                // disk before the frames key hashes it (same bytes as a later
                // cache hit). Voice-only does not need that and stays fire-and-forget.
                if (LiveTalkCache.IsEnabled && !string.IsNullOrEmpty(cacheKey) && audioClip != null)
                {
                    string cachePath = LiveTalkCache.GetFilePath(cacheKey);
                    if (!string.IsNullOrEmpty(cachePath))
                    {
                        var saveTask = AudioFileIO.SaveClipAsync(audioClip, cachePath);
                        if (expressionIndex != -1)
                        {
                            yield return TaskYield.Wait(saveTask, "Character.SaveClip");
                            cachedWavPath = cachePath;
                        }
                        else
                        {
                            cachedWavPath = cachePath;
                            _ = saveTask.ContinueWith(t => 
                            {
                                if (t.IsFaulted)
                                    Logger.LogWarning($"[Character] Failed to save audio to cache: {t.Exception?.InnerException?.Message}");
                                else
                                    Logger.LogVerbose($"[Character] Saved audio to cache: {cacheKey}");
                            });
                        }
                    }
                }
            }

            if (audioClip == null)
            {
                onError?.Invoke(new InvalidOperationException(
                    "Generated audio clip is null — the TTS engine returned no audio for this line; see the earlier error."));
                yield break;
            }

            outputStream ??= new FrameStream(0);
            
            // Voice-only: audio is done. MuseTalk is not run.
            if (expressionIndex == -1)
            {
                onAudioReady?.Invoke(outputStream, audioClip);
                onAnimationComplete?.Invoke(outputStream);
                var stopLocal = start.Elapsed;
                Logger.Log($"[Character] Speaking completed for {Name} in {stopLocal.TotalMilliseconds}ms{(audioFromCache ? " (cached)" : "")}");
                yield break;
            }

            // Streamed: the animation has been running since the first chunk
            // on the stream the host already holds. The finished clip is
            // delivered as always, for hosts that want the whole take.
            // Frames for this wav are not written on the stream path (the
            // hash exists only once the clip is finished); the next batch
            // SpeakAsync of the same take fills the cache.
            if (streamed)
            {
                onAudioReady?.Invoke(outputStream, audioClip);
                Logger.Log($"[Character] Audio ready for {Name} in {start.Elapsed.TotalMilliseconds}ms (streamed; animation in progress)");
                yield break;
            }

            // Frames key includes the wav hash, so a re-rolled take misses
            // mouths generated against the previous wav without deleting them.
            framesCacheKey = ResolveFramesCacheKey(Voice.Id, text, Avatar.Id, expressionIndex, audioClip, cachedWavPath);
            if (!string.IsNullOrEmpty(framesCacheKey))
            {
                (framesCached, cachedFramesFolder, cachedFrameCount) = LiveTalkCache.CheckFramesCacheExists(framesCacheKey);
                framesCached &= cachedFrameCount > 0;
            }

            // Check for cached animation frames
            if (framesCached)
            {
                Logger.LogVerbose($"[Character] Loading {cachedFrameCount} cached animation frames for: {text[..Math.Min(30, text.Length)]}...");
                
                // Load frames from cache into output stream. The frames were
                // rendered from whatever avatar frame the first speaking of
                // this line started on; the consumer reads it off the stream
                // and lines its idle up to it (or cuts) — it cannot be changed.
                outputStream = new FrameStream(cachedFrameCount)
                {
                    StartFrameIndex = LiveTalkCache.ReadFramesStartIndex(cachedFramesFolder)
                };
                
                // Audio ready callback
                onAudioReady?.Invoke(outputStream, audioClip);
                
                // Load frames and call animation complete when done. Guarded
                // so a failed read finishes the stream and reaches onError
                // rather than dying inside Unity's coroutine scheduler.
                var cachedStream = outputStream;
                liveTalkAPI.Controller.StartCoroutine(TaskYield.Guard(
                    LoadFramesFromCacheWithCallback(cachedFramesFolder, cachedFrameCount, cachedStream, onAnimationComplete),
                    ex => { cachedStream.Fail(ex); onError?.Invoke(ex); },
                    "Character.LoadFramesFromCache"));
                
                var stopCached = start.Elapsed;
                Logger.Log($"[Character] Audio ready for {Name} in {stopCached.TotalMilliseconds}ms (audio+frames cached, loading...)");
                yield break;
            }

            // Audio ready - callback immediately, animation will be generated in background
            var expressionData = Avatar.LoadedExpressions[expressionIndex];
            
            // Audio ready callback
            onAudioReady?.Invoke(outputStream, audioClip);
            var stopAudio = start.Elapsed;
            Logger.Log($"[Character] Audio ready for {Name} in {stopAudio.TotalMilliseconds}ms{(audioFromCache ? " (cached)" : "")}, animation pending...");

            // Start animation generation in background with queuing. The
            // Guard is what turns a fault inside the animation — a lip-sync
            // model that failed to load, most often — into a finished stream
            // plus an onError call, instead of a consumer that waits forever
            // and a MuseTalk lease that is never given back.
            var batchStream = outputStream;
            var clip = audioClip;
            // The frame count is a function of the audio length, so the start
            // frame provider can be told it up front.
            int expectedFrames = WhisperModel.FrameCountFor(Mathf.RoundToInt(clip.length * AudioUtils.SAMPLE_RATE));
            liveTalkAPI.Controller.StartCoroutine(TaskYield.Guard(
                GenerateAnimationWithQueue(liveTalkAPI, expressionData.Data,
                    start => liveTalkAPI.GenerateTalkingHeadWithPreloadedData(expressionData.Data, clip, start),
                    startFrameIndexProvider, expectedFrames,
                    batchStream, framesCacheKey, onAnimationComplete, features: null),
                ex => { batchStream.Fail(ex); onError?.Invoke(ex); },
                "Character.GenerateAnimation"));
        }

        /// <summary>
        /// Closes the streamed audio when the synthesis task settles, on the
        /// main thread and after every chunk report queued before it (the
        /// synchronisation context is FIFO, and the last chunk is reported
        /// before the task completes). The engine only flags a chunk final
        /// when frames remain past the last emitted one, so completion is
        /// keyed to the task. Any tail the chunks did not cover — a chunk
        /// boundary landing on the last frame, or one sample of per-chunk
        /// resampling rounding — is filled from the whole result so the
        /// streamed audio and the finished clip have the same length.
        /// </summary>
        private static async void CompleteStreamWhenSynthesised(
            Task<SpeechResult> audioTask,
            SpeechStream speechStream,
            StreamingAudioFeatures features,
            Action<SpeechStream> onStreamStarted)
        {
            try
            {
                var speech = await audioTask;
                if (!speechStream.AudioFinished)
                {
                    int streamed = speechStream.SamplesAvailable;
                    if (speech.Pcm != null && speech.Pcm.Length > streamed)
                    {
                        var tail = new float[speech.Pcm.Length - streamed];
                        Array.Copy(speech.Pcm, streamed, tail, 0, tail.Length);
                        speechStream.Append(tail);
                        features.Append(tail, speech.SampleRate);
                        // A line short enough to arrive in one go never
                        // reported a chunk; the host still gets its stream.
                        if (streamed == 0)
                            onStreamStarted?.Invoke(speechStream);
                    }
                    speechStream.Finish();
                }
                features.Complete();
            }
            catch (Exception ex)
            {
                speechStream.Fail(ex);
                features.Fail(new UpstreamFailure(ex));
            }
        }

        /// <summary>
        /// Generate animation frames with queuing to prevent parallel MuseTalk usage.
        /// <paramref name="startProducer"/> is invoked once the lease is held,
        /// with the avatar frame to start from, and returns the stream the
        /// engine produces into (batch: the whole clip; streaming: incremental
        /// from <paramref name="features"/>, which is disposed here when the
        /// run ends). The start frame comes from
        /// <paramref name="startFrameIndexProvider"/> at that same moment —
        /// as late as it can be decided — and is recorded on
        /// <paramref name="outputStream"/> and in the frames cache entry.
        /// </summary>
        private static IEnumerator GenerateAnimationWithQueue(
            LiveTalkAPI liveTalkAPI,
            AvatarData avatarData,
            Func<int, FrameStream> startProducer,
            Func<int, int> startFrameIndexProvider,
            int expectedFrames,
            FrameStream outputStream,
            string framesCacheKey,
            Action<FrameStream> onAnimationComplete,
            StreamingAudioFeatures features)
        {
            // Acquire MuseTalk queue lock. Released in the finally below on
            // every exit — success, fault, or the coroutine being disposed —
            // so a failed animation can never wedge the next one on Acquire.
            yield return TaskYield.Wait(liveTalkAPI.MuseTalkQueue.AcquireAsync(), "Character.MuseTalkQueue.Acquire");

            string framesFolder = null;
            bool completed = false;
            try
            {
                // Which avatar frame to render the first frame onto, decided
                // now that generation is actually about to start. Wrapped
                // into range so a caller may hand in a raw running counter.
                int startFrameIndex = startFrameIndexProvider?.Invoke(expectedFrames) ?? 0;
                int frameCount = avatarData.Latents.Count;
                if (frameCount > 0)
                    startFrameIndex = ((startFrameIndex % frameCount) + frameCount) % frameCount;
                outputStream.StartFrameIndex = startFrameIndex;

                // Generate talking head using MuseTalk with preloaded data
                var generatedStream = startProducer(startFrameIndex);
                outputStream.TotalExpectedFrames = generatedStream.TotalExpectedFrames;

                // Forward frames from generated stream to output stream
                // If caching is enabled, also save frames
                if (LiveTalkCache.IsEnabled && !string.IsNullOrEmpty(framesCacheKey))
                {
                    framesFolder = LiveTalkCache.CreateFramesCacheFolder(framesCacheKey);
                    if (framesFolder != null)
                        LiveTalkCache.WriteFramesStartIndex(framesFolder, startFrameIndex);
                }

                int frameIndex = 0;
                while (generatedStream.HasMoreFrames)
                {
                    var awaiter = generatedStream.WaitForNext();
                    yield return awaiter;

                    // The streaming producer learns the utterance length late.
                    if (generatedStream.TotalExpectedFrames != outputStream.TotalExpectedFrames)
                        outputStream.TotalExpectedFrames = generatedStream.TotalExpectedFrames;

                    if (awaiter.Texture != null)
                    {
                        // Forward frame to output stream
                        outputStream.Queue.Enqueue(awaiter.Texture);

                        // Cache frame if enabled
                        if (!string.IsNullOrEmpty(framesFolder))
                        {
                            byte[] pngData = awaiter.Texture.EncodeToPNG();
                            int currentIndex = frameIndex;
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    string framePath = Path.Combine(framesFolder, $"frame_{currentIndex:D6}.png");
                                    File.WriteAllBytes(framePath, pngData);
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogWarning($"[Character] Failed to cache frame {currentIndex}: {ex.Message}");
                                }
                            });
                        }

                        frameIndex++;
                    }
                }

                // The producer finishing early because it faulted must not
                // become a short animation that looks complete — and its
                // partial frames folder must not become a cache hit next time.
                if (generatedStream.Error != null)
                {
                    throw new InvalidOperationException(
                        $"Lip-sync animation failed after {frameIndex} frame(s): {generatedStream.Error.Message}",
                        generatedStream.Error);
                }

                outputStream.TotalExpectedFrames = frameIndex;
                outputStream.Finished = true;
                completed = true;
                Logger.LogVerbose($"[Character] Animation generation completed: {frameIndex} frames");
                
                // Animation complete callback
                onAnimationComplete?.Invoke(outputStream);
            }
            finally
            {
                // Consumers draining outputStream exit on every path; the
                // Guard that started this coroutine records the error on it.
                outputStream.Finished = true;

                // A run that faulted or was stopped part-way leaves a short
                // frames folder that the next SpeakAsync would take as a hit.
                if (!completed && framesFolder != null)
                {
                    LiveTalkCache.DeleteFramesCache(framesCacheKey);
                }

                // Streaming: the extractor's Whisper session and buffers.
                features?.Dispose();

                // Release MuseTalk queue lock
                liveTalkAPI.MuseTalkQueue.Release();
            }
        }

        /// <summary>
        /// Load cached animation frames from disk into a FrameStream with completion callback.
        /// </summary>
        private static IEnumerator LoadFramesFromCacheWithCallback(
            string framesFolder, 
            int frameCount, 
            FrameStream outputStream,
            Action<FrameStream> onAnimationComplete)
        {
            yield return LoadFramesFromCache(framesFolder, frameCount, outputStream);
            onAnimationComplete?.Invoke(outputStream);
        }

        /// <summary>
        /// Load cached animation frames from disk into a FrameStream.
        /// </summary>
        /// <param name="framesFolder">Path to the folder containing cached frames</param>
        /// <param name="frameCount">Number of frames to load</param>
        /// <param name="outputStream">The output stream to populate with frames</param>
        private static IEnumerator LoadFramesFromCache(string framesFolder, int frameCount, FrameStream outputStream)
        {
            outputStream.TotalExpectedFrames = frameCount;

            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    string framePath = Path.Combine(framesFolder, $"frame_{i:D6}.png");

                    if (!File.Exists(framePath))
                    {
                        Logger.LogWarning($"[Character] Cached frame not found: {framePath}");
                        continue;
                    }

                    // Load frame from disk. A single unreadable cached frame is
                    // skipped (the fault is observed, logged, and the rest of
                    // the clip still plays); the cache entry is best-effort.
                    var loadTask = Task.Run(() => File.ReadAllBytes(framePath));
                    yield return new WaitUntil(() => loadTask.IsCompleted);

                    if (loadTask.IsFaulted)
                    {
                        Logger.LogWarning($"[Character] Failed to load cached frame {i}: {loadTask.Exception?.GetBaseException().Message}");
                        continue;
                    }

                    // Create texture from bytes
                    var texture = new Texture2D(2, 2);
                    if (texture.LoadImage(loadTask.Result))
                    {
                        texture.name = $"cached_frame_{i}";
                        outputStream.Queue.Enqueue(texture);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                        Logger.LogWarning($"[Character] Failed to decode cached frame {i}");
                    }
                }

                Logger.LogVerbose($"[Character] Loaded {frameCount} frames from cache");
            }
            finally
            {
                outputStream.Finished = true;
            }
        }

        #endregion
    }
}
