# Deterministic VAO execution

Every imported prefab contains `VaoDeterministicExecutor`. `VaoSamplePlayer.ActivateControl` delegates to it, so existing Unity buttons, MIDI adapters, XR interactables, and generated controls use the typed VAO execution model without a second integration path.

The importer compiles event types, execution semantics, timing constraints, process models, declarative actions, render bindings, routing rules, random sources, timebases, tracks, and piecewise synchronization mappings into `VaoPackageAsset` records. The source JSON snapshots remain available for fields a host only needs to inspect.

## Scheduler guarantees

The executor maintains a timestamped queue and applies the VAO ordering contract: ascending time, descending priority, event identifier, then stable insertion sequence. Transition conditions use a state snapshot. Actions use execution group followed by source array order, run to completion, and honor declared delays. Reentrant input is queued or rejected, late input is rejected, clamped, or moved to the next execution quantum, and `maximumMicrosteps` terminates a zero-time runaway with an explicit exception.

Transition conflicts are resolved before action execution. Higher-priority transitions claim conflicting state, process, or render targets; atomic transitions are accepted or rejected as a unit. `last-event-wins` replaces the prior conflicting action deterministically, while `reject`, `priority`, and `merge-disjoint` retain only non-conflicting work.

```csharp
var executor = GetComponent<Modavis.Vao.VaoDeterministicExecutor>();

executor.ExecuteControlNow(controlId, eventTypeId,
    Modavis.Vao.VaoPrimitiveValue.FromNumber(64));

executor.ScheduleControlEvent(controlId, eventTypeId,
    Modavis.Vao.VaoPrimitiveValue.FromNumber(64), timestampSeconds: 2.5);

executor.AdvanceTo(3.0); // Useful for deterministic tests or a host-owned clock.
```

With the Unity clock enabled, `Update` advances the same queue automatically. Tests and offline hosts can schedule input and call `AdvanceTo` explicitly.

## Processes, routing, and rendering

One-shot, sustained, repeating, sequenced, compound, and stochastic process declarations execute their actions and child processes. Completion, maximum-iteration, duration-bound, control-release, and external-cancel policies are applied. The final 0.4.0 uniform and integer-weight categorical choices use exact high-tail rejection with the declared source. Both `pcg32` and `xoshiro256-star-star` are implemented: PCG uses the declared 16-hex-digit seed and stream, while xoshiro uses its declared 64-hex-digit state seed.

Key routing supports identity, transpose, fixed fan-out, and table fan-out transforms, input-range and state conditions, timing delays, and routed-event notification. `EventRouted` carries the declared target entity, output key, velocity, and timestamp to a host actuator or device adapter. Sample playback follows render bindings, keeping routing meaning separate from media selection.

Render bindings can be selected by an action or automatically by their event type. Single, simultaneous, ordered, state-dependent, and host-defined policies are preserved. Native sample mappings play through `VaoSamplePlayer`; `RenderBindingSelected` lets a host implement a specialized target.

The executor exposes `EventEmitted`, `EventRouted`, `ActionExecuted`, `UnhandledAction`, process lifecycle, and render-selection events. An operation that cannot be performed meaningfully by the generic Unity runtime is also forwarded through `VaoSamplePlayer.ActionRequested`; package-supplied code is never executed.

## Multimodal synchronization

`VaoSynchronizationEngine.TryMap` maps values through direct, inverse, or chained piecewise clock segments. Undeclared gaps are rejected rather than extrapolated, and wrapped timebases normalize around their declared origin. `TryMapSeconds` converts between timebase values and seconds. `VaoMediaPlayer` uses the linked realizations' tracks to synchronize audio and animation before falling back to normalized duration.

The importer retains exact rational timebase numerators and denominators alongside the runtime `double` value. Conformance-trace digests use RFC 8785 canonical JSON. The tests pin the sequences published by the VAO 0.4.0 reference implementation so changes in Unity or the .NET runtime cannot silently alter replay behavior.
