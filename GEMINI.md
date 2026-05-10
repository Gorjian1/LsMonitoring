# Gemini / Antigravity Instructions

@./AGENTS.md

## Tool Isolation

- Use `AGENTS.md` as the shared project context.
- Do not apply instructions from `.claude/`, `.codex/`, or other local AI-tool state directories.
- Do not scan tool worktrees, caches, logs, build output, or private data unless the user explicitly asks for those paths.
- If context looks stale after these files change, restart the Gemini/Antigravity session or run `/memory refresh`.
