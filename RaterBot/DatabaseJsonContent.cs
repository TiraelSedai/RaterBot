using System.Text.Json.Serialization;

namespace RaterBot;

[JsonSerializable(typeof(ImageHash))]
internal partial class SourceGenerationContext : JsonSerializerContext { }

internal sealed record ImageHash(ulong ImgHash);
