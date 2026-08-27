using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;

namespace Modavis.Vao.Editor
{
    /// <summary>
    /// Offline JSON Schema 2020-12 evaluator for every assertion keyword used by
    /// the immutable VAO 0.4.0 manifest, carrier, and materialization-receipt
    /// schemas. The schemas themselves are vendored byte-for-byte under Editor/Schemas.
    /// </summary>
    internal sealed class VaoJsonSchemaValidator
    {
        private const int MaximumErrors = 256;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
        private readonly JToken rootSchema;

        private VaoJsonSchemaValidator(JToken rootSchema) => this.rootSchema = rootSchema;

        public static IReadOnlyList<string> ValidateManifest(JToken instance) => Validate(instance, "vao-manifest-0.4.0.schema.json");
        public static IReadOnlyList<string> ValidateCarrier(JToken instance) => Validate(instance, "vao-carrier-0.4.0.schema.json");
        public static IReadOnlyList<string> ValidateMaterializationReceipt(JToken instance) => Validate(instance, "vao-materialization-receipt-0.4.0.schema.json");

        private static IReadOnlyList<string> Validate(JToken instance, string schemaFile)
        {
            var package = PackageInfo.FindForAssembly(typeof(VaoJsonSchemaValidator).Assembly);
            if (package == null) return new[] { $"Normative schema package location could not be resolved for {schemaFile}." };
            var path = Path.Combine(package.resolvedPath, "Editor", "Schemas", schemaFile);
            if (!File.Exists(path)) return new[] { $"Vendored normative schema is missing: {schemaFile}." };
            var schema = JToken.Parse(File.ReadAllText(path));
            var errors = new List<string>();
            new VaoJsonSchemaValidator(schema).Evaluate(instance, schema, "$", errors);
            return errors;
        }

        private bool Evaluate(JToken instance, JToken schemaToken, string path, List<string> errors)
        {
            if (errors.Count >= MaximumErrors) return false;
            if (schemaToken.Type == JTokenType.Boolean)
            {
                if (schemaToken.Value<bool>()) return true;
                Add(errors, path, "is rejected by the false schema");
                return false;
            }
            if (schemaToken is not JObject schema) return true;
            var before = errors.Count;

            if (schema.Value<string>("$ref") is { } reference)
            {
                var resolved = ResolveReference(reference);
                if (resolved == null) Add(errors, path, $"uses an unresolved schema reference '{reference}'");
                else Evaluate(instance, resolved, path, errors);
            }

            if (schema["type"] != null && !MatchesType(instance, schema["type"])) Add(errors, path, $"must be {DescribeTypes(schema["type"])} but is {DescribeType(instance)}");
            if (schema["const"] != null && !JToken.DeepEquals(instance, schema["const"])) Add(errors, path, $"must equal {schema["const"].ToString(Newtonsoft.Json.Formatting.None)}");
            if (schema["enum"] is JArray enumeration && !enumeration.Any(value => JToken.DeepEquals(value, instance))) Add(errors, path, "is not one of the allowed values");

            if (instance is JValue scalar && scalar.Type is JTokenType.Integer or JTokenType.Float) ValidateNumber(scalar, schema, path, errors);
            if (instance.Type == JTokenType.String) ValidateString(instance.Value<string>(), schema, path, errors);
            if (instance is JArray array) ValidateArray(array, schema, path, errors);
            if (instance is JObject value) ValidateObject(value, schema, path, errors);

            EvaluateCombinators(instance, schema, path, errors);
            return errors.Count == before;
        }

        private void ValidateNumber(JValue instance, JObject schema, string path, List<string> errors)
        {
            var number = instance.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number)) Add(errors, path, "must be a finite IEEE 754 binary64 number");
            if (instance.Type == JTokenType.Integer && BigInteger.TryParse(instance.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                && BigInteger.Abs(integer) > 9007199254740991L) Add(errors, path, "must be within the interoperable integer range -(2^53-1)..2^53-1");
            if (schema.Value<double?>("minimum") is { } minimum && number < minimum) Add(errors, path, $"must be >= {minimum.ToString("R", CultureInfo.InvariantCulture)}");
            if (schema.Value<double?>("maximum") is { } maximum && number > maximum) Add(errors, path, $"must be <= {maximum.ToString("R", CultureInfo.InvariantCulture)}");
            if (schema.Value<double?>("exclusiveMinimum") is { } exclusiveMinimum && number <= exclusiveMinimum) Add(errors, path, $"must be > {exclusiveMinimum.ToString("R", CultureInfo.InvariantCulture)}");
            if (schema.Value<double?>("exclusiveMaximum") is { } exclusiveMaximum && number >= exclusiveMaximum) Add(errors, path, $"must be < {exclusiveMaximum.ToString("R", CultureInfo.InvariantCulture)}");
        }

        private void ValidateString(string instance, JObject schema, string path, List<string> errors)
        {
            var length = UnicodeLength(instance);
            if (schema.Value<int?>("minLength") is { } minimum && length < minimum) Add(errors, path, $"must contain at least {minimum} Unicode characters");
            if (schema.Value<int?>("maxLength") is { } maximum && length > maximum) Add(errors, path, $"must contain at most {maximum} Unicode characters");
            if (schema.Value<string>("pattern") is { } pattern)
            {
                try { if (!Regex.IsMatch(instance, pattern, RegexOptions.CultureInvariant, RegexTimeout)) Add(errors, path, $"must match pattern {pattern}"); }
                catch (RegexMatchTimeoutException) { Add(errors, path, $"could not be checked safely against pattern {pattern}"); }
            }
            switch (schema.Value<string>("format"))
            {
                case "uri" when !Uri.TryCreate(instance, UriKind.Absolute, out _): Add(errors, path, "must be an absolute URI"); break;
                case "date-time" when !IsDateTime(instance): Add(errors, path, "must be an RFC 3339 date-time"); break;
            }
        }

        private void ValidateArray(JArray instance, JObject schema, string path, List<string> errors)
        {
            if (schema.Value<int?>("minItems") is { } minimum && instance.Count < minimum) Add(errors, path, $"must contain at least {minimum} items");
            if (schema.Value<int?>("maxItems") is { } maximum && instance.Count > maximum) Add(errors, path, $"must contain at most {maximum} items");
            if (schema.Value<bool?>("uniqueItems") == true)
                for (var left = 0; left < instance.Count; left++)
                    for (var right = left + 1; right < instance.Count; right++)
                        if (JToken.DeepEquals(instance[left], instance[right])) { Add(errors, path, $"must contain unique items (duplicates at {left} and {right})"); left = instance.Count; break; }
            var prefixCount = 0;
            var hasPrefixItems = schema["prefixItems"] is JArray;
            if (schema["prefixItems"] is JArray prefixItems)
            {
                prefixCount = Math.Min(instance.Count, prefixItems.Count);
                for (var index = 0; index < prefixCount; index++) Evaluate(instance[index], prefixItems[index], $"{path}[{index}]", errors);
            }
            if (schema["items"] is { } itemSchema)
                for (var index = hasPrefixItems ? prefixCount : 0; index < instance.Count; index++) Evaluate(instance[index], itemSchema, $"{path}[{index}]", errors);
            if (schema["contains"] is { } contains && !instance.Any(item => IsValid(item, contains))) Add(errors, path, "must contain an item matching the required schema");
        }

        private void ValidateObject(JObject instance, JObject schema, string path, List<string> errors)
        {
            if (schema.Value<int?>("minProperties") is { } minimum && instance.Count < minimum) Add(errors, path, $"must contain at least {minimum} properties");
            if (schema.Value<int?>("maxProperties") is { } maximum && instance.Count > maximum) Add(errors, path, $"must contain at most {maximum} properties");
            foreach (var required in schema["required"]?.Values<string>() ?? Enumerable.Empty<string>())
                if (instance.Property(required) == null) Add(errors, path, $"is missing required property '{required}'");

            var declared = schema["properties"] as JObject;
            foreach (var property in instance.Properties())
            {
                if (schema["propertyNames"] is { } nameSchema) Evaluate(new JValue(property.Name), nameSchema, $"{path}.<property-name>", errors);
                if (declared?[property.Name] is { } propertySchema) Evaluate(property.Value, propertySchema, ChildPath(path, property.Name), errors);
                else if (schema["additionalProperties"]?.Type == JTokenType.Boolean && !schema.Value<bool>("additionalProperties")) Add(errors, ChildPath(path, property.Name), "is not an allowed property");
                else if (schema["additionalProperties"] is JObject additionalSchema) Evaluate(property.Value, additionalSchema, ChildPath(path, property.Name), errors);
            }
        }

        private void EvaluateCombinators(JToken instance, JObject schema, string path, List<string> errors)
        {
            if (schema["allOf"] is JArray allOf) foreach (var child in allOf) Evaluate(instance, child, path, errors);
            if (schema["anyOf"] is JArray anyOf && !anyOf.Any(child => IsValid(instance, child))) Add(errors, path, "must match at least one anyOf alternative");
            if (schema["oneOf"] is JArray oneOf)
            {
                var matches = oneOf.Count(child => IsValid(instance, child));
                if (matches != 1) Add(errors, path, $"must match exactly one oneOf alternative (matched {matches})");
            }
            if (schema["not"] is { } not && IsValid(instance, not)) Add(errors, path, "must not match the forbidden schema");
            if (schema["if"] is { } condition)
            {
                var branch = IsValid(instance, condition) ? schema["then"] : schema["else"];
                if (branch != null) Evaluate(instance, branch, path, errors);
            }
        }

        private bool IsValid(JToken instance, JToken schema)
        {
            var errors = new List<string>();
            Evaluate(instance, schema, "$", errors);
            return errors.Count == 0;
        }

        private JToken ResolveReference(string reference)
        {
            if (reference == "#") return rootSchema;
            if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
            var current = rootSchema;
            foreach (var raw in reference.Substring(2).Split('/'))
            {
                var part = raw.Replace("~1", "/").Replace("~0", "~");
                current = current is JObject objectValue ? objectValue[part] : current is JArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count ? array[index] : null;
                if (current == null) return null;
            }
            return current;
        }

        private static bool MatchesType(JToken value, JToken declaration)
        {
            var names = declaration.Type == JTokenType.Array ? declaration.Values<string>() : new[] { declaration.Value<string>() };
            return names.Any(name => name switch
            {
                "null" => value.Type == JTokenType.Null,
                "boolean" => value.Type == JTokenType.Boolean,
                "object" => value.Type == JTokenType.Object,
                "array" => value.Type == JTokenType.Array,
                "number" => value.Type is JTokenType.Integer or JTokenType.Float,
                "integer" => value.Type == JTokenType.Integer,
                "string" => value.Type == JTokenType.String,
                _ => false
            });
        }

        private static string DescribeTypes(JToken declaration) => declaration.Type == JTokenType.Array ? string.Join(" or ", declaration.Values<string>()) : declaration.Value<string>();
        private static string DescribeType(JToken value) => value.Type.ToString().ToLowerInvariant();
        private static string ChildPath(string path, string name) => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") ? path + "." + name : path + "['" + name.Replace("'", "\\'") + "']";
        private static void Add(ICollection<string> errors, string path, string message) { if (errors.Count < MaximumErrors) errors.Add($"Schema {path} {message}."); }
        private static bool IsDateTime(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var match = Regex.Match(value,
                "^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})[Tt](?<hour>[0-9]{2}):(?<minute>[0-9]{2}):(?<second>[0-9]{2})(?:\\.[0-9]+)?(?<zone>[Zz]|[+-][0-9]{2}:[0-9]{2})$",
                RegexOptions.CultureInvariant, RegexTimeout);
            if (!match.Success || !DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
            if (!int.TryParse(match.Groups["hour"].Value, out var hour) || hour > 23
                || !int.TryParse(match.Groups["minute"].Value, out var minute) || minute > 59
                || !int.TryParse(match.Groups["second"].Value, out var second) || second > 60) return false;
            var zone = match.Groups["zone"].Value;
            return zone.Length == 1 || int.Parse(zone.Substring(1, 2), CultureInfo.InvariantCulture) <= 23 && int.Parse(zone.Substring(4, 2), CultureInfo.InvariantCulture) <= 59;
        }
        private static int UnicodeLength(string value)
        {
            var count = 0;
            for (var index = 0; index < value.Length; index++, count++) if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
            return count;
        }
    }
}
