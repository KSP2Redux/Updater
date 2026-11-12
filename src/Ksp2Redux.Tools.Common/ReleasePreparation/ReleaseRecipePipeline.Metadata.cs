using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ksp2Redux.Tools.Common.ReleasePreparation;

public partial class ReleaseRecipePipeline
{
    private static void WriteManifest(Stream output, ReleaseRecipeManifest manifest) =>
        JsonSerializer.Serialize(output, manifest, manifest.Version >= COMPACT_METADATA_VERSION ? COMPACT_JSON_OPTIONS : JSON_OPTIONS);

    private sealed class ChunkRecordConverter : JsonConverter<ReleaseRecipeChunk>
    {
        public override ReleaseRecipeChunk Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                return JsonSerializer.Deserialize<ReleaseRecipeChunk>(ref reader, JSON_OPTIONS)
                    ?? throw new JsonException("Missing release chunk.");
            if (reader.TokenType != JsonTokenType.StartArray || !reader.Read() || reader.TokenType != JsonTokenType.String ||
                (reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length) != 44 ||
                !reader.TryGetBytesFromBase64(out byte[]? hash) || hash.Length != 32)
                throw new JsonException("Invalid compact release chunk SHA-256.");

            int length = ReadInteger(ref reader);
            int packIndex = ReadInteger(ref reader);
            int offset = ReadInteger(ref reader);
            int storedLength = ReadInteger(ref reader);
            if (!reader.Read() || reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                throw new JsonException("Invalid compact release chunk compression flag.");
            bool compressed = reader.GetBoolean();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("Unexpected compact release chunk fields.");
            return new ReleaseRecipeChunk(Convert.ToHexString(hash), length, packIndex, offset, storedLength, compressed);
        }

        public override void Write(Utf8JsonWriter writer, ReleaseRecipeChunk value, JsonSerializerOptions options)
        {
            // The fixed field order keeps the complete chunk table within the feed's existing byte limit.
            writer.WriteStartArray();
            writer.WriteBase64StringValue(Convert.FromHexString(value.Sha256));
            writer.WriteNumberValue(value.Length);
            writer.WriteNumberValue(value.PackIndex);
            writer.WriteNumberValue(value.Offset);
            writer.WriteNumberValue(value.StoredLength);
            writer.WriteBooleanValue(value.IsCompressed);
            writer.WriteEndArray();
        }

        private static int ReadInteger(ref Utf8JsonReader reader)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
                throw new JsonException("Invalid compact release chunk number.");
            return value;
        }
    }
}
