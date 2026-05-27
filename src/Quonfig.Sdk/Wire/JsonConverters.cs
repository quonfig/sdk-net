using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quonfig.Sdk.Wire;

/// <summary>
/// System.Text.Json converter for the discriminated-union <see cref="ContextValue"/>. The wire
/// shape is <c>{"type":"&lt;tag&gt;","value":&lt;payload&gt;}</c> matching the cross-SDK config
/// wire format used in <c>integration-test-data</c>.
///
/// <para>For <c>string_list</c>, the payload is a JSON array of strings.</para>
/// </summary>
public sealed class ContextValueJsonConverter : JsonConverter<ContextValue>
{
    /// <inheritdoc/>
    public override ContextValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject for ContextValue, got {reader.TokenType}");
        }

        string? type = null;
        JsonElement valueElement = default;
        bool hasValue = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected PropertyName, got {reader.TokenType}");
            }
            string prop = reader.GetString()!;
            reader.Read();
            if (prop == "type")
            {
                type = reader.GetString();
            }
            else if (prop == "value")
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                valueElement = doc.RootElement.Clone();
                hasValue = true;
            }
            else
            {
                reader.Skip();
            }
        }

        if (type is null) throw new JsonException("ContextValue is missing \"type\" discriminator");
        if (!hasValue) throw new JsonException("ContextValue is missing \"value\" payload");

        switch (type)
        {
            case "string":
                return new ContextValueString(valueElement.GetString() ?? throw new JsonException("string payload must not be null"));
            case "int":
                if (valueElement.ValueKind == JsonValueKind.Number)
                {
                    return new ContextValueInt(valueElement.GetInt32());
                }
                if (valueElement.ValueKind == JsonValueKind.String)
                {
                    return new ContextValueInt(int.Parse(valueElement.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                }
                throw new JsonException($"int payload must be a number or numeric string, got {valueElement.ValueKind}");
            case "long":
                if (valueElement.ValueKind == JsonValueKind.Number)
                {
                    return new ContextValueLong(valueElement.GetInt64());
                }
                if (valueElement.ValueKind == JsonValueKind.String)
                {
                    return new ContextValueLong(long.Parse(valueElement.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                }
                throw new JsonException($"long payload must be a number or numeric string, got {valueElement.ValueKind}");
            case "double":
                if (valueElement.ValueKind == JsonValueKind.Number)
                {
                    return new ContextValueDouble(valueElement.GetDouble());
                }
                if (valueElement.ValueKind == JsonValueKind.String)
                {
                    return new ContextValueDouble(double.Parse(valueElement.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                }
                throw new JsonException($"double payload must be a number or numeric string, got {valueElement.ValueKind}");
            case "bool":
                return new ContextValueBool(valueElement.GetBoolean());
            case "string_list":
                if (valueElement.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException($"string_list payload must be a JSON array, got {valueElement.ValueKind}");
                }
                var list = new List<string>(valueElement.GetArrayLength());
                foreach (var item in valueElement.EnumerateArray())
                {
                    list.Add(item.GetString() ?? throw new JsonException("string_list items must not be null"));
                }
                return new ContextValueStringList(list);
            default:
                throw new JsonException($"Unknown ContextValue type discriminator: \"{type}\"");
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ContextValue value, JsonSerializerOptions options)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
#else
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        if (value is null) throw new ArgumentNullException(nameof(value));
#endif
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WritePropertyName("value");
        switch (value)
        {
            case ContextValueString s:
                writer.WriteStringValue(s.Value);
                break;
            case ContextValueInt i:
                writer.WriteNumberValue(i.Value);
                break;
            case ContextValueLong l:
                writer.WriteNumberValue(l.Value);
                break;
            case ContextValueDouble d:
                writer.WriteNumberValue(d.Value);
                break;
            case ContextValueBool b:
                writer.WriteBooleanValue(b.Value);
                break;
            case ContextValueStringList sl:
                writer.WriteStartArray();
                foreach (var item in sl.Values) writer.WriteStringValue(item);
                writer.WriteEndArray();
                break;
            default:
                throw new JsonException($"Unknown ContextValue subtype: {value.GetType().FullName}");
        }
        writer.WriteEndObject();
    }
}
