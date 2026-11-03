using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Jer.Core;

/// <summary>
/// A small JSON reader and writer for Jer's data files and the LRCLIB API.
/// .NET Framework has no System.Text.Json, and bundling it would make Jer.exe several times larger.
/// The format matches the files written by Jer 1.x: indented, property names as declared,
/// enums as names and dates in ISO 8601.
/// </summary>
public static class Json
{
    private const int MaxDepth = 64;
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>Parses JSON into null, bool, long, double, string, <see cref="List{T}"/> or a case-insensitive dictionary.</summary>
    public static object? Parse(string text)
    {
        var parser = new Parser(text);
        var value = parser.ReadValue(0);
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw parser.Error("Unexpected text after the JSON value");
        }

        return value;
    }

    public static T Deserialize<T>(string text) => (T)ConvertTo(Parse(text), typeof(T))!;

    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    /// <summary>Converts a parsed JSON value to <paramref name="type"/>. Unknown properties are ignored.</summary>
    public static object? ConvertTo(object? node, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (node is null)
        {
            return type.IsValueType && underlying is null ? Activator.CreateInstance(type) : null;
        }

        type = underlying ?? type;
        if (type == typeof(object))
        {
            return node;
        }

        if (type == typeof(string))
        {
            return node as string ?? Convert.ToString(node, CultureInfo.InvariantCulture);
        }

        if (type == typeof(bool))
        {
            return node is string s ? bool.Parse(s) : Convert.ToBoolean(node, CultureInfo.InvariantCulture);
        }

        if (type.IsEnum)
        {
            return node is string name
                ? Enum.Parse(type, name, ignoreCase: true)
                : Enum.ToObject(type, Convert.ToInt64(node, CultureInfo.InvariantCulture));
        }

        if (type == typeof(DateTime))
        {
            var parsed = DateTime.Parse((string)node, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed;
        }

        if (type == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse((string)node, CultureInfo.InvariantCulture);
        }

        if (type == typeof(Guid))
        {
            return Guid.Parse((string)node);
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.Parse((string)node, CultureInfo.InvariantCulture);
        }

        if (type.IsPrimitive || type == typeof(decimal))
        {
            return Convert.ChangeType(node, type, CultureInfo.InvariantCulture);
        }

        if (type.IsArray && node is List<object?> arrayItems)
        {
            var elementType = type.GetElementType()!;
            var array = Array.CreateInstance(elementType, arrayItems.Count);
            for (var i = 0; i < arrayItems.Count; i++)
            {
                array.SetValue(ConvertTo(arrayItems[i], elementType), i);
            }

            return array;
        }

        if (node is List<object?> items && TryGetListType(type, out var listType, out var itemType))
        {
            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var item in items)
            {
                list.Add(ConvertTo(item, itemType));
            }

            return list;
        }

        if (node is Dictionary<string, object?> fields)
        {
            if (TryGetDictionaryType(type, out var dictionaryType, out var valueType))
            {
                var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
                foreach (var pair in fields)
                {
                    dictionary[pair.Key] = ConvertTo(pair.Value, valueType);
                }

                return dictionary;
            }

            var instance = Activator.CreateInstance(type)!;
            foreach (var property in Properties(type))
            {
                if (!fields.TryGetValue(property.Name, out var value))
                {
                    continue;
                }

                try
                {
                    if (value is null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
                    {
                        continue; // Keep the default rather than writing 0 or false.
                    }

                    property.SetValue(instance, ConvertTo(value, property.PropertyType));
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
                {
                    // One bad value (for example a hand-edited file) shouldn't throw away everything else.
                    Log.Warn($"Ignoring the value of {type.Name}.{property.Name}", ex);
                }
            }

            return instance;
        }

        throw new FormatException($"Cannot read {DescribeNode(node)} as {type.Name}");
    }

    private static string DescribeNode(object node) => node switch
    {
        List<object?> => "an array",
        Dictionary<string, object?> => "an object",
        string => "a string",
        bool => "a boolean",
        _ => "a number",
    };

    private static bool TryGetListType(Type type, out Type listType, out Type itemType)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>) ||
                definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
            {
                itemType = type.GetGenericArguments()[0];
                listType = typeof(List<>).MakeGenericType(itemType);
                return true;
            }
        }

        listType = itemType = typeof(object);
        return false;
    }

    private static bool TryGetDictionaryType(Type type, out Type dictionaryType, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            if ((definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)) &&
                arguments[0] == typeof(string))
            {
                valueType = arguments[1];
                dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
                return true;
            }
        }

        dictionaryType = valueType = typeof(object);
        return false;
    }

    /// <summary>Public instance properties that can be both read and written, in declaration order.</summary>
    private static PropertyInfo[] Properties(Type type) => PropertyCache.GetOrAdd(type, t => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 &&
                    p.GetGetMethod() is not null && p.GetSetMethod() is not null)
        .OrderBy(p => p.MetadataToken)
        .ToArray());

    // ------------------------------------------------------------------ writing

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("The object is nested too deeply to write as JSON.");
        }

        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case Enum e:
                WriteString(sb, e.ToString());
                return;
            case DateTime dt:
                WriteString(sb, dt.ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset dto:
                WriteString(sb, dto.ToString("O", CultureInfo.InvariantCulture));
                return;
            case Guid g:
                WriteString(sb, g.ToString("D"));
                return;
            case TimeSpan ts:
                WriteString(sb, ts.ToString("c", CultureInfo.InvariantCulture));
                return;
            case double d:
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case decimal m:
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
                return;
            case IFormattable number when value.GetType().IsPrimitive:
                sb.Append(number.ToString(null, CultureInfo.InvariantCulture));
                return;
            case IDictionary dictionary:
                WriteObject(sb, depth, dictionary.Keys.Cast<object>().Select(k => (Name: Convert.ToString(k, CultureInfo.InvariantCulture) ?? "", Value: (object?)dictionary[k])));
                return;
            case IEnumerable sequence:
                WriteArray(sb, sequence, depth);
                return;
            default:
                WriteObject(sb, depth, Properties(value.GetType()).Select(p => (Name: p.Name, Value: (object?)p.GetValue(value))));
                return;
        }
    }

    private static void WriteObject(StringBuilder sb, int depth, IEnumerable<(string Name, object? Value)> members)
    {
        var any = false;
        sb.Append('{');
        foreach (var (name, member) in members)
        {
            sb.Append(any ? ",\n" : "\n");
            Indent(sb, depth + 1);
            WriteString(sb, name);
            sb.Append(": ");
            Write(sb, member, depth + 1);
            any = true;
        }

        if (any)
        {
            sb.Append('\n');
            Indent(sb, depth);
        }

        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, IEnumerable items, int depth)
    {
        var any = false;
        sb.Append('[');
        foreach (var item in items)
        {
            sb.Append(any ? ",\n" : "\n");
            Indent(sb, depth + 1);
            Write(sb, item, depth + 1);
            any = true;
        }

        if (any)
        {
            sb.Append('\n');
            Indent(sb, depth);
        }

        sb.Append(']');
    }

    private static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth * 2);

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || c == '\u2028' || c == '\u2029')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    // ------------------------------------------------------------------ reading

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;

        public Parser(string text) => _text = text ?? "";

        public bool AtEnd => _position >= _text.Length;

        public FormatException Error(string message) => new($"{message} (at character {_position + 1}).");

        public void SkipWhitespace()
        {
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (c is ' ' or '\t' or '\n' or '\r' or '\uFEFF')
                {
                    _position++;
                }
                else
                {
                    return;
                }
            }
        }

        public object? ReadValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error("JSON is nested too deeply");
            }

            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("Unexpected end of JSON");
            }

            var c = _text[_position];
            switch (c)
            {
                case '{':
                    return ReadObject(depth);
                case '[':
                    return ReadArray(depth);
                case '"':
                    return ReadString();
                case 't':
                    Expect("true");
                    return true;
                case 'f':
                    Expect("false");
                    return false;
                case 'n':
                    Expect("null");
                    return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ReadNumber();
                    }

                    throw Error($"Unexpected character '{c}'");
            }
        }

        private Dictionary<string, object?> ReadObject(int depth)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _position++; // {
            SkipWhitespace();
            if (Peek() == '}')
            {
                _position++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                {
                    throw Error("Expected a property name");
                }

                var name = ReadString();
                SkipWhitespace();
                if (Peek() != ':')
                {
                    throw Error("Expected ':'");
                }

                _position++;
                result[name] = ReadValue(depth + 1);
                SkipWhitespace();
                var next = Peek();
                _position++;
                if (next == '}')
                {
                    return result;
                }

                if (next != ',')
                {
                    throw Error("Expected ',' or '}'");
                }

                SkipWhitespace();
                if (Peek() == '}')
                {
                    _position++; // Tolerate a trailing comma.
                    return result;
                }
            }
        }

        private List<object?> ReadArray(int depth)
        {
            var result = new List<object?>();
            _position++; // [
            SkipWhitespace();
            if (Peek() == ']')
            {
                _position++;
                return result;
            }

            while (true)
            {
                result.Add(ReadValue(depth + 1));
                SkipWhitespace();
                var next = Peek();
                _position++;
                if (next == ']')
                {
                    return result;
                }

                if (next != ',')
                {
                    throw Error("Expected ',' or ']'");
                }

                SkipWhitespace();
                if (Peek() == ']')
                {
                    _position++;
                    return result;
                }
            }
        }

        private string ReadString()
        {
            _position++; // opening quote
            var start = _position;
            while (_position < _text.Length && _text[_position] != '"' && _text[_position] != '\\')
            {
                _position++;
            }

            if (_position < _text.Length && _text[_position] == '"')
            {
                return _text.Substring(start, _position++ - start); // No escapes: the common case.
            }

            var sb = new StringBuilder(_text, start, _position - start, Math.Max(16, (_position - start) * 2));
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("Unterminated string");
                }

                var c = _text[_position++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (AtEnd)
                {
                    throw Error("Unterminated escape");
                }

                var escape = _text[_position++];
                switch (escape)
                {
                    case '"':
                    case '\\':
                    case '/':
                        sb.Append(escape);
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (_position + 4 > _text.Length ||
                            !int.TryParse(_text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Error("Bad \\u escape");
                        }

                        sb.Append((char)code);
                        _position += 4;
                        break;
                    default:
                        throw Error($"Unknown escape '\\{escape}'");
                }
            }
        }

        private object ReadNumber()
        {
            var start = _position;
            if (Peek() == '-')
            {
                _position++;
            }

            var integral = true;
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (c >= '0' && c <= '9')
                {
                    _position++;
                }
                else if (c is '.' or 'e' or 'E' or '+' or '-')
                {
                    integral = false;
                    _position++;
                }
                else
                {
                    break;
                }
            }

            var token = _text.Substring(start, _position - start);
            if (integral && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            {
                return whole;
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            throw Error($"Bad number '{token}'");
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_text, _position, word, 0, word.Length) != 0)
            {
                throw Error($"Expected '{word}'");
            }

            _position += word.Length;
        }

        private char Peek() => _position < _text.Length ? _text[_position] : '\0';
    }
}
