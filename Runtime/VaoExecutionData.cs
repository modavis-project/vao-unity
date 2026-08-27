using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modavis.Vao
{
    [Serializable]
    public sealed class VaoEventTypeRecord
    {
        public string Identifier;
        public string Label;
        public string EventKind;
        public string ValueDomain;
        public int Priority;
    }

    [Serializable]
    public sealed class VaoExecutionSemanticsRecord
    {
        public string TimestampOrder = "ascending";
        public string SimultaneousEventOrder = "priority-then-event-id";
        public string TransitionEvaluation = "snapshot";
        public string ActionExecution = "execution-group-then-array-order";
        public bool RunToCompletion = true;
        public string ReentrancyPolicy = "queue";
        public string LateEventPolicy = "reject";
        public double TimeResolution = 1d;
        public string TimeResolutionUnit = "milliseconds";
        public long MaximumMicrosteps = 10000;
        public string VoiceAllocation;
        public long MaximumVoices;
    }

    [Serializable]
    public sealed class VaoTimingConstraintRecord
    {
        public string Identifier;
        public string TimingKind;
        public string Unit;
        public double Minimum;
        public double Typical;
        public double Maximum;
        public bool HasTypical;
        public bool HasMaximum;
        public string[] AppliesToIdentifiers = Array.Empty<string>();

        public double ToSeconds(int sampleRate = 48000)
        {
            var value = HasTypical ? Typical : Minimum;
            return Unit switch
            {
                "milliseconds" => value / 1000d,
                "audio-frames" or "samples" => value / Math.Max(1, sampleRate),
                _ => value
            };
        }
    }

    [Serializable]
    public sealed class VaoProcessModelRecord
    {
        public string Identifier;
        public string ProcessKind;
        public string Ordering;
        public List<VaoDeclarativeActionRecord> Actions = new();
        public string[] ChildProcessIdentifiers = Array.Empty<string>();
        public string[] TimingConstraintIdentifiers = Array.Empty<string>();
        public string TerminationPolicy;
        public long MaximumIterations;
        public string DurationConstraintIdentifier;
        public string CancellationControlIdentifier;
        public string RandomSourceIdentifier;
        public string ProbabilityDistributionKind;
        public string[] ProbabilityParameterNames = Array.Empty<string>();
        public long[] ProbabilityParameterValues = Array.Empty<long>();
    }

    [Serializable]
    public sealed class VaoRenderBindingRecord
    {
        public string Identifier;
        public string EventTypeIdentifier;
        public string ProcessModelIdentifier;
        public string SelectionPolicy;
        public List<VaoStateConditionRecord> Conditions = new();
        public string[] SampleMappingIdentifiers = Array.Empty<string>();
        public string[] SampleVariantIdentifiers = Array.Empty<string>();
    }

    [Serializable]
    public sealed class VaoKeyTransformEntryRecord
    {
        public int InputKey;
        public int[] OutputKeys = Array.Empty<int>();
    }

    [Serializable]
    public sealed class VaoRoutingRuleRecord
    {
        public string Identifier;
        public string SourceControlIdentifier;
        public string SourceEntityIdentifier;
        public string TargetEntityIdentifier;
        public string RoutingBehavior;
        public string InputKeyMeaning;
        public string OutputKeyMeaning;
        public int MinimumKey;
        public int MaximumKey = 127;
        public string KeyTransform;
        public int SemitoneOffset;
        public int[] FixedOutputKeys = Array.Empty<int>();
        public List<VaoKeyTransformEntryRecord> KeyTransformEntries = new();
        public string DelayConstraintIdentifier;
        public List<VaoStateConditionRecord> Conditions = new();
    }

    [Serializable]
    public sealed class VaoRandomSourceRecord
    {
        public string Identifier;
        public string Algorithm;
        public string Seed;
        public string Stream;
    }

    [Serializable]
    public sealed class VaoTimebaseRecord
    {
        public string Identifier;
        public string Kind;
        public string Unit;
        public string RateUnit;
        public double Rate = 1d;
        public bool HasRationalRate;
        public long RateNumerator;
        public long RateDenominator = 1;
        public double Origin;
        public string TimeScale;
        public string Epoch;
        public double WrapPeriod;
        public bool HasWrapPeriod;
    }

    [Serializable]
    public sealed class VaoTrackRecord
    {
        public string Identifier;
        public string Modality;
        public string TimebaseIdentifier;
        public string RealizationIdentifier;
        public string CoordinateFrameIdentifier;
        public string ChannelSelector;
        public string Continuity;
    }

    [Serializable]
    public sealed class VaoClockSegmentRecord
    {
        public double SourceStart;
        public double SourceEndExclusive;
        public double Scale = 1d;
        public double Offset;
        public string DiscontinuityAfter;
    }

    [Serializable]
    public sealed class VaoSynchronizationMappingRecord
    {
        public string Identifier;
        public string SourceTimebaseIdentifier;
        public string TargetTimebaseIdentifier;
        public string Method;
        public string ActivityIdentifier;
        public List<VaoClockSegmentRecord> Segments = new();
    }

    public static class VaoSynchronizationEngine
    {
        public static bool TryMap(VaoPackageAsset package, string sourceTimebaseIdentifier, string targetTimebaseIdentifier, double sourceValue, out double targetValue)
        {
            targetValue = sourceValue;
            if (package == null || string.IsNullOrEmpty(sourceTimebaseIdentifier) || string.IsNullOrEmpty(targetTimebaseIdentifier)) return false;
            sourceValue = Normalize(package.FindTimebase(sourceTimebaseIdentifier), sourceValue);
            targetValue = sourceValue;
            if (sourceTimebaseIdentifier == targetTimebaseIdentifier) return true;

            var visited = new HashSet<string>(StringComparer.Ordinal) { sourceTimebaseIdentifier };
            var queue = new Queue<(string timebase, double value)>();
            queue.Enqueue((sourceTimebaseIdentifier, sourceValue));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var mapping in package.SynchronizationMappings)
                {
                    string next;
                    double mapped;
                    if (mapping.SourceTimebaseIdentifier == current.timebase)
                    {
                        next = mapping.TargetTimebaseIdentifier;
                        if (!TryMapSegments(mapping.Segments, current.value, false, out mapped)) continue;
                    }
                    else if (mapping.TargetTimebaseIdentifier == current.timebase)
                    {
                        next = mapping.SourceTimebaseIdentifier;
                        if (!TryMapSegments(mapping.Segments, current.value, true, out mapped)) continue;
                    }
                    else continue;
                    mapped = Normalize(package.FindTimebase(next), mapped);
                    if (next == targetTimebaseIdentifier) { targetValue = mapped; return true; }
                    if (visited.Add(next)) queue.Enqueue((next, mapped));
                }
            }
            return false;
        }

        public static bool TryMapSeconds(VaoPackageAsset package, string sourceTimebaseIdentifier, string targetTimebaseIdentifier, double sourceSeconds, out double targetSeconds)
        {
            targetSeconds = sourceSeconds;
            var source = package?.FindTimebase(sourceTimebaseIdentifier);
            var target = package?.FindTimebase(targetTimebaseIdentifier);
            if (source == null || target == null) return false;
            var sourceValue = source.Origin + sourceSeconds * Math.Max(double.Epsilon, source.Rate);
            if (!TryMap(package, sourceTimebaseIdentifier, targetTimebaseIdentifier, sourceValue, out var targetValue)) return false;
            targetSeconds = (targetValue - target.Origin) / Math.Max(double.Epsilon, target.Rate);
            return true;
        }

        private static bool TryMapSegments(IReadOnlyList<VaoClockSegmentRecord> segments, double value, bool inverse, out double mapped)
        {
            mapped = value;
            if (segments == null || segments.Count == 0) return false;
            VaoClockSegmentRecord selected = null;
            if (!inverse)
                selected = FindSegment(segments, value);
            else
                foreach (var segment in segments)
                {
                    if (Math.Abs(segment.Scale) < double.Epsilon) continue;
                    var a = segment.SourceStart * segment.Scale + segment.Offset;
                    var b = segment.SourceEndExclusive * segment.Scale + segment.Offset;
                    if (value >= Math.Min(a, b) && value < Math.Max(a, b)) { selected = segment; break; }
                }
            if (selected == null || inverse && Math.Abs(selected.Scale) < double.Epsilon) return false;
            mapped = inverse ? (value - selected.Offset) / selected.Scale : value * selected.Scale + selected.Offset;
            return true;
        }

        private static VaoClockSegmentRecord FindSegment(IReadOnlyList<VaoClockSegmentRecord> segments, double value)
        {
            foreach (var segment in segments)
                if (value >= segment.SourceStart && value < segment.SourceEndExclusive) return segment;
            return null;
        }

        private static double Normalize(VaoTimebaseRecord timebase, double value)
        {
            if (timebase?.HasWrapPeriod != true || timebase.WrapPeriod <= 0d) return value;
            var relative = (value - timebase.Origin) % timebase.WrapPeriod;
            if (relative < 0d) relative += timebase.WrapPeriod;
            return timebase.Origin + relative;
        }
    }
}
