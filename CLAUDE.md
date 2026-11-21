# CLAUDE.md

VRChat PhotoSync: a small .NET 10 WinForms app that watches the VRChat camera roll and uploads new photos to an Immich server. The full design and the step-by-step checklist are in `PLAN.md`.

**This is a public repository.** The developer's real paths, server address and other personal details are in `CLAUDE.local.md` (git-ignored). Use them while working, but never copy them into committed files, commit messages or test data. Use example values instead (`D:\Photos\VRChat`, `https://immich.example.com`).

## Workflow
- Work through `PLAN.md` one step at a time.
- **After each step is complete, commit and push:**
  - Tick its checkbox(es) in `PLAN.md` in the same commit.
  - Use a short message that names the step.
  - Then `git push` (remote `origin`, branch `main`).
- Don't tick a step whose verification hasn't passed. If a step can only be partly done (for example, it needs VRChat running or a manual action from the user), commit what's done, leave the box unticked, and say what's left.
- Before each commit, check the staged diff for personal details (real paths, server names, user/world IDs, display names).

## Safety
- **Never write to the real camera roll** or to VRChat's `config.json` (`%LocalAppData%Low\VRChat\VRChat\config.json`). Test against a scratch folder using `--folder`.
- `sample-photos\` is the developer's temporary copy of recent camera-roll months, git-excluded.
  - Use it as a source of real files for manual checks: copy files from it into a scratch folder.
  - Never commit it, and never write automated tests that depend on it. Unit tests generate their own fixtures.
- Never commit `config.json`, `state.json`, logs, or any API key. Never log the API key.
- Real photos and prints include usernames, world names, and player lists in VRCX metadata. Don't copy real photos into the repo.

## Environment
- Windows. Python is not installed, so use PowerShell or the .NET tooling.
- .NET SDK 10.
- `tools/watch-vrcx.ps1` re-measures VRCX metadata timing (`-Root <camera roll> -Minutes N -Log <file>`).
