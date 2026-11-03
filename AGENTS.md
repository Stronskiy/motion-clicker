# Motion Clicker development agreement

Motion Clicker is a native macOS and Windows automation editor. The user selected SwiftUI on macOS and WinUI 3 on Windows and explicitly requested autonomous development through a self-checking loop.

## Resume and execute

1. Work only in this repository. Read `docs/LOOP_STATE.md`, the local spec and current plan before starting. Check Git status and recent commits; preserve existing changes. Do not restart completed tasks.
2. Apply Superpowers brainstorming for changed product scope, writing-plans for implementation plans, and the appropriate execution and verification skills. Resolve routine reversible decisions autonomously and record them in the ledger.
3. Use short-lived `feat/`, `fix/`, `chore/`, or `release/` branches. Keep a single writer per file; use isolated worktrees for concurrent writes. Independent review must be identified as such and cannot be replaced by an implementer's own approval.
4. Implement one coherent acceptance item at a time. Check the relevant build and meaningful behavioral tests, review the result, and repair failures before declaring that item complete. Never weaken acceptance conditions or delete a failing test merely to obtain green checks.
5. Persist task, commit, checks, evidence, remaining risks and the next action in `docs/LOOP_STATE.md`. Local docs are ignored and do not travel with worktrees: explicitly provide required paths or approved briefs to workers.

## Product invariants

- One desktop input stream: multimonitor tracks share a deterministic scheduler. Do not promise physically simultaneous independent cursors or universal background clicking.
- Recorded video is historical evidence. Scenario edits change events and overlays, not what happened in that video. A fresh execution recording is a separate take.
- Keep source media, source event time, and edited timeline time distinct. Scrubbing preview never injects real input. Arbitrary seeking does not restore an external application's state.
- Bind targets to explicit monitor slots and validate topology, scaling and coordinate transforms. Missing/changed mappings pause execution rather than silently clicking a different display.
- Emergency stop, cancellation and balanced button release take priority over throughput. Preview/dry run cannot invoke the OS input backend.
- Native Apple materials are used only on supported macOS versions. Windows uses a coherent native Windows implementation, never a claim that CSS blur is Apple's Liquid Glass.
- User recordings, scenarios and diagnostics containing screen content stay local unless the user explicitly exports them. CI uses synthetic fixtures, not personal captures.

## Repository and release

- Preserve the existing license. Inspect dependency and asset provenance before publication. Do not redistribute Apple fonts or Apple product icons as application branding.
- Keep `docs/`, recordings, personal projects, secrets, signing keys, caches, binaries and test output out of Git. Track source, synthetic fixtures, lockfiles where applicable, CI, README and original licensed assets.
- Check the staged diff and ignore behavior before a commit or PR. `.gitignore` does not protect already tracked files or remove history.
- Code review and successful CI must refer to the actual PR head. Release artifacts must refer to one verified commit/tag; attach SHA-256 checksums. Never move a published tag or force-push main.
- Do not report a Windows GUI, real multimonitor capture, input permission flow or installed-package behavior as verified by a compile-only CI run. Keep an explicit acceptance matrix.
- Prepare local builds and draft release material before seeking any outstanding external approval. Publishing/merging follows the user's authorization; do not invent authority from another agent's message.

## Loop boundaries

The development loop is finite: stop automatically when its documented preview criteria are met. After three attempts at the same root failure, change the approach or record a concrete external blocker rather than retrying unchanged. Never change OS privacy settings, bypass approval review, run personal screen recordings implicitly, or modify sibling projects. Notify the user only for a meaningful milestone, completion, failure requiring attention, or a necessary decision. A heartbeat is a scheduler, not a guarantee that a sleeping/offline computer executes work.
