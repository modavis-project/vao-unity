using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modavis.Vao
{
    [CreateAssetMenu(menuName = "MODAVIS/VAO Package", fileName = "VaoPackage")]
    public sealed class VaoPackageAsset : ScriptableObject
    {
        [SerializeField] private string formatVersion = "0.4.0";
        [SerializeField] private string identifier;
        [SerializeField] private string releaseIdentifier;
        [SerializeField] private string title;
        [SerializeField, TextArea] private string description;
        [SerializeField] private string sourceArchiveSha256;
        [SerializeField] private string sourceArchivePath;
        [SerializeField] private string sourceArchiveGuid;
        [SerializeField] private string importedAtUtc;
        [SerializeField] private VaoImportSettingsRecord importSettings = new();
        [SerializeField, TextArea(3, 12)] private string rawManifestJson;
        [SerializeField] private List<VaoJsonSectionRecord> profileSections = new();
        [SerializeField] private List<VaoEntityRecord> entities = new();
        [SerializeField] private List<VaoRelationRecord> relations = new();
        [SerializeField] private List<VaoLogicalAssetRecord> logicalAssets = new();
        [SerializeField] private List<VaoRealizationRecord> realizations = new();
        [SerializeField] private List<VaoDistributionRecord> distributions = new();
        [SerializeField] private List<VaoRepositoryBindingRecord> repositoryBindings = new();
        [SerializeField] private List<VaoRightsRecord> rights = new();
        [SerializeField] private List<VaoAssetGroupRecord> assetGroups = new();
        [SerializeField] private List<string> capabilities = new();
        [SerializeField] private List<VaoControlRecord> controls = new();
        [SerializeField] private List<VaoStateVariableRecord> stateVariables = new();
        [SerializeField] private List<VaoTransitionRecord> transitions = new();
        [SerializeField] private List<VaoProtocolBindingRecord> protocolBindings = new();
        [SerializeField] private List<VaoEventTypeRecord> eventTypes = new();
        [SerializeField] private List<VaoTimingConstraintRecord> timingConstraints = new();
        [SerializeField] private List<VaoProcessModelRecord> processModels = new();
        [SerializeField] private List<VaoRenderBindingRecord> renderBindings = new();
        [SerializeField] private List<VaoRoutingRuleRecord> routingRules = new();
        [SerializeField] private List<VaoRandomSourceRecord> randomSources = new();
        [SerializeField] private VaoExecutionSemanticsRecord executionSemantics = new();
        [SerializeField] private List<VaoTimebaseRecord> timebases = new();
        [SerializeField] private List<VaoTrackRecord> tracks = new();
        [SerializeField] private List<VaoSynchronizationMappingRecord> synchronizationMappings = new();
        [SerializeField] private List<VaoSampleBinding> sampleBindings = new();
        [SerializeField] private List<VaoAnimationLink> animationLinks = new();
        [SerializeField] private List<VaoAcousticSceneRecord> acousticScenes = new();
        [SerializeField] private List<VaoCoordinateFrameRecord> coordinateFrames = new();
        [SerializeField] private List<VaoPoseRecord> poses = new();
        [SerializeField] private List<VaoGeometryBindingRecord> geometryBindings = new();
        [SerializeField] private VaoMidiSequenceAsset[] midiSequences = Array.Empty<VaoMidiSequenceAsset>();
        [SerializeField] private GameObject prefab;

        public string FormatVersion { get => formatVersion; internal set => formatVersion = value; }
        public string Identifier { get => identifier; internal set => identifier = value; }
        public string ReleaseIdentifier { get => releaseIdentifier; internal set => releaseIdentifier = value; }
        public string Title { get => title; internal set => title = value; }
        public string Description { get => description; internal set => description = value; }
        public string SourceArchiveSha256 { get => sourceArchiveSha256; internal set => sourceArchiveSha256 = value; }
        public string SourceArchivePath { get => sourceArchivePath; internal set => sourceArchivePath = value; }
        public string SourceArchiveGuid { get => sourceArchiveGuid; internal set => sourceArchiveGuid = value; }
        public string ImportedAtUtc { get => importedAtUtc; internal set => importedAtUtc = value; }
        public VaoImportSettingsRecord ImportSettings => importSettings;
        public string RawManifestJson { get => rawManifestJson; internal set => rawManifestJson = value; }
        public List<VaoJsonSectionRecord> ProfileSections => profileSections;
        public List<VaoEntityRecord> Entities => entities;
        public List<VaoRelationRecord> Relations => relations;
        public List<VaoLogicalAssetRecord> LogicalAssets => logicalAssets;
        public List<VaoRealizationRecord> Realizations => realizations;
        public List<VaoDistributionRecord> Distributions => distributions;
        public List<VaoRepositoryBindingRecord> RepositoryBindings => repositoryBindings;
        public List<VaoRightsRecord> Rights => rights;
        public List<VaoAssetGroupRecord> AssetGroups => assetGroups;
        public List<string> Capabilities => capabilities;
        public List<VaoControlRecord> Controls => controls;
        public List<VaoStateVariableRecord> StateVariables => stateVariables;
        public List<VaoTransitionRecord> Transitions => transitions;
        public List<VaoProtocolBindingRecord> ProtocolBindings => protocolBindings;
        public List<VaoEventTypeRecord> EventTypes => eventTypes;
        public List<VaoTimingConstraintRecord> TimingConstraints => timingConstraints;
        public List<VaoProcessModelRecord> ProcessModels => processModels;
        public List<VaoRenderBindingRecord> RenderBindings => renderBindings;
        public List<VaoRoutingRuleRecord> RoutingRules => routingRules;
        public List<VaoRandomSourceRecord> RandomSources => randomSources;
        public VaoExecutionSemanticsRecord ExecutionSemantics => executionSemantics;
        public List<VaoTimebaseRecord> Timebases => timebases;
        public List<VaoTrackRecord> Tracks => tracks;
        public List<VaoSynchronizationMappingRecord> SynchronizationMappings => synchronizationMappings;
        public List<VaoSampleBinding> SampleBindings => sampleBindings;
        public List<VaoAnimationLink> AnimationLinks => animationLinks;
        public List<VaoAcousticSceneRecord> AcousticScenes => acousticScenes;
        public List<VaoCoordinateFrameRecord> CoordinateFrames => coordinateFrames;
        public List<VaoPoseRecord> Poses => poses;
        public List<VaoGeometryBindingRecord> GeometryBindings => geometryBindings;
        public VaoMidiSequenceAsset[] MidiSequences { get => midiSequences; internal set => midiSequences = value ?? Array.Empty<VaoMidiSequenceAsset>(); }
        public GameObject Prefab { get => prefab; internal set => prefab = value; }

        public VaoControlRecord FindControl(string id) => controls.Find(item => item.Identifier == id);
        public VaoLogicalAssetRecord FindLogicalAsset(string id) => logicalAssets.Find(item => item.Identifier == id);
        public VaoRealizationRecord FindRealization(string id) => realizations.Find(item => item.Identifier == id);
        public List<VaoRealizationRecord> FindRealizationsForLogicalAsset(string id) => realizations.FindAll(item => item.LogicalAssetIdentifier == id);
        public List<VaoDistributionRecord> FindDistributionsForRealization(string id)
        {
            var realization = FindRealization(id);
            return realization == null ? new List<VaoDistributionRecord>() : distributions.FindAll(item => Array.IndexOf(realization.DistributionIdentifiers, item.Identifier) >= 0);
        }
        public List<VaoRightsRecord> FindRightsForRealization(string id)
        {
            var realization = FindRealization(id);
            return realization == null ? new List<VaoRightsRecord>() : rights.FindAll(item => Array.IndexOf(realization.RightsIdentifiers, item.Identifier) >= 0 || Array.IndexOf(item.AppliesToIdentifiers, id) >= 0);
        }
        public VaoRepositoryBindingRecord FindRepositoryBinding(string id) => repositoryBindings.Find(item => item.Identifier == id);
        public List<VaoRelationRecord> FindRelationsFrom(string id) => relations.FindAll(item => item.SubjectIdentifier == id);
        public List<VaoRelationRecord> FindRelationsTo(string id) => relations.FindAll(item => item.ObjectIdentifier == id);
        public VaoJsonSectionRecord FindProfileSection(string name) => profileSections.Find(item => item.Name == name);
        public VaoPoseRecord FindPoseForSubject(string id) => poses.Find(item => item.SubjectIdentifier == id);
        public VaoCoordinateFrameRecord FindCoordinateFrame(string id) => coordinateFrames.Find(item => item.Identifier == id);
        public VaoProcessModelRecord FindProcessModel(string id) => processModels.Find(item => item.Identifier == id);
        public VaoRenderBindingRecord FindRenderBinding(string id) => renderBindings.Find(item => item.Identifier == id);
        public VaoTimingConstraintRecord FindTimingConstraint(string id) => timingConstraints.Find(item => item.Identifier == id);
        public VaoTimebaseRecord FindTimebase(string id) => timebases.Find(item => item.Identifier == id);
        public VaoTrackRecord FindTrack(string id) => tracks.Find(item => item.Identifier == id);
        public VaoPresentationBundle ResolvePresentation(string logicalAssetIdentifier, VaoPresentationResolveOptions options = null)
            => VaoPresentationResolver.Resolve(this, logicalAssetIdentifier, options);
    }

    [Serializable]
    public sealed class VaoJsonSectionRecord
    {
        public string Name;
        [TextArea(2, 10)] public string Json;
    }

    [Serializable]
    public sealed class VaoEntityRecord
    {
        public string Identifier;
        public string Kind;
        public string Label;
        public string[] Types = Array.Empty<string>();
    }

    [Serializable]
    public sealed class VaoRelationRecord
    {
        public string Identifier;
        public string SubjectIdentifier;
        public string Predicate;
        public string ObjectIdentifier;
        public string Status;
        [TextArea] public string PropertiesJson;
    }

    [Serializable]
    public sealed class VaoLogicalAssetRecord
    {
        public string Identifier;
        public string Label;
        public string[] Roles = Array.Empty<string>();
        public string[] AboutEntityIdentifiers = Array.Empty<string>();
        public string[] RealizationIdentifiers = Array.Empty<string>();
        [TextArea] public string PropertiesJson;
    }

    [Serializable]
    public sealed class VaoImportSettingsRecord
    {
        public string MaterializationMode;
        public string[] SelectedAssetGroupIdentifiers = Array.Empty<string>();
        public long MaximumMaterializedBytes;
        public bool CreatePrefab;
        public bool CreateRuntimeControlSurface;
        public bool GenerateMidiAnimationClips;
        public bool CopyGlbToStreamingAssets;
        public bool VerifyPayloadDigests;
        public string[] ManagedRelativePaths = Array.Empty<string>();
    }

    [Serializable]
    public sealed class VaoRealizationRecord
    {
        public string Identifier;
        public string LogicalAssetIdentifier;
        public string MediaType;
        public string Sha256;
        public long ByteSize;
        public string AssetPath;
        public string RuntimeUri;
        public string CarrierPath;
        public string CoordinateFrameIdentifier;
        public bool IsMaterialized;
        public string[] Roles = Array.Empty<string>();
        public string[] RightsIdentifiers = Array.Empty<string>();
        public string[] DistributionIdentifiers = Array.Empty<string>();
        public string QualityTier;
        public UnityEngine.Object ImportedObject;
    }

    [Serializable]
    public sealed class VaoDistributionRecord
    {
        public string Identifier;
        public string Kind;
        public string RepositoryBindingIdentifier;
        public string PersistentIdentifier;
        public string ConceptIdentifier;
        public string RecordIdentifier;
        public string FileIdentifier;
        public string Access;
        public string TransportSha256;
        public string PackRealizationIdentifier;
        public string MemberPath;
        public string PackManifestSha256;
    }

    [Serializable]
    public sealed class VaoRepositoryBindingRecord
    {
        public string Identifier;
        public string RepositoryType;
        public string Instance;
        public string ApiProfile;
        public string ResolutionPolicy;
    }

    [Serializable]
    public sealed class VaoRightsRecord
    {
        public string Identifier;
        public string[] AppliesToIdentifiers = Array.Empty<string>();
        public string License;
        public string Statement;
        public string Access;
        public string Attribution;
    }

    [Serializable]
    public sealed class VaoAssetGroupRecord
    {
        public string Identifier;
        public string Label;
        public string Availability;
        public string QualityTier;
        public string[] RealizationIdentifiers = Array.Empty<string>();
        public string[] DependencyIdentifiers = Array.Empty<string>();
        public long TotalByteSize;
        public bool Evictable;
        public int CachePriority;
    }

    [Serializable]
    public sealed class VaoControlRecord
    {
        public string Identifier;
        public string Label;
        public string Behavior;
        public string ValueType;
        public string StateVariableIdentifier;
        public bool DefaultBoolean;
        public int MidiChannel;
        public int MidiNumber;
        public string MidiMessageType;
    }

    [Serializable]
    public sealed class VaoStateVariableRecord
    {
        public string Identifier;
        public string Label;
        public string ValueType;
        public string Persistence;
        public string SubjectEntityIdentifier;
        public VaoPrimitiveValue DefaultValue;
        public double MinimumValue;
        public double MaximumValue;
        public bool HasMinimum;
        public bool HasMaximum;
    }

    [Serializable]
    public struct VaoPrimitiveValue
    {
        public string Type;
        public bool Boolean;
        public double Number;
        public string Text;

        public static VaoPrimitiveValue FromBoolean(bool value) => new() { Type = "boolean", Boolean = value };
        public static VaoPrimitiveValue FromNumber(double value) => new() { Type = "number", Number = value };
        public static VaoPrimitiveValue FromText(string value) => new() { Type = "string", Text = value };
    }

    [Serializable]
    public sealed class VaoStateConditionRecord
    {
        public string StateVariableIdentifier;
        public string Operator;
        public VaoPrimitiveValue Value;
    }

    [Serializable]
    public sealed class VaoDeclarativeActionRecord
    {
        public string Operation;
        public string TargetIdentifier;
        public VaoPrimitiveValue Value;
        public bool HasValue;
        public int KeyOffset;
        public string DelayConstraintIdentifier;
        public string ExecutionGroup;
    }

    [Serializable]
    public sealed class VaoTransitionRecord
    {
        public string Identifier;
        public string ControlIdentifier;
        public string EventTypeIdentifier;
        public bool Atomic;
        public string ConflictPolicy;
        public int Priority;
        public List<VaoStateConditionRecord> Conditions = new();
        public List<VaoDeclarativeActionRecord> Actions = new();
    }

    [Serializable]
    public sealed class VaoProtocolBindingRecord
    {
        public string Identifier;
        public string Protocol;
        public string Direction;
        public string ControlIdentifier;
        public string EventTypeIdentifier;
        public string MessageType;
        public int Channel;
        public int ChannelNumberingBase;
        public int Number;
        public VaoPrimitiveValue ActivationValue;
        public VaoPrimitiveValue DeactivationValue;
        public bool HasActivationValue;
        public bool HasDeactivationValue;
        public int UmpGroup;
        public int FunctionBlock;
        public int UmpMessageType;
        public int DataResolutionBits;
        public bool JrTimestamp;
    }

    [Serializable]
    public sealed class VaoSampleBinding
    {
        public string MappingIdentifier;
        public string VariantIdentifier;
        public string RealizationIdentifier;
        public string RankEntityIdentifier;
        public string StateVariableIdentifier;
        public string SelectionPolicy;
        public string Trigger;
        public string SignalRole;
        public string RoundRobinGroup;
        public int RoundRobinIndex;
        public float SelectionWeight = 1f;
        public int MinimumKey;
        public int MaximumKey = 127;
        public int MinimumVelocity = 1;
        public int MaximumVelocity = 127;
        public int SampleRootKey = 60;
        public int SoundingKeyOffset;
        public float GainDecibels;
        public float PitchTuningCents;
        public string NoteOffPolicy;
        public AudioClip Clip;
        public string RuntimeUri;
    }

    [Serializable]
    public sealed class VaoAnimationLink
    {
        public string Identifier;
        public string SourceLogicalAssetIdentifier;
        public string AnimationLogicalAssetIdentifier;
        public string TargetLogicalAssetIdentifier;
        public string TargetPathPattern = "{midiNote}";
        public int MinimumMidiNote;
        public int MaximumMidiNote = 127;
        public Vector3 RotationAxis = Vector3.right;
        public float PressedAngleDegrees = -4f;
        public int LayerOrder;
        public bool Additive;
        public float Weight = 1f;
        public float BlendSeconds = 0.08f;
        public float PlaybackSpeed = 1f;
        public AvatarMask Mask;
        public AnimationCurve SpeedCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        public AnimationClip SourceClip;
        public AnimationClip GeneratedMidiClip;
        public VaoMidiSequenceAsset MidiSequence;
    }

    [Serializable]
    public sealed class VaoAnimationTargetRoot
    {
        public string LogicalAssetIdentifier;
        public Transform Root;
    }

    [Serializable]
    public sealed class VaoAcousticSceneRecord
    {
        public string Identifier;
        public string SceneEntityIdentifier;
        public string RepresentationType;
        public string CoordinateFrameIdentifier;
        public string RenderConfigurationIdentifier;
        public string RenderStrategy;
        public string ResponseSetIdentifier;
        public string ResponseRealizationIdentifier;
        public string ResponseKind;
        public string ResponseEncoding;
        public string SofaConvention;
        public string InterpolationMethod;
        public string InterpolationDomainIdentifier;
        public string OutsideDomainPolicy;
        public string FallbackResponseSetIdentifier;
        public string ListenerMode;
        public string ReceiverIdentifier;
        public string ListenerPoseIdentifier;
        public string[] InputIdentifiers = Array.Empty<string>();
        public string[] FallbackIdentifiers = Array.Empty<string>();
        public List<VaoAcousticRuntimeFeatureRecord> RuntimeFeatures = new();
        public float TransitionSeconds;
        public AudioClip ImpulseResponse;
        public VaoSofaAsset Sofa;
        public List<VaoAcousticResponsePointRecord> ResponsePoints = new();
        public string RuntimeUri;
        public bool IsMeasured;
        public bool IsSimulated;
    }

    [Serializable]
    public sealed class VaoAcousticRuntimeFeatureRecord
    {
        public string Feature;
        public string Mode;
        public string[] InputIdentifiers = Array.Empty<string>();
    }

    [Serializable]
    public sealed class VaoAcousticResponsePointRecord
    {
        public string MeasurementIdentifier;
        public string RealizationIdentifier;
        public string SourceIdentifier;
        public string ReceiverIdentifier;
        public string SourcePoseIdentifier;
        public string ReceiverPoseIdentifier;
        public Vector3 SourcePosition;
        public Vector3 ReceiverPosition;
        public int[] ChannelIndices = Array.Empty<int>();
        public int SofaDataIndex = -1;
        public float DelaySamples;
        public AudioClip ImpulseResponse;
        public VaoSofaAsset Sofa;
    }

    [Serializable]
    public sealed class VaoCoordinateFrameRecord
    {
        public string Identifier;
        public string ParentFrameIdentifier;
        public string CoordinateType;
        public string Unit;
        public string UpAxis;
        public string ForwardAxis;
        public string Handedness;
        public float[] TransformToParent = Array.Empty<float>();
    }

    [Serializable]
    public sealed class VaoPoseRecord
    {
        public string Identifier;
        public string SubjectIdentifier;
        public string CoordinateFrameIdentifier;
        public Vector3 Position;
        public Quaternion Orientation = Quaternion.identity;
        public Vector3 Scale = Vector3.one;
        public string Interpolation;
    }

    [Serializable]
    public sealed class VaoGeometryBindingRecord
    {
        public string Identifier;
        public string LogicalAssetIdentifier;
        public string SubjectIdentifier;
        public string Role;
    }
}
