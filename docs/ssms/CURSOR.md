# Build SQLoryn with Cursor

Open the local repository in Cursor on the Mac. Select **Agent** and paste the assignment below. The working tree may contain the research and this file before they appear on GitHub. Use that checkout as the integration workspace.

## Cursor configuration for this assignment

| Capability | How to use it here |
|---|---|
| Agent and planning | Agent can explore, edit and run commands. Use its task list to track active, ready, dependent and externally blocked work. The brief already authorises implementation. A plan is working state until its changes reach the app. [Agent modes](https://cursor.com/help/ai-features/agent) |
| Persistent project instructions | Cursor reads the root AGENTS.md. Follow its links and read the implementation alongside the task. Keep product knowledge in the repository so another tool or fresh chat can resume. Avoid maintaining a second copy in Cursor rules. [Project rules](https://cursor.com/docs/rules) |
| Parallel agents and isolation | Subagents can share a checkout by default. Explicitly request separate **local Git worktrees** for concurrent writers. Each worker gets a narrow outcome, ownership and a completion test. Read-only exploration can share the checkout. In the IDE without the Agents Window worktree UI, use the documented worktree workflow or ordinary local Git worktrees. If subagents are unavailable, one Agent can consume the same queue. [Subagents](https://cursor.com/docs/subagents) · [Worktrees](https://cursor.com/docs/configuration/worktrees) |
| Review and continuation | Invoke the repository's [sqloryn-verifier](../../.cursor/agents/sqloryn-verifier.md) by name after integration. Resume the original worker for corrections where possible. Use the saved Git diff, test evidence and STATE to resume in a fresh chat. Checkpoints and chat history cannot establish release evidence. [Subagent invocation and resume](https://cursor.com/docs/subagents) |
| Terminal, research and permissions | Use terminal for locked restore, tests, packaging and Git evidence. Use first-party sources for uncertain APIs or licences. Keep normal tool approvals and sandbox restrictions. Cloud agents, new MCP connections, hooks with external side effects and paid services require a separate need and authorisation. Native screenshots, accessibility and Keychain evidence require the relevant Mac environment. [Run modes](https://cursor.com/docs/agent/security/run-modes) · [Cursor engineering guidance](https://cursor.com/blog/agent-best-practices) |

## Paste into Cursor Agent

> Read docs/ssms/CODEBASE-AUDIT.md and execute docs/ssms/DELIVERY-CONTRACT.md together with docs/ssms/MAC-EXPERIENCE.md in full. This is the active assignment over earlier incremental follow-ups. Start from the current checkout, including the local documentation changes. Check the current branch and PR state, preserve existing work and include the handoff documents in the integration branch before creating worker worktrees.
>
> Use isolated local worktrees for parallel writers and one integrator. Maintain the full CAP/SURF queue. Complete each user workflow through its discoverable Mac UI, Core implementation, failure handling and tests, then continue with the next ready item. Implement the twelve delivery workstreams and the required Mac screens concurrently. Apply the design study through coherent navigation, a professional SQL editor, dense readable results, object inspectors and real administration workspaces. Follow the visual and native interaction acceptance in MAC-EXPERIENCE.md.
>
> Use TDD for behaviour changes and regression fixes. Obtain independent verification on the integrated code. Record precise external blockers while continuing independent implementation. A completed correction list or a visual restyle is a progress checkpoint. Continue the full feasible assignment, and preserve exact next tasks if a tool or context limit interrupts execution.
>
> Follow the contract's completion checklist before reporting the application complete. Reconcile every catalogue item and provide the qualified installable app, actual-build screenshots, test evidence, remaining implementation and specific maintainer decisions. Preserve the existing boundaries on production, infrastructure, credentials, deferred AI, signing, merge and publication.


The [delivery contract](DELIVERY-CONTRACT.md) contains the detailed twelve workstream requirements, architecture, TDD rules, integration ownership, verification and completion conditions. The [codebase audit](CODEBASE-AUDIT.md) records independently checked implementation and gaps at 06bc2ad. Use the existing 18 ownership queues to distribute these requirements without overlapping writers.
