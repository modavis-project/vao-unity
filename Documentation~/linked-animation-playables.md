# Linked animation with Animator and Playables

`VaoLinkedAnimationPlayer` uses one `PlayableGraph` per target root. Each non-legacy linked clip becomes an ordered `AnimationLayerMixerPlayable` layer, so unrelated model roots remain independent and mechanisms on the same root can be combined.

The importer reads optional relation properties whose local names end in `layerOrder`, `additive`, `weight`, `blendSeconds`, `playbackSpeed`, `speedCurve`, or `avatarMaskLogicalAssetId`. Generated MIDI clips are non-legacy and use this backend automatically. Explicitly authored legacy clips continue to run through Unity's `Animation` component.

At runtime, use `PlayLinkedClip`, `PauseLinkedClip`, `ResumeLinkedClip`, `StopLinkedClip`, or `SetLinkedClipNormalizedTime` for transport. `BlendLinkedClipWeight` and `CrossFadeLinkedClips` adjust layer weights. `SetLayerConfiguration` can override order, additive mode, weight, fade time, speed, mask, and speed curve before graph creation.

Reusable `VaoAnimationSequence` objects contain ordered `VaoAnimationSequenceStep` ranges. A step can play forward or backward, choose speed and weight, fade in, hold at its endpoint, and rewind. Call `PlaySequence` with the sequence or its identifier; call `StopSequence` to cancel it.

If an existing `Animator` has a controller, it is preserved as the base mixer layer by default. Disable `PreserveAnimatorController` only when the VAO graph should own the complete pose. Avatar Masks must be imported Unity `AvatarMask` objects and declared or assigned explicitly; the importer never guesses masks from object names.
