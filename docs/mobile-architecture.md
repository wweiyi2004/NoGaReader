# NoGaReader mobile architecture

## Delivery strategy

NoGaReader Mobile will be delivered Android-first and iOS second. The existing
WPF application remains a supported client throughout the migration; extracting
shared code must not change its observable behavior or its persisted data.

The Android MVP includes EPUB, PDF, TXT, Markdown, HTML, FB2, images, and CBZ,
plus the library, table of contents, search, reading progress, bookmarks,
highlights, notes, themes, and synchronization. Desktop-tool-dependent formats
and features (MOBI/AZW conversion, CHM, XPS/OXPS, DjVu, document editing,
printing) remain desktop-only for the first mobile release.

## Project boundaries

- `NoGaReader.Core` targets platform-neutral .NET and owns shared models,
  persistence, synchronization, parsing, content sanitization, and reader web
  assets. `DocumentFormatSupport` exposes separate desktop and mobile-MVP
  capability catalogs so clients only advertise formats they can actually open.
  `PortableDocumentLoader` is the public preparation entry point shared by
  desktop and mobile reader hosts.
- `NoGaReader` remains the Windows WPF/WebView2 application and owns Windows
  dialogs, process integration, XPS rendering, Office editing, and desktop-only
  format conversion.
- `NoGaReader.Mobile` is a .NET MAUI Android application. It owns mobile UI,
  file picking/import, sandbox storage, lifecycle handling, a WebView
  host, PDF rendering, sharing, and secure platform storage.

The first extraction uses linked source files so the new assembly boundary can
be validated independently of a large path-only rename. Once the boundary is
stable, shared files can move under `src/NoGaReader.Core` in a mechanical
follow-up change.

## Data compatibility

- The existing SQLite schema, settings JSON, and sync manifest remain readable.
- Imported mobile books are copied into app-managed storage so access survives
  restarts and platform permission changes.
- A future schema migration adds a content fingerprint. Cross-device identity
  must not depend on an absolute file path.
- Sync output must not disclose local absolute paths. Concurrent writers need
  optimistic version checks and deterministic merging before mobile Beta.

## Implemented Android slice

- Android single-target MAUI project (`net9.0-android`) is part of the solution.
- The system picker imports supported files into app-managed storage.
- The shared SQLite library stores books, progress, bookmarks, highlights, notes,
  and the bounded full-text index.
- EPUB, FB2, TXT, Markdown, and sanitized content render in the mobile WebView;
  images and CBZ use the image host; PDF uses Android `PdfRenderer`.
- The reader WebView is offline: resource requests outside local files are
  blocked at the `WebViewClient` level, and tapped web/mail links are cancelled
  and handed to the system, matching the desktop interception guarantees.
- The reader exposes section/TOC navigation, previous/next controls, progress
  restoration, full-book search, selection annotations, and light/dark themes.
- Android/Linux path keys and containment checks follow case-sensitive filesystem
  semantics while preserving Windows compatibility.
- The mobile library flows (import, open, progress, bookmarks, annotations,
  search, delete) live in Core as `PortableLibraryService`; the Android client
  is a thin adapter, and the Windows smoke suite exercises the shared flows
  end-to-end without an emulator.
- The reader's paging/section/progress/contents state machine lives in Core as
  `MobileReaderPresenter`; `ReaderPage` is a thin host that renders presenter
  state and bridges WebView, `PdfRenderer`, and dialogs. Paging bounds,
  restore math, contents mapping, and in-book link resync are smoke-tested.
- Text annotations carry a portable `TextAnchor` (exact text plus surrounding
  context and progress). `MobileReaderScripts` builds the capture and mount
  JavaScript: mounting scores anchor context to disambiguate repeated text and
  falls back to first occurrence for legacy rows; the script contract and the
  anchor round-trip are smoke-tested.
- The shelf state (filtering, summary/empty-state copy, continue-reading pick)
  lives in Core as `MobileLibraryPresenter`; `MainPage` is a thin renderer.
- The mobile chrome uses the same warm-stone palette as the desktop client,
  including the continue-reading card and the muted-surface format badges.

## Remaining Beta work

- Extend the anchor bridge with DOM-path capture (desktop `StartPath`/
  `EndPath` parity) so anchors survive even when the visible text changes.
- Add Android Storage Access Framework folder synchronization. The existing
  filesystem sync service cannot treat a `content://` tree URI as a normal path.
- Add a content fingerprint migration, optimistic sync-manifest concurrency, CI
  emulator UI tests, accessibility review, release signing, and Play Store assets.

## Phase 0/1 acceptance criteria

1. `NoGaReader.Core` builds as `net8.0` without a Windows target framework.
2. No WPF, WebView2, registry, or Windows XPS dependency is compiled into Core.
3. The existing Release solution build has zero warnings and errors.
4. The existing smoke suite still passes without behavior changes.
5. Core is referenced by the MAUI target without referencing the WPF application.

## Toolchain decision

The repository pins .NET SDK 9.0.311 in `global.json`; `NoGaReader.Mobile`
targets `net9.0-android`, while Core stays at `net8.0` for compatibility with the
desktop client. Local Android builds require the `maui-android` workload,
Android SDK 35, and JDK 17 or 21.
