using System;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    public sealed class VaoArchiveAsset : ScriptableObject
    {
        [SerializeField] private string identifier;
        [SerializeField] private string releaseIdentifier;
        [SerializeField] private string title;
        [SerializeField] private string formatVersion;
        [SerializeField] private string archiveSha256;
        [SerializeField] private long verifiedPayloadBytes;
        [SerializeField] private bool valid;
        [SerializeField] private string[] errors = Array.Empty<string>();
        [SerializeField] private string[] warnings = Array.Empty<string>();

        public string Identifier { get => identifier; internal set => identifier = value; }
        public string ReleaseIdentifier { get => releaseIdentifier; internal set => releaseIdentifier = value; }
        public string Title { get => title; internal set => title = value; }
        public string FormatVersion { get => formatVersion; internal set => formatVersion = value; }
        public string ArchiveSha256 { get => archiveSha256; internal set => archiveSha256 = value; }
        public long VerifiedPayloadBytes { get => verifiedPayloadBytes; internal set => verifiedPayloadBytes = value; }
        public bool Valid { get => valid; internal set => valid = value; }
        public string[] Errors { get => errors; internal set => errors = value ?? Array.Empty<string>(); }
        public string[] Warnings { get => warnings; internal set => warnings = value ?? Array.Empty<string>(); }
    }
}
