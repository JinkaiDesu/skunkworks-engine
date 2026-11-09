# Skunkworks

> Workspace rules live in the Emergence Tech top-level `CLAUDE.md`; Claude Code loads it from the ancestor folder on both machines and in every worktree. Edit them there, never copy them here. What Skunkworks is and how to use it is in `README.md`; this file holds only the constraints that keep this vertical whole.

**Before writing, editing or reviewing any Seed** (`*.seed`, or Seed text inside other code), check that the Seed essentials from hirari's `CLAUDE.md` are loaded; the Emergence Tech workspace `CLAUDE.md`, in an ancestor folder of the main checkout and of agent worktrees alike, imports them. If they are not, read hirari's `CLAUDE.md` from its checkout (the workspace's `hirari Shell and Seed Lang/`) or https://github.com/JinkaiDesu/hirari-shell. For details, `SEED_SPEC.md` (§14 covers Unity reflection) is in the workspace's `Agent/hirari Shell and Seed Lang/`. **Never guess Seed syntax** from Bash, Python or JS.

## Constraints

- **Skunkworks is core.** It depends on Unity 6000.5.6f1 and the .NET CLR, nothing else. It never bundles, builds against or tests against another project (hirari, skunkworks_cli, Craftwork or any other); they depend on it, never the reverse.
- **Skunkworks is a host, not a runtime.** It owns the window, rendering, input capture, startup, shutdown and the player log, on a blank canvas. It has no world, simulation, shell, terminal, command surface or scripting interface, and needs none to start.
- **The computation is foreign.** A native, external system will compute; Skunkworks collects its input and presents its output. That system and its transport are not designed yet: add no interface, bridge, memory layout or placeholder type for them until the owner designs one.
  - **Foreign input** is `InputEvents` (Unity's Input System, observe only) copying device facts into `InputProgram`, a bounded journal read by sequence. Nothing is delivered or pumped.
  - **Foreign output** is Unity's renderer. `Home.unity` holds one camera that clears the canvas; nothing presents on it yet.
- **Unity's loop is Unity's.** `Update` and `FixedUpdate` pace Unity-specific work only. No host logic runs on them, and Unity's frame time, GameObjects and physics are never the authority for the foreign computation. `Main` has neither method.
- **Removed on 2026-11-08, last present in 0.6.1-experimental (`027f2c0`):** the CLR reflection broker, the `skunkworks.wire/1` named pipe and protocol package, the embedded terminal and shell supervision, `RuntimeBoundary`, `WindowControl` and `SystemPaths`. They do not come back as compatibility or convenience layers; restore one only if the owner asks. The DLL route (`Dev/`) was removed on 2026-10-16 as a failed design that mixed state, lifecycle and data in one node; never `git restore` or recreate `Dev/`.
- **No engine distribution.** Skunkworks is a standalone demo and portfolio piece; the owner has no Unity license to distribute it as a moddable engine. The only binary is the website demo, `skunkworks-v<version>-demo`. Never package, name or describe a build as a moddable or reusable engine, SDK or platform release: others build from source under their own Unity license.
- **No signing here.** The demo and its manifest are published unsigned; the VPS countersigns what the launcher trusts, after the owner approves a version. No signing keys live on this PC.
- **Two Markdown files:** this one and `README.md`. The README is the owner's, for people: under 80 lines, no internal docs or contributor detail. Internal notes live in the workspace's `Agent/skunkworks-engine/`.

## Working here

- **Layout:** `Unity/` (Unity 6000.5.6f1; the host is `Assets/Scripts`, the canvas is `Assets/Scenes/Home.unity`), `packaging/`, `tests/` (`host/` compiles the input journal without Unity), `Release/` (builds and logs, ignored), `component.json` (the version).
- **CI/CD is `ci.ps1`, run by hand, for Skunkworks only;** its inputs are in the git-ignored `ci.config.json` (see the script's help).
  - `verify`: `tests/host selftest`, the input journal's behavior. No Unity.
  - `build`: from a pushed, clean commit, `packaging/build-release.ps1` (the player and its license; `tests/demo-release.ps1` accepts it), `packaging/pack.ps1`, `tests/release.tests.ps1` against the running player, then `Release/skunkworks-v<version>-demo.manifest.json`.
  - `publish`: from this PC with the user's `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID`, to R2 `skunkworks/<version>/` (the zip and its manifest, immutable, never overwritten), then the moving `skunkworks/manifest.json` (no-cache). The launcher polls that file and its Play session depends on the 0.6.1 terminal, so publishing a host build breaks Play until the launcher changes.
- **The manifest is this project's whole interface** to the website and the launcher: `emergence.manifest/1` with `platform` and `artifact.{file,key,url,bytes,sha256}`. A rebuild is a new version. The zip's root holds `release.receipt.json`.
- **The player log is the host's voice:** `[skunkworks] started: …` and `[skunkworks] stopped: …`, in Unity's player log (`-logFile`).
- **Tests** are behavioral and live in `tests/`. Conventions belong here, never as test assertions.
- **Changes:** work in a worktree, `Agent/worktrees/skunkworks/<topic>` on `agent/<topic>`, run `pwsh ci.ps1 verify`, and push.
