namespace Podium.Core.Models;

/// <summary>Type of slide deck detected in a repository.</summary>
public enum DeckKind
{
    Unknown = 0,
    Slidev = 1,
    Presenterm = 2,
    /// <summary>A pre-rendered HTML/PDF committed to the repo; served as-is.</summary>
    Static = 3,
    /// <summary>Legacy GitPitch deck; indexed but not renderable.</summary>
    GitPitch = 4,
    /// <summary>A committed .pptx; converted to PDF for in-browser viewing, original offered for download.</summary>
    PowerPoint = 5,
    /// <summary>A committed standalone .pdf.</summary>
    Pdf = 6,
}

/// <summary>Who may access a deck or artifact.</summary>
public enum Visibility
{
    /// <summary>Owner only.</summary>
    Private = 0,
    /// <summary>Anyone holding a signed, revocable link.</summary>
    Link = 1,
    /// <summary>Owner plus explicitly granted principals.</summary>
    Shared = 2,
    /// <summary>Anyone, no authentication.</summary>
    Public = 3,
}

public enum BuildStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>How a PowerPoint deck is shown in the browser.</summary>
public enum PptxViewer
{
    /// <summary>The PDF rendition made by the builder (LibreOffice or a committed PDF). Nothing leaves Podium.</summary>
    Pdf = 0,
    /// <summary>Microsoft's Office Online viewer: real PowerPoint rendering, but Microsoft's service fetches the file through a short-lived link.</summary>
    Office = 1,
}

/// <summary>Kinds of artifacts a build can produce.</summary>
public enum ArtifactKind
{
    /// <summary>The built SPA (Slidev) or rendered HTML (presenterm).</summary>
    Site = 0,
    Pdf = 1,
    Pptx = 2,
    Log = 3,
    /// <summary>First-slide preview image; follows the site's visibility.</summary>
    Thumbnail = 4,
    /// <summary>notes.json: per-slide speaker notes. Presenters only (owner / Present grantees), never served from the external origin.</summary>
    Notes = 5,
    /// <summary>text.json: per-slide plain text extracted from the PDF, for search. Owner only.</summary>
    Text = 6,
    /// <summary>slides.jpg: every slide tiled into one image (go-to grid, slide strip); follows the site's visibility.</summary>
    SlideSheet = 7,
    /// <summary>slides.json: geometry of the slide sheet (columns, cell size, count); follows the site's visibility.</summary>
    SlideSheetMeta = 8,
    /// <summary>manifest.json: files of the site variants, for offline precaching; follows the site's visibility.</summary>
    Manifest = 9,
    /// <summary>thumbnail-sm.jpg: the first-slide preview at card size (640x360); a library shows a hundred of these. Access is that of <see cref="Thumbnail"/>.</summary>
    ThumbnailSmall = 10,
}

/// <summary>Severity of a deck-health finding reported by the builder.</summary>
public enum AnnotationLevel
{
    Notice = 0,
    Warning = 1,
    Failure = 2,
}

/// <summary>Lifecycle of a request for access to a Shared/Private deck.</summary>
public enum AccessRequestStatus
{
    Pending = 0,
    Granted = 1,
    Declined = 2,
}
