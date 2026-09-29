# Portable reviews

The Share/review page saves one .allegroreview file containing historical source
evidence, findings, measurements, annotations, limitations, decisions and any
captured image variants. Opening the file does not contact Allegro or give its
findings authority to change the current board.

To review an analysis, run a registered tool in Engine workspace and choose
Review current published findings. The action transfers the accepted publication,
its captured selection and its registered options into Share/review. The
tool's completion and analysis coverage remain visible. Completed execution
without analysis coverage is shown as Unknown, including a zero-finding result.

Select a finding, supply an informational reviewer label and choose a PD
disposition. Dismissal needs a reason. Resolved in later capture requires recorded
comparison evidence for that finding. Decisions belong to the exact review
subject; changing boards does not transfer them.

Save portable review creates a new file. Save revision updates an opened portable
review only when its prior revision and content digest still match. A conflicting
save is refused. Unknown optional extension data stays in the saved copy. A
required extension that the application does not understand prevents import.

Live review capture still provides independent raw and annotated images. Blank
stamp coordinates place the title at the captured viewport center. Explicit
coordinates must supply both values; object markers must refer to an object in
that capture. Only the selected image variant is retained as a decoded display
image. Clear held review releases local presentation without deleting a file.

Legacy review-bundle.json files remain readable. Import checks bounded sibling
image bytes and refuses redirected paths. It verifies and decodes the same
captured bytes. The exact original manifest is retained as optional legacy data;
unrecorded acquisition, analysis or producer evidence remains explicitly unknown.
Saving a portable copy does not rewrite, prune or acquire ownership of the
legacy directory. Removing a recent item only forgets that item.

## Source-only packaging

The generated source ZIP includes the private SDK dependency payload and is
for approved internal private-SDK recipients. Public PD CI neither restores the
private SDK nor creates/uploads this ZIP. Its package-only checks run in the
private Bridge integration workflow after exact-version injection. Portable
review/support data files have their own data contracts; they are not SDK source
distribution archives.

The portable source packager does not publish a runtime or require PowerShell:

    python scripts/package-source.py --output artifacts/PD-Simple-Source.zip
    python scripts/check-source-archive.py artifacts/PD-Simple-Source.zip

On Windows, the complete extracted package-only journey is:

    python scripts/check-source-archive.py artifacts/PD-Simple-Source.zip --run-managed

The checker creates an independent temporary extraction and package cache, checks
the original six-package manifest and hashes, builds PD and all documented
samples, and runs the managed checks including real WPF tests. Build.ps1
-SourceOnly delegates to the same Python packaging owner and uses the configured
PowerShell execution policy.

The source ZIP includes only the active Directory.Build.props generation's six
hash-verified packages, its exact versioned manifest, packages/README.md, and the
exact starter archive when declared by that manifest. The starter's version,
hash and entry inventory are verified. Historical packages and starter archives
stay in the checkout and are omitted. The independent checker rejects missing,
extra, stale or tampered generation payloads.
Exactly one active development or release manifest is retained under its original
filename. Ambiguous manifests or a mismatched schema/kind are rejected. Source
archive validation does not replace release signing or native acceptance gates.

New review APIs require the coherent Bridge candidate containing the portable
review implementation. The historical .183 bundle remains separately identified
by its recovered original manifest.
