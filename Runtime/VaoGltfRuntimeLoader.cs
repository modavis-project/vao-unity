using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace Modavis.Vao
{
    /// <summary>Optional glTFast bridge that keeps glTF support dependency-free.</summary>
    public sealed class VaoGltfRuntimeLoader : MonoBehaviour
    {
        [SerializeField] private string runtimeUri;
        [SerializeField] private string realizationIdentifier;
        [SerializeField] private bool loadOnStart = true;
        [SerializeField] private bool clearPreviousScene = true;
        [SerializeField] private VaoBooleanEvent loadedChanged = new();
        [SerializeField] private VaoStringEvent loadFailed = new();
        private object importer;
        private Transform sceneRoot;
        private VaoRuntimeMaterializer materializer;
        private int loadGeneration;
        public string RuntimeUri { get => runtimeUri; set => runtimeUri = value; }
        public string RealizationIdentifier { get => realizationIdentifier; set => realizationIdentifier = value; }
        public bool IsLoaded { get; private set; }
        public VaoBooleanEvent LoadedChanged => loadedChanged;
        public VaoStringEvent LoadFailed => loadFailed;

        private void OnEnable()
        {
            materializer = GetComponentInParent<VaoRuntimeMaterializer>();
            if (materializer != null) materializer.Materialized += OnMaterialized;
        }

        private async void Start() { if (loadOnStart && !string.IsNullOrEmpty(runtimeUri)) await LoadAsync(); }

        private void OnDisable() { if (materializer != null) materializer.Materialized -= OnMaterialized; materializer = null; }

        public async Task<bool> LoadAsync()
        {
            var generation = ++loadGeneration;
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("GLTFast.GltfImport", false)).FirstOrDefault(item => item != null);
            if (type == null)
            {
                Fail("VAO preserved a glTF/GLB model, but glTFast is not installed. Install com.unity.cloud.gltfast to instantiate it at runtime.");
                return false;
            }
            try
            {
                if (clearPreviousScene) Unload(false);
                if (importer is IDisposable disposable) disposable.Dispose();
                importer = Activator.CreateInstance(type);
                var resolvedUri = ResolveRuntimeUri(runtimeUri);
                if (!await InvokeTask(type, importer, "Load", resolvedUri) || generation != loadGeneration) return false;
                var holder = new GameObject("VAO glTF Scene").transform;
                holder.SetParent(transform, false);
                sceneRoot = holder;
                var instantiated = await InvokeTask(type, importer, "InstantiateMainSceneAsync", holder);
                if (generation != loadGeneration) { if (holder != null) Destroy(holder.gameObject); return false; }
                if (!instantiated) { if (holder != null) Destroy(holder.gameObject); sceneRoot = null; Fail("glTFast loaded the VAO model but could not instantiate its main scene."); return false; }
                IsLoaded = true;
                loadedChanged.Invoke(true);
                GetComponentInParent<VaoLinkedAnimationPlayer>()?.RebuildTargets();
                return true;
            }
            catch (Exception exception)
            {
                Fail("glTFast failed to load the VAO model: " + (exception is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException.Message : exception.Message));
                return false;
            }
        }

        public void Unload() => Unload(true);

        private void Unload(bool invalidateLoad)
        {
            if (invalidateLoad) loadGeneration++;
            if (sceneRoot != null) Destroy(sceneRoot.gameObject);
            sceneRoot = null;
            if (IsLoaded) loadedChanged.Invoke(false);
            IsLoaded = false;
        }

        private async void OnMaterialized(VaoMaterializationResult result)
        {
            if (!result.Succeeded || result.RealizationIdentifier != realizationIdentifier || string.IsNullOrEmpty(result.LocalPath)) return;
            var acquiredUri = new Uri(result.LocalPath).AbsoluteUri;
            if (IsLoaded && runtimeUri == acquiredUri) return;
            runtimeUri = acquiredUri;
            await LoadAsync();
        }

        private void Fail(string message)
        {
            IsLoaded = false;
            Debug.LogWarning(message, this);
            loadFailed.Invoke(message);
        }

        private static string ResolveRuntimeUri(string uri)
        {
            if (string.IsNullOrEmpty(uri) || Uri.TryCreate(uri, UriKind.Absolute, out _)) return uri;
            const string prefix = "Assets/StreamingAssets/";
            var relative = uri.StartsWith(prefix, StringComparison.Ordinal) ? uri.Substring(prefix.Length) : uri;
            if (Uri.TryCreate(Application.streamingAssetsPath.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)) return new Uri(baseUri, relative).AbsoluteUri;
            return new Uri(System.IO.Path.Combine(Application.streamingAssetsPath, relative)).AbsoluteUri;
        }

        private static async Task<bool> InvokeTask(Type type, object target, string name, object firstArgument)
        {
            var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(item => item.Name == name && item.GetParameters().Length > 0 && item.GetParameters()[0].ParameterType.IsInstanceOfType(firstArgument));
            if (method == null) return false;
            var parameters = method.GetParameters();
            var arguments = new object[parameters.Length];
            arguments[0] = firstArgument;
            for (var index = 1; index < arguments.Length; index++)
                arguments[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : parameters[index].ParameterType.IsValueType ? Activator.CreateInstance(parameters[index].ParameterType) : null;
            if (method.Invoke(target, arguments) is not Task task) return false;
            await task;
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            return result is not bool value || value;
        }

        private void OnDestroy()
        {
            loadGeneration++;
            if (materializer != null) materializer.Materialized -= OnMaterialized;
            if (importer is IDisposable disposable) disposable.Dispose();
            importer = null;
        }
    }
}
