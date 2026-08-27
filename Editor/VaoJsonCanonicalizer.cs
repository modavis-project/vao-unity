using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Modavis.Vao.Editor
{
    /// <summary>RFC 8785 JSON Canonicalization Scheme writer for VAO trace tuples.</summary>
    internal static class VaoJsonCanonicalizer
    {
        public static byte[] Canonicalize(JToken value)
        {
            var builder = new StringBuilder();
            Write(value, builder);
            return new UTF8Encoding(false, true).GetBytes(builder.ToString());
        }

        private static void Write(JToken value, StringBuilder output)
        {
            switch (value?.Type ?? JTokenType.Null)
            {
                case JTokenType.Object:
                    output.Append('{');
                    var firstProperty = true;
                    foreach (var property in ((JObject)value).Properties().OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        if (!firstProperty) output.Append(',');
                        firstProperty = false;
                        WriteString(property.Name, output);
                        output.Append(':');
                        Write(property.Value, output);
                    }
                    output.Append('}');
                    break;
                case JTokenType.Array:
                    output.Append('[');
                    var firstItem = true;
                    foreach (var item in (JArray)value)
                    {
                        if (!firstItem) output.Append(',');
                        firstItem = false;
                        Write(item, output);
                    }
                    output.Append(']');
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    output.Append(CanonicalNumber(value.Value<double>()));
                    break;
                case JTokenType.String:
                    WriteString(value.Value<string>(), output);
                    break;
                case JTokenType.Boolean:
                    output.Append(value.Value<bool>() ? "true" : "false");
                    break;
                case JTokenType.Null:
                case JTokenType.Undefined:
                    output.Append("null");
                    break;
                default:
                    throw new InvalidOperationException($"JToken type {value.Type} is not part of the VAO JSON domain.");
            }
        }

        private static string CanonicalNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("RFC 8785 does not admit non-finite numbers.");
            if (value == 0d) return "0";
            var negative = value < 0d;
            var raw = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
            var exponentMarker = raw.IndexOf('e');
            var exponent = exponentMarker >= 0 ? int.Parse(raw[(exponentMarker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
            var mantissa = exponentMarker >= 0 ? raw[..exponentMarker] : raw;
            var decimalPoint = mantissa.IndexOf('.');
            var decimalPosition = decimalPoint >= 0 ? decimalPoint : mantissa.Length;
            var digits = mantissa.Replace(".", string.Empty);
            var leading = 0;
            while (leading < digits.Length && digits[leading] == '0') leading++;
            digits = leading == digits.Length ? "0" : digits[leading..];
            decimalPosition -= leading;
            while (digits.Length > 1 && digits[^1] == '0') digits = digits[..^1];
            var scientificExponent = exponent + decimalPosition - 1;
            string result;
            if (scientificExponent is >= -6 and < 21)
            {
                var point = scientificExponent + 1;
                if (point <= 0) result = "0." + new string('0', -point) + digits;
                else if (point >= digits.Length) result = digits + new string('0', point - digits.Length);
                else result = digits[..point] + "." + digits[point..];
            }
            else
            {
                result = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
                result += "e" + (scientificExponent >= 0 ? "+" : string.Empty) + scientificExponent.ToString(CultureInfo.InvariantCulture);
            }
            return negative ? "-" + result : result;
        }

        private static void WriteString(string value, StringBuilder output)
        {
            output.Append('"');
            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\t': output.Append("\\t"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\r': output.Append("\\r"); break;
                    default:
                        if (character < 0x20) output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(character);
                        break;
                }
            }
            output.Append('"');
        }
    }
}
