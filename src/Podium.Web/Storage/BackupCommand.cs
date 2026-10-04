using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace Podium.Web.Storage;

/// <summary>
/// `Podium.Web export`: dumps every Podium table to JSON in the "backups" blob container (one folder per run) and
/// exits. Runs as a scheduled Container Apps Job with the web app's identity, so no credential leaves Azure. The
/// container has a lifecycle rule in Bicep that deletes runs older than 30 days.
/// </summary>
public static class BackupCommand
{
    public static readonly string[] Tables = ["sources", "decks", "builds", "activebuilds", "grants", "sharelinks", "views", "deckviews", "recentdismissals", "sessions", "livesessions", "accessrequests", "audit", "settings"];

    public static async Task<int> RunAsync(TableServiceClient tables, BlobServiceClient blobs, string prefix, TextWriter output, CancellationToken ct)
    {
        var container = blobs.GetBlobContainerClient("backups");
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        var folder = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'");
        var total = 0;
        foreach (var name in Tables)
        {
            var table = tables.GetTableClient(prefix + name);
            var rows = new List<JsonElement>();
            try
            {
                await foreach (var e in table.QueryAsync<TableEntity>(cancellationToken: ct))
                {
                    // Entities carry the record as JSON in "Json" (plus a few index columns); keep the readable form.
                    if (e.TryGetValue("Json", out var j) && j is string json) rows.Add(JsonDocument.Parse(json).RootElement.Clone());
                    else rows.Add(JsonSerializer.SerializeToElement(e.Where(kv => kv.Key is not ("odata.etag")).ToDictionary(kv => kv.Key, kv => kv.Value)));
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { await output.WriteLineAsync($"  {name,-18} (table not created yet)"); continue; }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rows, new JsonSerializerOptions { WriteIndented = false });
            await container.GetBlobClient($"{folder}/{name}.json").UploadAsync(BinaryData.FromBytes(bytes), overwrite: true, ct);
            await output.WriteLineAsync($"  {name,-18} {rows.Count,6} records");
            total += rows.Count;
        }
        await output.WriteLineAsync($"Backup {folder}: {total} records in {Tables.Length} tables");
        return 0;
    }
}
