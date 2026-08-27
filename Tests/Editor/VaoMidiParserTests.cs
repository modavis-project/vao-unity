using System.Linq;
using Modavis.Vao;
using NUnit.Framework;
using UnityEngine;

namespace Modavis.Vao.Editor.Tests
{
    public sealed class VaoMidiParserTests
    {
        [Test]
        public void TempoAndRunningStatusProduceDeterministicSeconds()
        {
            var bytes = Hex("4d546864000000060000000100604d54726b0000001300ff510307a12000903c4060803c4000ff2f00");
            var sequence = VaoMidiParser.Parse(bytes, "test");
            try
            {
                Assert.That(sequence.TicksPerQuarterNote, Is.EqualTo(96));
                Assert.That(sequence.Events.Count, Is.EqualTo(2));
                Assert.That(sequence.Events[0].Kind, Is.EqualTo(VaoMidiEventKind.NoteOn));
                Assert.That(sequence.Events[1].Kind, Is.EqualTo(VaoMidiEventKind.NoteOff));
                Assert.That(sequence.Events[1].TimeSeconds, Is.EqualTo(0.5d).Within(1e-9));
            }
            finally { UnityEngine.Object.DestroyImmediate(sequence); }
        }

        [Test]
        public void MidiAnimationUsesManifestLinkedPaths()
        {
            var bytes = Hex("4d546864000000060000000100604d54726b0000001300ff510307a12000903c4060803c4000ff2f00");
            var sequence = VaoMidiParser.Parse(bytes, "test");
            var clip = VaoMidiParser.BuildAnimationClip(sequence, "clip", "M1.{midiNote}", Vector3.right, -4f);
            try
            {
                var binding = UnityEditor.AnimationUtility.GetCurveBindings(clip).Single();
                Assert.That(clip.legacy, Is.False, "Generated MIDI clips must run through Animator/PlayableGraph.");
                Assert.That(binding.path, Is.EqualTo("M1.60"));
                Assert.That(binding.propertyName, Is.EqualTo("localEulerAnglesRaw.x"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(sequence);
            }
        }

        [Test]
        public void MidiAnimationHonorsManifestNoteRange()
        {
            var sequence = ScriptableObject.CreateInstance<VaoMidiSequenceAsset>();
            sequence.Events.Add(new VaoMidiEvent { TimeSeconds = 0d, Kind = VaoMidiEventKind.NoteOn, Number = 29, Value = 100 });
            sequence.Events.Add(new VaoMidiEvent { TimeSeconds = 0.25d, Kind = VaoMidiEventKind.NoteOff, Number = 29 });
            sequence.Events.Add(new VaoMidiEvent { TimeSeconds = 0d, Kind = VaoMidiEventKind.NoteOn, Number = 60, Value = 100 });
            sequence.Events.Add(new VaoMidiEvent { TimeSeconds = 0.25d, Kind = VaoMidiEventKind.NoteOff, Number = 60 });
            var clip = VaoMidiParser.BuildAnimationClip(sequence, "clip", "M1.{midiNote}", Vector3.right, -4f, 36, 84);
            try
            {
                var bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);
                Assert.That(bindings.Select(item => item.path), Is.EquivalentTo(new[] { "M1.60" }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(sequence);
            }
        }

        private static byte[] Hex(string value)
        {
            var result = new byte[value.Length / 2];
            for (var index = 0; index < result.Length; index++)
                result[index] = byte.Parse(value.Substring(index * 2, 2), System.Globalization.NumberStyles.HexNumber);
            return result;
        }
    }
}
