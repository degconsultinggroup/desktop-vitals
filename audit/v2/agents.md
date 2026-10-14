# What an agent is doing

Phase 1 notes for fact 5. Formats below were read on a live dev box (structure only). Examples are synthetic. Nothing in this file is a real prompt, path, user, or machine.

A pass must work with no session log. The log is an optional layer per tool. If a log is missing, unreadable, or a format we do not know, the line still shows model (from the command line), repo, branch, worktree, the child process, and git progress.

## Process to session

Walk the process list with `NtQuerySystemInformation` (class 5). Parent pid is `InheritedFromUniqueProcessId` (x64 offset 88). Creation time is the `FILETIME` at offset 32. Cwd and command line come from the PEB (`PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`), the same read Desktop Vitals already uses. A parent counts only if its creation time is earlier than the child's, so a recycled pid is not a parent.

Match a session only when both of these hold:

1. Session cwd equals the process cwd (string match on the path the tool stored).
2. The process started before the session's latest write, and the session was created at or after the process start (a few seconds of skew is normal). Claude stores the process `FILETIME` and checks it; the others use the session file's created time against the process creation time.

If several sessions share a cwd, pick the one whose created time is closest to the process start and whose file is still growing. If none match, skip the log and keep the general facts.

## Claude Code

Transcripts live at `~/.claude/projects/<encoded-cwd>/<session-id>.jsonl`.

Encode a cwd by replacing every character that is not `A-Z`, `a-z`, or `0-9` with `-`. `C:\repo\app` becomes `C--repo-app`. Checked against the cwd field on 92 session files: all 92 encoded to their parent folder name.

`<session-id>` is the UUID in the record's `sessionId` (36 characters). The file name is that UUID plus `.jsonl`.

Side files, not read for the line:

- `~/.claude/sessions/<pid>.json` is the live registry: `pid`, `sessionId`, `cwd`, `startedAt` (unix ms), `procStart` (process `FILETIME`, 18 digits), `status`, `kind`, `entrypoint`. This is how a running `claude.exe` maps to a transcript: the pid file, then confirm `procStart` so a reused pid does not attach to an old session.
- `~/.claude/sessions/<pid>.<64-hex>.key` repeats `procStartFt` and a peer token. The token is not shown.
- `<encoded-cwd>/<session-id>/subagents/*.jsonl` is a subagent. `<encoded-cwd>/<session-id>/tool-results/` holds bulky tool output. Neither is shown.
- `~/.claude/history.jsonl` is an index whose `display` field is prompt text. Do not read it.

Record shape: one JSON object per line. Transcript records carry `type`, `uuid`, `parentUuid`, `sessionId`, `timestamp`, `cwd`, `gitBranch`. `timestamp` is UTC `YYYY-MM-DDTHH:mm:ss.sssZ` (24 characters) and often sits in the middle or at the end of the line, so a reader has to finish the line before it can age the action.

Top-level `type` values seen across the transcripts on this box: `assistant`, `user`, `attachment`, `system`, `queue-operation`, `permission-mode`, `mode`, `file-history-snapshot`, `file-history-delta`, plus bookkeeping (`last-prompt`, `ai-title`, `atis-latch`, `pr-link`, `cost-state`, `bridge-session`, `frame-link`).

What each useful record means:

| Record | Meaning |
| --- | --- |
| `assistant` with `message.content[]` of `type: thinking` | The model is thinking. `stop_reason` is often already `tool_use` on that line. `thinkingDisplay` and `thinkingDurationMs` sit on the record. The `thinking` string is the private trace. Do not keep it. |
| `assistant` with `message.content[]` of `type: text` and `message.stop_reason: end_turn` | The model is answering. The `text` string is the answer. Do not keep it. |
| `assistant` with `message.content[]` of `type: tool_use` (`id`, `name`, `input`) and `message.stop_reason: tool_use` | A tool call started. `name` is `Bash`, `PowerShell`, `Edit`, `Write`, `Read`, `Grep`, and similar. File tools put the path in `input.file_path` or `input.path`, and that key is before the file body. |
| `user` with `message.content[]` of `type: tool_result` (`tool_use_id`) | That tool call finished. `toolUseResult` is the structured output (`stdout`, `content`, patches). Do not keep it. |
| `permission-mode` | The mode changed. Observed values: `default`, `acceptEdits`, `plan`, `bypassPermissions`, `dontAsk`. This is a setting, not a pending prompt. |
| `user` field `permissionDecision` | Written after a decision, on the tool-result line. Shape: `decision`, `source`, `reasonType`. |
| `user` field `toolDenialKind` | The tool was refused. Observed: `user-rejected`, `permission-rule`. |
| `system` `subtype: turn_duration` | The turn ended. |
| `queue-operation` | A prompt was queued or dequeued (`enqueue`, `dequeue`, `remove`). Not a tool call. |

Waiting for permission has no record of its own. While the prompt is up, the last `assistant` tool_use has no matching `tool_result`, and the registry `status` is not `shell`. Observed statuses: `idle`, `shell`. `shell` means a shell tool is running. `idle` plus an unmatched `tool_use` is the wait. After the person decides, `permissionDecision` or `toolDenialKind` appears on the following `user` line.

Synthetic lines:

```json
{"type":"assistant","sessionId":"00000000-0000-4000-8000-000000000001","timestamp":"2026-01-01T00:00:01.000Z","cwd":"C:\\repo\\app","gitBranch":"main","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"toolu_example","name":"Edit","input":{"file_path":"C:\\repo\\app\\notes.md","old_string":"a","new_string":"b"}}]}}
{"type":"user","sessionId":"00000000-0000-4000-8000-000000000001","timestamp":"2026-01-01T00:00:04.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_example","is_error":false}]},"permissionDecision":{"decision":"accept","source":"config","reasonType":"mode"}}
```

Model: `requestedModel` on the `assistant` record, else `--model` on the command line.

## Codex

Transcripts live at `~/.codex/sessions/YYYY/MM/DD/rollout-YYYY-MM-DDTHH-MM-SS-<uuid>.jsonl`. The stem is 64 characters: the prefix `rollout-`, a timestamp with `:` turned into `-`, and the session UUID. On 89 of 93 files the trailing 36 characters equal `payload.session_id` in the first `session_meta` record.

There is no pid registry. Map `codex.exe` by cwd plus time: `session_meta.payload.cwd` equals the process cwd, and the filename timestamp is the closest one at or after the process start. Ignore `codex-windows-sandbox-service.exe` and the computer-use helper; they are children, not a second agent.

One JSON object per line. Every record has `type`, `ordinal`, `timestamp` (same 24-character UTC form), and `payload`.

| `type` | `payload.type` | Meaning |
| --- | --- | --- |
| `session_meta` | (none) | Session start. `cwd`, `session_id`, `id`, `model_provider`. `git` is usually null. |
| `turn_context` | (none) | Turn settings: `model`, `cwd`, `approval_policy`, `sandbox_policy`. |
| `event_msg` | `task_started` / `task_complete` | A turn started or finished. Not a single tool. |
| `event_msg` | `item_completed` | A tool or message finished. The item is `payload.item`. |
| `event_msg` | `agent_message` / `user_message` | A short message event. The text is the answer or the prompt. Do not keep it. |
| `event_msg` | `thread_settings_applied` | `thread_settings.model`, `approval_policy`. |
| `response_item` | `message` | `role` is `assistant`, `user`, or `developer`. Content blocks are `output_text` or `input_text`. Do not keep the text. |
| `response_item` | `custom_tool_call` | A tool call with `name`, `status`. `input` is the arguments. Do not keep them. |

`payload.item.type` on `item_completed`:

| Item | Use |
| --- | --- |
| `Reasoning` | Thinking. `summary_text` and `raw_content` stay in the process. |
| `AgentMessage` | Answering. `phase` is `commentary` or `final_answer`. |
| `CommandExecution` | A shell command finished. `status` is `completed` or `failed`. `command` and `aggregated_output` stay in the process. There is no file target. |
| `FileChange` | An edit finished. `status` is `completed`. `changes` is a dict keyed by absolute path. Each value has `type` (`add` or `update`) and either `content` or `unified_diff`. Keep the file name only. |
| `McpToolCall` | A tool finished. `status` is `completed` or `failed`. `tool` is the tool name. Arguments and `result` stay in the process. |

Codex writes the item when it completes, not when it starts. A command that is still running is visible as a child process, not as a log line. The previous completed item remains the last action until the new one lands. That lag is the tool's runtime.

`approval_policy` is the string `never` or `on-request`, or an object `granular` with `sandbox_approval`, `skill_approval`, and `request_permissions`. No "approval requested" event showed up in this box's transcripts. Waiting is inferred: `approval_policy` is `on-request`, a `task_started` has no `task_complete`, and the process has no child.

Model: `turn_context.payload.model` or `thread_settings.model`, else `--model` on the command line.

Synthetic line:

```json
{"type":"event_msg","ordinal":4,"timestamp":"2026-01-01T00:00:09.000Z","payload":{"type":"item_completed","item":{"type":"FileChange","status":"completed","changes":{"C:\\repo\\app\\notes.md":{"type":"update","unified_diff":"..."}}}}}
```

## Cursor CLI

Chats live at `~/.cursor/chats/<workspace-hash>/<chat-id>/`.

- `<workspace-hash>` is MD5 of the cwd string in UTF-8, lowercase hex. The cwd is used as stored (`C:\repo\app`, backslashes kept). Checked on 15 workspace folders.
- `<chat-id>` is a UUID.
- `meta.json` keys: `schemaVersion`, `createdAtMs`, `updatedAtMs`, `cwd`, `hasConversation`. Times are unix milliseconds.

The agent process is `node.exe` whose own command line contains both `--model` and `--worktree`. The `cmd.exe` / `nohup.exe` / `powershell.exe` parents repeat those flags because they were the launcher command. Keep the leaf `node.exe`. Map it by cwd: hash the cwd, open that folder, and choose the chat whose `meta.json` `cwd` equals the process cwd and whose `createdAtMs` is closest to the process start. `updatedAtMs` breaks a tie toward the chat still being written.

`store.db` (plus `store.db-wal` and `store.db-shm` while the agent is writing) is SQLite. Open it with `winsqlite3.dll`, `sqlite3_open_v2`, `SQLITE_OPEN_READONLY`. Readonly does not checkpoint and does not write. Hold the connection only for the query, with a zero busy timeout, so the agent is not blocked.

Tables:

```sql
CREATE TABLE blobs (id TEXT PRIMARY KEY, data BLOB);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
```

`meta` key `0` is hex-encoded JSON:

| Field | Use |
| --- | --- |
| `agentId` | UUID for this chat. |
| `latestRootBlobId` | 64-hex id of the chain head. |
| `createdAt` | Unix ms. |
| `mode` | Observed: `default`. |
| `isRunEverything` | When true, tool prompts are not waiting on the person. |
| `name` | A short title. Do not show it. |
| `blobEncryptionKey` | Present. Do not read it into the line, and do not decrypt with it. |

`blobs.id` is 64 hex characters. Plaintext message blobs are JSON and start with `{`. Others are opaque (first byte `0x0a`, `0x12`, or `0x1a`) and are skipped.

The root blob is a packed list of 32-byte ids: the byte `0x0a`, the byte `0x20`, then 32 bytes. Hex-encode those 32 bytes to get a `blobs.id`. Walking that list from the start of a live chat gives `system`, `user`, then pairs of assistant and tool messages. Read the list from the tail so a pass does not scan the whole history.

Plaintext message:

| `role` | `content[]` | Meaning |
| --- | --- | --- |
| `assistant` | `type: reasoning` | Thinking. The `text` stays in the process. |
| `assistant` | `type: text` | Answering. |
| `assistant` | `type: tool-call` (`toolName`, `toolCallId`, `args`) | A tool call started. |
| `tool` | `type: tool-result` (`toolName`, `toolCallId`) | That tool call finished. `result` stays in the process. |

Tool names seen: `Shell`, `Read`, `Grep`, `Glob`, `Write`, `StrReplace`, `GetDynamicTools`, `CallDynamicTool`.

File target is `args.path` (`Read`, `Write`, `StrReplace`, `Grep`) or `args.target_directory` (`Glob`). On most blobs `path` is the first argument. On some `StrReplace` and `Write` blobs it is last, after `old_string` or `contents`, so a reader checks the first few kilobytes and the last kilobyte of the blob for the key `"path"` and copies only the quoted value. `command`, `contents`, `old_string`, and `new_string` are never copied.

Waiting for permission has no blob of its own. `isRunEverything: true` means the agent is not asking. An unmatched `tool-call` with no child process is the wait when that flag is false. Otherwise the child process is the thing that is running.

Model: the `--model` argument on the `node.exe` command line.

Synthetic blob:

```json
{"role":"assistant","content":[{"type":"tool-call","toolCallId":"call_example","toolName":"StrReplace","args":{"path":"C:\\repo\\app\\notes.md","old_string":"a","new_string":"b"}}]}
```
