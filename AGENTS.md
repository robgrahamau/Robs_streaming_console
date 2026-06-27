# Steaming — Rules for AI Agents

**READ THIS ENTIRE FILE BEFORE WRITING A SINGLE LINE OF CODE.**
**READ ARCHITECTURE.md FOR TECHNICAL DETAILS.**
**READ MISTAKES.md FOR PAST FAILURES THAT MUST NOT REPEAT.**
**EVERY RULE HERE EXISTS BECAUSE A PREVIOUS AGENT VIOLATED IT AND WASTED THE USER'S TIME.**
**NONE ARE OPTIONAL. NONE HAVE EXCEPTIONS.**

---

## BEFORE YOU TOUCH ANYTHING

### B1. Read the codebase first. Always.
- Before proposing a fix, writing code, or answering an architecture question: read the relevant files.
- "Relevant files" means every file in the call chain — not just the one you think is the problem.
- You cannot know what is wrong without reading what is there. Guessing wastes the user's time.
- This has been violated repeatedly. Agents changed code without reading it and introduced regressions.

### B2. State what currently works before touching it.
- Before editing any file, explicitly identify what functionality in that area is currently working.
- Your change must not break it. If it does, that is worse than the original bug.

### B3. Commit before any destructive operation.
- A git commit IS the backup that Rule 1 requires.
- Before running any DB patch, file rewrite, or data migration: commit the current state first.
- A session that ran a DB patch without committing overwrote production viewer data. That data was unrecoverable without raw snapshot rows.
- **No commit = no backup = Rule 1 violation.**

### B4. Bump the version before committing changes.
- Version is in `Steaming.Core/Steaming.Core.csproj` — `Version`, `AssemblyVersion`, `FileVersion`.
- Every set of changes gets a patch bump (0.6.1 → 0.6.2) before the commit.
- Do not skip this. Do not do it after the commit.

---

## CORE RULES — NON-NEGOTIABLE

### 1. NEVER DELETE OR OVERWRITE DATA WITHOUT A BACKUP.
- Never delete a file without a backup.
- Never overwrite existing positive data in a DB with a fixed value (`SET peak = 3`). Use `MAX(existing, new)` for peaks. Use deltas for counters.
- Patch in place when possible. If you cannot patch safely, stop and explain before proceeding.
- The git commit is the backup. See B3.

### 2. THIS APP USES MVVM. NO EXCEPTIONS.
- ALL application state and commands live in ViewModels (`Steaming.Application/ViewModels/`).
- ViewModels have ZERO WPF or WinUI imports.
- Code-behind contains ONLY: `InitializeComponent`, event→command wiring, pure view animations.
- Business logic in code-behind = rule violation. Fix it immediately.

### 3. Never claim something works unless you have verified it.
- Build before telling the user something is done.
- If build passes but runtime behaviour is unverified, say so explicitly.
- "It should work" is not acceptable. Ever.
- For UI: trace the full round-trip — the XAML template, the code that populates it, the code that commits it. All three.

### 4. Read all relevant code before writing anything.
- Read every file in the call chain before touching any of them.
- For UI controls: XAML template + populate code + commit handler. All three. Missing any one means the fix is incomplete.
- Check for: multiple instances, shared global state, wire format that must match on both sides.

### 5. Find the root cause. Do not patch symptoms.
- Before writing any fix: state the hypothesis, name the file and line that proves it, confirm it in the code.
- If you cannot point to the line that causes the problem, you have not found the cause. Keep looking.
- Patching symptoms that passes a build is NOT a fix. The bug is still there.
- This has been violated repeatedly. A competing tool found the correct root cause while this agent was fixing surface symptoms one by one.

### 6. Do not half-implement features.
- If the user asked for it, implement all of it.
- No stubs, no TODOs left behind, no "for this use case", no stopping halfway.
- If a feature is genuinely too large for one response, say so BEFORE starting — do not start and stop.

### 7. Do not change the architecture without asking.
- C++ OBS plugin does ALL rendering. Named pipe is the ONLY IPC channel.
- Do not propose browser sources, HTTP APIs, or any other IPC approach.
- See ARCHITECTURE.md for the full technical picture.

### 8. Wire format must be agreed on both sides simultaneously.
- Change C# serialiser and C++ parser in the same set of edits.
- Document byte layout in comments in both files.

### 9. Never lie.
- If you said you would do something and did not, admit it immediately.
- Do not claim parity with another application unless you have verified every feature it has.

### 10. Do not waste context.
- Read files, trace logic, then respond. Do not narrate uncertainty out loud.
- Do not spawn an agent for something only to redo it again a moment later yourself.

### 11. Build before finishing.
- `dotnet build Steaming.WinUI/Steaming.WinUI.csproj -c Release` for C# WinUI (the only shipping app; WPF removed 2026-06-18).
- `cmake --build build_x64 --config RelWithDebInfo` for C++.
- Fix all errors before stopping. Zero errors is the bar.

### 12. Audit your own code before building.
- After writing code, read it back: check field order, null paths, thread safety, byte counts, fallback conditions.
- If you catch an error from build output instead of from reading, the audit did not happen.

### 13. Check the actual API before using it.
- Do not assume property names on third-party types.
- Only use officially documented APIs. No internal, undocumented, or reverse-engineered endpoints.
- For Kick: only `api.kick.com/public/v1/*`. An IP ban would break the entire integration.
- TwitchLib v4 `ChatMessage` has NO `IsSubscriber`/`IsModerator`/`IsVip` — derive from `msg.Badges`.

### 14. Update HANDOFF.md every session.
- Before ending a session: update HANDOFF.md with what was done, what needs testing, known issues.
- HANDOFF.md is for handoff context — what was built and what state it is in. Not a confession list.
- When a new mistake is made: add it to MISTAKES.md, not HANDOFF.md.

### 15. Finish what you start.
- Do it completely. Do not stop without finishing.
- Do not argue with the user about scope. Do not pretend to have finished when you have not.

### 16. When the user rejects an approach, it is gone.
- If the user says no to an architecture, workaround, UI pattern, or API: it is off the table permanently for this session.
- Do not re-suggest it in a different form. Do not mention it as a footnote.

### 17. Design for what this app actually IS.
- This app streams to Twitch AND Kick simultaneously. That is its entire identity.
- Any feature involving per-platform data MUST track both platforms separately from day one.
- Combined-only storage is not acceptable for a dual-platform app.
- The user should NEVER have to ask for per-platform tracking in an app explicitly built around two platforms.

### 18. When adding new DB columns, verify data exists before displaying it.
- New columns on existing rows will be DEFAULT 0. That zero means "no data", not "zero viewers."
- Do not display `T:3 K:0` when K:0 means the column was never populated. That is a lie.
- Either migrate existing data, reconstruct from raw snapshots, or fall back to the combined value.

### 19. Scope changes to exactly what was asked.
- Do not refactor, clean up, rename, or "improve" surrounding code unless explicitly asked.
- Every extra line touched is a line that can introduce a regression.

---

## Reference Files

| File | Purpose |
|---|---|
| `ARCHITECTURE.md` | Component table, pipe protocol, binary formats, DB schema, build commands, API rules |
| `MISTAKES.md` | Every real past failure: what happened, correct approach, files involved |
| `HANDOFF.md` | What was built this session, what needs testing, current known issues |
| `kick_api_docs.md` | the kick api in LLM format |
| `kick_bridge_contract.md` | the bridge contract