using System;
using System.Collections;
using UnityEngine;

namespace Modavis.Vao
{
    /// <summary>Dependency-free MIDI 1.0 byte and MIDI 2.0 UMP ingress for imported protocol bindings.</summary>
    [DisallowMultipleComponent]
    public sealed class VaoMidiRouter : MonoBehaviour, IVaoPackageConsumer
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private VaoSamplePlayer samplePlayer;
        [SerializeField] private VaoLinkedAnimationPlayer animationPlayer;
        private ushort jrClock;
        private ushort? pendingJrTimestamp;
        private double jrClockDspTime;

        public VaoPackageAsset Package { get => package; set => package = value; }
        public event Action<VaoProtocolBindingRecord, int> BindingReceived;
        public event Action<VaoProtocolBindingRecord, uint> HighResolutionBindingReceived;
        public double PendingJrDelaySeconds { get; private set; }

        public void SetPackage(VaoPackageAsset value) => Package = value;

        private void Awake()
        {
            samplePlayer ??= GetComponent<VaoSamplePlayer>();
            animationPlayer ??= GetComponent<VaoLinkedAnimationPlayer>();
        }

        public void ProcessMidi1(byte status, byte data1, byte data2 = 0)
        {
            ResolvePlayers();
            if (package == null || status < 0x80 || status >= 0xf0) return;
            var kind = status & 0xf0;
            var channel = status & 0x0f;
            if (kind is 0x80 or 0x90)
            {
                if (!HasInputBinding("MIDI-1.0", "note", channel, data1, -1, -1)) return;
                if (kind == 0x90 && data2 > 0) { samplePlayer?.NoteOn(data1, data2); animationPlayer?.NoteOn(data1); }
                else { samplePlayer?.NoteOff(data1); animationPlayer?.NoteOff(data1); }
                return;
            }
            var messageType = kind switch { 0xb0 => "control-change", 0xc0 => "program-change", 0xd0 => "channel-pressure", 0xe0 => "pitch-bend", _ => null };
            if (messageType == null) return;
            foreach (var binding in package.ProtocolBindings)
            {
                if (!Matches(binding, "MIDI-1.0", messageType, channel, -1, -1)) continue;
                if (binding.Number >= 0 && binding.Number != data1) continue;
                var value = messageType == "pitch-bend" ? data1 | (data2 << 7) : messageType == "program-change" ? data1 : data2;
                if (messageType == "control-change" && IsDeactivation(binding, value)) continue;
                samplePlayer?.ActivateControl(binding.ControlIdentifier, binding.EventTypeIdentifier, VaoPrimitiveValue.FromNumber(value));
                BindingReceived?.Invoke(binding, value);
            }
        }

        /// <summary>Processes one 64-bit MIDI 2.0 Channel Voice UMP (two 32-bit words).</summary>
        public void ProcessUmpUtility(uint word)
        {
            if ((word >> 28) != 0) return;
            var status = (word >> 20) & 0x0f;
            var value = (ushort)(word & 0xffff);
            if (status == 1) { jrClock = value; jrClockDspTime = AudioSettings.dspTime; }
            else if (status == 2) pendingJrTimestamp = value;
        }

        public void ProcessMidi2Ump(uint word0, uint word1, int functionBlock = -1)
        {
            ResolvePlayers();
            if (package == null) return;
            var messageType = (int)((word0 >> 28) & 0x0f);
            if (messageType != 4) return;
            var group = (int)((word0 >> 24) & 0x0f);
            var status = (int)((word0 >> 20) & 0x0f);
            var channel = (int)((word0 >> 16) & 0x0f);
            var note = (int)((word0 >> 8) & 0x7f);
            if (pendingJrTimestamp.HasValue)
            {
                var delta = (short)(pendingJrTimestamp.Value - jrClock);
                pendingJrTimestamp = null;
                PendingJrDelaySeconds = Math.Max(0d, jrClockDspTime + delta / 31250d - AudioSettings.dspTime);
                if (PendingJrDelaySeconds > 0.0001d) { StartCoroutine(DispatchMidi2After((float)PendingJrDelaySeconds, word0, word1, functionBlock)); return; }
            }
            PendingJrDelaySeconds = 0d;
            DispatchMidi2(word0, word1, functionBlock, messageType, group, status, channel, note);
        }

        private void DispatchMidi2(uint word0, uint word1, int functionBlock, int messageType, int group, int status, int channel, int note)
        {
            if (status is 8 or 9)
            {
                if (!HasInputBinding("MIDI-2.0", "note", channel, note, group, messageType, functionBlock)) return;
                var velocity16 = (int)((word1 >> 16) & 0xffff);
                var velocity7 = Mathf.Clamp(Mathf.RoundToInt(velocity16 * 127f / 65535f), 0, 127);
                if (status == 9 && velocity16 > 0) { samplePlayer?.NoteOn(note, velocity7); animationPlayer?.NoteOn(note); }
                else { samplePlayer?.NoteOff(note); animationPlayer?.NoteOff(note); }
                return;
            }
            var mappedType = status switch
            {
                0x0 or 0x1 or 0x2 or 0x3 or 0x4 or 0x5 or 0xa => "addressed-value",
                0x6 => "pitch-bend",
                0xb => "control-change",
                0xc => "program-change",
                0xd => "channel-pressure",
                0xe => "pitch-bend",
                _ => null
            };
            if (mappedType == null) return;
            var number = status switch
            {
                0x0 or 0x1 or 0x2 or 0x3 or 0x4 or 0x5 or 0xb => (int)((word0 >> 8) & 0x7f),
                0x6 or 0xa => note,
                0xc => (int)((word1 >> 24) & 0x7f),
                _ => -1
            };
            var unsignedValue = status == 0xc ? (uint)number : word1;
            foreach (var binding in package.ProtocolBindings)
            {
                if (!Matches(binding, "MIDI-2.0", mappedType, channel, group, messageType, functionBlock)) continue;
                if (binding.Number >= 0 && binding.Number != number) continue;
                if (IsDeactivation(binding, unsignedValue)) continue;
                samplePlayer?.ActivateControl(binding.ControlIdentifier, binding.EventTypeIdentifier, VaoPrimitiveValue.FromNumber(unsignedValue));
                BindingReceived?.Invoke(binding, unchecked((int)unsignedValue));
                HighResolutionBindingReceived?.Invoke(binding, unsignedValue);
            }
        }

        private IEnumerator DispatchMidi2After(float seconds, uint word0, uint word1, int functionBlock)
        {
            yield return new WaitForSecondsRealtime(seconds);
            var messageType = (int)((word0 >> 28) & 0x0f);
            DispatchMidi2(word0, word1, functionBlock, messageType, (int)((word0 >> 24) & 0x0f), (int)((word0 >> 20) & 0x0f), (int)((word0 >> 16) & 0x0f), (int)((word0 >> 8) & 0x7f));
        }

        private bool HasInputBinding(string protocol, string messageType, int channel, int number, int group, int umpType, int functionBlock = -1)
        {
            foreach (var binding in package.ProtocolBindings)
                if (Matches(binding, protocol, messageType, channel, group, umpType, functionBlock) && (binding.Number < 0 || binding.Number == number)) return true;
            return false;
        }

        private void ResolvePlayers()
        {
            samplePlayer ??= GetComponent<VaoSamplePlayer>();
            animationPlayer ??= GetComponent<VaoLinkedAnimationPlayer>();
        }

        private static bool Matches(VaoProtocolBindingRecord binding, string protocol, string messageType, int channel, int group, int umpType, int functionBlock = -1)
        {
            if (binding.Protocol != protocol || binding.Direction != "input" || binding.MessageType != messageType) return false;
            if (binding.Channel - binding.ChannelNumberingBase != channel) return false;
            if (group >= 0 && binding.UmpGroup >= 0 && binding.UmpGroup != group) return false;
            if (functionBlock >= 0 && binding.FunctionBlock >= 0 && binding.FunctionBlock != functionBlock) return false;
            return umpType < 0 || binding.UmpMessageType < 0 || binding.UmpMessageType == umpType;
        }

        private static bool IsDeactivation(VaoProtocolBindingRecord binding, double value) => binding.HasDeactivationValue && binding.DeactivationValue.Type is "number" or "integer" && Math.Abs(binding.DeactivationValue.Number - value) < double.Epsilon;
    }
}
