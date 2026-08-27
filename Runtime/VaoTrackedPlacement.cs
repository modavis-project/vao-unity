using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Modavis.Vao
{
    [Serializable] public sealed class VaoBooleanEvent : UnityEvent<bool> { }

    /// <summary>
    /// SDK-neutral placement and tracking bridge. Vuforia, AR Foundation, OpenXR,
    /// or a custom tracker can forward anchor and tracking callbacks to this
    /// component without introducing a package dependency on that SDK.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VaoTrackedPlacement : MonoBehaviour
    {
        [SerializeField] private Transform placementRoot;
        [SerializeField] private Transform contentRoot;
        [SerializeField] private bool hideWhenTrackingIsLost = true;
        [SerializeField] private bool muteWhenTrackingIsLost = true;
        [SerializeField] private bool snapToAnchor = true;
        [SerializeField, Min(0.0001f)] private float minimumScale = 0.05f;
        [SerializeField, Min(0.0001f)] private float maximumScale = 10f;
        [SerializeField] private string scalePersistenceKey;
        [SerializeField] private VaoBooleanEvent trackingChanged = new();

        private readonly Dictionary<Renderer, bool> renderers = new();
        private readonly Dictionary<Collider, bool> colliders = new();
        private readonly Dictionary<Canvas, bool> canvases = new();
        private readonly Dictionary<AudioSource, bool> audioMuteStates = new();
        private Transform initialParent;
        private Vector3 initialLocalPosition;
        private Quaternion initialLocalRotation;
        private Vector3 initialLocalScale;
        private bool isTracking = true;

        public Transform PlacementRoot { get => placementRoot != null ? placementRoot : transform; set => placementRoot = value; }
        public Transform ContentRoot { get => contentRoot != null ? contentRoot : PlacementRoot; set { contentRoot = value; RefreshContentState(); } }
        public bool IsTracking => isTracking;
        public float UniformScale => PlacementRoot.localScale.x;
        public VaoBooleanEvent TrackingChanged => trackingChanged;

        private void Awake()
        {
            var root = PlacementRoot;
            initialParent = root.parent;
            initialLocalPosition = root.localPosition;
            initialLocalRotation = root.localRotation;
            initialLocalScale = root.localScale;
            RefreshContentState();
            if (!string.IsNullOrWhiteSpace(scalePersistenceKey) && PlayerPrefs.HasKey(scalePersistenceKey))
                SetUniformScale(PlayerPrefs.GetFloat(scalePersistenceKey));
        }

        public void RefreshContentState()
        {
            renderers.Clear(); colliders.Clear(); canvases.Clear(); audioMuteStates.Clear();
            var root = ContentRoot;
            if (root == null) return;
            foreach (var item in root.GetComponentsInChildren<Renderer>(true)) renderers[item] = item.enabled;
            foreach (var item in root.GetComponentsInChildren<Collider>(true)) colliders[item] = item.enabled;
            foreach (var item in root.GetComponentsInChildren<Canvas>(true)) canvases[item] = item.enabled;
            foreach (var item in root.GetComponentsInChildren<AudioSource>(true)) audioMuteStates[item] = item.mute;
        }

        public void AttachToAnchor(Transform anchor)
        {
            if (anchor == null) return;
            var root = PlacementRoot;
            root.SetParent(anchor, !snapToAnchor);
            if (!snapToAnchor) return;
            root.localPosition = Vector3.zero;
            root.localRotation = Quaternion.identity;
        }

        public void SetTrackedWorldPose(Vector3 position, Quaternion rotation)
        {
            var root = PlacementRoot;
            root.SetPositionAndRotation(position, rotation);
        }

        public void SetTrackedLocalPose(Vector3 position, Quaternion rotation)
        {
            var root = PlacementRoot;
            root.localPosition = position;
            root.localRotation = rotation;
        }

        public void SetTrackingActive(bool active)
        {
            if (isTracking == active) return;
            isTracking = active;
            if (hideWhenTrackingIsLost) SetContentVisible(active);
            trackingChanged.Invoke(active);
        }

        public void OnTrackingFound() => SetTrackingActive(true);
        public void OnTrackingLost() => SetTrackingActive(false);

        public void SetContentVisible(bool visible)
        {
            foreach (var item in renderers) if (item.Key != null) item.Key.enabled = visible && item.Value;
            foreach (var item in colliders) if (item.Key != null) item.Key.enabled = visible && item.Value;
            foreach (var item in canvases) if (item.Key != null) item.Key.enabled = visible && item.Value;
            if (muteWhenTrackingIsLost)
                foreach (var item in audioMuteStates) if (item.Key != null) item.Key.mute = visible ? item.Value : true;
        }

        public void SetUniformScale(float value)
        {
            var scale = Mathf.Clamp(value, Mathf.Min(minimumScale, maximumScale), Mathf.Max(minimumScale, maximumScale));
            PlacementRoot.localScale = Vector3.one * scale;
            if (string.IsNullOrWhiteSpace(scalePersistenceKey)) return;
            PlayerPrefs.SetFloat(scalePersistenceKey, scale);
            PlayerPrefs.Save();
        }

        public void SetNormalizedScale(float normalized)
            => SetUniformScale(Mathf.Lerp(Mathf.Min(minimumScale, maximumScale), Mathf.Max(minimumScale, maximumScale), Mathf.Clamp01(normalized)));

        public void ResetPlacement()
        {
            var root = PlacementRoot;
            root.SetParent(initialParent, false);
            root.localPosition = initialLocalPosition;
            root.localRotation = initialLocalRotation;
            root.localScale = initialLocalScale;
            if (!string.IsNullOrWhiteSpace(scalePersistenceKey)) PlayerPrefs.DeleteKey(scalePersistenceKey);
            SetTrackingActive(true);
        }
    }
}
