using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Modavis.Vao;
using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public static class VaoMidiParser
    {
        private sealed class RawEvent
        {
            public long Tick;
            public int Order;
            public VaoMidiEventKind Kind;
            public int Channel;
            public int Number;
            public int Value;
        }

        private readonly struct TempoEvent
        {
            public TempoEvent(long tick, int microseconds) { Tick = tick; Microseconds = microseconds; }
            public long Tick { get; }
            public int Microseconds { get; }
        }

        public static VaoMidiSequenceAsset Parse(byte[] data, string name = "VAO MIDI Sequence")
        {
            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream);
            if (ReadAscii(reader, 4) != "MThd") throw new InvalidDataException("MIDI header chunk is missing.");
            var headerLength = ReadUInt32(reader);
            if (headerLength < 6) throw new InvalidDataException("MIDI header chunk is too short.");
            var format = ReadUInt16(reader);
            var trackCount = ReadUInt16(reader);
            var division = ReadUInt16(reader);
            if ((division & 0x8000) != 0) throw new InvalidDataException("SMPTE MIDI time division is not currently supported.");
            if (division == 0 || trackCount == 0) throw new InvalidDataException("MIDI timing metadata is invalid.");
            stream.Position += headerLength - 6;

            var events = new List<RawEvent>();
            var tempos = new List<TempoEvent> { new(0, 500000) };
            var order = 0;
            for (var track = 0; track < trackCount; track++) ParseTrack(reader, events, tempos, ref order);
            tempos = tempos.GroupBy(item => item.Tick).Select(group => group.Last()).OrderBy(item => item.Tick).ToList();
            events.Sort((left, right) => left.Tick != right.Tick ? left.Tick.CompareTo(right.Tick) : left.Order.CompareTo(right.Order));

            var sequence = ScriptableObject.CreateInstance<VaoMidiSequenceAsset>();
            sequence.name = name;
            sequence.Format = format;
            sequence.TicksPerQuarterNote = division;
            foreach (var item in events)
            {
                var time = TicksToSeconds(item.Tick, division, tempos);
                sequence.Events.Add(new VaoMidiEvent { Tick = item.Tick, TimeSeconds = time, Kind = item.Kind, Channel = item.Channel, Number = item.Number, Value = item.Value });
                sequence.DurationSeconds = Math.Max(sequence.DurationSeconds, time);
            }
            return sequence;
        }

        public static VaoMidiSequenceAsset ParseFile(string absolutePath, string name = null) => Parse(File.ReadAllBytes(absolutePath), name ?? Path.GetFileNameWithoutExtension(absolutePath));

        public static AnimationClip BuildAnimationClip(VaoMidiSequenceAsset sequence, string name, string pathPattern, Vector3 rotationAxis, float pressedAngleDegrees, int minimumMidiNote = 0, int maximumMidiNote = 127)
        {
            // Generated VAO clips target the Animator/PlayableGraph backend. The
            // runtime still accepts explicitly authored legacy clips as a fallback.
            var clip = new AnimationClip { name = name, legacy = false, frameRate = 60f };
            var axis = DominantAxis(rotationAxis);
            var property = axis switch { 1 => "localEulerAnglesRaw.y", 2 => "localEulerAnglesRaw.z", _ => "localEulerAnglesRaw.x" };
            var direction = axis switch { 1 => Mathf.Sign(rotationAxis.y), 2 => Mathf.Sign(rotationAxis.z), _ => Mathf.Sign(rotationAxis.x) };
            if (Mathf.Approximately(direction, 0f)) direction = 1f;
            foreach (var group in sequence.Events.Where(item => item.Kind is VaoMidiEventKind.NoteOn or VaoMidiEventKind.NoteOff).Where(item => item.Number >= minimumMidiNote && item.Number <= maximumMidiNote).GroupBy(item => item.Number))
            {
                var curve = new AnimationCurve(new Keyframe(0f, 0f));
                foreach (var item in group)
                {
                    var pressed = item.Kind == VaoMidiEventKind.NoteOn && item.Value > 0;
                    curve.AddKey(new Keyframe((float)item.TimeSeconds, pressed ? pressedAngleDegrees * direction : 0f));
                }
                var path = (pathPattern ?? "{midiNote}").Replace("{midiNote}", group.Key.ToString());
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), property), curve);
            }
            return clip;
        }

        private static void ParseTrack(BinaryReader reader, ICollection<RawEvent> events, ICollection<TempoEvent> tempos, ref int order)
        {
            if (ReadAscii(reader, 4) != "MTrk") throw new InvalidDataException("MIDI track chunk is missing.");
            var length = ReadUInt32(reader);
            var end = checked(reader.BaseStream.Position + length);
            long tick = 0;
            byte runningStatus = 0;
            while (reader.BaseStream.Position < end)
            {
                tick += ReadVariableLength(reader);
                var first = reader.ReadByte();
                byte status;
                var hasFirstData = first < 0x80;
                byte firstData = 0;
                if (hasFirstData)
                {
                    if (runningStatus == 0) throw new InvalidDataException("MIDI running status has no preceding channel status.");
                    status = runningStatus;
                    firstData = first;
                }
                else status = first;

                if (status == 0xFF)
                {
                    runningStatus = 0;
                    var metaType = reader.ReadByte();
                    var metaLength = ReadVariableLength(reader);
                    if (metaType == 0x51 && metaLength == 3)
                    {
                        var tempo = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
                        tempos.Add(new TempoEvent(tick, tempo));
                    }
                    else reader.BaseStream.Position += metaLength;
                    continue;
                }
                if (status is 0xF0 or 0xF7)
                {
                    runningStatus = 0;
                    reader.BaseStream.Position += ReadVariableLength(reader);
                    continue;
                }
                if (status >= 0xF0) throw new InvalidDataException($"Unsupported MIDI system status 0x{status:X2}.");
                runningStatus = status;
                var kind = status & 0xF0;
                var channel = status & 0x0F;
                var data1 = hasFirstData ? firstData : reader.ReadByte();
                var data2 = kind is 0xC0 or 0xD0 ? 0 : reader.ReadByte();
                var eventKind = kind switch
                {
                    0x80 => VaoMidiEventKind.NoteOff,
                    0x90 => data2 == 0 ? VaoMidiEventKind.NoteOff : VaoMidiEventKind.NoteOn,
                    0xB0 => VaoMidiEventKind.ControlChange,
                    0xC0 => VaoMidiEventKind.ProgramChange,
                    0xE0 => VaoMidiEventKind.PitchBend,
                    _ => VaoMidiEventKind.Other
                };
                var value = kind == 0xE0 ? data1 | (data2 << 7) : kind == 0xC0 ? data1 : data2;
                var number = kind == 0xC0 ? data1 : data1;
                events.Add(new RawEvent { Tick = tick, Order = order++, Kind = eventKind, Channel = channel, Number = number, Value = value });
            }
            if (reader.BaseStream.Position != end) throw new InvalidDataException("MIDI track length is inconsistent.");
        }

        private static double TicksToSeconds(long tick, int division, IReadOnlyList<TempoEvent> tempos)
        {
            var seconds = 0d;
            long previousTick = 0;
            var tempo = 500000;
            foreach (var change in tempos)
            {
                if (change.Tick > tick) break;
                seconds += (change.Tick - previousTick) * tempo / (division * 1_000_000d);
                previousTick = change.Tick;
                tempo = change.Microseconds;
            }
            return seconds + (tick - previousTick) * tempo / (division * 1_000_000d);
        }

        private static int DominantAxis(Vector3 axis)
        {
            var absolute = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
            return absolute.y > absolute.x && absolute.y >= absolute.z ? 1 : absolute.z > absolute.x ? 2 : 0;
        }

        private static string ReadAscii(BinaryReader reader, int length) => System.Text.Encoding.ASCII.GetString(reader.ReadBytes(length));
        private static ushort ReadUInt16(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(2);
            if (bytes.Length != 2) throw new EndOfStreamException();
            return (ushort)((bytes[0] << 8) | bytes[1]);
        }
        private static uint ReadUInt32(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4) throw new EndOfStreamException();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
        private static long ReadVariableLength(BinaryReader reader)
        {
            long value = 0;
            for (var index = 0; index < 4; index++)
            {
                var next = reader.ReadByte();
                value = (value << 7) | (uint)(next & 0x7F);
                if ((next & 0x80) == 0) return value;
            }
            throw new InvalidDataException("MIDI variable-length value exceeds four bytes.");
        }
    }
}
