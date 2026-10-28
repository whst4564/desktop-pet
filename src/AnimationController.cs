using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexCompanion
{
    public sealed class AnimationPose
    {
        public string Clip { get; internal set; }
        // GUI should take Frame modulo its atlas clip frame count, or use
        // ClipSeconds with the native atlas frame-duration metadata.
        public int Frame { get; internal set; }
        public double ClipSeconds { get; internal set; }
        public double OffsetX { get; internal set; }
        public double OffsetY { get; internal set; }
        public double Rotation { get; internal set; }
        public double ScaleX { get; internal set; }
        public double ScaleY { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Family { get; internal set; }
        public bool AllowGaze { get; internal set; }
        public bool IsSpecial { get; internal set; }
    }

    public sealed class AnimationStep
    {
        public string Clip { get; private set; }
        public double Seconds { get; private set; }
        public AnimationStep(string clip, double seconds)
        {
            if (String.IsNullOrWhiteSpace(clip) || Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds <= 0)
                throw new ArgumentException("Animation step requires a clip and a positive duration.");
            Clip = clip; Seconds = seconds;
        }
    }

    /// <summary>
    /// GUI-independent animation clock. Pass logical distance in the same
    /// coordinate units as the requested 150/200-pixel approach thresholds.
    /// Drag/panel blocks new actions. Interaction > work > idle detail > base.
    /// Gaze is deliberately supplied by the native 16-look atlas in the GUI.
    /// </summary>
    public sealed class AnimationController
    {
        readonly Random random;
        readonly List<ActionDefinition> near = new List<ActionDefinition>();
        readonly List<ActionDefinition> idle = new List<ActionDefinition>();
        readonly List<ActionDefinition> work = new List<ActionDefinition>();
        readonly Queue<int> nearBag = new Queue<int>();
        readonly Queue<int> idleBag = new Queue<int>();
        readonly Queue<int> workBag = new Queue<int>();
        int previousNear = -1, previousIdle = -1, previousWork = -1;
        double now, started, lastNear = -1000, nextIdle, nextNearRepeat;
        bool inside, pendingEntry;
        ActionDefinition active;

        public AnimationPose Current { get; private set; }
        public int InteractionSerial { get; private set; }
        public string[] InteractionNames { get { return near.Select(delegate(ActionDefinition d) { return d.Name; }).ToArray(); } }
        public string[] InteractionClipSequences { get { return near.Select(delegate(ActionDefinition d) { return String.Join(">", d.Steps.Select(delegate(AnimationStep s) { return s.Clip; }).ToArray()); }).ToArray(); } }
        public string[] IdleNames { get { return idle.Select(delegate(ActionDefinition d) { return d.Name; }).ToArray(); } }
        public string[] WorkNames { get { return work.Select(delegate(ActionDefinition d) { return d.Name; }).ToArray(); } }

        public AnimationController(int seed = 0)
        {
            random = seed == 0 ? new Random() : new Random(seed);
            AddDefaults();
            nextIdle = IdleDelay();
            Current = BasePose(false);
        }

        // Additional config actions extend the bag; the eight built-ins remain.
        public void AddInteraction(string displayName, params AnimationStep[] steps)
        {
            if (String.IsNullOrWhiteSpace(displayName) || steps == null || steps.Length == 0 || steps.Any(delegate(AnimationStep s) { return s == null; }))
                throw new ArgumentException("Custom interaction requires a name and at least one step.");
            double duration = steps.Sum(delegate(AnimationStep s) { return s.Seconds; });
            if (duration < 2 || duration > 4) throw new ArgumentException("Interaction duration must be between 2 and 4 seconds.");
            near.Add(new ActionDefinition(displayName, "interaction", steps.ToArray(), 3, 3, 2));
            nearBag.Clear();
        }

        public AnimationPose Tick(double dt, double pointerDistance, bool isDragging, bool isPanelOpen, CompanionWorkState state)
        {
            if (!Double.IsNaN(dt) && !Double.IsInfinity(dt) && dt > 0) now += dt;
            if (Double.IsNaN(pointerDistance) || pointerDistance < 0) pointerDistance = Double.PositiveInfinity;
            if (!inside && pointerDistance < 150) { inside = true; pendingEntry = true; }
            else if (inside && pointerDistance > 200) { inside = false; pendingEntry = false; }

            if (active != null && now - started >= active.Duration)
            {
                if (active.Family != "work") nextIdle = now + IdleDelay();
                active = null;
            }
            bool blocked = isDragging || isPanelOpen;
            if (blocked) { Current = BasePose(true); return Current; }
            if (inside && ((pendingEntry && now - lastNear >= 3) || (!pendingEntry && now >= nextNearRepeat)))
            {
                Start(near[TakeBag(near.Count, nearBag, ref previousNear)]);
                lastNear = now;
                nextNearRepeat=now+15+random.NextDouble()*20;
                pendingEntry = false;
                InteractionSerial++;
            }
            if (active != null && active.Family == "work" && state != CompanionWorkState.Working) active = null;
            if (active != null && active.Family == "idle" && state == CompanionWorkState.Working) active = null;
            if (active == null)
            {
                if (state == CompanionWorkState.Working)
                    Start(work[TakeBag(work.Count, workBag, ref previousWork)]);
                else if (now >= nextIdle)
                    Start(idle[TakeBag(idle.Count, idleBag, ref previousIdle)]);
            }
            Current = active == null ? BasePose(false) : ActionPose(active, now - started);
            return Current;
        }

        void Start(ActionDefinition definition) { active = definition; started = now; }
        // The UI advances quiet animation time at 0.55x: this corresponds to
        // about 33–105 real seconds between idle details.
        double IdleDelay() { return 18 + random.NextDouble() * 40; }

        int TakeBag(int count, Queue<int> bag, ref int previous)
        {
            if (bag.Count == 0)
            {
                int[] values = Enumerable.Range(0, count).ToArray();
                for (int i = values.Length - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1); int temp = values[i]; values[i] = values[j]; values[j] = temp;
                }
                if (count > 1 && values[0] == previous)
                {
                    int j = 1 + random.Next(count - 1); int temp = values[0]; values[0] = values[j]; values[j] = temp;
                }
                foreach (int value in values) bag.Enqueue(value);
            }
            previous = bag.Dequeue();
            return previous;
        }

        AnimationPose BasePose(bool blocked)
        {
            double breath = Math.Sin(now * 1.8);
            return new AnimationPose {
                Clip = "idle", Frame = (int)(now / .12) % 100000,
                ClipSeconds = now, OffsetY = breath * .6,
                Rotation = Math.Sin(now * .7) * .25,
                ScaleX = 1 - breath * .002, ScaleY = 1 + breath * .003,
                DisplayName = blocked ? "陪你看看" : "轻轻呼吸",
                Family = blocked ? "paused" : "base", AllowGaze = !blocked, IsSpecial = false
            };
        }

        AnimationPose ActionPose(ActionDefinition definition, double elapsed)
        {
            double local = Math.Max(0, elapsed);
            AnimationStep selected = definition.Steps[definition.Steps.Length - 1];
            foreach (AnimationStep step in definition.Steps)
            {
                selected = step;
                if (local < step.Seconds) break;
                local -= step.Seconds;
            }
            double p = Math.Max(0, Math.Min(1, elapsed / definition.Duration));
            double envelope = Math.Sin(Math.PI * p);
            double sway = Math.Sin(p * Math.PI * 2);
            double bounce = Math.Sin(p * Math.PI * 4);
            bool quiet = definition.Family == "work";
            return new AnimationPose {
                Clip = selected.Clip, Frame = (int)(Math.Max(0, local) / .12), ClipSeconds = Math.Max(0, local),
                OffsetX = definition.X * sway * envelope,
                OffsetY = -definition.Y * Math.Abs(bounce) * envelope,
                Rotation = definition.Tilt * sway * envelope,
                ScaleX = quiet ? 1 : 1 + .012 * bounce * envelope,
                ScaleY = quiet ? 1 : 1 - .012 * bounce * envelope,
                DisplayName = definition.Name, Family = definition.Family,
                AllowGaze = false, IsSpecial = true
            };
        }

        static AnimationStep S(string clip, double seconds) { return new AnimationStep(clip, seconds); }
        void AddDefaults()
        {
            near.Add(new ActionDefinition("挥手招呼", "interaction", new[] { S("waving", .9), S("idle", .4), S("waving", .9) }, 2, 1, 2));
            near.Add(new ActionDefinition("轻快蹦跳", "interaction", new[] { S("jumping", .84), S("jumping", .84), S("idle", .6) }, 1, 6, 1));
            near.Add(new ActionDefinition("歪头好奇", "interaction", new[] { S("waiting", 1), S("review", 1), S("idle", .6) }, 2, 1, 4));
            near.Add(new ActionDefinition("害羞躲闪", "interaction", new[] { S("running-left", 1.06), S("waiting", 1), S("waving", .9) }, -5, 2, -2));
            near.Add(new ActionDefinition("贴近看看", "interaction", new[] { S("running-right", 1.06), S("review", .9), S("waving", 1) }, 5, 1, 2));
            near.Add(new ActionDefinition("忙里回应", "interaction", new[] { S("running", .85), S("jumping", .84), S("waving", .8) }, 4, 3, 4));
            near.Add(new ActionDefinition("挥手蹦跳", "interaction", new[] { S("waving", .75), S("jumping", .84), S("waving", 1.1) }, 2, 4, -2));
            near.Add(new ActionDefinition("认真回应", "interaction", new[] { S("review", 1.1), S("waiting", .8), S("jumping", .84) }, 1, 2, 1));

            idle.Add(new ActionDefinition("眨眼回神", "idle", new[] { S("waiting", .6), S("idle", 1.6) }, .5, .5, .5));
            idle.Add(new ActionDefinition("挥手回神", "idle", new[] { S("waving", .8), S("waiting", .8), S("idle", 1) }, 1, .5, 1));
            idle.Add(new ActionDefinition("舒展身体", "idle", new[] { S("jumping", 1), S("waving", .8), S("idle", .8) }, .5, 2, .5));
            idle.Add(new ActionDefinition("左边望望", "idle", new[] { S("running-left", 1.06), S("idle", 1.8) }, 0, 0, 0));
            idle.Add(new ActionDefinition("右边望望", "idle", new[] { S("running-right", 1.06), S("idle", 1.8) }, 0, 0, 0));
            idle.Add(new ActionDefinition("低头想想", "idle", new[] { S("review", 1.4), S("waiting", .8), S("idle", .6) }, .5, .5, 1));
            idle.Add(new ActionDefinition("跳起张望", "idle", new[] { S("jumping", .84), S("waiting", 1), S("idle", .6) }, .5, 2, 1));
            idle.Add(new ActionDefinition("短暂忙碌", "idle", new[] { S("idle", .7), S("running", .65), S("idle", 1.1) }, 1.5, 1, 1));

            // Quiet thinking uses one registered head/hand rig. Switching a
            // work group must not alternate unrelated heads or walking poses.
            work.Add(new ActionDefinition("认真思考", "work", new[] { S("running", 7.2) }, 0, 0, 0));
            work.Add(new ActionDefinition("推敲思路", "work", new[] { S("running", 8.4) }, 0, 0, 0));
            work.Add(new ActionDefinition("梳理步骤", "work", new[] { S("running", 9.1) }, 0, 0, 0));
            work.Add(new ActionDefinition("检查逻辑", "work", new[] { S("running", 7.8) }, 0, 0, 0));
            work.Add(new ActionDefinition("整理思路", "work", new[] { S("running", 10.2) }, 0, 0, 0));
            work.Add(new ActionDefinition("专心分析", "work", new[] { S("running", 8.8) }, 0, 0, 0));
        }

        sealed class ActionDefinition
        {
            public readonly string Name, Family;
            public readonly AnimationStep[] Steps;
            public readonly double X, Y, Tilt, Duration;
            public ActionDefinition(string name, string family, AnimationStep[] steps, double x, double y, double tilt)
            {
                Name = name; Family = family; Steps = steps;
                X = x; Y = y; Tilt = tilt;
                Duration = steps.Sum(delegate(AnimationStep s) { return s.Seconds; });
            }
        }
    }
}
