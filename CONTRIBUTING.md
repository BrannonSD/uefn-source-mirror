# Contributing

Build on Windows, run `--self-test`, and run `python scripts/check_public_repo.py` before opening a pull request. Add a meaningful safety test when changing snapshot generation, pruning, path validation, or file inclusion.

Keep the mirror engine conservative: source projects are authoritative, destinations stay separate, repositories stay private, and automatic pushes must not force or merge remote edits.

Use generic temporary fixtures and examples. Never commit real profiles, local paths, project mirrors, credentials, logs, verification reports, or screenshots containing personal information. Public visuals should be generic illustrations or redacted screenshots.

The icon can be regenerated with `python make_icon.py` when Pillow is installed. The normal build uses the checked-in icon and does not need Python or Pillow.
