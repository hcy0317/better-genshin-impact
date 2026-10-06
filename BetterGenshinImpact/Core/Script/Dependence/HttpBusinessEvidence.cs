using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterGenshinImpact.Core.Script.Dependence;

internal static class HttpBusinessEvidence
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    internal static Dictionary<string, string> Read(string body)
    {
        var fields = new Dictionary<string, string> { ["businessStatus"] = "unknown:not-returned",
            ["lockHolder"] = "unknown:not-returned", ["lockConflict"] = "unknown:not-returned" };
        if (body.Length > 65536) { fields["businessStatus"] = "unknown:payload-too-large"; return fields; }
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) return fields;
            if (data.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object) data = nested;
            if (data.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                fields["businessStatus"] = status.GetString() is "BUSY" or "WAITING" or "ACTION" or "NEEDS_RECONCILE" or
                    "NO_TARGETS" or "REPLANNING" or "COMPLETED" or "STOPPED_NO_PROGRESS" ? status.GetString()! : "unknown:other-status";
            foreach (var name in new[] { "actionId", "executorId", "lockHolder", "lockConflict", "leaseId", "activeActionId", "inventoryReconcileCause" })
                if (data.TryGetProperty(name, out var value))
                {
                    var id = value.ValueKind == JsonValueKind.String ? value.GetString() :
                        value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
                    fields[name] = id is { Length: > 0 and <= 512 }
                        ? "opaque:" + Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(id)))
                        : "unknown:unsupported-shape";
                }
            foreach (var name in new[] { "revision", "expectedRevision", "observedCount" })
                if (data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                    fields[name] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (JsonException) { fields["businessStatus"] = "unknown:invalid-json"; }
        return fields;
    }
}
