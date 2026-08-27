using System;
using System.IO;
using System.Linq;
using PureHDF;
using UnityEngine;

namespace Modavis.Vao
{
    /// <summary>Platform-neutral AES69-SOFA FIR decoder backed by the managed PureHDF reader.</summary>
    public static class VaoSofaDecoder
    {
        private const long MaximumFirValueCount = 256L * 1024L * 1024L;

        public static VaoSofaAsset Decode(string path, string name = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("SOFA realization is missing.", path);
            using var file = H5File.OpenRead(path);
            var conventions = AttributeString(file, "Conventions");
            var convention = AttributeString(file, "SOFAConventions");
            var version = AttributeString(file, "SOFAConventionsVersion");
            var dataType = AttributeString(file, "DataType");
            if (!string.Equals(conventions, "SOFA", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{path} is HDF5 but does not declare the SOFA convention.");
            if (!string.Equals(dataType, "FIR", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException($"SOFA DataType '{dataType}' is not a time-domain FIR response. VAO Unity supports AES69 FIR SOFA realizations.");

            var irDataset = file.Dataset("Data.IR");
            var dimensions = irDataset.Space.Dimensions.Select(value => checked((int)value)).ToArray();
            if (dimensions.Length < 3 || dimensions.Any(value => value <= 0)) throw new InvalidDataException("SOFA Data.IR must have at least M, R, and N dimensions.");
            var measurements = dimensions[0];
            var receivers = dimensions[1];
            var samples = dimensions[^1];
            var emitters = dimensions.Skip(2).Take(dimensions.Length - 3).Aggregate(1, (left, right) => checked(left * right));
            if (emitters != 1) throw new NotSupportedException("SOFA FIR data with multiple emitter dimensions is not supported by the single-emitter VAO convolution voice. Split or select the emitter explicitly before import.");
            var sourceValueCount = dimensions.Aggregate(1L, (left, right) => checked(left * right));
            var outputValueCount = checked((long)measurements * receivers * samples);
            if (sourceValueCount > MaximumFirValueCount || outputValueCount > MaximumFirValueCount) throw new InvalidDataException("SOFA FIR data exceeds the managed import safety limit.");
            var sourceIr = ReadNumeric(irDataset, "Data.IR");
            if (sourceIr.LongLength != sourceValueCount) throw new InvalidDataException("SOFA Data.IR decoded length does not match its dataspace.");
            var responses = new float[checked((int)outputValueCount)];
            for (var measurement = 0; measurement < measurements; measurement++)
                for (var receiver = 0; receiver < receivers; receiver++)
                    for (var sample = 0; sample < samples; sample++)
                    {
                        var sourceIndex = ((measurement * receivers + receiver) * emitters) * samples + sample;
                        responses[(measurement * receivers + receiver) * samples + sample] = checked((float)sourceIr[sourceIndex]);
                    }

            var rateValues = ReadNumeric(file.Dataset("Data.SamplingRate"), "Data.SamplingRate");
            var sampleRate = rateValues.Length > 0 ? Mathf.RoundToInt((float)rateValues[0]) : 0;
            if (sampleRate <= 0) throw new InvalidDataException("SOFA Data.SamplingRate is missing or invalid.");
            var asset = ScriptableObject.CreateInstance<VaoSofaAsset>();
            asset.name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) + " SOFA" : name;
            asset.Initialize(convention, version, dataType, measurements, receivers, samples, sampleRate, ReadSourcePositions(file, measurements), responses, ReadDelays(file, measurements, receivers));
            return asset;
        }

        private static Vector3[] ReadSourcePositions(IH5Group file, int measurements)
        {
            var dataset = file.Dataset("SourcePosition");
            var values = ReadNumeric(dataset, "SourcePosition");
            var dimensions = dataset.Space.Dimensions.Select(value => checked((int)value)).ToArray();
            var coordinateAxis = Array.FindIndex(dimensions, 1, value => value >= 3);
            if (dimensions.Length < 2 || coordinateAxis < 1) throw new InvalidDataException("SOFA SourcePosition must contain a coordinate dimension with at least three values.");
            var count = dimensions[0];
            if (count != 1 && count < measurements) throw new InvalidDataException("SOFA SourcePosition has fewer rows than Data.IR measurements.");
            var type = AttributeString(dataset, "Type");
            var units = AttributeString(dataset, "Units");
            var rowStride = dimensions.Skip(1).Aggregate(1, (left, right) => checked(left * right));
            var coordinateStride = dimensions.Skip(coordinateAxis + 1).Aggregate(1, (left, right) => checked(left * right));
            var positions = new Vector3[measurements];
            for (var index = 0; index < measurements; index++)
            {
                var row = count == 1 ? 0 : index;
                var offset = row * rowStride;
                var a = (float)values[offset];
                var b = (float)values[offset + coordinateStride];
                var c = (float)values[offset + 2 * coordinateStride];
                positions[index] = string.Equals(type, "spherical", StringComparison.OrdinalIgnoreCase) ? SphericalToUnity(a, b, c, units) : new Vector3(-b, c, a);
            }
            return positions;
        }

        private static Vector3 SphericalToUnity(float azimuth, float elevation, float radius, string units)
        {
            if (units?.IndexOf("radian", StringComparison.OrdinalIgnoreCase) < 0) { azimuth *= Mathf.Deg2Rad; elevation *= Mathf.Deg2Rad; }
            radius = Mathf.Approximately(radius, 0f) ? 1f : radius;
            var horizontal = Mathf.Cos(elevation) * radius;
            return new Vector3(-Mathf.Sin(azimuth) * horizontal, Mathf.Sin(elevation) * radius, Mathf.Cos(azimuth) * horizontal);
        }

        private static float[] ReadDelays(IH5Group file, int measurements, int receivers)
        {
            if (!file.LinkExists("Data.Delay")) return Array.Empty<float>();
            var values = ReadNumeric(file.Dataset("Data.Delay"), "Data.Delay");
            if (values.Length != receivers && values.Length != measurements * receivers) return Array.Empty<float>();
            return values.Select(value => checked((float)value)).ToArray();
        }

        private static string AttributeString(IH5Object value, string name)
        {
            if (value == null || !value.AttributeExists(name)) return null;
            try { return value.Attribute(name).Read<string>(); }
            catch { return value.Attribute(name).Read<string[]>().FirstOrDefault(); }
        }

        private static double[] ReadNumeric(IH5Dataset dataset, string name)
        {
            try { return dataset.Read<double[]>(); }
            catch (Exception doubleFailure)
            {
                try { return dataset.Read<float[]>().Select(value => (double)value).ToArray(); }
                catch (Exception floatFailure) { throw new InvalidDataException($"SOFA {name} must be a floating-point numeric dataset.", new AggregateException(doubleFailure, floatFailure)); }
            }
        }
    }
}
