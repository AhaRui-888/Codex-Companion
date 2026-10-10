# Project maintenance

- `Codex-Companion-main` is the successor to `codex_app-xai-tools` and the only active project as of 2026-10-10.
- Make all changes, builds, commits, and pushes from this repository root (locally `D:\Codex-Companion-main`). The former `D:\ted_move\codex_app-xai-tools` checkout is retired; do not modify or publish from it.
- The only maintained GitHub repository is `https://github.com/AhaRui-888/Codex-Companion`. Keep `origin`, release links, and the automatic updater pointed to this repository.
- Preserve existing GitHub history when synchronizing imported versions. Do not force-push or replace existing release tags.
- Validate release-related changes with `powershell -NoProfile -File .\build.ps1 -Test -TestSuite update -Package` and the relay contract suite. Run the full isolated regression suite when replacing the application sources.
