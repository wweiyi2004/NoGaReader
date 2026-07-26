using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Pure reading-state machine for the mobile reader host: paging versus
/// section navigation, progress math, contents mapping, search-hit and
/// in-book link targeting. Holds no platform types so the smoke suite can
/// exercise every transition off-device; the MAUI page stays a thin host
/// that renders this state and bridges WebView/PDF specifics.
/// </summary>
public sealed class MobileReaderPresenter
{
    public MobileReaderPresenter(ReaderSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        VisualPageCount = session.Kind switch
        {
            ReaderDocumentKind.Comic => Math.Max(1, session.ComicPages.Count),
            _ => 1
        };
        Contents = BuildContents(session);
    }

    public ReaderSession Session { get; }

    /// <summary>PDF and comics page; everything else navigates by section.</summary>
    public bool IsPaged => Session.Kind is ReaderDocumentKind.Pdf or ReaderDocumentKind.Comic;

    public bool UsesWebView => Session.Kind is not (
        ReaderDocumentKind.Pdf or
        ReaderDocumentKind.Image or
        ReaderDocumentKind.Comic);

    public int VisualPageIndex { get; private set; }

    public int VisualPageCount { get; private set; }

    private double _sectionProgress;

    public double SectionProgress
    {
        get => _sectionProgress;
        set => _sectionProgress = Math.Clamp(value, 0, 1);
    }

    public double VisualProgress => VisualPageCount <= 1
        ? 0
        : (double)VisualPageIndex / (VisualPageCount - 1);

    /// <summary>The section progress that reading-position saves should use.</summary>
    public double ProgressForSave => IsPaged ? VisualProgress : SectionProgress;

    public double DocumentProgress => IsPaged
        ? VisualProgress
        : Math.Clamp(
            (Session.CurrentSectionIndex + SectionProgress) / Math.Max(1, Session.Sections.Count),
            0,
            1);

    public IReadOnlyList<ContentsEntry> Contents { get; }

    public int SelectedContentsIndex
    {
        get
        {
            for (var index = 0; index < Contents.Count; index++)
            {
                if (Contents[index].SectionIndex == Session.CurrentSectionIndex)
                {
                    return index;
                }
            }

            return 0;
        }
    }

    public string PositionText => IsPaged
        ? $"{VisualPageIndex + 1} / {VisualPageCount}"
        : Session.Sections.Count > 1
            ? $"{Session.CurrentSectionIndex + 1} / {Session.Sections.Count} · {DocumentProgress:P0}"
            : $"{SectionProgress:P0}";

    public string PreviousButtonText => IsPaged ? "‹ 上一页" : "‹ 上一章";

    public string NextButtonText => IsPaged ? "下一页 ›" : "下一章 ›";

    public bool CanMovePrevious => IsPaged
        ? VisualPageIndex > 0
        : Session.CurrentSectionIndex > 0;

    public bool CanMoveNext => IsPaged
        ? VisualPageIndex < VisualPageCount - 1
        : Session.CurrentSectionIndex < Session.Sections.Count - 1;

    /// <summary>
    /// Current comic page path, honoring the visual page index. Only valid for
    /// comic sessions (they always contain at least one page).
    /// </summary>
    public string CurrentComicPagePath =>
        Session.ComicPages[Math.Clamp(VisualPageIndex, 0, Session.ComicPages.Count - 1)].FullPath;

    /// <summary>PDF page count becomes known once the platform renderer opens the file.</summary>
    public void SetPdfPageCount(int pageCount)
    {
        VisualPageCount = Math.Max(1, pageCount);
        VisualPageIndex = Math.Clamp(VisualPageIndex, 0, VisualPageCount - 1);
    }

    /// <summary>
    /// Positions a paged document from a saved whole-document progress value.
    /// Section-based documents restore through the session's saved section
    /// index and per-section progress instead.
    /// </summary>
    public void RestoreFromDocumentProgress(double savedDocumentProgress)
    {
        if (!IsPaged)
        {
            return;
        }

        var progress = Math.Clamp(savedDocumentProgress, 0, 1);
        VisualPageIndex = Math.Clamp(
            (int)Math.Round(progress * Math.Max(0, VisualPageCount - 1)),
            0,
            VisualPageCount - 1);
        SyncComicSection();
        SectionProgress = VisualProgress;
    }

    public MoveResult Move(int delta)
    {
        if (IsPaged)
        {
            var target = Math.Clamp(VisualPageIndex + delta, 0, VisualPageCount - 1);
            if (target == VisualPageIndex)
            {
                return MoveResult.None;
            }

            VisualPageIndex = target;
            SyncComicSection();
            SectionProgress = VisualProgress;
            return MoveResult.PageChanged;
        }

        if (Session.Sections.Count <= 1)
        {
            return MoveResult.None;
        }

        var targetSection = Math.Clamp(
            Session.CurrentSectionIndex + delta,
            0,
            Session.Sections.Count - 1);
        if (targetSection == Session.CurrentSectionIndex)
        {
            return MoveResult.None;
        }

        Session.CurrentSectionIndex = targetSection;
        SectionProgress = delta > 0 ? 0 : 1;
        return MoveResult.SectionChanged;
    }

    public void JumpToContents(ContentsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Session.CurrentSectionIndex = Math.Clamp(
            entry.SectionIndex,
            0,
            Session.Sections.Count - 1);
        if (Session.Kind == ReaderDocumentKind.Comic)
        {
            VisualPageIndex = Math.Clamp(entry.SectionIndex, 0, VisualPageCount - 1);
        }

        SectionProgress = 0;
    }

    public void JumpToSearchHit(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        Session.CurrentSectionIndex = Math.Clamp(hit.SectionIndex, 0, Session.Sections.Count - 1);
        SectionProgress = Math.Clamp(hit.SectionProgress, 0, 1);
    }

    /// <summary>
    /// Resyncs the session when the document navigated on its own (an in-book
    /// link). Returns true when the current section changed.
    /// </summary>
    public bool SyncSectionFromPath(string navigatedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(navigatedPath);
        string normalized;
        try
        {
            normalized = Path.GetFullPath(navigatedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var index = FindSectionIndex(Session, normalized);
        if (index < 0 || index == Session.CurrentSectionIndex)
        {
            return false;
        }

        Session.CurrentSectionIndex = index;
        SectionProgress = 0;
        return true;
    }

    private void SyncComicSection()
    {
        if (Session.Kind == ReaderDocumentKind.Comic)
        {
            Session.CurrentSectionIndex = Math.Clamp(
                VisualPageIndex,
                0,
                Session.Sections.Count - 1);
        }
    }

    private static List<ContentsEntry> BuildContents(ReaderSession session)
    {
        var entries = new List<ContentsEntry>();
        foreach (var node in session.TableOfContents.SelectMany(node => node.Flatten()))
        {
            var index = FindSectionIndex(session, node.FullPath.Split('#', 2)[0]);
            if (index >= 0)
            {
                entries.Add(new ContentsEntry(node.Title, index));
            }
        }

        if (entries.Count == 0)
        {
            entries.AddRange(session.Sections.Select((section, index) =>
                new ContentsEntry(section.Title, index)));
        }

        return entries;
    }

    private static int FindSectionIndex(ReaderSession session, string path)
    {
        // PathSemantics keeps this correct on both case-sensitive Android
        // storage and the case-insensitive Windows smoke environment.
        for (var index = 0; index < session.Sections.Count; index++)
        {
            if (PathSemantics.Equals(session.Sections[index].FullPath, path))
            {
                return index;
            }
        }

        return -1;
    }

    public enum MoveResult
    {
        None,
        PageChanged,
        SectionChanged
    }

    public sealed record ContentsEntry(string Title, int SectionIndex);
}
