# Skunkworks

> Workspace rules live in the Emergence Tech top-level `CLAUDE.md`; Claude Code loads it from the ancestor folder on both machines and in every worktree. Edit them there, never copy them here. What Skunkworks is and how to use it is in `README.md`; this file holds only the constraints that keep this vertical whole.

**Before writing, editing or reviewing any Seed** (`*.seed`, or Seed text inside other code), check that the Seed essentials from hirari's `CLAUDE.md` are loaded; the Emergence Tech workspace `CLAUDE.md`, in an ancestor folder of the main checkout and of agent worktrees alike, imports them. If they are not, read hirari's `CLAUDE.md` from its checkout (the workspace's `hirari Shell and Seed Lang/`) or https://github.com/JinkaiDesu/hirari-shell. For details, `SEED_SPEC.md` (§14 covers Unity reflection) is in the workspace's `Agent/hirari Shell and Seed Lang/`. **Never guess Seed syntax** from Bash, Python or JS.

## Constraints

- **Skunkworks is core.** It depends on Unity 6000.5.6f1 and the .NET CLR, nothing else. It never bundles, builds against or tests against another project (hirari, skunkworks_cli, Craftwork or any other); they depend on it, never the reverse.
- **Unity owns** objects, components, rendering and lifecycle. Reflection requests run on Unity's `Update`.
- **The shell is configuration, and there is no default.** `SKUNK_TERMINAL_CONFIG` (or a product's `StreamingAssets/terminal.config`) selects the executable, environment, PATH and startup; without one the terminal stays stopped and says so. The C# supervisor knows no shell's syntax.
- **The protocol is a published contract.** `Unity/Packages/com.skunkworks.protocol/schema.json` is canonical; `generate.ps1` writes the C# codec from it and `-Check` fails on drift. Clients copy the schema and follow it; Skunkworks never checks them.
- **Handles belong to their connection** and are released when it closes. Registered roots (`RegisterRoot`) persist.
- **The `unity` command belongs to skunkworks-cli,** which publishes it beside `skunkworks_cli.exe`. The DLL route (`Dev/`) was removed on 2026-10-16 as a failed design that mixed state, lifecycle and data in one node; never `git restore` or recreate `Dev/`.
- **No engine distribution.** Skunkworks is a standalone demo and portfolio piece; the owner has no Unity license to distribute it as a primarily reflective, moddable engine. The only binary is the website demo, `skunkworks-v<version>-demo`. Never package, name or describe a build as a moddable or reusable engine, SDK or platform release: others build from source under their own Unity license.
- **No signing here.** The demo and its manifest are published unsigned; the VPS countersigns what the launcher trusts, after the owner approves a version. No signing keys live on this PC.
- **Protected semantics:** reflection, IPC and protocol changes go on a branch, not `main`, unless the owner says otherwise.
- **Two Markdown files:** this one and `README.md`. The README is the owner's, for people: under 80 lines, no internal docs or contributor detail. Internal notes live in the workspace's `Agent/Skunkworks Unity Project/`.

## Working here

- **Layout:** `Unity/` (Unity 6000.5.6f1; runtime in `Assets/Scripts`, protocol in `Packages/com.skunkworks.protocol`), `packaging/`, `tests/` (`host/` is the headless player and its .NET client), `Release/` (builds and logs, ignored), `component.json` (the version).
- **CI/CD is `ci.ps1`, run by hand, for Skunkworks only;** its inputs are in the git-ignored `ci.config.json` (see the script's help).
  - `verify`: the generated protocol, then `tests/host selftest`, the protocol's behavior against the headless player. No Unity.
  - `build`: from a pushed, clean commit, `packaging/build-release.ps1` (the player and its licenses; `tests/demo-release.ps1` accepts it), `packaging/pack.ps1`, `tests/release.tests.ps1` against the running player, then `Release/skunkworks-v<version>-demo.manifest.json`.
  - `publish`: from this PC with the user's `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID`, to R2 `skunkworks/<version>/` (the zip and its manifest, immutable, never overwritten), then the moving `skunkworks/manifest.json` (no-cache).
- **The manifest is this project's whole interface** to the website and the launcher: `emergence.manifest/1` with `platform` and `artifact.{file,key,url,bytes,sha256}`. A rebuild is a new version. The zip's root holds `release.receipt.json`.
- **Tests** are behavioral and live in `tests/`. Conventions belong here, never as test assertions.
- **Changes:** work in a worktree, `Agent/worktrees/skunkworks/<topic>` on `agent/<topic>`, run `pwsh ci.ps1 verify`, and push.
