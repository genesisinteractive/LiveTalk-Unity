using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiveTalk.Core
{
    using API;
    using Utils;

    /// <summary>One tick of the expression track for one character.</summary>
    internal readonly struct PoseStep
    {
        /// <summary>Expression whose stored frame this is; -1 when rendered from <see cref="Pose"/>.</summary>
        public readonly int Expression;

        /// <summary>Frame index in that expression, when <see cref="Expression"/> ≥ 0.</summary>
        public readonly int Frame;

        /// <summary>The 63-float driving pose, always set (stored frames carry theirs too).</summary>
        public readonly float[] Pose;

        public bool IsStored => Expression >= 0;

        public PoseStep(int expression, int frame, float[] pose)
        {
            Expression = expression;
            Frame = frame;
            Pose = pose;
        }

        public static PoseStep Stored(int expression, int frame, float[] pose) => new(expression, frame, pose);
        public static PoseStep Rendered(float[] pose) => new(-1, -1, pose);
    }

    /// <summary>An utterance with its clock position fixed.</summary>
    internal sealed class TimedUtterance
    {
        public Utterance Cue;
        public float Start;
        public float Duration;
        public AudioClip Clip;
        public string CachedWavPath;
        public float End => Start + Duration;
        public int StartTick => Mathf.RoundToInt(Start * Performance.Fps);
    }

    /// <summary>An expression cue with its clock position fixed.</summary>
    internal sealed class TimedExpression
    {
        public ExpressionCue Cue;
        public float Start;
        public float End;   // last tick this cue owns (before blend-out completes)
    }

    /// <summary>The resolved scene: absolute times and, per animated character, a pose per tick.</summary>
    internal sealed class ResolvedPerformance
    {
        public float Duration;
        public int TickCount;
        public readonly List<TimedUtterance> Utterances = new();
        public readonly List<TimedExpression> Expressions = new();
        public readonly Dictionary<Character, PoseStep[]> Plans = new();
        public readonly List<CaptionEvent> Captions = new();
    }

    /// <summary>
    /// Turns a <see cref="Performance"/> plus known utterance durations into
    /// absolute times and a per-tick pose plan. Pure: no models, no I/O.
    ///
    /// Plan rules (see LiveTalk-Performance-Redesign.md §3.2):
    /// idle (expression 0) runs forward and wraps for the whole scene; a cue
    /// blends in from the pose on screen the tick before it starts, plays its
    /// clip (to the end, or to the peak then holds), and blends out into
    /// whatever the idle is doing by then. A later cue interrupts an earlier
    /// one. Ticks that land exactly on a stored clip frame with no blend are
    /// <see cref="PoseStep.IsStored"/> and cost nothing to render.
    /// </summary>
    internal static class PerformanceResolver
    {
        const int IdleExpression = 0;
        static readonly int[] EyeKeypoints = { 11, 13, 15, 16 };

        public static ResolvedPerformance Resolve(Performance p, IReadOnlyDictionary<Utterance, float> durations)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            var r = new ResolvedPerformance();

            // ── times ──
            var utterStart = new Dictionary<CueId, float>();
            var utterEnd = new Dictionary<CueId, float>();
            var exprStart = new Dictionary<CueId, float>();
            var exprEnd = new Dictionary<CueId, float>();

            // Iterate until every anchor resolves (anchors may point forward).
            int unresolved = int.MaxValue;
            for (int pass = 0; pass < 64 && unresolved > 0; pass++)
            {
                unresolved = 0;
                foreach (var u in p.Utterances)
                {
                    if (utterStart.ContainsKey(u.Id)) continue;
                    if (!durations.TryGetValue(u, out float dur))
                        throw new InvalidOperationException($"No duration for {u}; synthesise its audio first.");
                    if (TryAnchor(p, u.At, u, utterStart, utterEnd, exprStart, exprEnd, out float t))
                    {
                        utterStart[u.Id] = Mathf.Max(0f, t);
                        utterEnd[u.Id] = utterStart[u.Id] + dur;
                    }
                    else unresolved++;
                }
                foreach (var e in p.Expressions)
                {
                    if (exprStart.ContainsKey(e.Id)) continue;
                    if (TryAnchor(p, e.At, e, utterStart, utterEnd, exprStart, exprEnd, out float t))
                    {
                        exprStart[e.Id] = Mathf.Max(0f, t);
                        exprEnd[e.Id] = exprStart[e.Id] + CueLength(e);
                    }
                    else unresolved++;
                }
            }
            if (unresolved > 0)
                throw new InvalidOperationException("Performance anchors form a cycle or reference a missing cue.");

            // Same character never talks over itself: push the later one.
            var byChar = new Dictionary<Character, List<Utterance>>();
            foreach (var u in p.Utterances)
            {
                if (!byChar.TryGetValue(u.Character, out var list)) byChar[u.Character] = list = new List<Utterance>();
                list.Add(u);
            }
            foreach (var list in byChar.Values)
            {
                list.Sort((a, b) => utterStart[a.Id].CompareTo(utterStart[b.Id]));
                for (int i = 1; i < list.Count; i++)
                {
                    float prevEnd = utterEnd[list[i - 1].Id];
                    if (utterStart[list[i].Id] < prevEnd)
                    {
                        float dur = utterEnd[list[i].Id] - utterStart[list[i].Id];
                        Logger.LogWarning($"[Performance] {list[i]} overlaps the same character's previous line; pushed to {prevEnd:0.00}s.");
                        utterStart[list[i].Id] = prevEnd;
                        utterEnd[list[i].Id] = prevEnd + dur;
                    }
                }
            }

            float last = 0f;
            foreach (var u in p.Utterances)
            {
                var tu = new TimedUtterance { Cue = u, Start = utterStart[u.Id], Duration = utterEnd[u.Id] - utterStart[u.Id] };
                r.Utterances.Add(tu);
                r.Captions.Add(new CaptionEvent(u.Character, u.Caption ?? u.Text, tu.Start, tu.End));
                last = Mathf.Max(last, tu.End);
            }
            foreach (var e in p.Expressions)
            {
                r.Expressions.Add(new TimedExpression { Cue = e, Start = exprStart[e.Id], End = exprEnd[e.Id] });
                last = Mathf.Max(last, exprEnd[e.Id] + e.BlendOut);
            }
            r.Utterances.Sort((a, b) => a.Start.CompareTo(b.Start));
            r.Expressions.Sort((a, b) => a.Start.CompareTo(b.Start));
            r.Captions.Sort((a, b) => a.Start.CompareTo(b.Start));

            r.Duration = last + Mathf.Max(0f, p.Tail);
            r.TickCount = Mathf.Max(1, Mathf.CeilToInt(r.Duration * Performance.Fps));

            // ── pose plans ──
            foreach (var c in p.Characters)
            {
                if (c.Avatar == null || !c.Avatar.CanAnimate) continue;
                r.Plans[c] = BuildPlan(c, r);
            }
            return r;
        }

        static float CueLength(ExpressionCue e)
        {
            var avatar = e.Character.Avatar;
            if (avatar == null || !avatar.LoadedExpressions.TryGetValue(e.Expression, out var data))
                throw new InvalidOperationException($"{e.Character.Name} has no expression {e.Expression} to perform.");
            int frames = data.FrameCount;
            float clip = frames / Performance.Fps;
            if (e.Mode == ExpressionMode.HoldAtPeak)
                return Mathf.Clamp01(e.Peak01) * clip + Mathf.Max(0f, e.HoldSeconds);
            return clip;
        }

        static bool TryAnchor(
            Performance p, Anchor a, object self,
            Dictionary<CueId, float> uStart, Dictionary<CueId, float> uEnd,
            Dictionary<CueId, float> eStart, Dictionary<CueId, float> eEnd,
            out float t)
        {
            t = 0f;
            switch (a.Type)
            {
                case Anchor.Kind.Absolute:
                    t = a.Offset;
                    return true;
                case Anchor.Kind.AfterPrevious:
                {
                    Utterance prev = self is Utterance u ? p.PreviousUtterance(u) : p.PreviousUtterance((ExpressionCue)self);
                    if (prev == null) { t = Mathf.Max(0f, a.Offset); return true; }
                    if (!uEnd.TryGetValue(prev.Id, out float end)) return false;
                    t = end + a.Offset;
                    return true;
                }
                case Anchor.Kind.StartOf:
                    if (uStart.TryGetValue(a.Ref, out float us)) { t = us + a.Offset; return true; }
                    if (eStart.TryGetValue(a.Ref, out float es)) { t = es + a.Offset; return true; }
                    if (p.Find(a.Ref) == null) throw new InvalidOperationException($"Anchor references unknown {a.Ref}.");
                    return false;
                case Anchor.Kind.EndOf:
                    if (uEnd.TryGetValue(a.Ref, out float ue)) { t = ue + a.Offset; return true; }
                    if (eEnd.TryGetValue(a.Ref, out float ee)) { t = ee + a.Offset; return true; }
                    if (p.Find(a.Ref) == null) throw new InvalidOperationException($"Anchor references unknown {a.Ref}.");
                    return false;
            }
            return false;
        }

        // ───────────────────────── pose plan ─────────────────────────

        static PoseStep[] BuildPlan(Character c, ResolvedPerformance r)
        {
            var avatar = c.Avatar;
            if (!avatar.LoadedExpressions.TryGetValue(IdleExpression, out var idle) || idle.Poses.Length == 0)
                throw new InvalidOperationException(
                    $"{c.Name}'s avatar has no poses for expression {IdleExpression} (rebuild the avatar; motion.bin is v{Avatar.Version}).");

            int n = r.TickCount;
            var plan = new PoseStep[n];
            var cues = new List<TimedExpression>();
            foreach (var te in r.Expressions) if (te.Cue.Character == c) cues.Add(te);

            // Idle underneath everything: forward, wrapping.
            for (int k = 0; k < n; k++)
                plan[k] = PoseStep.Stored(IdleExpression, k % idle.Poses.Length, idle.Poses[k % idle.Poses.Length]);

            // Cues in start order; a later one takes over the ticks it covers.
            // Each cue: blend-in from the pose already planned at its first
            // tick − 1, clip frames (or hold), then blend-out into what the
            // plan holds after it (idle, or the next cue's blend-in).
            for (int ci = 0; ci < cues.Count; ci++)
            {
                var te = cues[ci];
                var cue = te.Cue;
                if (!avatar.LoadedExpressions.TryGetValue(cue.Expression, out var expr) || expr.Poses.Length == 0)
                    throw new InvalidOperationException($"{c.Name}'s avatar has no poses for expression {cue.Expression}.");

                int startTick = Mathf.Clamp(Mathf.RoundToInt(te.Start * Performance.Fps), 0, n - 1);
                int clipFrames = expr.Poses.Length;
                int peakFrame = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(cue.Peak01) * (clipFrames - 1)), 0, clipFrames - 1);
                int holdTicks = cue.Mode == ExpressionMode.HoldAtPeak ? Mathf.RoundToInt(Mathf.Max(0f, cue.HoldSeconds) * Performance.Fps) : 0;
                int playFrames = cue.Mode == ExpressionMode.HoldAtPeak ? peakFrame + 1 : clipFrames;
                int ownedTicks = playFrames + holdTicks;

                // A later cue that starts inside this one truncates it.
                int hardEnd = n;
                if (ci + 1 < cues.Count)
                    hardEnd = Mathf.Clamp(Mathf.RoundToInt(cues[ci + 1].Start * Performance.Fps), startTick, n);
                int endTick = Mathf.Min(startTick + ownedTicks, hardEnd); // exclusive

                float[] from = startTick > 0 ? plan[startTick - 1].Pose : plan[0].Pose;
                int blendIn = Mathf.RoundToInt(Mathf.Max(0f, cue.BlendIn) * Performance.Fps);
                int blendOut = Mathf.RoundToInt(Mathf.Max(0f, cue.BlendOut) * Performance.Fps);

                // Body of the cue.
                for (int k = startTick; k < endTick; k++)
                {
                    int local = k - startTick;
                    float[] target;
                    int storedFrame = -1;
                    if (local < playFrames)
                    {
                        storedFrame = local;
                        target = expr.Poses[local];
                    }
                    else
                    {
                        // Hold: peak pose + micro × idle sway, eyes at 1.
                        int j = k % idle.Poses.Length;
                        target = HoldPose(expr.Poses[peakFrame], idle.Poses[j], idle.Poses[0], cue.Micro01);
                    }

                    if (blendIn > 0 && local < blendIn)
                    {
                        float t = Smooth((local + 1) / (float)blendIn);
                        plan[k] = PoseStep.Rendered(Lerp(from, target, t));
                    }
                    else if (storedFrame >= 0)
                    {
                        plan[k] = PoseStep.Stored(cue.Expression, storedFrame, target);
                    }
                    else
                    {
                        plan[k] = PoseStep.Rendered(target);
                    }
                }

                // Blend-out into whatever follows (idle already there, or the
                // next cue — which will itself blend in from what we leave).
                if (endTick < n && blendOut > 0 && endTick > startTick)
                {
                    float[] lastPose = plan[endTick - 1].Pose;
                    int outEnd = Mathf.Min(n, endTick + blendOut);
                    if (ci + 1 < cues.Count)
                        outEnd = Mathf.Min(outEnd, Mathf.RoundToInt(cues[ci + 1].Start * Performance.Fps));
                    for (int k = endTick; k < outEnd; k++)
                    {
                        float t = Smooth((k - endTick + 1) / (float)blendOut);
                        plan[k] = PoseStep.Rendered(Lerp(lastPose, plan[k].Pose, t));
                    }
                }
            }
            return plan;
        }

        static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        static float[] Lerp(float[] a, float[] b, float t)
        {
            var o = new float[a.Length];
            for (int i = 0; i < a.Length; i++) o[i] = a[i] + (b[i] - a[i]) * t;
            return o;
        }

        /// <summary>peak + gain·(idle − idleRest), eye keypoints at gain 1.</summary>
        internal static float[] HoldPose(float[] peak, float[] idle, float[] idleRest, float micro)
        {
            var o = new float[peak.Length];
            micro = Mathf.Clamp01(micro);
            for (int kp = 0; kp < peak.Length / 3; kp++)
            {
                float g = Array.IndexOf(EyeKeypoints, kp) >= 0 ? 1f : micro;
                for (int d = 0; d < 3; d++)
                {
                    int i = kp * 3 + d;
                    o[i] = peak[i] + g * (idle[i] - idleRest[i]);
                }
            }
            return o;
        }

        /// <summary>Stable key for a rendered pose: 4-decimal quantised floats hashed with the avatar id.</summary>
        internal static string PoseKey(string avatarId, float[] pose)
        {
            var sb = new System.Text.StringBuilder(avatarId).Append(":pose_v1:");
            for (int i = 0; i < pose.Length; i++)
                sb.Append(Mathf.RoundToInt(pose[i] * 10000f)).Append(',');
            return HashUtils.GenerateTextHash(sb.ToString());
        }
    }
}
