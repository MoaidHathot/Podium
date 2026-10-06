using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Security;

namespace Podium.Web.Serving;

/// <summary>
/// Owner-only downloads around talks, plus the one public asset:
///   /talks/{id}/cfp.md          the CfP pack as Markdown (title, abstracts, format, outline, takeaways, speaker)
///   /talks/{id}/cfp.txt         the same as plain text, in the order CfP forms ask for it (Sessionize-style)
///   /talks/photo/{owner}/{repo} the speaker photo when speaker.md points at a file in the repository (read through
///                               the GitHub App, so private repositories work; image types only; cached)
/// </summary>
public static class TalkEndpoints
{
    /// <summary>Largest speaker photo served (bytes).</summary>
    public const int MaxPhotoBytes = 3 * 1024 * 1024;

    public static IEndpointRouteBuilder MapTalks(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/talks").RequireAuthorization(PodiumClaims.OwnerPolicy);
        owner.MapGet("/{id}/cfp.md", (string id, ITalkStore talks, HttpContext http, CancellationToken ct) => PackAsync(id, "md", talks, http, ct));
        owner.MapGet("/{id}/cfp.txt", (string id, ITalkStore talks, HttpContext http, CancellationToken ct) => PackAsync(id, "txt", talks, http, ct));

        app.MapGet("/talks/photo/{owner}/{repo}", async (string owner, string repo, ITalkStore talks, ISourceStore sources, IRepositoryClient repos, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, HttpContext http, CancellationToken ct) =>
        {
            var source = await sources.GetAsync(Source.MakeId(owner, repo), ct);
            if (source is null || !source.Trusted) return Results.NotFound();
            var speaker = await talks.GetSpeakerAsync(source.Id, ct);
            // Only repository-relative photos are served here; absolute URLs are linked directly by the page.
            if (speaker?.Photo is not { } path || Podium.Core.Discovery.TalkFiles.RepoPath(path) is null || path.Contains("://", StringComparison.Ordinal)) return Results.NotFound();
            var sha = source.LastSeenSha ?? "HEAD";
            var entry = await cache.GetOrCreateAsync<(byte[] Bytes, string Type)?>($"speaker-photo:{source.Id}:{sha}:{path}", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
                e.Size = 1;
                try
                {
                    var bytes = await repos.ReadFileAsync(source, sha, path, ct);
                    if (bytes is null || bytes.Length == 0 || bytes.Length > MaxPhotoBytes) return null;
                    var type = ImageType(bytes);
                    return type is null ? null : (Bytes: bytes, Type: type);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Podium.Talks").LogWarning(ex, "Could not read speaker photo {Path} of {Source}", path, source.Id);
                    return null;
                }
            });
            if (entry is not { } photo) return Results.NotFound();
            http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=3600";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Bytes(photo.Bytes, photo.Type);
        }).RequireRateLimiting("probe");
        return app;
    }

    /// <summary>Content type from the file signature; null when the bytes are not a web image.</summary>
    internal static string? ImageType(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return "image/webp";
        if (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8') return "image/gif";
        return null;
    }

    private static async Task<IResult> PackAsync(string id, string format, ITalkStore talks, HttpContext http, CancellationToken ct)
    {
        if (!Podium.Core.Slug.IsValid(id)) return Results.NotFound();
        var talk = await talks.GetAsync(id, ct);
        if (talk is null || talk.Archived) return Results.NotFound();
        var speaker = await talks.GetSpeakerAsync(talk.SourceId, ct);
        var markdown = format == "md";
        var body = CfpPack.Build(talk, speaker, markdown);
        http.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
        var name = $"{talk.LocalId}-cfp.{format}";
        http.Response.Headers[HeaderNames.ContentDisposition] = markdown ? $"attachment; filename=\"{name}\"" : $"inline; filename=\"{name}\"";
        return Results.Content(body, markdown ? "text/markdown; charset=utf-8" : "text/plain; charset=utf-8");
    }
}

/// <summary>
/// Everything a call-for-papers form asks for, in the order forms usually ask: title, abstract (long), short abstract,
/// format and level, tags, outline, takeaways, notes for the committee, then the speaker (name, tagline, bio, links).
/// The Markdown flavour keeps the author's formatting; the text flavour strips it for pasting into plain fields.
/// </summary>
public static class CfpPack
{
    public static string Build(Talk talk, Speaker? speaker, bool markdown)
    {
        var sb = new StringBuilder();
        string Text(string? md) => markdown ? (md ?? "").Trim() : Markdown.ToText(md);
        void Section(string title, string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            sb.Append(markdown ? "## " : "").Append(title).Append(markdown ? "\n\n" : "\n").Append(Text(content)).Append("\n\n");
        }

        sb.Append(markdown ? "# " : "").Append(talk.Title).Append("\n\n");
        Section("Abstract", talk.Abstract);
        var shortPart = talk.Part("short abstract") ?? talk.Part("short");
        Section("Short abstract", shortPart?.Markdown);
        var elevator = talk.Part("elevator pitch");
        Section("Elevator pitch", elevator?.Markdown);

        var format = new List<string>();
        if (talk.Durations.Count > 0) format.Add(string.Join(" / ", talk.Durations.Select(d => $"{d} min")));
        if (talk.Level is not null) format.Add(char.ToUpperInvariant(talk.Level[0]) + talk.Level[1..]);
        if (format.Count > 0) Section("Format", string.Join(" · ", format));
        if (talk.Tags.Count > 0) Section("Tags", string.Join(", ", talk.Tags));

        var known = new HashSet<string> { "shortabstract", "short", "elevatorpitch", "bio" };
        foreach (var part in talk.Parts.Where(p => !known.Contains(p.Key)))
            Section(part.Name, part.Markdown);

        var bio = talk.Part("bio")?.Markdown ?? speaker?.Bio;
        if (speaker is not null || bio is not null)
        {
            sb.Append(markdown ? "## Speaker\n\n" : "Speaker\n");
            if (speaker is not null)
            {
                if (speaker.Name.Length > 0) sb.Append(markdown ? "**" : "").Append(speaker.Name).Append(markdown ? "**" : "");
                if (speaker.Tagline is not null) sb.Append(speaker.Name.Length > 0 ? " — " : "").Append(speaker.Tagline);
                if (speaker.Name.Length > 0 || speaker.Tagline is not null) sb.Append("\n\n");
            }
            if (!string.IsNullOrWhiteSpace(bio)) sb.Append(Text(bio)).Append("\n\n");
            if (speaker?.Part("short bio") is { } shortBio) sb.Append(markdown ? "### Short bio\n\n" : "Short bio\n").Append(Text(shortBio.Markdown)).Append("\n\n");
            if (speaker?.Links.Count > 0)
            {
                foreach (var l in speaker.Links) sb.Append(markdown ? $"- {l.Key}: {l.Value}" : $"{l.Key}: {l.Value}").Append('\n');
                sb.Append('\n');
            }
            if (speaker?.Photo is not null) sb.Append(markdown ? $"Photo: {speaker.Photo}" : $"Photo: {speaker.Photo}").Append("\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
