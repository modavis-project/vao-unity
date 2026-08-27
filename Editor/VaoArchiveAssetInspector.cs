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
                if (GUILayout.Button("Import verified VAO payload…"))
                {
                    var path = AssetDatabase.GetAssetPath(target);
                    var result = VaoImporter.Import(path);
                    EditorGUIUtility.PingObject(result.Package);
                }
            }
        }
    }
}
