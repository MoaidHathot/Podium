using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Podium.Core.Abstractions;
using Podium.Core.Models;
using Podium.Web.Security;

namespace Podium.Web.Serving;

/// <summary>
/// Owner-only downloads around talks:
///   /talks/{id}/cfp.md   the CfP pack as Markdown (title, abstracts, format, outline, takeaways, speaker)
///   /talks/{id}/cfp.txt  the same as plain text, in the order CfP forms ask for it (Sessionize-style)
/// </summary>
public static class TalkEndpoints
{
    public static IEndpointRouteBuilder MapTalks(this IEndpointRouteBuilder app)
    {
        var owner = app.MapGroup("/talks").RequireAuthorization(PodiumClaims.OwnerPolicy);
        owner.MapGet("/{id}/cfp.md", (string id, ITalkStore talks, HttpContext http, CancellationToken ct) => PackAsync(id, "md", talks, http, ct));
        owner.MapGet("/{id}/cfp.txt", (string id, ITalkStore talks, HttpContext http, CancellationToken ct) => PackAsync(id, "txt", talks, http, ct));
        return app;
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
