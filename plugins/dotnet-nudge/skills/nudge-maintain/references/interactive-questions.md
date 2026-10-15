# Plain-language maintenance questions

Talk like a helpful teammate, not a diagnostic report. Use the host's native
question tool for Nudge choices: `AskUserQuestion` in Claude Code, `ask_user` in
Copilot. Invoke the actual tool; printing options or JSON in chat does not open
the picker. The host agent calls its own tool, not a shell, hook or MCP server.

## Start with what matters

Explain the relevant update in one or two short sentences, then ask a natural
question. Lead with what changed and what the user can do, not how the scan ran,
which tool returned it, or a list of everything that remains unchecked.

For example:

> I noticed there's an update to the .NET SDK, from 10.0.101 to 10.0.113.
> Would you like to hear what's new or get help updating?

For a preview, retain that distinction:

> There's a newer .NET 11 preview: RC 2 is available, and this project currently
> asks for RC 1. Want to know what's new or look at updating?

For a package:

> There's a newer version of Newtonsoft.Json available for this project.
> Want to see what's changed or get help updating it?

For security information:

> This update includes security fixes. I haven't checked whether those fixes
> affect your app yet. Would you like to know more or look at updating?

Use these only when the verified information supports them. A security release
is not proof that the app is vulnerable or that this update fixes it. Do not
invent release-note details, features or compatibility claims. If only a
project's requested SDK is known, say that; do not claim it is the installed or
running SDK.

## Keep the plumbing behind the scenes

Keep internal IDs, revision tokens, assessment labels, raw JSON and ISO
timestamps out of ordinary explanations, questions, option labels, option
descriptions and confirmations. Those values belong in tool arguments, not in
the user's decision.

Use "Remind me next week", not a timestamp or a database action. Show a readable
date/time only when it helps the user choose a duration. Prefer "I haven't
checked that yet" over status labels such as partial or unknown. Say which
project matters when needed, rather than adding a scope record to the question.

Use short release names for long preview/build versions when unambiguous,
such as ".NET 11 RC 2"; retain the exact version internally and show it in an
update plan when needed to identify the change. Never hide a preview, change
the target, or abbreviate two different releases into the same display name.
Technical detail is available when asked and whenever it is needed for an
informed approval; it should not dominate the first question.

## Make the choices useful

Ask one focused, single-select question with 2-4 short choices, including
`Not now` or another no-change option. Do not batch unrelated updates or make
mutually exclusive actions multi-select. The host supplies custom-answer
entry; do not add an `Other` choice.

Normally offer information and a way forward, not just a menu of reminder
settings. Choose only options supported by the current information and
available tools:

| User-facing choice | Internal handling |
| --- | --- |
| What's new? | Explain verified changes or release information without changing anything. If details are unavailable, say so; do not invent them. |
| Help me update | Offer only when a supported next step exists. Explain what would change and ask for approval of that specific plan before editing, downloading, installing or executing anything. |
| Remind me next week | After the explicit choice, record `snooze` for this same update for seven days. Compute the exact expiry internally from the time of the question; never silently substitute a different duration. |
| Don't ask about this update | Offer only when a concrete candidate exists. Record `skip-version` for that exact candidate; later versions can still be offered. |
| Not now | Continue without a durable preference mutation. Cancellation, dismissal and no answer also grant no consent. Existing hook delivery cooldowns still apply. |

## Apply questions to every maintenance category

Do not reserve the picker for SDK updates. After an actionable package or
toolchain assessment, or a newly delivered eligible hook finding, offer one
focused native question about that exact dependency or tool. Reading a tool
result is not enough: invoke the question tool rather than ending with a list
of updates and "let me know." An explicit report-only request remains report-only.

Use the finding's scope and current evidence to name the package/tool, project
when relevant, and exact current/candidate versions. Never identify a NuGet
package update as an SDK update or combine unrelated dependencies into one
approval. Use these routes only when evidence and supported next steps permit:

| Finding | Question and choices |
| --- | --- |
| `package-update` | "There's an update to <package>, from <current> to <candidate>. Want to hear what's changed or get help updating it?" Offer What's new?, Help me update, Remind me next week, Not now. |
| `package-advisory` with a candidate | "An advisory affects the restored version of <package>, and <candidate> is a candidate to investigate. Want to hear about the advisory or get help updating?" Offer Tell me more, Help me update, Remind me next week, Not now. Say whether a fix is known or only a candidate; do not claim the app is exploitable. |
| `package-advisory` without a candidate | "There's an advisory for <package>, but I haven't identified a verified update yet. Want help investigating a fix?" Offer Tell me more, Help me find a fix, Remind me next week, Not now. |
| `package-deprecation` | "<package> <current> is deprecated. Want to hear why or get help finding an alternative?" Offer Tell me more, Help me find an alternative, Remind me next week, Not now. Publisher replacement metadata does not authorize querying that package or prove compatibility. |
| `workload-set-update` | "The supplied workload information lists a newer set, <candidate>. Want to hear what's changed or get help planning an update?" Offer What's new?, Help me update, Remind me next week, Not now. Preserve the current pin until separately approved; the trial runner cannot install workloads. |
| `workload-missing` | "The supplied inventory doesn't list the selected <workload> workload. Want help checking what would be needed to install it?" Offer Tell me more, Help me plan installation, Remind me next week, Not now. Do not describe offline inventory as live discovery. |
| `workload-set-difference` / `workload-manifest-difference` | "The supplied workload inventory differs from this project's selection. Want help checking the mismatch?" Offer Tell me more, Help me check the mismatch, Remind me next week, Not now. A mismatch is not automatically an upgrade or a reason to overwrite a pin. |
| `host-tool-incompatibility` | "The supplied <tool> information doesn't match this project's requirements. Want help checking compatibility?" Offer Tell me more, Help me check compatibility, Remind me next week, Not now. Do not promise an unknown compatible version. |
| `local-runtime-security-servicing` | "There's a security servicing update for the local <runtime>, from <current> to <candidate>. Want to hear more or get help updating it?" Offer Tell me more, Help me update, Remind me next week, Not now. Explain that this does not update deployed apps. |
| `deployment-runtime-security-servicing` | "The supplied deployment information points to a security servicing update for <runtime>. Want help planning that deployment update?" Offer Tell me more, Help me update, Remind me next week, Not now. Do not substitute a local runtime install for a container/self-contained deployment change. |
| `restored-graph-change` | "<package> already changed in the restored dependency graph. Want to hear what changed?" Offer Tell me more and Not now. This is an observed change, not an available update. |

"Help me find a fix", "Help me find an alternative", "Help me check the
mismatch", "Help me check compatibility" and "Help me plan installation"
request investigation or a plan only, just like "Help me update." Separately
approve new metadata acquisition, file edits, downloads, installs and trial
execution. Do not skip package-source approval or the human trial approval.

If several findings exist, prioritize security and other higher-priority
actionable findings, then present one relevant question at a natural boundary.
Do not ask a picker for every dependency or stack multiple questions to bypass
the repository's routine-delivery limit. New categories do not get separate
cooldowns, and an answer for one dependency never authorizes another.

For a question about reminder preferences, "Stop SDK reminders" or "Turn SDK
reminders back on" can map to category disable/enable. Explain that it affects
all reminders of that type for this repository, including security reminders
when relevant. Do not silently turn a version skip into a category mute.

If there is no concrete update, use an information choice such as "Tell me
more" and a no-change choice. Do not offer to install an unknown version, imply
an unsupported updater exists, or promise an update when only instructions or
a separate-checkout experiment are available. Describe the next step honestly.

## Native tool call shapes

These are arguments for actual tool calls, not JSON to paste into chat.
Replace display-version placeholders with verified, readable release names.
Keep the exact finding ID, revision, versions and expiry internally.

### Copilot: `ask_user`

```json
{
  "question": "I noticed there's an update to the .NET SDK, from <current display version> to <new display version>. Would you like to hear what's new or get help updating?",
  "choices": [
    "What's new?",
    "Help me update",
    "Remind me next week",
    "Not now"
  ]
}
```

### Claude Code: `AskUserQuestion`

```json
{
  "questions": [
    {
      "question": "I noticed there's an update to the .NET SDK, from <current display version> to <new display version>. Would you like to hear what's new or get help updating?",
      "header": "SDK update",
      "multiSelect": false,
      "options": [
        {
          "label": "What's new?",
          "description": "Tell me what the update includes without changing anything."
        },
        {
          "label": "Help me update",
          "description": "Show me how to update and what would change."
        },
        {
          "label": "Remind me next week",
          "description": "Ask me again in a week."
        },
        {
          "label": "Not now",
          "description": "Leave things as they are."
        }
      ]
    }
  ]
}
```

Use the schema actually advertised by the host. Wait for the real answer;
a custom reply authorizes only what it explicitly says. Clarify an ambiguous
answer with another plain-language native question.

## Read current information and respect reminders

Read `nudge_context` or `nudge_status` (or CLI `status --json`) before asking.
Use only eligible, confirmed/partial findings for unsolicited suggestions.
Unknown, stale, blocked or suppressed findings are report-only, with one
delivery exception: when handling a newly delivered hook finding, the exact
ID/revision can now show `delivered-recently` because the hook already recorded
context delivery. Offer that exact hook finding once if the current evidence
still matches; this cooldown is not a user decline. Do not use this exception
for an old hook message, another revision, or a finding found only in status.
`scan-pending`, `snoozed`, `version-skipped` and `category-disabled` still block
an unsolicited question, even after a hook. An explicit request to inspect a
suppressed finding can still be answered read-only.

Keep routine suggestions at a natural task boundary, not in the middle of
unrelated work. A request to check for updates may end with one relevant
update question when actionable evidence exists, including for NuGet and
toolchains; it does not itself authorize a change or reminder decision.
An explicit request for just a report or cached status does not require a question.
Do not repeat the same question after answering "What's new?" unless the user
asks to proceed or another real decision is needed.

## Follow through without widening approval

Keep an internal binding between the question and its exact finding ID,
revision, candidate, action and any expiry. Before saving a reminder choice,
re-read the finding and verify the ID and revision still match that binding.
If the information changed, explain the new facts in plain language and ask
again. Do not transfer an old answer to another revision or retry a failed
decision with a new one. Use `nudge_decide`/`nudge_preferences` or the advertised
CLI command, and confirm success only when it reports persistence.

Confirm the outcome simply: "I won't bring this update up again for a week" or
"I won't ask about this update again." Do not promise a timed notification:
reminders become eligible at a later conversation boundary, not through a
scheduled message. Do not print a JSON/YAML decision record.
If saving fails or the result is inconclusive, explain that the reminder
change was not saved or could not be confirmed; do not claim success.

"What's new?" is information only. "Help me update" requests the next step;
it is not blanket permission to execute an unspecified update. Describe the
actual target version, files, downloads, commands and important limits in
plain language, then ask a focused question such as "Update global.json to
use SDK 10.0.113?" with "Update it" and "Not now". This example is valid only
when that exact file change is the supported, fully described action;
changing a project's SDK selection is not the same as installing an SDK.

For package metadata acquisition, separately name the package, project,
target framework, optional runtime target and configured source names before
asking. For a separate-checkout experiment, explain what will run and that it
is not a security sandbox. Execution still requires the human's exact
persisted-plan revision/digest confirmation in an interactive
`dotnet-nudge trial approve` terminal; a native question does not bypass it.
Do not impersonate the human or hide that required terminal confirmation.

If the question tool is absent, denied, fails or the session is
noninteractive/headless, explain the limitation briefly in plain language and
report the information without assuming an answer, saving a decision or
executing anything. Do not work around it with terminal input, transcript
scraping, synthetic answers or a Stop hook that forces another turn.

Hooks deliver context, not a verified question presentation or user answer.
The host controls tool availability and UI rendering; these instructions
cannot force a picker in hosts without native user-input support.

## Host references

- [Claude Code user-input tool and question schema](https://code.claude.com/docs/en/agent-sdk/user-input#question-format)
- [Copilot SDK native ask_user examples](https://github.com/github/copilot-sdk/blob/main/nodejs/test/e2e/ask_user.e2e.test.ts)
