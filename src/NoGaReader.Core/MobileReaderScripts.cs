using System.Text.Json;
using NoGaReader.Models;

namespace NoGaReader.Services;

/// <summary>
/// Builds the JavaScript the mobile reader host injects for annotation
/// capture and mounting. Capture returns the selected text with surrounding
/// context so annotations get a portable <see cref="TextAnchor"/>; mounting
/// disambiguates repeated text by scoring that context instead of always
/// taking the first occurrence, and survives publisher DOM changes that keep
/// the text intact. Pure string building — smoke-testable off-device.
/// </summary>
public static class MobileReaderScripts
{
    /// <summary>Characters of context captured on each side of a selection.</summary>
    public const int AnchorContextLength = 64;

    private static readonly JsonSerializerOptions CaptureJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Returns JSON <c>{ text, prefix, suffix }</c> for the current selection,
    /// or <c>null</c> when nothing usable is selected. Offsets are computed on
    /// the same walker-concatenated text the mount script uses, so the context
    /// aligns with mounting even when the selection crosses elements.
    /// </summary>
    public static string SelectionCaptureScript { get; } = $$"""
        (() => {
          const selection = window.getSelection();
          if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
            return JSON.stringify(null);
          }
          const range = selection.getRangeAt(0);
          const nodes = [];
          let full = '';
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          while (walker.nextNode()) {
            const node = walker.currentNode;
            if (node.parentElement?.closest('mark,script,style')) continue;
            nodes.push({ node, start: full.length });
            full += String(node.nodeValue || '');
          }
          let start = -1, end = -1;
          for (const rec of nodes) {
            if (rec.node === range.startContainer) start = rec.start + range.startOffset;
            if (rec.node === range.endContainer) end = rec.start + range.endOffset;
          }
          let text;
          if (start >= 0 && end > start) {
            text = full.slice(start, end);
          } else {
            text = String(selection);
            start = full.indexOf(text);
            end = start >= 0 ? start + text.length : -1;
          }
          if (!text || !text.trim()) {
            return JSON.stringify(null);
          }
          const prefix = start > 0 ? full.slice(Math.max(0, start - {{AnchorContextLength}}), start) : '';
          const suffix = end >= 0 && end < full.length
            ? full.slice(end, Math.min(full.length, end + {{AnchorContextLength}}))
            : '';
          return JSON.stringify({ text, prefix, suffix });
        })()
        """;

    public sealed record SelectedTextCapture(string Text, string Prefix, string Suffix);

    /// <summary>
    /// Parses the JSON produced by <see cref="SelectionCaptureScript"/>;
    /// returns null for empty or unusable selections.
    /// </summary>
    public static SelectedTextCapture? ParseSelectionCapture(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json is "null")
        {
            return null;
        }

        CaptureDto? capture;
        try
        {
            capture = JsonSerializer.Deserialize<CaptureDto>(json, CaptureJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        var text = capture?.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return new SelectedTextCapture(text, capture!.Prefix ?? string.Empty, capture.Suffix ?? string.Empty);
    }

    /// <summary>
    /// Builds the mount script for a section's annotations. Occurrences of each
    /// annotation's text are scored against the anchor's prefix/suffix context;
    /// highlights are applied per text node in descending document order so
    /// earlier offsets stay valid while the DOM is being split. Annotations
    /// without anchors (legacy rows) fall back to the first occurrence.
    /// </summary>
    public static string BuildAnnotationMountScript(IReadOnlyList<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var payload = JsonSerializer.Serialize(annotations
            .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
            .Select(item => new
            {
                text = item.SelectedText,
                note = item.Note,
                prefix = item.Anchor?.Prefix,
                suffix = item.Anchor?.Suffix
            }));
        return $$"""
            (() => {
              const annotations = {{payload}};
              const nodes = [];
              let full = '';
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                const node = walker.currentNode;
                if (node.parentElement?.closest('mark,script,style')) continue;
                nodes.push({ node, start: full.length });
                full += String(node.nodeValue || '');
              }

              const sharedTail = (candidate, expected) => {
                let length = 0;
                while (length < candidate.length && length < expected.length &&
                       candidate[candidate.length - 1 - length] === expected[expected.length - 1 - length]) {
                  length++;
                }
                return length;
              };
              const sharedHead = (candidate, expected) => {
                let length = 0;
                while (length < candidate.length && length < expected.length &&
                       candidate[length] === expected[length]) {
                  length++;
                }
                return length;
              };

              const segments = [];
              for (const annotation of annotations) {
                if (!annotation.text) continue;
                const candidates = [];
                let from = 0;
                while (candidates.length < 64) {
                  const index = full.indexOf(annotation.text, from);
                  if (index < 0) break;
                  candidates.push(index);
                  from = index + Math.max(1, annotation.text.length);
                }
                if (!candidates.length) continue;
                let best = candidates[0];
                let bestScore = -1;
                for (const index of candidates) {
                  let score = 0;
                  if (annotation.prefix) {
                    const before = full.slice(Math.max(0, index - annotation.prefix.length), index);
                    score += sharedTail(before, annotation.prefix) / annotation.prefix.length;
                  }
                  if (annotation.suffix) {
                    const after = full.slice(index + annotation.text.length,
                                             index + annotation.text.length + annotation.suffix.length);
                    score += sharedHead(after, annotation.suffix) / annotation.suffix.length;
                  }
                  if (score > bestScore) { bestScore = score; best = index; }
                }
                segments.push({ start: best, end: best + annotation.text.length, note: annotation.note || null });
              }

              segments.sort((a, b) => b.start - a.start);
              for (const segment of segments) {
                for (let i = nodes.length - 1; i >= 0; i--) {
                  const rec = nodes[i];
                  const nodeEnd = rec.start + String(rec.node.nodeValue || '').length;
                  const from = Math.max(segment.start, rec.start);
                  const to = Math.min(segment.end, nodeEnd);
                  if (from >= to) continue;
                  try {
                    const range = document.createRange();
                    range.setStart(rec.node, from - rec.start);
                    range.setEnd(rec.node, to - rec.start);
                    const mark = document.createElement('mark');
                    mark.className = 'nogar-mobile-note';
                    if (segment.note) mark.title = segment.note;
                    range.surroundContents(mark);
                  } catch {
                    // One unanchorable span must not stop the rest.
                  }
                }
              }
              return segments.length;
            })();
            """;
    }

    private sealed record CaptureDto(string? Text, string? Prefix, string? Suffix);
}
