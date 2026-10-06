# Release notes convention

Every release needs its own notes file: **`.github/release-notes/vX.Y.Z.md`** (matching the
`Version:` field in `src/HydraSync/extension.yaml` and the git tag).

The release workflow uses that file as the GitHub release body. If the file is missing, CI
still publishes the release but logs a warning and falls back to a minimal generated body —
so the right habit is to write the notes *before* tagging.

## Version numbering

The version is deliberately not bumped more than it needs to be:

| Change | Bump | Example |
|---|---|---|
| New feature | **+0.1** (minor) | 1.8.0 → **1.9.0** |
| Bug fix only | **+0.0.1** (patch) | 1.9.0 → **1.9.1** |
| Large release with a batch of features | major | 1.9.0 → **2.0.0** |

Only reach for a new major version for something that genuinely justifies it — a batch of
features, not routine additions.

## How to cut a release

1. Update `Version:` in `src/HydraSync/extension.yaml` (see the numbering table above).
2. Write `.github/release-notes/vX.Y.Z.md` — start from `_template.md`.
   Notes describe **this version only**; don't accumulate older highlights.
3. Bump the version references in `README.md` (package name, checklist).
4. Commit, tag and push:
   ```bash
   git add -A
   git commit -m "Release vX.Y.Z"
   git tag vX.Y.Z && git push origin main && git push origin vX.Y.Z
   ```
5. CI (`windows-latest`) verifies the tag matches the manifest version, builds, packages the
   `.pext`, publishes the release using the notes file, and records the release in
   `InstallerManifest.yaml`.

## Tone / wording

- Write for users: what changed, what they need to do, how to verify.
- Mention upgrade notes when behaviour changes (e.g. settings that became opt-in).
- No emulator/crack brand names — describe "achievement files read from disk".