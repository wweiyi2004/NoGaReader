using System.Text.Json;
using NoGaReader.Models;

namespace NoGaReader.Services;

public static class ReaderRuntime
{
    public static string BuildBootstrapScript(
        AppSettings settings,
        double restoreProgress,
        string annotationsJson = "[]",
        string restoreAnchorJson = "null",
        bool isDarkAppTheme = false)
    {
        // Auto follows the effective app chrome (system/light/dark), not a fixed paper tone.
        var resolvedTheme = settings.ReaderTheme switch
        {
            ReaderThemeMode.Auto => isDarkAppTheme ? "auto-dark" : "auto-light",
            ReaderThemeMode.Paper => "paper",
            ReaderThemeMode.Light => "light",
            ReaderThemeMode.Dark => "dark",
            _ => isDarkAppTheme ? "auto-dark" : "auto-light"
        };
        var configuration = JsonSerializer.Serialize(new
        {
            theme = resolvedTheme,
            flow = settings.ReaderFlow.ToString().ToLowerInvariant(),
            fontSize = settings.ReaderFontSize,
            lineHeight = settings.ReaderLineHeight,
            contentWidth = settings.ReaderContentWidth,
            usePublisherFont = settings.UsePublisherFont,
            restoreProgress = Math.Clamp(restoreProgress, 0, 1)
        });

        return $"window.__nogareaderInitialize " +
               $"? window.__nogareaderInitialize({configuration}, {annotationsJson}, {restoreAnchorJson}) " +
               ": { ready: false };";
    }

    public static string BuildRuntimeScript()
    {
        return """
                 (() => {
                   window.__nogareaderInitialize = (config, initialAnnotations, initialRestoreAnchor) => {
                   try {
                   const root = document.documentElement;
                   const body = document.body;
                   if (!root || !body) return { ready: false };

                   const palettes = {
                      paper: { background: '#fbf7ed', foreground: '#37312a', muted: '#746b60', link: '#57534e', rule: '#d8cfbe' },
                      light: { background: '#ffffff', foreground: '#1c1917', muted: '#78716c', link: '#57534e', rule: '#e7e5e4' },
                      dark: { background: '#141414', foreground: '#f5f5f4', muted: '#a8a29e', link: '#d6d3d1', rule: '#3f3f3f' },
                      'auto-light': { background: '#faf9f7', foreground: '#1c1917', muted: '#78716c', link: '#57534e', rule: '#e7e5e4' },
                      'auto-dark': { background: '#1a1a1a', foreground: '#f5f5f4', muted: '#a8a29e', link: '#d6d3d1', rule: '#3f3f3f' }
                    };
                    const palette = palettes[config.theme] || palettes['auto-light'];
                   const contentWidth = Math.max(360, Math.min(config.contentWidth, window.innerWidth - 112));
                   const pageSide = Math.max(56, (window.innerWidth - contentWidth) / 2);
                   const pageGap = Math.max(112, window.innerWidth - contentWidth);
                   const usePublisherFont = config.usePublisherFont !== false;
                   const fontFamilyCss = usePublisherFont
                     ? ''
                     : 'font-family: "Microsoft YaHei UI", "PingFang SC", "Noto Sans CJK SC", sans-serif !important;';
                   const paragraphFontCss = usePublisherFont
                     ? 'font-size: inherit !important; line-height: inherit !important;'
                     : 'font: inherit !important; font-weight: 400 !important; line-height: inherit !important;';
                   const headingFontCss = usePublisherFont
                     ? 'font-weight: 650 !important;'
                     : 'font-family: inherit !important; font-weight: 650 !important;';

                   let style = document.getElementById('nogareader-runtime-style');
                   if (!style) {
                     style = document.createElement('style');
                     style.id = 'nogareader-runtime-style';
                     document.head.appendChild(style);
                   }

                   style.textContent = `
                     :root {
                       color-scheme: ${(config.theme === 'dark' || config.theme === 'auto-dark') ? 'dark' : 'light'};
                       --nr-background: ${palette.background};
                       --nr-foreground: ${palette.foreground};
                       --nr-muted: ${palette.muted};
                       --nr-link: ${palette.link};
                       --nr-rule: ${palette.rule};
                       --nr-font-size: ${config.fontSize}px;
                       --nr-line-height: ${config.lineHeight};
                       --nr-content-width: ${contentWidth}px;
                       --nr-page-side: ${pageSide}px;
                       --nr-page-gap: ${pageGap}px;
                     }
                     html, body { background: var(--nr-background) !important; color: var(--nr-foreground) !important; }
                     html { -webkit-text-size-adjust: 100%; scrollbar-color: var(--nr-rule) transparent; }
                     body.nogar-reader {
                       box-sizing: border-box !important;
                       color: var(--nr-foreground) !important;
                       background: var(--nr-background) !important;
                       ${fontFamilyCss}
                       font-size: var(--nr-font-size) !important;
                       font-weight: 400 !important;
                       line-height: var(--nr-line-height) !important;
                       letter-spacing: .012em !important;
                       overflow-wrap: anywhere;
                       text-rendering: optimizeLegibility;
                     }
                     body.nogar-reader p {
                       margin: .82em 0 !important;
                       color: inherit !important;
                       ${paragraphFontCss}
                       text-align: justify;
                     }
                     body.nogar-reader:not(.nogareader-markdown):not(.nogareader-plain) p { text-indent: 2em; }
                     body.nogar-reader h1, body.nogar-reader h2, body.nogar-reader h3,
                     body.nogar-reader h4, body.nogar-reader h5, body.nogar-reader h6 {
                       color: inherit !important; ${headingFontCss}
                       line-height: 1.38 !important; break-after: avoid; break-inside: avoid;
                     }
                     body.nogar-reader h1 { font-size: 1.72em !important; margin: .4em 0 1.2em !important; }
                     body.nogar-reader h2 { font-size: 1.42em !important; margin: 1.7em 0 .85em !important; }
                     body.nogar-reader h3 { font-size: 1.2em !important; margin: 1.45em 0 .7em !important; }
                     body.nogar-reader strong, body.nogar-reader b { font-weight: 700 !important; }
                     body.nogar-reader a { color: var(--nr-link) !important; text-underline-offset: .16em; }
                     body.nogar-reader blockquote { color: var(--nr-muted) !important; border-color: var(--nr-rule) !important; }
                     body.nogar-reader hr { border-color: var(--nr-rule) !important; }
                     body.nogar-reader img, body.nogar-reader svg, body.nogar-reader video {
                       max-width: 100% !important; height: auto !important; object-fit: contain; break-inside: avoid;
                     }
                     body.nogar-reader table { max-width: 100% !important; border-color: var(--nr-rule) !important; }
                     body.nogar-reader pre { max-width: 100%; overflow: auto; white-space: pre-wrap; }
                      body.nogar-reader ::selection { background: rgba(87, 83, 78, .28); }
                      body.nogar-reader sup, body.nogar-reader sub {
                        font-size: .68em !important; line-height: 0 !important;
                        vertical-align: super; position: relative; top: -.12em;
                      }
                      body.nogar-reader sub { vertical-align: sub; top: .12em; }
                      body.nogar-reader a[epub\\:type~="noteref"],
                      body.nogar-reader a[epub\\:type~="footnote"],
                      body.nogar-reader a.duokan-footnote,
                      body.nogar-reader a.footnote,
                      body.nogar-reader a.noteref,
                      body.nogar-reader span.duokan-footnote,
                      body.nogar-reader .footnote-ref,
                      body.nogar-reader .noteref {
                        font-size: .68em !important; line-height: 1 !important;
                        font-weight: 600 !important; vertical-align: super;
                        text-decoration: none !important;
                      }
                      mark.nogar-annotation { color: inherit !important; padding: 0 .04em; cursor: pointer;
                                              border-radius: .12em; box-decoration-break: clone;
                                              -webkit-box-decoration-break: clone; }
                      mark.nogar-annotation:hover, mark.nogar-annotation:focus-visible {
                        outline: 1.5px solid var(--nr-link); outline-offset: 1px;
                      }
                      mark.nogar-yellow { background: rgba(250, 204, 21, .42) !important; }
                      mark.nogar-green { background: rgba(74, 222, 128, .32) !important; }
                      mark.nogar-blue { background: rgba(96, 165, 250, .33) !important; }
                      mark.nogar-pink { background: rgba(244, 114, 182, .30) !important; }
                      mark.nogar-note {
                        border-bottom: 1.5px solid var(--nr-link);
                        padding-right: .04em;
                      }
                      mark.nogar-note::after {
                        content: '注';
                        display: inline-block;
                        margin-left: .1em;
                        padding: 0 .16em;
                        min-width: 0;
                        height: auto;
                        line-height: 1.1;
                        font-size: .48em;
                        font-weight: 600;
                        letter-spacing: 0;
                        vertical-align: .35em;
                        border-radius: .2em;
                        color: #fff !important;
                        background: var(--nr-link) !important;
                        box-shadow: none;
                        transform: scale(.92);
                        transform-origin: left center;
                      }
                      mark.nogar-search-hit { color: inherit !important; background: rgba(249, 115, 22, .42) !important;
                                              outline: 2px solid rgba(249, 115, 22, .72); border-radius: .18em; }

                     html.nogar-scrolling { overflow-x: hidden !important; overflow-y: auto !important; }
                     html.nogar-scrolling body.nogar-reader {
                       width: auto !important; max-width: var(--nr-content-width) !important;
                       min-height: 100vh !important; height: auto !important;
                       margin: 0 auto !important; padding: 56px 0 120px !important;
                       columns: auto !important; overflow: visible !important;
                     }

                     html.nogar-paged { width: 100% !important; height: 100% !important;
                                        overflow-x: auto !important; overflow-y: hidden !important;
                                        scrollbar-width: none; scroll-behavior: smooth; }
                     html.nogar-paged::-webkit-scrollbar { display: none; }
                     html.nogar-paged body.nogar-reader {
                       display: block !important; box-sizing: border-box !important;
                       width: 100vw !important; max-width: none !important;
                       height: 100vh !important; min-height: 0 !important;
                       margin: 0 !important;
                       padding: 48px var(--nr-page-side) 58px !important;
                       column-width: var(--nr-content-width) !important;
                       column-gap: var(--nr-page-gap) !important;
                       column-fill: auto !important; column-rule: none !important;
                       overflow: visible !important;
                     }
                   `;

                   body.classList.add('nogar-reader');
                   root.classList.toggle('nogar-paged', config.flow === 'paged');
                   root.classList.toggle('nogar-scrolling', config.flow !== 'paged');

                   const location = () => {
                     const paged = root.classList.contains('nogar-paged');
                     const extent = paged
                       ? Math.max(0, root.scrollWidth - window.innerWidth)
                       : Math.max(0, root.scrollHeight - window.innerHeight);
                     const offset = paged ? window.scrollX : window.scrollY;
                     const progress = extent <= 1 ? 1 : Math.max(0, Math.min(1, offset / extent));
                     const pageCount = paged ? Math.max(1, Math.round(extent / window.innerWidth) + 1) : 0;
                     const page = paged ? Math.max(1, Math.min(pageCount, Math.round(offset / window.innerWidth) + 1)) : 0;
                     return { progress, page, pageCount, paged };
                   };

                   const postLocation = () => {
                     if (window.chrome && window.chrome.webview) {
                       window.chrome.webview.postMessage({ type: 'nogareader.location', ...preciseLocation() });
                     }
                   };

                   const restore = progress => {
                     const paged = root.classList.contains('nogar-paged');
                     const extent = paged
                       ? Math.max(0, root.scrollWidth - window.innerWidth)
                       : Math.max(0, root.scrollHeight - window.innerHeight);
                     const rawTarget = Math.max(0, Math.min(extent, extent * progress));
                     const target = paged
                       ? Math.max(0, Math.min(extent, Math.round(rawTarget / window.innerWidth) * window.innerWidth))
                       : rawTarget;
                     if (paged) window.scrollTo({ left: target, top: 0, behavior: 'auto' });
                     else window.scrollTo({ left: 0, top: target, behavior: 'auto' });
                     postLocation();
                   };

                   const turnPage = direction => {
                     const current = location();
                     const paged = current.paged;
                     const extent = paged
                       ? Math.max(0, root.scrollWidth - window.innerWidth)
                       : Math.max(0, root.scrollHeight - window.innerHeight);
                     const offset = paged ? window.scrollX : window.scrollY;
                     const step = paged ? window.innerWidth : Math.max(240, window.innerHeight * .86);
                     const atStart = offset <= 3;
                     const atEnd = offset >= extent - 3;
                     if ((direction < 0 && atStart) || (direction > 0 && atEnd)) {
                       return { moved: false, boundary: direction < 0 ? 'start' : 'end', ...current };
                     }

                     const target = paged
                       ? Math.max(0, Math.min(extent, (Math.round(offset / step) + direction) * step))
                       : Math.max(0, Math.min(extent, offset + direction * step));
                     if (paged) window.scrollTo({ left: target, top: 0, behavior: 'smooth' });
                     else window.scrollTo({ left: 0, top: target, behavior: 'smooth' });
                     window.setTimeout(postLocation, 260);
                     return { moved: true, ...current };
                   };

                   const collectTextNodes = () => {
                     const nodes = [];
                     const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT, {
                       acceptNode: node => {
                         if (!node.nodeValue || !node.nodeValue.length) return NodeFilter.FILTER_REJECT;
                         const parent = node.parentElement;
                         if (!parent || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)) {
                           return NodeFilter.FILTER_REJECT;
                         }
                         return NodeFilter.FILTER_ACCEPT;
                       }
                     });
                     while (walker.nextNode()) nodes.push(walker.currentNode);
                     return nodes;
                   };

                   const nodePath = node => {
                     const indexes = [];
                     let current = node;
                     while (current && current !== body) {
                       const parent = current.parentNode;
                       if (!parent) return '';
                       indexes.push(Array.prototype.indexOf.call(parent.childNodes, current));
                       current = parent;
                     }
                     return current === body ? indexes.reverse().join('/') : '';
                   };

                   const resolvePath = path => {
                     if (path === '') return body;
                     let current = body;
                     for (const part of String(path || '').split('/')) {
                       const index = Number(part);
                       if (!Number.isInteger(index) || !current || index < 0 || index >= current.childNodes.length) {
                         return null;
                       }
                       current = current.childNodes[index];
                     }
                     return current;
                   };

                   const globalOffset = (targetNode, targetOffset, nodes) => {
                     let offset = 0;
                     for (const node of nodes) {
                       if (node === targetNode) return offset + Math.max(0, Math.min(targetOffset, node.nodeValue.length));
                       offset += node.nodeValue.length;
                     }
                     return -1;
                   };

                   const selectionAnchor = () => {
                     const selection = window.getSelection();
                     if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return null;
                     const range = selection.getRangeAt(0);
                     if (!body.contains(range.commonAncestorContainer)) return null;
                     const selectedText = range.toString();
                     if (!selectedText.trim() || selectedText.length > 4096) return null;
                     const nodes = collectTextNodes();
                     const start = globalOffset(range.startContainer, range.startOffset, nodes);
                     const end = globalOffset(range.endContainer, range.endOffset, nodes);
                     const allText = nodes.map(node => node.nodeValue).join('');
                     return {
                       startPath: nodePath(range.startContainer),
                       startOffset: range.startOffset,
                       endPath: nodePath(range.endContainer),
                       endOffset: range.endOffset,
                       exactText: selectedText,
                       prefix: start >= 0 ? allText.slice(Math.max(0, start - 64), start) : '',
                       suffix: end >= 0 ? allText.slice(end, Math.min(allText.length, end + 64)) : '',
                       progress: location().progress
                     };
                   };

                   const preciseLocation = () => {
                     const current = location();
                     const points = [
                       [Math.max(12, Math.min(window.innerWidth - 12, pageSide + 12)), 64],
                       [Math.round(window.innerWidth / 2), 64],
                       [Math.round(window.innerWidth / 2), Math.round(window.innerHeight / 2)]
                     ];
                     let caret = null;
                     for (const point of points) {
                       if (document.caretRangeFromPoint) caret = document.caretRangeFromPoint(point[0], point[1]);
                       if (caret && body.contains(caret.startContainer) &&
                           (caret.startContainer.nodeType === Node.TEXT_NODE ||
                            (caret.startContainer !== body && caret.startContainer !== root))) break;
                       caret = null;
                     }
                     if (!caret || !body.contains(caret.startContainer)) return current;
                     let node = caret.startContainer;
                     let offset = caret.startOffset;
                     if (node.nodeType !== Node.TEXT_NODE) {
                       const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
                       node = walker.nextNode();
                       offset = 0;
                     }
                     if (!node || node.nodeType !== Node.TEXT_NODE || !node.nodeValue) return current;
                     const value = node.nodeValue;
                     let start = Math.max(0, Math.min(offset, Math.max(0, value.length - 1)));
                     while (start < value.length && /\s/.test(value[start])) start++;
                     if (start >= value.length) start = Math.max(0, Math.min(offset, value.length - 1));
                     const end = Math.min(value.length, start + 128);
                     const exactText = value.slice(start, end);
                     if (!exactText.trim()) return current;
                     return {
                       ...current,
                       anchor: {
                         startPath: nodePath(node), startOffset: start,
                         endPath: nodePath(node), endOffset: end,
                         exactText,
                         prefix: value.slice(Math.max(0, start - 64), start),
                         suffix: value.slice(end, Math.min(value.length, end + 64)),
                         progress: current.progress
                       }
                     };
                   };

                   const postSelection = () => {
                     const anchor = selectionAnchor();
                     if (window.chrome && window.chrome.webview) {
                       if (anchor) {
                         window.chrome.webview.postMessage({
                           type: 'nogareader.selection',
                           selectedText: anchor.exactText.trim(),
                           anchor
                         });
                       } else {
                         window.chrome.webview.postMessage({ type: 'nogareader.selection-clear' });
                       }
                     }
                   };

                   const locateGlobalPosition = (nodes, position) => {
                     let offset = 0;
                     for (const node of nodes) {
                       const next = offset + node.nodeValue.length;
                       if (position <= next) return { node, offset: Math.max(0, position - offset) };
                       offset = next;
                     }
                     const last = nodes[nodes.length - 1];
                     return last ? { node: last, offset: last.nodeValue.length } : null;
                   };

                   const quoteRange = (anchor, snapshot = null) => {
                     const exact = String(anchor.exactText || anchor.ExactText || anchor.quote || anchor.Quote || '');
                     if (!exact) return null;
                     const nodes = snapshot?.nodes || collectTextNodes();
                     const allText = snapshot?.allText ?? nodes.map(node => node.nodeValue).join('');
                     let index = allText.indexOf(exact);
                     if (index < 0) return null;
                     const prefix = String(anchor.prefix || anchor.Prefix || '');
                     const suffix = String(anchor.suffix || anchor.Suffix || '');
                     let bestIndex = index;
                     let bestScore = -1;
                     while (index >= 0) {
                       let score = 0;
                       if (prefix && allText.slice(Math.max(0, index - prefix.length), index) === prefix) score += 2;
                       if (suffix && allText.slice(index + exact.length, index + exact.length + suffix.length) === suffix) score += 2;
                       if (score > bestScore) { bestIndex = index; bestScore = score; }
                       index = allText.indexOf(exact, index + 1);
                     }
                     const start = locateGlobalPosition(nodes, bestIndex);
                     const end = locateGlobalPosition(nodes, bestIndex + exact.length);
                     if (!start || !end) return null;
                     const range = document.createRange();
                     range.setStart(start.node, start.offset);
                     range.setEnd(end.node, end.offset);
                     return range;
                   };

                   const anchorRange = (anchor, snapshot = null) => {
                     try {
                       const startNode = resolvePath(anchor.startPath ?? anchor.StartPath);
                       const endNode = resolvePath(anchor.endPath ?? anchor.EndPath);
                       if (startNode && endNode && startNode.nodeType === Node.TEXT_NODE && endNode.nodeType === Node.TEXT_NODE) {
                         const range = document.createRange();
                         range.setStart(startNode, Math.min(Number(anchor.startOffset ?? anchor.StartOffset ?? 0), startNode.nodeValue.length));
                         range.setEnd(endNode, Math.min(Number(anchor.endOffset ?? anchor.EndOffset ?? 0), endNode.nodeValue.length));
                         const expected = String(anchor.exactText || anchor.ExactText || anchor.quote || anchor.Quote || '');
                         if (!expected || range.toString() === expected) return range;
                       }
                     } catch (_) { }
                     return quoteRange(anchor, snapshot);
                   };

                   const restoreAnchor = anchor => {
                     if (!anchor) return false;
                     const range = anchorRange(anchor);
                     if (!range) return false;
                     const target = range.startContainer.parentElement || range.commonAncestorContainer;
                     if (!target || !target.scrollIntoView) return false;
                     target.scrollIntoView({ block: 'start', inline: 'start', behavior: 'auto' });
                     if (root.classList.contains('nogar-paged')) {
                       const page = Math.round(window.scrollX / window.innerWidth) * window.innerWidth;
                       window.scrollTo({ left: page, top: 0, behavior: 'auto' });
                     }
                     const expected = Number(anchor.progress ?? anchor.Progress);
                     const actual = location().progress;
                     if (Number.isFinite(expected) && Math.abs(actual - expected) > .15) return false;
                     postLocation();
                     return true;
                   };

                   const wrapRange = (range, id, color, isNote, extraClass = '', textNodes = null) => {
                     const segments = [];
                     for (const node of textNodes || collectTextNodes()) {
                       try {
                         if (!range.intersectsNode(node)) continue;
                       } catch (_) { continue; }
                       let start = 0;
                       let end = node.nodeValue.length;
                       if (node === range.startContainer) start = range.startOffset;
                       if (node === range.endContainer) end = range.endOffset;
                       if (end > start) segments.push({ node, start, end });
                     }
                     for (let index = segments.length - 1; index >= 0; index--) {
                       const segment = segments[index];
                       const selected = segment.node.splitText(segment.start);
                       selected.splitText(segment.end - segment.start);
                       const mark = document.createElement('mark');
                       mark.dataset.nogarId = id;
                       mark.className = `nogar-annotation nogar-${color || 'yellow'}${isNote ? ' nogar-note' : ''}${extraClass ? ` ${extraClass}` : ''}`;
                       if (id !== '__search__') {
                         mark.tabIndex = 0;
                         mark.setAttribute('role', 'button');
                         mark.title = isNote ? '点击查看完整笔记' : '点击管理高亮';
                       }
                       selected.parentNode.insertBefore(mark, selected);
                       mark.appendChild(selected);
                     }
                     return segments.length > 0;
                   };

                   const clearAnnotationMarks = () => {
                     for (const mark of Array.from(document.querySelectorAll('mark[data-nogar-id]'))) {
                       const parent = mark.parentNode;
                       while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                       parent.removeChild(mark);
                       parent.normalize();
                     }
                   };

                   const applyAnnotations = items => {
                     clearAnnotationMarks();
                     const nodes = collectTextNodes();
                     const snapshot = { nodes, allText: nodes.map(node => node.nodeValue).join('') };
                     const pending = [];
                     for (const item of Array.isArray(items) ? items : []) {
                       const type = String(item.type ?? item.Type ?? '').toLowerCase();
                       const anchor = item.anchor ?? item.Anchor;
                       if (!anchor || (type !== 'highlight' && type !== 'note' && type !== '1' && type !== '2')) continue;
                       const range = anchorRange(anchor, snapshot);
                       if (range) pending.push({ item, type, range });
                     }
                     pending.sort((left, right) =>
                       right.range.compareBoundaryPoints(Range.START_TO_START, left.range));
                     let applied = 0;
                     for (const { item, type, range } of pending) {
                       const id = String(item.id ?? item.Id ?? '');
                       const color = String(item.color ?? item.Color ?? 'yellow').toLowerCase();
                       if (wrapRange(range, id, color, type === 'note' || type === '2', '', snapshot.nodes)) applied++;
                     }
                     return applied;
                   };

                   const goToAnnotation = id => {
                     const mark = document.querySelector(`mark[data-nogar-id="${CSS.escape(String(id))}"]`);
                     if (!mark) return false;
                     mark.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
                     window.setTimeout(() => {
                       if (root.classList.contains('nogar-paged')) {
                         const target = Math.round(window.scrollX / window.innerWidth) * window.innerWidth;
                         window.scrollTo({ left: target, top: 0, behavior: 'smooth' });
                       }
                       postLocation();
                     }, 260);
                     return true;
                   };

                   const clearSearchHit = () => {
                     for (const mark of Array.from(document.querySelectorAll('mark.nogar-search-hit'))) {
                       const parent = mark.parentNode;
                       while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                       parent.removeChild(mark);
                       parent.normalize();
                     }
                   };

                   const revealText = (query, occurrenceIndex = 0) => {
                     clearSearchHit();
                     const needle = String(query || '').trim();
                     if (!needle) return false;
                     const nodes = collectTextNodes();
                     const allText = nodes.map(node => node.nodeValue).join('');
                     const haystack = allText.toLocaleLowerCase();
                     const normalizedNeedle = needle.toLocaleLowerCase();
                     const requestedOccurrence = Math.max(0, Math.min(10000, Number.parseInt(occurrenceIndex, 10) || 0));
                     let index = -1;
                     let searchFrom = 0;
                     for (let occurrence = 0; occurrence <= requestedOccurrence; occurrence++) {
                       index = haystack.indexOf(normalizedNeedle, searchFrom);
                       if (index < 0) return false;
                       searchFrom = index + Math.max(1, normalizedNeedle.length);
                     }
                     if (index < 0) return false;
                     const start = locateGlobalPosition(nodes, index);
                     const end = locateGlobalPosition(nodes, index + needle.length);
                     if (!start || !end) return false;
                     const range = document.createRange();
                     range.setStart(start.node, start.offset);
                     range.setEnd(end.node, end.offset);
                     if (!wrapRange(range, '__search__', 'yellow', false, 'nogar-search-hit')) return false;
                     const hit = document.querySelector('mark.nogar-search-hit');
                     if (hit) hit.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
                     window.setTimeout(postLocation, 260);
                     return true;
                   };

                   const clearSelection = () => {
                     const selection = window.getSelection();
                     if (selection) selection.removeAllRanges();
                     postSelection();
                   };

                   window.__nogareader = {
                     location, restore, turnPage, postLocation,
                     preciseLocation, restoreAnchor, selectionAnchor,
                     applyAnnotations, goToAnnotation, revealText, clearSelection
                   };
                   applyAnnotations(initialAnnotations);
                   if (window.__nogareaderSelectionHandler) {
                     document.removeEventListener('mouseup', window.__nogareaderSelectionHandler);
                     document.removeEventListener('keyup', window.__nogareaderSelectionHandler);
                   }
                   window.__nogareaderSelectionHandler = () => window.setTimeout(postSelection, 0);
                   document.addEventListener('mouseup', window.__nogareaderSelectionHandler, { passive: true });
                   document.addEventListener('keyup', window.__nogareaderSelectionHandler, { passive: true });
                   if (window.__nogareaderAnnotationClickHandler) {
                     document.removeEventListener('click', window.__nogareaderAnnotationClickHandler);
                     document.removeEventListener('keydown', window.__nogareaderAnnotationClickHandler);
                   }
                   window.__nogareaderAnnotationClickHandler = event => {
                     const target = event.target instanceof Element
                       ? event.target.closest('mark.nogar-annotation[data-nogar-id]')
                       : null;
                     if (!target || target.dataset.nogarId === '__search__') return;
                     const keyboardOpen = event.type === 'keydown' && (event.key === 'Enter' || event.key === ' ');
                     if (event.type === 'keydown' && !keyboardOpen) return;
                     const selection = window.getSelection();
                     if (event.type === 'click' && selection && !selection.isCollapsed) return;
                     if (keyboardOpen) event.preventDefault();
                     if (window.chrome && window.chrome.webview) {
                       window.chrome.webview.postMessage({
                         type: 'nogareader.annotation-click',
                         id: String(target.dataset.nogarId || '')
                       });
                     }
                   };
                   document.addEventListener('click', window.__nogareaderAnnotationClickHandler);
                   document.addEventListener('keydown', window.__nogareaderAnnotationClickHandler);
                   if (window.__nogareaderScrollHandler) {
                     window.removeEventListener('scroll', window.__nogareaderScrollHandler);
                   }
                   let scrollTimer = 0;
                   window.__nogareaderScrollHandler = () => {
                     window.clearTimeout(scrollTimer);
                     scrollTimer = window.setTimeout(postLocation, 140);
                   };
                   window.addEventListener('scroll', window.__nogareaderScrollHandler, { passive: true });

                   if (window.__nogareaderResizeHandler) {
                     window.removeEventListener('resize', window.__nogareaderResizeHandler);
                   }
                   let resizeTimer = 0;
                   window.__nogareaderResizeHandler = () => {
                     const saved = location().progress;
                     window.clearTimeout(resizeTimer);
                     resizeTimer = window.setTimeout(() => {
                       if (window.chrome && window.chrome.webview) {
                         window.chrome.webview.postMessage({ type: 'nogareader.resize', progress: saved });
                       }
                     }, 180);
                   };
                   window.addEventListener('resize', window.__nogareaderResizeHandler, { passive: true });

                   requestAnimationFrame(() => requestAnimationFrame(() => {
                     if (!restoreAnchor(initialRestoreAnchor)) restore(config.restoreProgress);
                   }));
                   return { ready: true, ...location() };
                   } catch (error) {
                     const message = String(error && error.message ? error.message : error).slice(0, 240);
                     if (window.chrome && window.chrome.webview) {
                       window.chrome.webview.postMessage({ type: 'nogareader.runtime-error', message });
                     }
                     return { ready: false, error: message };
                   }
                   };
                 })();
                 """;
    }
}
