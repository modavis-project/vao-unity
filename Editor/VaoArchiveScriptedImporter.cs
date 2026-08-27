using System.Linq;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    [ScriptedImporter(1, "vao")]
    public sealed class VaoArchiveScriptedImporter : ScriptedImporter
    {
        [SerializeField] private bool verifyPayloadDigests = true;

        public override void OnImportAsset(AssetImportContext context)
        {
            var inspection = VaoArchiveReader.Inspect(context.assetPath, new VaoValidationPolicy { VerifyPayloadDigests = verifyPayloadDigests });
            var asset = ScriptableObject.CreateInstance<VaoArchiveAsset>();
            asset.name = string.IsNullOrWhiteSpace(inspection.Title) ? System.IO.Path.GetFileNameWithoutExtension(context.assetPath) : inspection.Title;
            asset.Identifier = inspection.Identifier;
            asset.ReleaseIdentifier = inspection.Manifest?.SelectToken("release.id")?.ToObject<string>();
            asset.Title = inspection.Title;
            asset.FormatVersion = inspection.Manifest?.Value<string>("formatVersion");
            asset.ArchiveSha256 = inspection.ArchiveSha256;
            asset.VerifiedPayloadBytes = inspection.VerifiedPayloadBytes;
            asset.Valid = inspection.IsValid;
            asset.Errors = inspection.Errors.ToArray();
            asset.Warnings = inspection.Warnings.ToArray();
            context.AddObjectToAsset("VAO", asset);
            context.SetMainObject(asset);
            foreach (var error in inspection.Errors) context.LogImportError(error);
            foreach (var warning in inspection.Warnings) context.LogImportWarning(warning);
        }
    }
}
