using MoreUseResources.Core;
using Xunit;

namespace MoreUseResources.Tests
{
    public class PhaseResolverTests
    {
        private static RawGameSignals Playing() => new RawGameSignals
        {
            PlayDataLoaded = true, Stage = ProgramStage.Playing, Focused = true,
        };

        [Fact]
        public void Startup_until_play_data_is_loaded()
        {
            var s = new RawGameSignals { Focused = true, LongEventActive = true };
            Assert.Equal(GamePhase.Startup, PhaseResolver.Resolve(s, 0).Phase);
        }

        [Fact]
        public void Main_menu_when_loaded_and_in_entry()
        {
            var s = new RawGameSignals { PlayDataLoaded = true, Stage = ProgramStage.Entry, Focused = true };
            Assert.Equal(GamePhase.MainMenu, PhaseResolver.Resolve(s, 0).Phase);
        }

        [Fact]
        public void Long_event_or_map_init_is_loading()
        {
            var s = Playing(); s.LongEventActive = true;
            Assert.Equal(GamePhase.Loading, PhaseResolver.Resolve(s, 0).Phase);
            var m = new RawGameSignals { PlayDataLoaded = true, Stage = ProgramStage.MapInitializing, Focused = true };
            Assert.Equal(GamePhase.Loading, PhaseResolver.Resolve(m, 0).Phase);
        }

        [Fact]
        public void Play_speeds_and_pause()
        {
            var s = Playing();
            Assert.Equal(GamePhase.PlayingNormal, PhaseResolver.Resolve(s, 0).Phase);
            s.FastSpeed = true;
            Assert.Equal(GamePhase.PlayingFast, PhaseResolver.Resolve(s, 0).Phase);
            s.Paused = true;
            Assert.Equal(GamePhase.Paused, PhaseResolver.Resolve(s, 0).Phase);
        }

        [Fact]
        public void Saving_beats_everything_except_quitting()
        {
            var s = Playing(); s.Paused = true; s.LongEventActive = true; s.Saving = true;
            Assert.Equal(GamePhase.Saving, PhaseResolver.Resolve(s, 0).Phase);
            s.Quitting = true;
            Assert.Equal(GamePhase.Quitting, PhaseResolver.Resolve(s, 0).Phase);
        }

        [Fact]
        public void Modifiers()
        {
            var s = Playing(); s.WorldView = true; s.Focused = false; s.SecondsSinceInput = 61;
            PhaseSnapshot p = PhaseResolver.Resolve(s, 60);
            Assert.True(p.Has(PhaseModifiers.WorldView));
            Assert.True(p.Has(PhaseModifiers.Unfocused));
            Assert.True(p.Has(PhaseModifiers.Idle));
            Assert.False(PhaseResolver.Resolve(s, 0).Has(PhaseModifiers.Idle));
            var menu = new RawGameSignals { PlayDataLoaded = true, Focused = true, WorldView = true };
            Assert.False(PhaseResolver.Resolve(menu, 0).Has(PhaseModifiers.WorldView));
        }
    }

    public class ActivationPlannerTests
    {
        private static PhaseSnapshot P(GamePhase p, PhaseModifiers m = PhaseModifiers.None) => new PhaseSnapshot(p, m);

        [Fact]
        public void Defaults_run_in_play_but_not_while_saving()
        {
            var s = new ResourceSettings();
            ActivationPlan plan = ActivationPlanner.Plan(s, P(GamePhase.PlayingNormal), ManualMode.Auto);
            Assert.True(plan.Cpu.Active); Assert.True(plan.Ram.Active); Assert.True(plan.Gpu.Active);
            Assert.Equal(0.5f, plan.Cpu.Intensity, 3);
            plan = ActivationPlanner.Plan(s, P(GamePhase.Saving), ManualMode.Auto);
            Assert.False(plan.Cpu.Active); Assert.False(plan.Ram.Active); Assert.False(plan.Gpu.Active);
            Assert.Equal(ActivationReason.PhaseOff, plan.Cpu.Reason);
        }

        [Fact]
        public void Each_phase_and_resource_is_independent()
        {
            var s = new ResourceSettings();
            s.CpuPhases[(int)GamePhase.Paused] = false;
            s.GpuPhases[(int)GamePhase.PlayingFast] = false;
            Assert.False(ActivationPlanner.Plan(s, P(GamePhase.Paused), ManualMode.Auto).Cpu.Active);
            Assert.True(ActivationPlanner.Plan(s, P(GamePhase.Paused), ManualMode.Auto).Ram.Active);
            Assert.False(ActivationPlanner.Plan(s, P(GamePhase.PlayingFast), ManualMode.Auto).Gpu.Active);
            Assert.True(ActivationPlanner.Plan(s, P(GamePhase.PlayingFast), ManualMode.Auto).Cpu.Active);
        }

        [Fact]
        public void Master_and_resource_switches()
        {
            var s = new ResourceSettings { RamEnabled = false };
            ActivationPlan plan = ActivationPlanner.Plan(s, P(GamePhase.Paused), ManualMode.Auto);
            Assert.Equal(ActivationReason.ResourceOff, plan.Ram.Reason);
            Assert.True(plan.Cpu.Active);
            s.Enabled = false;
            plan = ActivationPlanner.Plan(s, P(GamePhase.Paused), ManualMode.ForceRun);
            Assert.Equal(ActivationReason.MasterOff, plan.Cpu.Reason);
        }

        [Fact]
        public void Quitting_cannot_be_overridden()
        {
            var s = new ResourceSettings();
            ActivationPlan plan = ActivationPlanner.Plan(s, P(GamePhase.Quitting), ManualMode.ForceRun);
            Assert.False(plan.Cpu.Active); Assert.False(plan.Ram.Active); Assert.False(plan.Gpu.Active);
            Assert.Equal(ActivationReason.Quitting, plan.Gpu.Reason);
        }

        [Fact]
        public void Manual_modes()
        {
            var s = new ResourceSettings();
            s.CpuPhases[(int)GamePhase.PlayingNormal] = false;
            Assert.True(ActivationPlanner.Plan(s, P(GamePhase.PlayingNormal), ManualMode.ForceRun).Cpu.Active);
            Assert.Equal(1f, ActivationPlanner.Plan(s, P(GamePhase.PlayingNormal), ManualMode.ForceRun).Cpu.Intensity);
            Assert.Equal(ActivationReason.PausedByUser, ActivationPlanner.Plan(s, P(GamePhase.Paused), ManualMode.Paused).Ram.Reason);
            // "Run now" respects a disabled saving phase and never enables GPU work during startup.
            Assert.False(ActivationPlanner.Plan(s, P(GamePhase.Saving), ManualMode.ForceRun).Cpu.Active);
            Assert.False(ActivationPlanner.Plan(s, P(GamePhase.Startup), ManualMode.ForceRun).Gpu.Active);
            Assert.True(ActivationPlanner.Plan(s, P(GamePhase.Startup), ManualMode.ForceRun).Cpu.Active);
        }

        [Fact]
        public void Run_now_respects_focus_and_world_view_rules()
        {
            var s = new ResourceSettings { AllowWhenUnfocused = false, AllowInWorldView = false };
            Assert.Equal(ActivationReason.Unfocused, ActivationPlanner.PlanOne(s,
                P(GamePhase.PlayingNormal, PhaseModifiers.Unfocused), ManualMode.ForceRun, ResourceKind.Cpu).Reason);
            Assert.Equal(ActivationReason.WorldViewOff, ActivationPlanner.PlanOne(s,
                P(GamePhase.PlayingNormal, PhaseModifiers.WorldView), ManualMode.ForceRun, ResourceKind.Ram).Reason);
        }

        [Fact]
        public void Modifiers_gate_and_boost()
        {
            var s = new ResourceSettings { AllowWhenUnfocused = false, AllowInWorldView = false, IdleBoost = true };
            Assert.Equal(ActivationReason.Unfocused, ActivationPlanner.Plan(s, P(GamePhase.PlayingNormal, PhaseModifiers.Unfocused), ManualMode.Auto).Cpu.Reason);
            Assert.Equal(ActivationReason.WorldViewOff, ActivationPlanner.Plan(s, P(GamePhase.PlayingNormal, PhaseModifiers.WorldView), ManualMode.Auto).Cpu.Reason);
            Assert.Equal(1f, ActivationPlanner.Plan(s, P(GamePhase.PlayingFast, PhaseModifiers.Idle), ManualMode.Auto).Cpu.Intensity);
            Assert.Equal(0.3f, ActivationPlanner.Plan(s, P(GamePhase.PlayingFast), ManualMode.Auto).Cpu.Intensity, 3);
        }
    }

    public class SettingsTests
    {
        [Fact]
        public void Clamp_repairs_bad_values()
        {
            var s = new ResourceSettings
            {
                CpuPhases = new bool[2], PhaseIntensity = new[] { 1, 2 }, GpuTargetPercent = 99, RamMinFreeMiB = 0,
                CpuMaxWorkers = -5, GpuFrameBudgetTenthMs = 1000, FrameGuardMs = 3, Preset = (IntensityPreset)42,
            };
            s.GpuPhases[(int)GamePhase.Startup] = true;
            s.Clamp();
            Assert.Equal(ResourceSettings.PhaseCount, s.CpuPhases.Length);
            Assert.Equal(ResourceSettings.PhaseCount, s.PhaseIntensity.Length);
            Assert.Equal(ResourceSettings.MaxGpuTargetPercent, s.GpuTargetPercent);
            Assert.Equal(1024, s.RamMinFreeMiB);
            Assert.Equal(0, s.CpuMaxWorkers);
            Assert.Equal(80, s.GpuFrameBudgetTenthMs);
            Assert.Equal(20, s.FrameGuardMs);
            Assert.Equal(IntensityPreset.Standard, s.Preset);
            Assert.False(s.GpuPhases[(int)GamePhase.Startup]);
            Assert.All(s.PhaseIntensity, v => Assert.InRange(v, 5, 100));
        }

        [Fact]
        public void Clone_is_deep()
        {
            var s = new ResourceSettings();
            ResourceSettings c = s.Clone();
            c.CpuPhases[0] = !s.CpuPhases[0];
            c.PhaseIntensity[0] = 7;
            Assert.NotEqual(s.CpuPhases[0], c.CpuPhases[0]);
            Assert.NotEqual(7, s.PhaseIntensity[0]);
        }

        [Fact]
        public void Synthetic_features_default_off()
        {
            var s = new ResourceSettings();
            Assert.False(s.CpuSyntheticLoad); Assert.False(s.RamReserve); Assert.False(s.GpuReserve);
            Assert.False(s.RamGcLowLatency);
            Assert.Equal(ProcessPriorityChoice.Unchanged, s.ProcessPriority);
        }

        [Theory]
        [InlineData(16, 0, 14)]
        [InlineData(2, 0, 1)]
        [InlineData(1, 0, 1)]
        [InlineData(16, 4, 4)]
        [InlineData(4, 64, 4)]
        public void Worker_count(int cpus, int setting, int expected)
        {
            Assert.Equal(expected, new ResourceSettings { CpuMaxWorkers = setting }.EffectiveMaxWorkers(cpus));
        }

        [Fact]
        public void Frame_guard_has_hysteresis()
        {
            var g = new FrameGuard();
            for (int i = 0; i < 200; i++) g.Sample(16, 66);
            Assert.False(g.Throttled);
            for (int i = 0; i < 200; i++) g.Sample(120, 66);
            Assert.True(g.Throttled);
            for (int i = 0; i < 30; i++) g.Sample(16, 66);
            Assert.True(g.Throttled); // not yet two good seconds
            for (int i = 0; i < 400; i++) g.Sample(16, 66);
            Assert.False(g.Throttled);
            for (int i = 0; i < 200; i++) g.Sample(500, 0);
            Assert.False(g.Throttled); // disabled
        }
    }
}
