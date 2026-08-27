using System;
using System.IO;
using System.Linq;
using Modavis.Vao;
using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    internal static class VaoSofaImporter
    {
        public static VaoSofaAsset Import(string sourceAssetPath, string generatedFolder, string name, VaoImportResult result)
        {
            if (string.IsNullOrWhiteSpace(sourceAssetPath)) return null;
            var packageInfo = sourceAssetPath.StartsWith("Packages/", StringComparison.Ordinal) ? UnityEditor.PackageManager.PackageInfo.FindForAssetPath(sourceAssetPath) : null;
            var absolute = packageInfo != null
                ? Path.Combine(packageInfo.resolvedPath, sourceAssetPath.Substring(("Packages/" + packageInfo.name + "/").Length))
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", sourceAssetPath));
            var asset = VaoSofaDecoder.Decode(absolute, string.IsNullOrWhiteSpace(name) ? "Decoded SOFA" : name + " SOFA");
            var path = AssetDatabase.GenerateUniqueAssetPath(generatedFolder.TrimEnd('/') + "/" + SafeFileName(asset.name) + ".asset");
            AssetDatabase.CreateAsset(asset, path);
            result?.ImportedAssetPaths.Add(path);
            return asset;
        }

        private static string SafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string((value ?? "SOFA").Select(character => invalid.Contains(character) || character is '/' or '\\' ? '-' : character).ToArray());
        }
    }
}
