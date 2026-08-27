using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using UnityEngine;

namespace Modavis.Vao
{
    [Serializable]
    public sealed class VaoExecutionEvent
    {
        public string Identifier;
        public string ControlIdentifier;
        public string EventTypeIdentifier;
        public string ProcessIdentifier;
        public string TargetIdentifier;
        public double Timestamp;
        public int Priority;
        public int Velocity = 127;
        public long Sequence;
        public VaoPrimitiveValue Value;
    }

    [DisallowMultipleComponent]
    public sealed class VaoDeterministicExecutor : MonoBehaviour, IVaoPackageConsumer
    {
        private enum WorkKind { ControlEvent, Event, RoutedEvent, Action, ProcessCycle, ProcessComplete, ProcessStop }

        private sealed class WorkItem
        {
            public WorkKind Kind;
            public double Timestamp;
            public int Priority;
            public int Velocity = 127;
            public long Sequence;
            public string Identifier;
            public string ControlIdentifier;
            public string EventTypeIdentifier;
            public string ProcessIdentifier;
            public string OwnerProcessIdentifier;
            public string TargetIdentifier;
            public VaoPrimitiveValue Value;
            public VaoDeclarativeActionRecord Action;
        }

        private sealed class ActiveProcess
        {
            public VaoProcessModelRecord Declaration;
            public long Iteration;
        }

        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private VaoSamplePlayer samplePlayer;
        [SerializeField] private bool useUnscaledUnityClock = true;
        private readonly List<WorkItem> queue = new();
        private readonly Dictionary<string, ActiveProcess> activeProcesses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VaoDeterministicRandom> randomSources = new(StringComparer.Ordinal);
        private long sequence;
        private long microsteps;
        private long microstepLimit = 10000;
        private double unityClockOrigin;
        private bool processing;

        public VaoPackageAsset Package { get => package; set { package = value; ResetExecutor(); } }
        public double CurrentTime { get; private set; }
        public int ScheduledCount => queue.Count;
        public bool IsDispatching => processing;
        public IReadOnlyCollection<string> ActiveProcessIdentifiers => activeProcesses.Keys;
        public event Action<VaoExecutionEvent> EventEmitted;
        public event Action<VaoExecutionEvent> EventRouted;
        public event Action<VaoDeclarativeActionRecord> ActionExecuted;
        public event Action<VaoDeclarativeActionRecord> UnhandledAction;
        public event Action<string> ProcessStarted;
        public event Action<string> ProcessStopped;
        public event Action<string> ProcessCompleted;
        public event Action<VaoRenderBindingRecord> RenderBindingSelected;

        public void SetPackage(VaoPackageAsset value) => Package = value;

        private void Awake()
        {
            samplePlayer ??= GetComponent<VaoSamplePlayer>();
            ResetExecutor();
        }

        private void OnEnable() => unityClockOrigin = (useUnscaledUnityClock ? Time.unscaledTimeAsDouble : Time.timeAsDouble) - CurrentTime;

        private void Update()
        {
            var now = (useUnscaledUnityClock ? Time.unscaledTimeAsDouble : Time.timeAsDouble) - unityClockOrigin;
            AdvanceTo(Math.Max(CurrentTime, now));
        }

        public void ResetExecutor()
        {
            queue.Clear();
            activeProcesses.Clear();
            randomSources.Clear();
            sequence = 0;
            microsteps = 0;
            microstepLimit = Math.Max(1L, package?.ExecutionSemantics.MaximumMicrosteps ?? 10000L);
            CurrentTime = 0d;
            samplePlayer ??= GetComponent<VaoSamplePlayer>();
            if (package != null)
                foreach (var declaration in package.RandomSources) randomSources[declaration.Identifier] = new VaoDeterministicRandom(declaration);
            unityClockOrigin = useUnscaledUnityClock ? Time.unscaledTimeAsDouble : Time.timeAsDouble;
        }

        public bool ExecuteControlNow(string controlIdentifier, string eventTypeIdentifier, VaoPrimitiveValue value, int velocity = 127)
        {
            if (processing && package?.ExecutionSemantics.ReentrancyPolicy == "reject") return false;
            if (!ScheduleControlEvent(controlIdentifier, eventTypeIdentifier, value, CurrentTime, velocity: velocity)) return false;
            if (!processing) AdvanceTo(CurrentTime);
            return true;
        }

        public bool ScheduleControlEvent(string controlIdentifier, string eventTypeIdentifier, VaoPrimitiveValue value, double timestamp, int priority = 0, string identifier = null, int velocity = 127)
        {
            if (package == null) return false;
            if (!NormalizeTimestamp(ref timestamp)) return false;
            Enqueue(new WorkItem
            {
                Kind = WorkKind.ControlEvent, Timestamp = timestamp, Priority = priority, Identifier = identifier ?? eventTypeIdentifier ?? controlIdentifier,
                ControlIdentifier = controlIdentifier, EventTypeIdentifier = eventTypeIdentifier, Value = value, Velocity = Mathf.Clamp(velocity, 0, 127)
            });
            return true;
        }

        public bool ScheduleSynchronizedControlEvent(string sourceTimebaseIdentifier, string targetTimebaseIdentifier, double sourceTime, string controlIdentifier, string eventTypeIdentifier, VaoPrimitiveValue value, int priority = 0, int velocity = 127)
        {
            return VaoSynchronizationEngine.TryMap(package, sourceTimebaseIdentifier, targetTimebaseIdentifier, sourceTime, out var mapped)
                && ScheduleControlEvent(controlIdentifier, eventTypeIdentifier, value, mapped, priority, velocity: velocity);
        }

        public void AdvanceTo(double timestamp)
        {
            if (package == null || processing) return;
            timestamp = Math.Max(CurrentTime, timestamp);
            processing = true;
            microsteps = 0;
            microstepLimit = Math.Max(1L, package.ExecutionSemantics.MaximumMicrosteps);
            try
            {
                while (true)
                {
                    queue.Sort(CompareWork);
                    if (queue.Count == 0 || queue[0].Timestamp > timestamp) break;
                    var item = queue[0];
                    queue.RemoveAt(0);
                    CurrentTime = Math.Max(CurrentTime, item.Timestamp);
                    if (item.Kind == WorkKind.ControlEvent) microsteps = 0;
                    ExecuteWork(item);
                }
                CurrentTime = timestamp;
            }
            finally { processing = false; }
        }

        public bool StartProcess(string processIdentifier, VaoPrimitiveValue value = default, int velocity = 127)
        {
            var declaration = package?.FindProcessModel(processIdentifier);
            if (declaration == null) return false;
            if (activeProcesses.ContainsKey(processIdentifier)) return true;
            activeProcesses[processIdentifier] = new ActiveProcess { Declaration = declaration };
            ProcessStarted?.Invoke(processIdentifier);
            Enqueue(new WorkItem { Kind = WorkKind.ProcessCycle, Timestamp = CurrentTime, Identifier = processIdentifier, ProcessIdentifier = processIdentifier, OwnerProcessIdentifier = processIdentifier, Value = value, Velocity = velocity });
            if (declaration.TerminationPolicy == "duration-bound")
            {
                var duration = ConstraintSeconds(declaration.DurationConstraintIdentifier);
                Enqueue(new WorkItem { Kind = WorkKind.ProcessStop, Timestamp = CurrentTime + duration, Identifier = processIdentifier, ProcessIdentifier = processIdentifier, OwnerProcessIdentifier = processIdentifier });
            }
            return true;
        }

        public bool StopProcess(string processIdentifier)
        {
            if (string.IsNullOrEmpty(processIdentifier) || !activeProcesses.Remove(processIdentifier)) return false;
            queue.RemoveAll(item => item.OwnerProcessIdentifier == processIdentifier);
            ProcessStopped?.Invoke(processIdentifier);
            return true;
        }

        private void ExecuteWork(WorkItem item)
        {
            switch (item.Kind)
            {
                case WorkKind.ControlEvent:
                    ExecuteControlEvent(item);
                    break;
                case WorkKind.Event:
                    ExecuteEvent(item);
                    break;
                case WorkKind.RoutedEvent:
                    EventRouted?.Invoke(ToPublicEvent(item));
                    break;
                case WorkKind.Action:
                    ExecuteAction(item.Action, item.Value, item.OwnerProcessIdentifier, item.Velocity);
                    break;
                case WorkKind.ProcessCycle:
                    ExecuteProcessCycle(item);
                    break;
                case WorkKind.ProcessComplete:
                    CompleteProcess(item.ProcessIdentifier);
                    break;
                case WorkKind.ProcessStop:
                    StopProcess(item.ProcessIdentifier);
                    break;
            }
        }

        private void ExecuteControlEvent(WorkItem item)
        {
            var declaration = package.EventTypes.Find(value => value.Identifier == item.EventTypeIdentifier);
            foreach (var process in activeProcesses.Values.Where(value => value.Declaration.CancellationControlIdentifier == item.ControlIdentifier).ToArray())
                if (process.Declaration.TerminationPolicy == "external-cancel" || process.Declaration.TerminationPolicy == "on-control-release" && declaration?.EventKind is "control-off" or "note-off") StopProcess(process.Declaration.Identifier);

            var snapshot = SnapshotState();
            var eligible = package.Transitions
                .Where(transition => string.IsNullOrEmpty(transition.ControlIdentifier) || transition.ControlIdentifier == item.ControlIdentifier)
                .Where(transition => string.IsNullOrEmpty(item.EventTypeIdentifier) || transition.EventTypeIdentifier == item.EventTypeIdentifier)
                .Where(transition => ConditionsMatch(transition.Conditions, snapshot))
                .OrderByDescending(transition => transition.Priority)
                .ThenBy(transition => transition.Identifier, VaoUtf8StringComparer.Instance).ToList();
            QueueTransitionActions(eligible, item.Value, null, item.Priority, item.Velocity);
            DispatchRoutingRules(item);
            DispatchRenderBindings(item, ExplicitRenderBindings(eligible));
        }

        private void ExecuteEvent(WorkItem item)
        {
            EventEmitted?.Invoke(ToPublicEvent(item));
            var snapshot = SnapshotState();
            var eligible = package.Transitions.Where(transition => string.IsNullOrEmpty(transition.ControlIdentifier) && transition.EventTypeIdentifier == item.EventTypeIdentifier)
                .Where(transition => ConditionsMatch(transition.Conditions, snapshot)).OrderByDescending(transition => transition.Priority).ThenBy(transition => transition.Identifier, VaoUtf8StringComparer.Instance).ToList();
            QueueTransitionActions(eligible, item.Value, item.OwnerProcessIdentifier, item.Priority, item.Velocity);
            DispatchRenderBindings(item, ExplicitRenderBindings(eligible));
        }

        private void QueueTransitionActions(IReadOnlyList<VaoTransitionRecord> transitions, VaoPrimitiveValue input, string ownerProcessIdentifier, int eventPriority, int velocity)
        {
            if (transitions == null || transitions.Count == 0) return;
            var accepted = new List<(VaoTransitionRecord transition, List<VaoDeclarativeActionRecord> actions)>();
            var claimedTargets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var transition in transitions)
            {
                var actions = transition.Actions?.Where(action => action != null).ToList() ?? new List<VaoDeclarativeActionRecord>();
                var conflictingTargets = actions.Select(ActionConflictKey).Where(key => key != null && claimedTargets.ContainsKey(key)).Distinct(StringComparer.Ordinal).ToArray();
                if (conflictingTargets.Length > 0)
                {
                    if (transition.ConflictPolicy == "last-event-wins")
                    {
                        foreach (var key in conflictingTargets)
                        {
                            var previousIndex = claimedTargets[key];
                            var previous = accepted[previousIndex];
                            if (previous.transition.Atomic)
                            {
                                foreach (var previousKey in previous.actions.Select(ActionConflictKey).Where(value => value != null).ToArray())
                                    if (claimedTargets.TryGetValue(previousKey, out var owner) && owner == previousIndex) claimedTargets.Remove(previousKey);
                                previous.actions.Clear();
                            }
                            else previous.actions.RemoveAll(action => ActionConflictKey(action) == key);
                            claimedTargets[key] = accepted.Count;
                        }
                    }
                    else if (transition.Atomic)
                    {
                        continue;
                    }
                    else
                    {
                        actions.RemoveAll(action =>
                        {
                            var key = ActionConflictKey(action);
                            return key != null && claimedTargets.ContainsKey(key);
                        });
                    }
                }
                var acceptedIndex = accepted.Count;
                accepted.Add((transition, actions));
                foreach (var key in actions.Select(ActionConflictKey).Where(key => key != null)) claimedTargets[key] = acceptedIndex;
            }
            foreach (var item in accepted)
                QueueActions(item.actions, input, ownerProcessIdentifier, Math.Max(eventPriority, item.transition.Priority), velocity: velocity);
        }

        private static string ActionConflictKey(VaoDeclarativeActionRecord action)
        {
            if (action == null || string.IsNullOrEmpty(action.TargetIdentifier)) return null;
            return action.Operation switch
            {
                "set-state" or "toggle-state" or "increment-state" => "state:" + action.TargetIdentifier,
                "start-process" or "stop-process" => "process:" + action.TargetIdentifier,
                "select-render-binding" => "render:" + action.TargetIdentifier,
                _ => null
            };
        }

        private void QueueActions(IReadOnlyList<VaoDeclarativeActionRecord> actions, VaoPrimitiveValue input, string ownerProcessIdentifier, int priority, double baseDelay = 0d, int velocity = 127)
        {
            if (actions == null) return;
            var ordered = actions.Select((action, index) => (action, index))
                .OrderBy(item => item.action.ExecutionGroup ?? string.Empty, VaoUtf8StringComparer.Instance).ThenBy(item => item.index);
            foreach (var item in ordered)
            {
                var delay = baseDelay + ConstraintSeconds(item.action.DelayConstraintIdentifier);
                if (delay <= 0d) ExecuteAction(item.action, input, ownerProcessIdentifier, velocity);
                else Enqueue(new WorkItem { Kind = WorkKind.Action, Timestamp = CurrentTime + delay, Priority = priority, Identifier = item.action.TargetIdentifier, Action = item.action, Value = input, OwnerProcessIdentifier = ownerProcessIdentifier, Velocity = velocity });
            }
        }

        private void ExecuteAction(VaoDeclarativeActionRecord action, VaoPrimitiveValue input, string ownerProcessIdentifier, int velocity)
        {
            if (action == null) return;
            ConsumeMicrostep();
            if (samplePlayer != null && samplePlayer.ExecuteStateAction(action, input)) { ActionExecuted?.Invoke(action); return; }
            switch (action.Operation)
            {
                case "emit-event":
                    Enqueue(new WorkItem { Kind = WorkKind.Event, Timestamp = CurrentTime, Identifier = action.TargetIdentifier, EventTypeIdentifier = action.TargetIdentifier, Value = action.HasValue ? action.Value : input, OwnerProcessIdentifier = ownerProcessIdentifier });
                    break;
                case "start-process":
                    StartProcess(action.TargetIdentifier, action.HasValue ? action.Value : input, velocity);
                    break;
                case "stop-process":
                    StopProcess(action.TargetIdentifier);
                    break;
                case "route-event":
                    RouteEvent(action, input, ownerProcessIdentifier, velocity);
                    break;
                case "select-render-binding":
                    SelectRenderBinding(action, input, velocity);
                    break;
                default:
                    UnhandledAction?.Invoke(action);
                    samplePlayer?.RequestHostAction(action);
                    return;
            }
            ActionExecuted?.Invoke(action);
        }

        private void SelectRenderBinding(VaoDeclarativeActionRecord action, VaoPrimitiveValue input, int velocity)
        {
            var binding = package.FindRenderBinding(action.TargetIdentifier);
            if (binding == null || !ConditionsMatch(binding.Conditions, SnapshotState())) return;
            var key = Mathf.Clamp((int)Math.Round((action.HasValue ? action.Value : input).Number) + action.KeyOffset, 0, 127);
            ExecuteRenderBinding(binding, key, velocity);
        }

        private static HashSet<string> ExplicitRenderBindings(IEnumerable<VaoTransitionRecord> transitions)
            => new(transitions.SelectMany(transition => transition.Actions ?? Enumerable.Empty<VaoDeclarativeActionRecord>())
                .Where(action => action?.Operation == "select-render-binding").Select(action => action.TargetIdentifier), StringComparer.Ordinal);

        private void DispatchRenderBindings(WorkItem item, HashSet<string> excluded)
        {
            if (string.IsNullOrEmpty(item.EventTypeIdentifier)) return;
            var eligible = package.RenderBindings.Where(binding => binding.EventTypeIdentifier == item.EventTypeIdentifier && ConditionsMatch(binding.Conditions, SnapshotState()))
                .Where(binding => excluded == null || !excluded.Contains(binding.Identifier))
                .OrderBy(binding => binding.Identifier, VaoUtf8StringComparer.Instance).ToList();
            var key = Mathf.Clamp((int)Math.Round(item.Value.Number), 0, 127);
            foreach (var binding in eligible) ExecuteRenderBinding(binding, key, item.Velocity);
        }

        private void ExecuteRenderBinding(VaoRenderBindingRecord binding, int key, int velocity)
        {
            if (!string.IsNullOrEmpty(binding.ProcessModelIdentifier)) StartProcess(binding.ProcessModelIdentifier, VaoPrimitiveValue.FromNumber(key), velocity);
            samplePlayer?.RenderBinding(binding.Identifier, key, velocity);
            RenderBindingSelected?.Invoke(binding);
        }

        private void RouteEvent(VaoDeclarativeActionRecord action, VaoPrimitiveValue input, string ownerProcessIdentifier, int velocity)
        {
            var rule = package.RoutingRules.Find(item => item.Identifier == action.TargetIdentifier);
            if (rule == null)
            {
                if (package.EventTypes.Any(item => item.Identifier == action.TargetIdentifier))
                    Enqueue(new WorkItem { Kind = WorkKind.Event, Timestamp = CurrentTime, Identifier = action.TargetIdentifier, EventTypeIdentifier = action.TargetIdentifier, Value = input, OwnerProcessIdentifier = ownerProcessIdentifier });
                return;
            }
            RouteRule(rule, input, action.KeyOffset, ownerProcessIdentifier, velocity);
        }

        private void DispatchRoutingRules(WorkItem item)
        {
            foreach (var rule in package.RoutingRules.Where(rule => rule.SourceControlIdentifier == item.ControlIdentifier).OrderBy(rule => rule.Identifier, VaoUtf8StringComparer.Instance))
                RouteRule(rule, item.Value, 0, item.OwnerProcessIdentifier, item.Velocity);
        }

        private void RouteRule(VaoRoutingRuleRecord rule, VaoPrimitiveValue input, int keyOffset, string ownerProcessIdentifier, int velocity)
        {
            if (rule == null || !ConditionsMatch(rule.Conditions, SnapshotState())) return;
            var inputKey = Mathf.Clamp((int)Math.Round(input.Number) + keyOffset, 0, 127);
            if (inputKey < rule.MinimumKey || inputKey > rule.MaximumKey) return;
            var outputKeys = TransformKeys(rule, inputKey);
            if (outputKeys.Length == 0) return;
            var delay = ConstraintSeconds(rule.DelayConstraintIdentifier);
            foreach (var outputKey in outputKeys)
            {
                var routed = new WorkItem { Kind = WorkKind.RoutedEvent, Timestamp = CurrentTime + delay, Identifier = rule.Identifier, EventTypeIdentifier = rule.Identifier, ProcessIdentifier = ownerProcessIdentifier, TargetIdentifier = rule.TargetEntityIdentifier, Value = VaoPrimitiveValue.FromNumber(outputKey), Velocity = velocity };
                if (delay <= 0d) { routed.Sequence = sequence++; EventRouted?.Invoke(ToPublicEvent(routed)); } else Enqueue(routed);
            }
        }

        private static int[] TransformKeys(VaoRoutingRuleRecord rule, int inputKey)
        {
            IEnumerable<int> values = rule.KeyTransform switch
            {
                "transpose" => new[] { inputKey + rule.SemitoneOffset },
                "fixed" => rule.FixedOutputKeys ?? Array.Empty<int>(),
                "table" => rule.KeyTransformEntries?.FirstOrDefault(entry => entry.InputKey == inputKey)?.OutputKeys ?? Array.Empty<int>(),
                _ => new[] { inputKey }
            };
            return values.Where(value => value is >= 0 and <= 127).Distinct().ToArray();
        }

        private void ExecuteProcessCycle(WorkItem item)
        {
            if (!activeProcesses.TryGetValue(item.ProcessIdentifier, out var active)) return;
            var declaration = active.Declaration;
            var actions = declaration.Actions;
            var children = declaration.ChildProcessIdentifiers ?? Array.Empty<string>();
            if (declaration.Ordering == "stochastic")
            {
                var count = actions.Count + children.Length;
                if (count > 0)
                {
                    var choice = Choose(declaration, count);
                    if (choice < actions.Count) QueueActions(new[] { actions[choice] }, item.Value, declaration.Identifier, item.Priority, velocity: item.Velocity);
                    else StartProcess(children[choice - actions.Count], item.Value, item.Velocity);
                }
            }
            else if (declaration.Ordering == "sequential")
            {
                var spacing = ProcessInterval(declaration);
                for (var index = 0; index < actions.Count; index++) QueueActions(new[] { actions[index] }, item.Value, declaration.Identifier, item.Priority, index * spacing, item.Velocity);
                for (var index = 0; index < children.Length; index++)
                    Enqueue(new WorkItem { Kind = WorkKind.Action, Timestamp = CurrentTime + (actions.Count + index) * spacing, Identifier = children[index], OwnerProcessIdentifier = declaration.Identifier, Value = item.Value, Velocity = item.Velocity, Action = new VaoDeclarativeActionRecord { Operation = "start-process", TargetIdentifier = children[index] } });
            }
            else
            {
                QueueActions(actions, item.Value, declaration.Identifier, item.Priority, velocity: item.Velocity);
                foreach (var child in children) StartProcess(child, item.Value, item.Velocity);
            }

            active.Iteration++;
            var maximumReached = declaration.MaximumIterations > 0 && active.Iteration >= declaration.MaximumIterations;
            var repeats = declaration.ProcessKind == "repeating" || declaration.TerminationPolicy == "maximum-iterations";
            if (repeats && !maximumReached)
            {
                Enqueue(new WorkItem { Kind = WorkKind.ProcessCycle, Timestamp = CurrentTime + Math.Max(TimeResolutionSeconds(), ProcessInterval(declaration)), Identifier = declaration.Identifier, ProcessIdentifier = declaration.Identifier, OwnerProcessIdentifier = declaration.Identifier, Value = item.Value });
            }
            else if (declaration.TerminationPolicy == "completed" || declaration.TerminationPolicy == "maximum-iterations" || declaration.ProcessKind is "one-shot" or "sequenced")
            {
                var completionDelay = 0d;
                var spacing = declaration.Ordering == "sequential" ? ProcessInterval(declaration) : 0d;
                for (var index = 0; index < actions.Count; index++) completionDelay = Math.Max(completionDelay, index * spacing + ConstraintSeconds(actions[index].DelayConstraintIdentifier));
                if (children.Length > 0 && declaration.Ordering == "sequential") completionDelay = Math.Max(completionDelay, (actions.Count + children.Length) * spacing);
                Enqueue(new WorkItem { Kind = WorkKind.ProcessComplete, Timestamp = CurrentTime + completionDelay, Identifier = declaration.Identifier, ProcessIdentifier = declaration.Identifier, OwnerProcessIdentifier = declaration.Identifier });
            }
        }

        private void CompleteProcess(string processIdentifier)
        {
            if (!activeProcesses.Remove(processIdentifier)) return;
            ProcessCompleted?.Invoke(processIdentifier);
        }

        private int Choose(VaoProcessModelRecord process, int count)
        {
            if (!randomSources.TryGetValue(process.RandomSourceIdentifier ?? string.Empty, out var random)) throw new InvalidOperationException($"Stochastic process {process.Identifier} has no declared deterministic random source.");
            if (count <= 1) return 0;
            var weights = new long[count];
            if (process.ProbabilityDistributionKind == "categorical")
            {
                for (var index = 0; index < Math.Min(process.ProbabilityParameterNames?.Length ?? 0, process.ProbabilityParameterValues?.Length ?? 0); index++)
                    if (int.TryParse(process.ProbabilityParameterNames[index], NumberStyles.None, CultureInfo.InvariantCulture, out var candidate) && candidate >= 0 && candidate < count) weights[candidate] = process.ProbabilityParameterValues[index];
            }
            while (true)
            {
                ConsumeMicrostep();
                var word = random.NextWord(out var width);
                var span = BigInteger.One << width;
                var total = process.ProbabilityDistributionKind == "categorical" ? weights.Aggregate(BigInteger.Zero, (sum, value) => value > 0 ? sum + value : sum) : count;
                if (total <= 0 || total > 9007199254740991L || total > span) throw new InvalidOperationException($"Stochastic process {process.Identifier} has invalid {process.ProbabilityDistributionKind} selection weights.");
                var limit = span - span % total;
                var sample = new BigInteger(word);
                if (sample >= limit) continue;
                var ticket = sample / (limit / total);
                if (process.ProbabilityDistributionKind != "categorical") return (int)ticket;
                var cumulative = BigInteger.Zero;
                for (var index = 0; index < weights.Length; index++)
                {
                    cumulative += Math.Max(0L, weights[index]);
                    if (ticket < cumulative) return index;
                }
                throw new InvalidOperationException($"Stochastic process {process.Identifier} could not map its categorical selection.");
            }
        }

        private void ConsumeMicrostep()
        {
            if (++microsteps > microstepLimit) throw new InvalidOperationException($"VAO deterministic execution exceeded maximumMicrosteps ({microstepLimit}).");
        }

        private Dictionary<string, VaoPrimitiveValue> SnapshotState()
        {
            var snapshot = new Dictionary<string, VaoPrimitiveValue>(StringComparer.Ordinal);
            if (package == null || samplePlayer == null) return snapshot;
            foreach (var state in package.StateVariables) snapshot[state.Identifier] = samplePlayer.GetStateValue(state.Identifier);
            return snapshot;
        }

        private static bool ConditionsMatch(IEnumerable<VaoStateConditionRecord> conditions, IReadOnlyDictionary<string, VaoPrimitiveValue> snapshot)
        {
            foreach (var condition in conditions ?? Enumerable.Empty<VaoStateConditionRecord>())
            {
                snapshot.TryGetValue(condition.StateVariableIdentifier ?? string.Empty, out var actual);
                var comparison = Compare(actual, condition.Value);
                var matches = condition.Operator switch
                {
                    "not-equals" => comparison != 0, "greater-than" => comparison > 0, "greater-than-or-equal" => comparison >= 0,
                    "less-than" => comparison < 0, "less-than-or-equal" => comparison <= 0, _ => comparison == 0
                };
                if (!matches) return false;
            }
            return true;
        }

        private static int Compare(VaoPrimitiveValue left, VaoPrimitiveValue right)
        {
            if (left.Type is "number" or "integer" || right.Type is "number" or "integer") return left.Number.CompareTo(right.Number);
            if (left.Type == "boolean" || right.Type == "boolean") return left.Boolean.CompareTo(right.Boolean);
            return string.CompareOrdinal(left.Text, right.Text);
        }

        private double ProcessInterval(VaoProcessModelRecord process)
        {
            foreach (var id in process.TimingConstraintIdentifiers ?? Array.Empty<string>())
            {
                var constraint = package.FindTimingConstraint(id);
                if (constraint != null && constraint.TimingKind is "repeat-interval" or "duration" or "recovery") return Math.Max(TimeResolutionSeconds(), constraint.ToSeconds(AudioSettings.outputSampleRate));
            }
            return TimeResolutionSeconds();
        }

        private double ConstraintSeconds(string identifier)
        {
            var constraint = package?.FindTimingConstraint(identifier);
            return constraint?.ToSeconds(AudioSettings.outputSampleRate) ?? 0d;
        }

        private double TimeResolutionSeconds()
        {
            if (package == null) return 0.001d;
            var value = package.ExecutionSemantics.TimeResolution;
            return package.ExecutionSemantics.TimeResolutionUnit switch { "milliseconds" => value / 1000d, "samples" or "audio-frames" => value / Math.Max(1, AudioSettings.outputSampleRate), _ => value };
        }

        private bool NormalizeTimestamp(ref double timestamp)
        {
            if (timestamp >= CurrentTime) return true;
            switch (package.ExecutionSemantics.LateEventPolicy)
            {
                case "clamp": timestamp = CurrentTime; return true;
                case "queue-next-cycle": timestamp = CurrentTime + TimeResolutionSeconds(); return true;
                default: return false;
            }
        }

        private void Enqueue(WorkItem item)
        {
            item.Sequence = sequence++;
            queue.Add(item);
        }

        private static int CompareWork(WorkItem left, WorkItem right)
        {
            var timestamp = left.Timestamp.CompareTo(right.Timestamp);
            if (timestamp != 0) return timestamp;
            var priority = right.Priority.CompareTo(left.Priority);
            if (priority != 0) return priority;
            var leftIsEvent = left.Kind is WorkKind.ControlEvent or WorkKind.Event or WorkKind.RoutedEvent;
            var rightIsEvent = right.Kind is WorkKind.ControlEvent or WorkKind.Event or WorkKind.RoutedEvent;
            if (leftIsEvent && rightIsEvent)
            {
                var identifier = VaoUtf8StringComparer.Instance.Compare(left.Identifier, right.Identifier);
                if (identifier != 0) return identifier;
            }
            return left.Sequence.CompareTo(right.Sequence);
        }

        private static VaoExecutionEvent ToPublicEvent(WorkItem item) => new()
        {
            Identifier = item.Identifier, ControlIdentifier = item.ControlIdentifier, EventTypeIdentifier = item.EventTypeIdentifier, ProcessIdentifier = item.ProcessIdentifier,
            TargetIdentifier = item.TargetIdentifier ?? item.Action?.TargetIdentifier, Timestamp = item.Timestamp, Priority = item.Priority, Velocity = item.Velocity, Sequence = item.Sequence, Value = item.Value
        };

        private void OnDisable()
        {
            queue.Clear();
            foreach (var process in activeProcesses.Keys.ToArray()) StopProcess(process);
        }
    }

    internal sealed class VaoUtf8StringComparer : IComparer<string>
    {
        public static readonly VaoUtf8StringComparer Instance = new();

        public int Compare(string left, string right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftScalar = NextScalar(left, ref leftIndex);
                var rightScalar = NextScalar(right, ref rightIndex);
                var comparison = leftScalar.CompareTo(rightScalar);
                if (comparison != 0) return comparison;
            }
            return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        }

        private static int NextScalar(string value, ref int index)
        {
            var first = value[index++];
            if (!char.IsHighSurrogate(first) || index >= value.Length || !char.IsLowSurrogate(value[index])) return first;
            return char.ConvertToUtf32(first, value[index++]);
        }
    }

    internal sealed class VaoDeterministicRandom
    {
        private readonly string algorithm;
        private ulong state;
        private readonly ulong increment;
        private readonly ulong[] xoshiro = new ulong[4];

        public VaoDeterministicRandom(VaoRandomSourceRecord declaration)
        {
            algorithm = declaration?.Algorithm ?? "pcg32";
            if (algorithm == "xoshiro256-star-star")
            {
                InitializeXoshiro(declaration?.Seed);
                return;
            }
            if (!ulong.TryParse(declaration?.Seed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seed)) seed = 0x853c49e6748fea9bUL;
            if (!ulong.TryParse(declaration?.Stream, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var stream)) stream = 0UL;
            increment = unchecked((stream << 1) | 1UL);
            state = 0UL;
            NextPcg32();
            state = unchecked(state + seed);
            NextPcg32();
        }

        public double NextDouble() => algorithm == "xoshiro256-star-star" ? (NextXoshiro() >> 11) * (1d / 9007199254740992d) : NextPcg32() * (1d / 4294967296d);

        public ulong NextWord(out int width)
        {
            if (algorithm == "xoshiro256-star-star") { width = 64; return NextXoshiro(); }
            width = 32;
            return NextPcg32();
        }

        private uint NextPcg32()
        {
            var old = state;
            state = unchecked(old * 6364136223846793005UL + increment);
            var shifted = (uint)(((old >> 18) ^ old) >> 27);
            var rotation = (int)(old >> 59);
            return shifted >> rotation | shifted << ((-rotation) & 31);
        }

        private void InitializeXoshiro(string seedText)
        {
            for (var index = 0; index < 4; index++)
                if (string.IsNullOrEmpty(seedText) || seedText.Length != 64 || !ulong.TryParse(seedText.Substring(index * 16, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out xoshiro[index])) xoshiro[index] = 0UL;
            if ((xoshiro[0] | xoshiro[1] | xoshiro[2] | xoshiro[3]) == 0) xoshiro[0] = 1;
        }

        private ulong NextXoshiro()
        {
            var result = RotateLeft(unchecked(xoshiro[1] * 5UL), 7) * 9UL;
            var temporary = xoshiro[1] << 17;
            xoshiro[2] ^= xoshiro[0]; xoshiro[3] ^= xoshiro[1]; xoshiro[1] ^= xoshiro[2]; xoshiro[0] ^= xoshiro[3];
            xoshiro[2] ^= temporary;
            xoshiro[3] = RotateLeft(xoshiro[3], 45);
            return result;
        }

        private static ulong RotateLeft(ulong value, int count) => value << count | value >> (64 - count);
    }
}
