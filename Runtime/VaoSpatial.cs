using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modavis.Vao
{
    public enum VaoSpatialRole { Geometry, AcousticEmitter, AcousticReceiver, Instrument, Other }

    [DisallowMultipleComponent]
    public sealed class VaoSpatialAnchor : MonoBehaviour
    {
        [SerializeField] private string subjectIdentifier;
        [SerializeField] private string poseIdentifier;
        [SerializeField] private string coordinateFrameIdentifier;
        [SerializeField] private VaoSpatialRole role;

        public string SubjectIdentifier { get => subjectIdentifier; set => subjectIdentifier = value; }
        public string PoseIdentifier { get => poseIdentifier; set => poseIdentifier = value; }
        public string CoordinateFrameIdentifier { get => coordinateFrameIdentifier; set => coordinateFrameIdentifier = value; }
        public VaoSpatialRole Role { get => role; set => role = value; }
    }

    public static class VaoSpatialMath
    {
        public static Func<string, float?> CustomUnitScaleResolver { get; set; }

        public static Matrix4x4 FrameToUnity(VaoPackageAsset package, string frameIdentifier)
        {
            return FrameToUnity(package, frameIdentifier, new HashSet<string>(StringComparer.Ordinal));
        }

        public static Matrix4x4 PoseToUnity(VaoPackageAsset package, VaoPoseRecord pose)
        {
            if (pose == null) return Matrix4x4.identity;
            return FrameToUnity(package, pose.CoordinateFrameIdentifier) * Matrix4x4.TRS(pose.Position, pose.Orientation, pose.Scale == Vector3.zero ? Vector3.one : pose.Scale);
        }

        public static void Apply(Transform target, Matrix4x4 matrix)
        {
            target.localPosition = matrix.GetColumn(3);
            var right = (Vector3)matrix.GetColumn(0);
            var up = (Vector3)matrix.GetColumn(1);
            var forward = (Vector3)matrix.GetColumn(2);
            var scale = new Vector3(right.magnitude, up.magnitude, forward.magnitude);
            if (Vector3.Dot(Vector3.Cross(right, up), forward) < 0f) scale.z = -scale.z;
            if (Mathf.Abs(scale.x) < 1e-8f || Mathf.Abs(scale.y) < 1e-8f || Mathf.Abs(scale.z) < 1e-8f)
            {
                target.localRotation = Quaternion.identity;
                target.localScale = Vector3.one;
                return;
            }
            target.localRotation = Quaternion.LookRotation(forward / Mathf.Abs(scale.z), up / scale.y);
            target.localScale = scale;
        }

        public static float UnitScale(string unit)
        {
            var builtIn = unit switch
            {
                "http://qudt.org/vocab/unit/M" or "https://qudt.org/vocab/unit/M" => 1f,
                "http://qudt.org/vocab/unit/DeciM" or "https://qudt.org/vocab/unit/DeciM" => 0.1f,
                "http://qudt.org/vocab/unit/CentiM" or "https://qudt.org/vocab/unit/CentiM" => 0.01f,
                "http://qudt.org/vocab/unit/MilliM" or "https://qudt.org/vocab/unit/MilliM" => 0.001f,
                "http://qudt.org/vocab/unit/KiloM" or "https://qudt.org/vocab/unit/KiloM" => 1000f,
                "http://qudt.org/vocab/unit/FT" or "https://qudt.org/vocab/unit/FT" => 0.3048f,
                "http://qudt.org/vocab/unit/IN" or "https://qudt.org/vocab/unit/IN" => 0.0254f,
                _ => float.NaN
            };
            if (!float.IsNaN(builtIn)) return builtIn;
            var custom = CustomUnitScaleResolver?.Invoke(unit);
            if (custom is > 0f) return custom.Value;
            throw new NotSupportedException($"No Unity scale is registered for coordinate unit '{unit}'.");
        }

        private static Matrix4x4 FrameToUnity(VaoPackageAsset package, string frameIdentifier, ISet<string> visited)
        {
            if (package == null || string.IsNullOrEmpty(frameIdentifier)) return Matrix4x4.identity;
            if (!visited.Add(frameIdentifier)) throw new InvalidOperationException($"Coordinate-frame cycle at {frameIdentifier}.");
            var frame = package.FindCoordinateFrame(frameIdentifier);
            if (frame == null) return Matrix4x4.identity;
            if (!string.IsNullOrEmpty(frame.ParentFrameIdentifier))
                return FrameToUnity(package, frame.ParentFrameIdentifier, visited) * ReadMatrix(frame.TransformToParent);
            var axis = frame.UpAxis switch
            {
                "+Z" or "Z" => Matrix4x4.Rotate(Quaternion.Euler(-90f, 0f, 0f)),
                "+X" or "X" => Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, 90f)),
                "-Z" => Matrix4x4.Rotate(Quaternion.Euler(90f, 0f, 0f)),
                "-X" => Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, -90f)),
                "-Y" => Matrix4x4.Rotate(Quaternion.Euler(180f, 0f, 0f)),
                _ => Matrix4x4.identity
            };
            if (TryAxis(frame.ForwardAxis, out var declaredForward))
            {
                var mappedForward = axis.MultiplyVector(declaredForward);
                mappedForward.y = 0f;
                if (mappedForward.sqrMagnitude > 1e-8f)
                    axis = Matrix4x4.Rotate(Quaternion.FromToRotation(mappedForward.normalized, Vector3.forward)) * axis;
            }
            if (frame.Handedness == "left") axis *= Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            return axis * Matrix4x4.Scale(Vector3.one * UnitScale(frame.Unit));
        }

        private static bool TryAxis(string value, out Vector3 axis)
        {
            axis = value switch
            {
                "+X" => Vector3.right, "-X" => Vector3.left,
                "+Y" => Vector3.up, "-Y" => Vector3.down,
                "+Z" => Vector3.forward, "-Z" => Vector3.back,
                _ => Vector3.zero
            };
            return axis != Vector3.zero;
        }

        private static Matrix4x4 ReadMatrix(IReadOnlyList<float> values)
        {
            if (values == null || values.Count != 16) return Matrix4x4.identity;
            var matrix = new Matrix4x4();
            for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++) matrix[row, column] = values[row * 4 + column];
            return matrix;
        }
    }
}
