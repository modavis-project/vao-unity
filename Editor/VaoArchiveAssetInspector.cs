using UnityEditor;
using UnityEngine;

namespace Modavis.Vao.Editor
{
    [CustomEditor(typeof(VaoArchiveAsset))]
    public sealed class VaoArchiveAssetInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var archive = (VaoArchiveAsset)target;
            using (new EditorGUI.DisabledScope(!archive.Valid))
            {
                var metadataOnly = archive.HasMechanicalMusicProfile;
                if (GUILayout.Button(metadataOnly ? "Import VAO metadata…" : "Import verified VAO payload…"))
                {
                    var path = AssetDatabase.GetAssetPath(target);
                    var options = metadataOnly
                        ? new VaoImportOptions { MaterializationMode = VaoMaterializationMode.MetadataOnly, CreatePrefab = false }
                        : null;
                    var result = VaoImporter.Import(path, options);
                    EditorGUIUtility.PingObject(result.Package);
                }
            }
        }
    }
}
