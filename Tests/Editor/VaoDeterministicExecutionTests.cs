using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Modavis.Vao.Editor.Tests
{
    public sealed class VaoDeterministicExecutionTests
    {
        [Test]
        public void SchedulerOrdersActionsExecutesProcessesRenderBindingsAndRejectsLateEvents()
        {
            const string numberState = "urn:state:number";
            const string flagState = "urn:state:flag";
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.ExecutionSemantics.LateEventPolicy = "reject";
            package.ExecutionSemantics.MaximumMicrosteps = 100;
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = numberState, ValueType = "number", DefaultValue = VaoPrimitiveValue.FromNumber(0) });
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = flagState, ValueType = "boolean", DefaultValue = VaoPrimitiveValue.FromBoolean(false) });
            package.TimingConstraints.Add(new VaoTimingConstraintRecord { Identifier = "urn:delay", Unit = "milliseconds", Minimum = 50 });
            package.Transitions.Add(new VaoTransitionRecord
            {
                Identifier = "urn:transition:ordered", ControlIdentifier = "urn:control:ordered", EventTypeIdentifier = "urn:event:ordered",
                Actions =
                {
                    new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = numberState, HasValue = true, Value = VaoPrimitiveValue.FromNumber(1), ExecutionGroup = "z" },
                    new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = numberState, HasValue = true, Value = VaoPrimitiveValue.FromNumber(2), ExecutionGroup = "a" }
                }
            });
            package.ProcessModels.Add(new VaoProcessModelRecord
            {
                Identifier = "urn:process", ProcessKind = "one-shot", Ordering = "simultaneous", TerminationPolicy = "completed",
                Actions =
                {
                    new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = flagState, HasValue = true, Value = VaoPrimitiveValue.FromBoolean(true) },
                    new VaoDeclarativeActionRecord { Operation = "increment-state", TargetIdentifier = numberState, HasValue = true, Value = VaoPrimitiveValue.FromNumber(3), DelayConstraintIdentifier = "urn:delay" }
                }
            });
            package.SampleBindings.Add(new VaoSampleBinding { MappingIdentifier = "urn:mapping", VariantIdentifier = "urn:variant", MinimumKey = 60, MaximumKey = 60, MinimumVelocity = 1, MaximumVelocity = 127 });
            package.RenderBindings.Add(new VaoRenderBindingRecord { Identifier = "urn:render", SampleMappingIdentifiers = new[] { "urn:mapping" } });
            package.Transitions.Add(new VaoTransitionRecord
            {
                Identifier = "urn:transition:render", ControlIdentifier = "urn:control:render", EventTypeIdentifier = "urn:event:render",
                Actions = { new VaoDeclarativeActionRecord { Operation = "select-render-binding", TargetIdentifier = "urn:render" } }
            });

            var root = new GameObject("deterministic executor test");
            try
            {
                var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
                var executor = root.AddComponent<VaoDeterministicExecutor>(); executor.Package = package;
                Assert.That(executor.ExecuteControlNow("urn:control:ordered", "urn:event:ordered", default), Is.True);
                Assert.That(player.GetStateValue(numberState).Number, Is.EqualTo(1), "Execution groups must sort before source array order.");

                Assert.That(executor.StartProcess("urn:process"), Is.True);
                executor.AdvanceTo(0);
                Assert.That(player.GetState(flagState), Is.True);
                Assert.That(player.GetStateValue(numberState).Number, Is.EqualTo(1));
                executor.AdvanceTo(0.049);
                Assert.That(player.GetStateValue(numberState).Number, Is.EqualTo(1));
                executor.AdvanceTo(0.051);
                Assert.That(player.GetStateValue(numberState).Number, Is.EqualTo(4));

                var rendered = false;
                executor.RenderBindingSelected += binding => rendered = binding.Identifier == "urn:render";
                Assert.That(executor.ExecuteControlNow("urn:control:render", "urn:event:render", VaoPrimitiveValue.FromNumber(60)), Is.True);
                Assert.That(rendered, Is.True);
                Assert.That(executor.ScheduleControlEvent("urn:late", "urn:late", default, 0), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(package);
            }
        }

        [Test]
        public void SynchronizationMappingsArePiecewiseChainableAndInvertible()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            try
            {
                package.Timebases.Add(new VaoTimebaseRecord { Identifier = "a", Rate = 1, Origin = 0 });
                package.Timebases.Add(new VaoTimebaseRecord { Identifier = "b", Rate = 1, Origin = 0 });
                package.Timebases.Add(new VaoTimebaseRecord { Identifier = "c", Rate = 10, Origin = 0 });
                package.SynchronizationMappings.Add(new VaoSynchronizationMappingRecord
                {
                    Identifier = "ab", SourceTimebaseIdentifier = "a", TargetTimebaseIdentifier = "b",
                    Segments = { new VaoClockSegmentRecord { SourceStart = 0, SourceEndExclusive = 100, Scale = 2, Offset = 1 } }
                });
                package.SynchronizationMappings.Add(new VaoSynchronizationMappingRecord
                {
                    Identifier = "bc", SourceTimebaseIdentifier = "b", TargetTimebaseIdentifier = "c",
                    Segments = { new VaoClockSegmentRecord { SourceStart = 0, SourceEndExclusive = 1000, Scale = 0.5, Offset = 3 } }
                });
                Assert.That(VaoSynchronizationEngine.TryMap(package, "a", "c", 2, out var mapped), Is.True);
                Assert.That(mapped, Is.EqualTo(5.5).Within(1e-9));
                Assert.That(VaoSynchronizationEngine.TryMap(package, "c", "a", mapped, out var inverse), Is.True);
                Assert.That(inverse, Is.EqualTo(2).Within(1e-9));
                Assert.That(VaoSynchronizationEngine.TryMapSeconds(package, "a", "c", 2, out var seconds), Is.True);
                Assert.That(seconds, Is.EqualTo(0.55).Within(1e-9));
                Assert.That(VaoSynchronizationEngine.TryMap(package, "a", "c", 2000, out _), Is.False, "A synchronization map must not silently extrapolate across an undeclared clock region.");

                package.Timebases[0].HasWrapPeriod = true;
                package.Timebases[0].WrapPeriod = 100;
                Assert.That(VaoSynchronizationEngine.TryMap(package, "a", "b", 102, out var wrapped), Is.True);
                Assert.That(wrapped, Is.EqualTo(5).Within(1e-9));
            }
            finally { Object.DestroyImmediate(package); }
        }

        [Test]
        public void RoutingSupportsFixedAndTableFanOut()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.RoutingRules.Add(new VaoRoutingRuleRecord
            {
                Identifier = "fixed", RoutingBehavior = "activates", MinimumKey = 0, MaximumKey = 127, KeyTransform = "fixed", FixedOutputKeys = new[] { 60, 64, 67 }
            });
            package.RoutingRules.Add(new VaoRoutingRuleRecord
            {
                Identifier = "table", RoutingBehavior = "activates", MinimumKey = 0, MaximumKey = 127, KeyTransform = "table",
                KeyTransformEntries = { new VaoKeyTransformEntryRecord { InputKey = 5, OutputKeys = new[] { 72, 76 } } }
            });
            package.Transitions.Add(new VaoTransitionRecord
            {
                Identifier = "route-fixed", ControlIdentifier = "control-fixed", EventTypeIdentifier = "event",
                Actions = { new VaoDeclarativeActionRecord { Operation = "route-event", TargetIdentifier = "fixed" } }
            });
            package.Transitions.Add(new VaoTransitionRecord
            {
                Identifier = "route-table", ControlIdentifier = "control-table", EventTypeIdentifier = "event",
                Actions = { new VaoDeclarativeActionRecord { Operation = "route-event", TargetIdentifier = "table" } }
            });
            var root = new GameObject("routing test");
            try
            {
                var notes = new List<int>();
                var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
                var executor = root.AddComponent<VaoDeterministicExecutor>(); executor.Package = package;
                executor.EventRouted += value => notes.Add((int)value.Value.Number);
                executor.ExecuteControlNow("control-fixed", "event", VaoPrimitiveValue.FromNumber(5));
                executor.ExecuteControlNow("control-table", "event", VaoPrimitiveValue.FromNumber(5));
                Assert.That(notes, Is.EqualTo(new[] { 60, 64, 67, 72, 76 }));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(package); }
        }

        [Test]
        public void ConflictPoliciesAndEventRenderBindingsAreDeterministic()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = "state", ValueType = "number", DefaultValue = VaoPrimitiveValue.FromNumber(0) });
            package.Transitions.Add(new VaoTransitionRecord
            {
                Identifier = "high", ControlIdentifier = "control", EventTypeIdentifier = "event", Priority = 10, Atomic = true, ConflictPolicy = "priority",
                Actions = { new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = "state", HasValue = true, Value = VaoPrimitiveValue.FromNumber(10) } }
            });
            package.Transitions.Add(new VaoTransitionRecord
            {
                Identifier = "low", ControlIdentifier = "control", EventTypeIdentifier = "event", Priority = 1, Atomic = true, ConflictPolicy = "priority",
                Actions = { new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = "state", HasValue = true, Value = VaoPrimitiveValue.FromNumber(1) } }
            });
            package.RenderBindings.Add(new VaoRenderBindingRecord { Identifier = "auto-render", EventTypeIdentifier = "event", SelectionPolicy = "single" });
            var root = new GameObject("conflict test");
            try
            {
                var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
                var executor = root.AddComponent<VaoDeterministicExecutor>(); executor.Package = package;
                var renderCount = 0; executor.RenderBindingSelected += _ => renderCount++;
                executor.ExecuteControlNow("control", "event", VaoPrimitiveValue.FromNumber(60));
                Assert.That(player.GetStateValue("state").Number, Is.EqualTo(10));
                Assert.That(renderCount, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(package); }
        }

        [Test]
        public void MaximumMicrostepsCountsEachActionWithinAnInputEvent()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            package.ExecutionSemantics.MaximumMicrosteps = 1;
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = "first", ValueType = "number", DefaultValue = VaoPrimitiveValue.FromNumber(0) });
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = "second", ValueType = "number", DefaultValue = VaoPrimitiveValue.FromNumber(0) });
            package.Transitions.Add(new VaoTransitionRecord
            {
                Identifier = "transition", ControlIdentifier = "control", EventTypeIdentifier = "event",
                Actions =
                {
                    new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = "first", HasValue = true, Value = VaoPrimitiveValue.FromNumber(1) },
                    new VaoDeclarativeActionRecord { Operation = "set-state", TargetIdentifier = "second", HasValue = true, Value = VaoPrimitiveValue.FromNumber(2) }
                }
            });
            var root = new GameObject("microstep limit test");
            try
            {
                var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
                var executor = root.AddComponent<VaoDeterministicExecutor>(); executor.Package = package;
                var error = Assert.Throws<System.InvalidOperationException>(() => executor.ExecuteControlNow("control", "event", default));
                Assert.That(error.Message, Does.Contain("maximumMicrosteps"));
                Assert.That(player.GetStateValue("first").Number, Is.EqualTo(1));
                Assert.That(player.GetStateValue("second").Number, Is.Zero);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(package); }
        }

        [Test]
        public void DeterministicStringOrderUsesUnicodeScalarsRatherThanUtf16CodeUnits()
        {
            var supplementary = char.ConvertFromUtf32(0x10000);
            var privateUse = "\uE000";
            Assert.That(VaoUtf8StringComparer.Instance.Compare(supplementary, privateUse), Is.GreaterThan(0));
        }

        [Test]
        public void Pcg32MatchesThePublishedVaoSequence()
        {
            var random = new VaoDeterministicRandom(new VaoRandomSourceRecord { Algorithm = "pcg32", Seed = "0123456789abcdef", Stream = "0000000000000002" });
            var expected = new ulong[] { 3121666835, 3657810905, 1294439499, 2474226060, 481314821, 1719383715, 2470147407, 3755842638 };
            foreach (var value in expected) { Assert.That(random.NextWord(out var width), Is.EqualTo(value)); Assert.That(width, Is.EqualTo(32)); }
        }

        [Test]
        public void Xoshiro256StarStarMatchesThePublishedVaoSequence()
        {
            var random = new VaoDeterministicRandom(new VaoRandomSourceRecord { Algorithm = "xoshiro256-star-star", Seed = "0123456789abcdeffedcba987654321000112233445566778899aabbccddeeff" });
            var expected = new ulong[] { 7378697629483822181, 9114861777597659255, 2399773111056919599, 3387024411415421498, 6448765383325957668, 17019861448958397095, 5281226866712796830, 6676824100206671344 };
            foreach (var value in expected) { Assert.That(random.NextWord(out var width), Is.EqualTo(value)); Assert.That(width, Is.EqualTo(64)); }
        }

        [Test]
        public void FinalHexStreamsAndRationalTimebaseRatesCompileWithoutPrecisionLoss()
        {
            var manifest = new Newtonsoft.Json.Linq.JObject
            {
                ["runtime"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["executionSemantics"] = new Newtonsoft.Json.Linq.JObject
                    {
                        ["timestampOrder"] = "ascending", ["simultaneousEventOrder"] = "priority-then-event-id", ["transitionEvaluation"] = "snapshot",
                        ["actionExecution"] = "execution-group-then-array-order", ["runToCompletion"] = true, ["reentrancyPolicy"] = "queue", ["lateEventPolicy"] = "reject",
                        ["timeResolution"] = new Newtonsoft.Json.Linq.JObject { ["value"] = 1, ["unit"] = "http://qudt.org/vocab/unit/MilliSEC" },
                        ["maximumMicrosteps"] = 9007199254740991L, ["voiceAllocation"] = "lowest-free-then-oldest", ["maximumVoices"] = 9007199254740991L
                    },
                    ["randomSources"] = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
                    {
                        ["id"] = "urn:random", ["algorithm"] = "pcg32", ["seed"] = "0123456789abcdef", ["stream"] = "7fffffffffffffff"
                    })
                },
                ["multimodal"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["timebases"] = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
                    {
                        ["id"] = "urn:timebase", ["kind"] = "media", ["unit"] = "urn:unit:frame", ["rateUnit"] = "urn:unit:frame-per-second",
                        ["rate"] = new Newtonsoft.Json.Linq.JObject { ["numerator"] = 30000, ["denominator"] = 1001 }, ["origin"] = 0
                    }),
                    ["tracks"] = new Newtonsoft.Json.Linq.JArray(), ["synchronizationMappings"] = new Newtonsoft.Json.Linq.JArray()
                }
            };
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            try
            {
                VaoImporter.CompileExecution(manifest, package);
                Assert.That(package.ExecutionSemantics.MaximumMicrosteps, Is.EqualTo(9007199254740991L));
                Assert.That(package.ExecutionSemantics.MaximumVoices, Is.EqualTo(9007199254740991L));
                Assert.That(package.RandomSources.Single().Stream, Is.EqualTo("7fffffffffffffff"));
                Assert.That(package.Timebases.Single().HasRationalRate, Is.True);
                Assert.That(package.Timebases.Single().RateNumerator, Is.EqualTo(30000));
                Assert.That(package.Timebases.Single().RateDenominator, Is.EqualTo(1001));
                Assert.That(package.Timebases.Single().Rate, Is.EqualTo(30000d / 1001d));
            }
            finally { Object.DestroyImmediate(package); }
        }

        [Test]
        public void DeclaredVoiceLimitAndMonophonicPolicyControlAllocation()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var clip = AudioClip.Create("voice", 64, 1, 48000, false);
            package.ExecutionSemantics.MaximumVoices = 1;
            package.ExecutionSemantics.VoiceAllocation = "monophonic-priority";
            package.SampleBindings.Add(new VaoSampleBinding { MappingIdentifier = "mapping", VariantIdentifier = "variant", MinimumKey = 0, MaximumKey = 127, MinimumVelocity = 1, MaximumVelocity = 127, Clip = clip });
            var root = new GameObject("voice allocation test");
            try
            {
                var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
                player.NoteOn(60); player.NoteOn(64);
                Assert.That(player.ActiveVoiceCount, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(clip); Object.DestroyImmediate(package); }
        }

        [Test]
        public void DeclaredNoteEventRendersEveryEligibleOrganStopWithoutLosingVoices()
        {
            var package = ScriptableObject.CreateInstance<VaoPackageAsset>();
            var firstClip = AudioClip.Create("stop one", 64, 1, 48000, false);
            var secondClip = AudioClip.Create("stop two", 64, 1, 48000, false);
            package.EventTypes.Add(new VaoEventTypeRecord { Identifier = "note-on", EventKind = "note-on", ValueDomain = "midi-key" });
            package.ProtocolBindings.Add(new VaoProtocolBindingRecord { Direction = "input", MessageType = "note", ControlIdentifier = "key", EventTypeIdentifier = "note-on", Number = 60 });
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = "stop-one", ValueType = "boolean", DefaultValue = VaoPrimitiveValue.FromBoolean(true) });
            package.StateVariables.Add(new VaoStateVariableRecord { Identifier = "stop-two", ValueType = "boolean", DefaultValue = VaoPrimitiveValue.FromBoolean(true) });
            package.SampleBindings.Add(new VaoSampleBinding { MappingIdentifier = "map-one", VariantIdentifier = "one", MinimumKey = 60, MaximumKey = 60, MinimumVelocity = 1, MaximumVelocity = 127, Clip = firstClip });
            package.SampleBindings.Add(new VaoSampleBinding { MappingIdentifier = "map-two", VariantIdentifier = "two", MinimumKey = 60, MaximumKey = 60, MinimumVelocity = 1, MaximumVelocity = 127, Clip = secondClip });
            package.RenderBindings.Add(new VaoRenderBindingRecord { Identifier = "render-one", EventTypeIdentifier = "note-on", SelectionPolicy = "state-dependent", SampleMappingIdentifiers = new[] { "map-one" }, Conditions = { new VaoStateConditionRecord { StateVariableIdentifier = "stop-one", Operator = "equals", Value = VaoPrimitiveValue.FromBoolean(true) } } });
            package.RenderBindings.Add(new VaoRenderBindingRecord { Identifier = "render-two", EventTypeIdentifier = "note-on", SelectionPolicy = "state-dependent", SampleMappingIdentifiers = new[] { "map-two" }, Conditions = { new VaoStateConditionRecord { StateVariableIdentifier = "stop-two", Operator = "equals", Value = VaoPrimitiveValue.FromBoolean(true) } } });
            var root = new GameObject("organ render binding test");
            try
            {
                var player = root.AddComponent<VaoSamplePlayer>(); player.Package = package;
                var executor = root.AddComponent<VaoDeterministicExecutor>(); executor.Package = package;
                player.NoteOn(60, 96);
                Assert.That(player.ActiveVoiceCount, Is.EqualTo(2));
                player.AllNotesOff();
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(firstClip); Object.DestroyImmediate(secondClip); Object.DestroyImmediate(package); }
        }
    }
}
