using UnityEngine;

namespace Modavis.Vao
{
    [DisallowMultipleComponent]
    public sealed class VaoRuntimeObject : MonoBehaviour
    {
        [SerializeField] private VaoPackageAsset package;
        [SerializeField] private Transform visualRoot;

        public VaoPackageAsset Package { get => package; set => package = value; }
        public Transform VisualRoot { get => visualRoot != null ? visualRoot : transform; set => visualRoot = value; }

        private void Awake()
        {
            foreach (var component in GetComponents<MonoBehaviour>())
            {
                if (component is IVaoPackageConsumer consumer) consumer.SetPackage(package);
            }
        }
    }

    public interface IVaoPackageConsumer
    {
        void SetPackage(VaoPackageAsset package);
    }
}
