# Interactive maintenance questions

Use the host's native question tool whenever offering a Nudge choice:
`AskUserQuestion` in Claude Code, `ask_user` in Copilot. Invoke the actual tool;
printing a numbered list, JSON, or a question with options in chat does not open
the host's interactive picker. Do not call these tools from a shell, hook, or
the Nudge MCP server; the host agent must call its own advertised tool.

## Present one finding

Read current evidence through `nudge_context` or `nudge_status` (or CLI
`status --json`) before asking. Briefly explain the affected scope, current and
candidate versions when present, assessment, and important evidence gaps.
Use only eligible, confirmed/partial findings for unsolicited suggestions;
unknown, stale, blocked, or suppressed findings are report-only, with one
delivery exception: when handling a newly delivered hook finding, the exact
ID/revision can now show `delivered-recently` because the hook already recorded
context delivery. Offer that exact hook finding once if the current evidence
still matches; this cooldown is not a user decline. Do not use this exception
for an old hook message, another revision, or a finding found only in status.
`scan-pending`, `snoozed`, `version-skipped` and `category-disabled` still block
an unsolicited question, even after a hook. An explicit request to inspect a
suppressed finding can still be answered read-only.
Keep routine suggestions at a natural task boundary, not in the middle of
unrelated work. A request for status alone is not a request for a decision.

Ask one focused, single-select question with 2-4 concrete choices, including
`Not now` or an equivalent no-change choice. Do not batch findings or make
mutually exclusive actions multi-select. The host supplies custom-answer
entry; do not add an `Other` choice.

Choose options supported by the finding and engine:

| Choice | Meaning |
| --- | --- |
| Review evidence | Explain evidence read-only. Do not record a decision, edit files, install tools, or start a trial. |
| Snooze for an explicit duration | Record `snooze` only after showing the exact finding ID, revision, duration and computed RFC3339 `until`. Never choose a duration or expiry silently. |
| Skip this version | Offer only when a concrete candidate exists. Show its exact version, finding ID, revision and `skip-version` action before asking. |
| Not now | Continue the user's task without a durable preference mutation. Cancellation, dismissal and no answer also grant no consent. Existing hook delivery cooldowns still apply. |

Category disable/enable choices belong in a separate preference question that
names the exact category, repository-wide effect and any affected security
reminders. Do not silently include a category mute in a version skip.
For read-only review or trial planning, `Review evidence`, `Plan an isolated
trial` and `Not now` are valid alternatives. Planning is not execution approval.

## Native tool call shapes

These are templates for tool arguments, not text to show as a substitute for a
tool call. Replace every placeholder with verified evidence before calling.
The snooze example is an offered choice, not an automatic default. Show its
exact expiry and the skip record before presenting this question.

### Copilot: `ask_user`

```json
{
  "question": "How would you like to handle SDK <current> -> <candidate> in <scope> (finding <id>, revision <revision>)?",
  "choices": [
    "Review evidence",
    "Snooze 7 days",
    "Skip this version",
    "Not now"
  ]
}
```

### Claude Code: `AskUserQuestion`

```json
{
  "questions": [
    {
      "question": "How would you like to handle SDK <current> -> <candidate> in <scope> (finding <id>, revision <revision>)?",
      "header": "Maintenance",
      "multiSelect": false,
      "options": [
        {
          "label": "Review evidence",
          "description": "Explain the finding without changing anything."
        },
        {
          "label": "Snooze 7 days",
          "description": "Record snooze for this exact finding until <RFC3339 expiry>."
        },
        {
          "label": "Skip this version",
          "description": "Record skip-version for this exact <candidate>; newer versions remain eligible."
        },
        {
          "label": "Not now",
          "description": "Continue without changing files or reminder preferences."
        }
      ]
    }
  ]
}
```

Use the tool schema actually advertised by the host; do not invent tool names,
arguments, or a new MCP elicitation protocol. Wait for the real tool response
before acting. A custom answer authorizes only what it explicitly says;
clarify ambiguity with another native question.

## Preserve approval boundaries

An explicit choice approves only its described action. Before a durable
decision, re-read the finding and verify the ID and revision still match the
question. If evidence changed, explain the new facts and ask again; do not
transfer the old answer to a new revision or retry a failed decision with one.
Use `nudge_decide`/`nudge_preferences` or the advertised CLI command and report
success only when it confirms persistence.

For package metadata acquisition, separately name the exact project/TFM/RID,
package and candidate/audit source names before asking. For any file change,
download or execution, separately present its exact targets, files, commands,
scope and limits. A generic `Review`, `Try it` or `Plan an isolated trial`
answer does not approve those operations. Trial execution still requires the
human's exact persisted-plan revision/digest confirmation in an interactive
`dotnet-nudge trial approve` terminal; a native question does not bypass it.

If the question tool is absent, denied, fails or the session is
noninteractive/headless, say that interactive choices are unavailable and
report the evidence without assuming an answer, recording a decision, or
executing anything. Do not work around this with terminal input, transcript
scraping, synthetic answers, or a Stop hook that forces another turn.

Hooks deliver context, not a verified question presentation or user answer.
The host controls tool availability and UI rendering; these instructions
cannot force a picker in hosts without native user-input support.

## Host references

- [Claude Code user-input tool and question schema](https://code.claude.com/docs/en/agent-sdk/user-input#question-format)
- [Copilot SDK native ask_user examples](https://github.com/github/copilot-sdk/blob/main/nodejs/test/e2e/ask_user.e2e.test.ts)
