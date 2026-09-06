using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace LiveTalk.Core
{
    using API;
    using Utils;

    /// <summary>
    /// Renders a <see cref="Performance"/> to disk once. Everything a
    /// <see cref="PerformancePlayer"/> needs is in the manifest it writes:
    /// per animated character a frame path per tick, every utterance's wav
    /// and clock position, and the captions.
    ///
    /// Frames are never duplicated. A tick on a stored driving frame points
    /// at the avatar's own PNG; a blend / hold tick points at the pose cache
    /// (<c>pose_&lt;key&gt;.png</c>, rendered once ever per avatar + pose); a
    /// lip-synced tick points at the mouth cache
    /// (<c>hash(voice, text, avatar, planSlice)/_frames</c>), also once ever
    /// for that slice. A new performance fingerprint that does not change a
    /// line's base faces reuses those mouths.
    ///
    /// Order: audio for every utterance (cached by voice + text) → resolve →
    /// render missing poses → MuseTalk only for lip-synced slices whose
    /// mouth cache misses → manifest. The manifest's presence marks the
    /// folder complete; a later render with the same fingerprint returns it.
    /// </summary>
    internal static class PerformanceRenderer
    {
        public const string ManifestFileName = "performance.json";
        const string PosePrefix = "pose_";

        public static string FolderFor(string fingerprint) =>
            System.IO.Path.Combine(LiveTalkCache.Path, "perf_" + fingerprint);

        public static IEnumerator RenderAsync(
            LiveTalkAPI api,
            Performance performance,
            Action<string, float> onProgress,
            Action<RenderedPerformance> onComplete)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (performance == null) throw new ArgumentNullException(nameof(performance));
            if (!LiveTalkCache.IsInitialized)
                throw new InvalidOperationException("LiveTalk cache is not initialised; performances render into it.");

            string fingerprint = performance.Fingerprint();
            string folder = FolderFor(fingerprint);
            string manifestPath = System.IO.Path.Combine(folder, ManifestFileName);

            if (File.Exists(manifestPath))
            {
                var cached = RenderedPerformance.Load(manifestPath, performance);
                if (cached != null)
                {
                    Logger.Log($"[Performance] Reusing rendered performance {fingerprint} ({cached.TickCount} ticks).");
                    onComplete?.Invoke(cached);
                    yield break;
                }
                Logger.LogWarning($"[Performance] {manifestPath} unreadable; re-rendering.");
                LiveTalkStorage.DeleteFolder(folder);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Directory.CreateDirectory(folder);
            bool committed = false;
            try
            {
                // ── 1. audio ──
                var durations = new Dictionary<Utterance, float>();
                var clips = new Dictionary<Utterance, (AudioClip clip, string wav)>();
                int ui = 0;
                foreach (var u in performance.Utterances)
                {
                    onProgress?.Invoke($"Audio {++ui}/{performance.Utterances.Count}: {u.Character.Name}", 0.05f * ui / performance.Utterances.Count);
                    AudioClip clip = null;
                    Exception fail = null;
                    yield return u.Character.SpeakAsync(
                        u.Text, expressionIndex: -1,
                        onAudioReady: (_, c) => clip = c,
                        onAnimationComplete: _ => { },
                        onError: ex => fail = ex);
                    if (fail != null) throw fail;
                    if (clip == null) throw new InvalidOperationException($"No audio for {u}.");

                    string wav = LiveTalkCache.GetFilePath(HashUtils.GenerateSpeechCacheKey(u.Character.Voice.Id, u.Text));
                    if (wav == null || !File.Exists(wav))
                    {
                        // Cache disabled or the engine skipped the write: keep our own copy.
                        wav = System.IO.Path.Combine(folder, $"utt_{u.Id.Value}.wav");
                        yield return TaskYield.Wait(AudioFileIO.SaveClipAsync(clip, wav), $"Performance.SaveClip {u.Id}");
                    }
                    durations[u] = clip.length;
                    clips[u] = (clip, wav);
                }

                // ── 2. resolve ──
                var resolved = PerformanceResolver.Resolve(performance, durations);
                foreach (var tu in resolved.Utterances)
                {
                    tu.Clip = clips[tu.Cue].clip;
                    tu.CachedWavPath = clips[tu.Cue].wav;
                }
                Logger.Log($"[Performance] {fingerprint}: {resolved.TickCount} ticks ({resolved.Duration:0.0}s), " +
                           $"{resolved.Utterances.Count} utterances, {resolved.Expressions.Count} expression cues, " +
                           $"{resolved.Plans.Count} animated character(s).");

                var manifest = new PerformanceManifest
                {
                    fingerprint = fingerprint,
                    fps = Performance.Fps,
                    tickCount = resolved.TickCount,
                    duration = resolved.Duration,
                };
                foreach (var tu in resolved.Utterances)
                {
                    manifest.utterances.Add(new ManifestUtterance
                    {
                        cueId = tu.Cue.Id.Value,
                        characterId = tu.Cue.Character.Id,
                        characterName = tu.Cue.Character.Name,
                        start = tu.Start,
                        duration = tu.Duration,
                        wav = tu.CachedWavPath,
                        caption = tu.Cue.Caption ?? tu.Cue.Text,
                        lipSync = tu.Cue.LipSync,
                    });
                }

                // ── 3 + 4. per animated character: poses, then lip-sync ──
                foreach (var kv in resolved.Plans)
                {
                    var character = kv.Key;
                    var plan = kv.Value;
                    var avatar = character.Avatar;
                    var frames = new string[plan.Length];
                    var poseFiles = new Dictionary<string, string>(); // pose key -> png

                    // Base frames: stored → avatar png; rendered → pose cache.
                    var toRender = new List<(string key, float[] pose)>();
                    var seen = new HashSet<string>();
                    for (int k = 0; k < plan.Length; k++)
                    {
                        var step = plan[k];
                        if (step.IsStored)
                        {
                            frames[k] = System.IO.Path.Combine(avatar.ExpressionFolder(step.Expression), $"{step.Frame:D5}.png");
                            continue;
                        }
                        string key = PerformanceResolver.PoseKey(avatar.Id, step.Pose);
                        string png = LiveTalkCache.GetFilePath(PosePrefix + key, ".png");
                        frames[k] = png;
                        poseFiles[key] = png;
                        if (!File.Exists(png) && seen.Add(key))
                            toRender.Add((key, step.Pose));
                    }

                    int stored = plan.Count(s => s.IsStored);
                    Logger.Log($"[Performance] {character.Name}: {stored} stored ticks, {plan.Length - stored} rendered ticks " +
                               $"({toRender.Count} new poses to render, {poseFiles.Count - toRender.Count} cached).");

                    if (toRender.Count > 0)
                    {
                        var poses = toRender.Select(t => t.pose).ToList();
                        int done = 0;
                        Exception renderFail = null;
                        yield return api.RenderPosesAsync(
                            avatar.Image, poses,
                            onFrame: (i, tex) =>
                            {
                                string png = poseFiles[toRender[i].key];
                                File.WriteAllBytes(png, tex.EncodeToPNG());
                                Release(tex);
                                done++;
                                onProgress?.Invoke($"Pose {done}/{toRender.Count} ({character.Name})", 0.1f + 0.4f * done / toRender.Count);
                            },
                            onError: ex => renderFail = ex);
                        if (renderFail != null) throw renderFail;
                    }

                    // Lip-sync: one MuseTalk pass per utterance of this
                    // character over the plan slice it covers, unless that
                    // slice is already in the mouth cache.
                    string composedFolder = System.IO.Path.Combine(folder, "frames_" + SafeName(character.Id));
                    Directory.CreateDirectory(composedFolder);
                    var mine = resolved.Utterances.Where(t => t.Cue.Character == character && t.Cue.LipSync).ToList();
                    int mouthHits = 0, mouthMiss = 0;
                    for (int m = 0; m < mine.Count; m++)
                    {
                        var tu = mine[m];
                        int k0 = Mathf.Clamp(tu.StartTick, 0, plan.Length - 1);
                        int count = Mathf.Min(Mathf.FloorToInt(tu.Duration * Performance.Fps), plan.Length - k0);
                        if (count <= 0) continue;

                        string mouthKey = MouthCacheKey(tu, avatar, plan, k0, count);
                        if (TryApplyMouthCache(mouthKey, count, frames, k0))
                        {
                            mouthHits++;
                            onProgress?.Invoke($"Lip-sync {m + 1}/{mine.Count} (cached): {tu.Cue.Text}",
                                0.5f + 0.45f * m / Mathf.Max(1, mine.Count));
                            continue;
                        }

                        mouthMiss++;
                        onProgress?.Invoke($"Lip-sync {m + 1}/{mine.Count}: {tu.Cue.Text}",
                            0.5f + 0.45f * m / Mathf.Max(1, mine.Count));
                        yield return RenderMouthSliceAsync(api, avatar, plan, frames, tu, k0, count, mouthKey, composedFolder);
                    }
                    Logger.Log($"[Performance] {character.Name}: lip-sync {mouthHits} cache hit, {mouthMiss} rendered.");

                    manifest.characters.Add(new ManifestCharacter
                    {
                        characterId = character.Id,
                        characterName = character.Name,
                        frames = frames,
                    });
                }

                foreach (var c in resolved.Captions)
                    manifest.captions.Add(new ManifestCaption { characterId = c.Character?.Id, text = c.Text, start = c.Start, end = c.End });

                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
                committed = true;
                Logger.Log($"[Performance] Rendered {fingerprint} in {sw.Elapsed.TotalSeconds:0.0}s → {folder}");
                onProgress?.Invoke("Done", 1f);
                onComplete?.Invoke(RenderedPerformance.From(manifest, folder, performance));
            }
            finally
            {
                if (!committed)
                    LiveTalkStorage.DeleteFolder(folder);
            }
        }

        static string MouthCacheKey(TimedUtterance tu, Avatar avatar, PoseStep[] plan, int k0, int count)
        {
            if (tu?.Cue?.Character?.Voice == null || avatar == null) return null;
            string slice = PlanSliceHash(avatar.Id, plan, k0, count);
            long wavBytes = 0;
            if (!string.IsNullOrEmpty(tu.CachedWavPath) && File.Exists(tu.CachedWavPath))
                wavBytes = new FileInfo(tu.CachedWavPath).Length;
            return HashUtils.GeneratePerformanceMouthKey(
                tu.Cue.Character.Voice.Id, tu.Cue.Text, avatar.Id, slice, wavBytes);
        }

        /// <summary>
        /// Identity of the base-face sequence MuseTalk will inpaint. Stored
        /// ticks are expression+frame; rendered ticks are the pose-cache key.
        /// Clock position is not part of it — only which faces, in order.
        /// </summary>
        static string PlanSliceHash(string avatarId, PoseStep[] plan, int k0, int count)
        {
            var sb = new System.Text.StringBuilder(64 + count * 12);
            sb.Append("slice_v1;").Append(count).Append(';');
            for (int i = 0; i < count; i++)
            {
                var step = plan[k0 + i];
                if (step.IsStored)
                    sb.Append('S').Append(step.Expression).Append(':').Append(step.Frame).Append(';');
                else
                    sb.Append('R').Append(PerformanceResolver.PoseKey(avatarId, step.Pose)).Append(';');
            }
            return HashUtils.GenerateTextHash(sb.ToString());
        }

        static bool TryApplyMouthCache(string mouthKey, int count, string[] frames, int k0)
        {
            if (string.IsNullOrEmpty(mouthKey) || count <= 0) return false;
            for (int i = 0; i < count; i++)
            {
                string png = LiveTalkCache.GetFramePath(mouthKey, i);
                if (string.IsNullOrEmpty(png) || !File.Exists(png)) return false;
            }
            for (int i = 0; i < count; i++)
                frames[k0 + i] = LiveTalkCache.GetFramePath(mouthKey, i);
            return true;
        }

        static IEnumerator RenderMouthSliceAsync(
            LiveTalkAPI api, Avatar avatar, PoseStep[] plan, string[] frames,
            TimedUtterance tu, int k0, int count, string mouthKey, string composedFolder)
        {
            if (!string.IsNullOrEmpty(mouthKey))
                LiveTalkCache.CreateFramesCacheFolder(mouthKey);

            AvatarData slice = null;
            yield return BuildSliceAsync(api, avatar, plan, frames, k0, count, d => slice = d);

            yield return TaskYield.Wait(api.MuseTalkQueue.AcquireAsync(), "Performance.MuseTalkQueue.Acquire");
            bool ok = false;
            try
            {
                var stream = api.GenerateTalkingHeadWithPreloadedData(slice, tu.Clip, 0);
                int i = 0;
                while (stream.HasMoreFrames)
                {
                    var awaiter = stream.WaitForNext();
                    yield return awaiter;
                    var tex = awaiter.Texture;
                    if (tex == null) continue;
                    if (i < count)
                    {
                        string png = MouthWritePath(mouthKey, i, composedFolder, k0);
                        File.WriteAllBytes(png, tex.EncodeToPNG());
                        frames[k0 + i] = png;
                    }
                    Release(tex);
                    i++;
                }
                if (stream.Error != null) throw stream.Error;
                if (i < count)
                    Logger.LogWarning($"[Performance] {tu.Cue}: lip-sync produced {i} frames for {count} ticks; the tail keeps the base face.");
                ok = true;
            }
            finally
            {
                api.MuseTalkQueue.Release();
                if (!ok && !string.IsNullOrEmpty(mouthKey))
                    LiveTalkCache.DeleteFramesCache(mouthKey);
            }
        }

        static string MouthWritePath(string mouthKey, int i, string composedFolder, int k0)
        {
            string cached = LiveTalkCache.GetFramePath(mouthKey, i);
            if (!string.IsNullOrEmpty(cached)) return cached;
            return System.IO.Path.Combine(composedFolder, $"{k0 + i:D6}.png");
        }

        /// <summary>
        /// The MuseTalk input for ticks [k0, k0+count): latent + face per tick,
        /// stored ones from the expression data, rendered ones by running the
        /// avatar prep on their PNGs. Indexing is tick-relative so
        /// <see cref="AvatarData.AvatarFrameIndex"/> with start 0 is identity.
        /// </summary>
        static IEnumerator BuildSliceAsync(
            LiveTalkAPI api, Avatar avatar, PoseStep[] plan, string[] frames, int k0, int count,
            Action<AvatarData> onComplete)
        {
            var slice = new AvatarData();
            var renderedPaths = new List<string>();
            var renderedAt = new List<int>();
            for (int i = 0; i < count; i++)
            {
                var step = plan[k0 + i];
                if (step.IsStored)
                {
                    var data = avatar.LoadedExpressions[step.Expression].Data;
                    slice.Latents.Add(data.Latents[step.Frame]);
                    slice.FaceRegions.Add(data.FaceRegions[step.Frame]);
                }
                else
                {
                    slice.Latents.Add(null);
                    slice.FaceRegions.Add(null);
                    renderedPaths.Add(frames[k0 + i]);
                    renderedAt.Add(i);
                }
            }

            if (renderedPaths.Count > 0)
            {
                AvatarData prepped = null;
                yield return TaskYield.Wait(api.MuseTalk.ProcessAvatarImages(renderedPaths), r => prepped = r,
                    "Performance.ProcessAvatarImages(rendered)");
                if (prepped == null || prepped.Latents.Count != renderedPaths.Count)
                    throw new InvalidOperationException(
                        $"MuseTalk prep returned {prepped?.Latents.Count ?? 0} latents for {renderedPaths.Count} rendered frames.");
                for (int r = 0; r < renderedAt.Count; r++)
                {
                    slice.Latents[renderedAt[r]] = prepped.Latents[r];
                    slice.FaceRegions[renderedAt[r]] = prepped.FaceRegions[r];
                }
            }
            onComplete(slice);
        }

        static void Release(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        static string SafeName(string s)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars();
            return new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
        }
    }

    // ───────────────────────── manifest ─────────────────────────

    [Serializable]
    internal class PerformanceManifest
    {
        public string version = "1";
        public string fingerprint;
        public float fps;
        public int tickCount;
        public float duration;
        public List<ManifestCharacter> characters = new();
        public List<ManifestUtterance> utterances = new();
        public List<ManifestCaption> captions = new();
    }

    [Serializable]
    internal class ManifestCharacter
    {
        public string characterId;
        public string characterName;
        public string[] frames;
    }

    [Serializable]
    internal class ManifestUtterance
    {
        public int cueId;
        public string characterId;
        public string characterName;
        public float start;
        public float duration;
        public string wav;
        public string caption;
        public bool lipSync;
    }

    [Serializable]
    internal class ManifestCaption
    {
        public string characterId;
        public string text;
        public float start;
        public float end;
    }
}
