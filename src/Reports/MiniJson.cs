using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NomisKitchen.Reports
{
    internal static class MiniJson
    {
        public static string Write(object value)
        {
            var output = new StringBuilder();
            Append(output, value, 0);
            return output.ToString();
        }

        static void Append(StringBuilder output, object value, int depth)
        {
            switch (value)
            {
                case null:
                    output.Append("null");
                    return;
                case bool flag:
                    output.Append(flag ? "true" : "false");
                    return;
                case string text:
                    AppendString(output, text);
                    return;
                case double number:
                    output.Append(double.IsNaN(number) || double.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case float number:
                    output.Append(float.IsNaN(number) || float.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                case decimal _:
                    output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case IDictionary map:
                    AppendObject(output, map, depth);
                    return;
                case IEnumerable items:
                    AppendArray(output, items, depth);
                    return;
                default:
                    AppendString(output, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        static void AppendObject(StringBuilder output, IDictionary map, int depth)
        {
            if (map.Count == 0)
            {
                output.Append("{}");
                return;
            }
            output.Append('{');
            bool first = true;
            foreach (DictionaryEntry pair in map)
            {
                if (!first) output.Append(',');
                first = false;
                NewLine(output, depth + 1);
                AppendString(output, Convert.ToString(pair.Key, CultureInfo.InvariantCulture));
                output.Append(": ");
                Append(output, pair.Value, depth + 1);
            }
            NewLine(output, depth);
            output.Append('}');
        }

        static void AppendArray(StringBuilder output, IEnumerable items, int depth)
        {
            var list = new List<object>();
            foreach (var item in items) list.Add(item);
            if (list.Count == 0)
            {
                output.Append("[]");
                return;
            }
            output.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) output.Append(',');
                NewLine(output, depth + 1);
                Append(output, list[i], depth + 1);
            }
            NewLine(output, depth);
            output.Append(']');
        }

        static void NewLine(StringBuilder output, int depth)
        {
            output.Append('\n').Append(' ', depth * 2);
        }

        static void AppendString(StringBuilder output, string text)
        {
            output.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }
    }
}
