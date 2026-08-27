using System.Collections;
using UnityEngine;

namespace Modavis.Vao
{
    [DisallowMultipleComponent]
    public sealed class VaoMidiSequencePlayer : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private VaoMidiSequenceAsset sequence;
        [SerializeField] private bool playOnStart;
        [SerializeField] private VaoSamplePlayer samplePlayer;
        [SerializeField] private VaoLinkedAnimationPlayer animationPlayer;
        private Coroutine playback;

        public VaoPackageAsset Package { get => package; set => package = value; }
        public VaoMidiSequenceAsset Sequence { get => sequence; set => sequence = value; }
        public bool IsPlaying => playback != null;
        public void SetPackage(VaoPackageAsset value)
        {
            package = value;
            if (sequence == null && package != null && package.MidiSequences.Length > 0) sequence = package.MidiSequences[0];
        }

        private void Awake()
        {
            samplePlayer ??= GetComponent<VaoSamplePlayer>();
            animationPlayer ??= GetComponent<VaoLinkedAnimationPlayer>();
            SetPackage(package);
        }

        private void Start() { if (playOnStart) Play(); }

        public void Play()
        {
            Stop();
            if (sequence != null) playback = StartCoroutine(Perform());
        }

        public void Stop()
        {
            if (playback != null) StopCoroutine(playback);
            playback = null;
            samplePlayer?.AllNotesOff();
        }

        private IEnumerator Perform()
        {
            var previous = 0d;
            foreach (var midiEvent in sequence.Events)
            {
                var delay = midiEvent.TimeSeconds - previous;
                if (delay > 0d) yield return new WaitForSeconds((float)delay);
                previous = midiEvent.TimeSeconds;
                switch (midiEvent.Kind)
                {
                    case VaoMidiEventKind.NoteOn when midiEvent.Value > 0:
                        samplePlayer?.NoteOn(midiEvent.Number, midiEvent.Value);
                        animationPlayer?.NoteOn(midiEvent.Number);
                        break;
                    case VaoMidiEventKind.NoteOn:
                    case VaoMidiEventKind.NoteOff:
                        samplePlayer?.NoteOff(midiEvent.Number);
                        animationPlayer?.NoteOff(midiEvent.Number);
                        break;
                }
            }
            playback = null;
        }

        private void OnDisable() => Stop();
    }
}
