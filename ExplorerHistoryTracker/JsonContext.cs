using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExplorerHistoryTracker.Models;
using ExplorerHistoryTracker.Services;

namespace ExplorerHistoryTracker
{
    /// <summary>
    /// JsonSerializerContext for NativeAOT trim compatibility.
    /// System.Text.Json under NativeAOT requires pre-generated serialization metadata.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(List<FolderHistoryItem>))]
    internal partial class JsonContext : JsonSerializerContext
    {
    }
}
