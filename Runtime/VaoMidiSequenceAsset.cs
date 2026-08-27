using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modavis.Vao
{
    public enum VaoMidiEventKind
    {
        NoteOn,
        NoteOff,
        ControlChange,
        ProgramChange,
        PitchBend,
        Other
    }

    [Serializable]
    public struct VaoMidiEvent
    {
        public double TimeSeconds;
        public long Tick;
        public VaoMidiEventKind Kind;
        public int Channel;
        public int Number;
        public int Value;
    }

    [CreateAssetMenu(menuName = "MODAVIS/VAO MIDI Sequence", fileName = "VaoMidiSequence")]
    public sealed class VaoMidiSequenceAsset : ScriptableObject
    {
        [SerializeField] private int format;
        [SerializeField] private int ticksPerQuarterNote = 480;
        [SerializeField] private double durationSeconds;
        [SerializeField] private List<VaoMidiEvent> events = new();

        public int Format { get => format; internal set => format = value; }
        public int TicksPerQuarterNote { get => ticksPerQuarterNote; internal set => ticksPerQuarterNote = value; }
        public double DurationSeconds { get => durationSeconds; internal set => durationSeconds = value; }
        public List<VaoMidiEvent> Events => events;
    }
}
