# Mac product experience and visual acceptance

This is a required part of the full delivery contract. Apply it while implementing every feature. The maintainer rejected the current basic appearance and limited visible functionality. A successful build or colour change does not close this feedback.

## Observed baseline

The 4 November review of the existing local package showed a neutral dark welcome screen, large stacked welcome buttons, a text-heavy toolbar and an empty Explorer. The source uses a plain multiline TextBox for SQL and generic controls across dialogs. Native menus and system appearance have improved, but the presentation still lacks the information hierarchy, editing affordances and management surfaces expected of a professional database application. The reviewed package has separate provenance from the freshly built source, as recorded in CODEBASE-AUDIT.md.

## References and decisions

The following are design inferences for SQLoryn based on official guidance and product documentation, refreshed 4 November 2026. The existing 56-product desk study remains supporting research.

| Reference | Pattern to adapt | SQLoryn requirement |
|---|---|---|
| Apple macOS guidance | Comfortable information density, resizable windows, menu commands, keyboard use and personalisation | Make the workspace usable on a laptop and external display with persistent pane choices, accessible keyboard paths and complete native menu access. |
| Apple sidebar guidance | An intermediate content list can make a deep hierarchy easier to navigate | Give Explorer selection a proper object list/details workspace, with search and inspector, so managing a database does not require endless tree expansion. |
| TablePlus | Object jump, data filters, multiple workspaces and review of data changes | Make navigation and data editing prominent, with precise target and pending-change context. |
| DataGrip | Associated query/data-source context and context-specific database actions | Keep the current connection/database visible and offer relevant commands at the selected object, editor and result. |
| Nova | Editor, navigation and inspection coexist in a cohesive Mac workspace | Use a consistent shell, compact controls and feature-local panels across development and administration. |

Sources are [Apple macOS guidance](https://developer.apple.com/design/human-interface-guidelines/designing-for-macos/), [Apple sidebars](https://developer.apple.com/design/human-interface-guidelines/sidebars), [TablePlus](https://tableplus.com/), [DataGrip Explorer](https://www.jetbrains.com/help/datagrip/database-explorer.html), [DataGrip query consoles](https://www.jetbrains.com/help/datagrip/query-consoles.html) and [Nova](https://nova.app/). These references guide original interaction design. Use independently created assets and verify redistribution rights for any imported symbol library.

## Workspace composition

Use one coherent workspace with these regions. Adapt to the window width without hiding the active target or destructive confirmation.

- **Title and toolbar.** Respect native traffic-light placement and draggable titlebar behaviour. Group sidebar toggle, connection/database selectors and contextual primary actions. Use restrained symbolic controls with labels or tooltips and keyboard equivalents. Keep Run and Cancel visually related. Put less frequent actions in the native menu and contextual overflow.
- **Navigator.** Connections/groups and service sections with recognizable object symbols, searchable hierarchy, selection state and subtle separators. Remember expansion per connection. Allow switching between objects and project/files/history where implemented.
- **Document centre.** Compact query/object tabs, dirty/running indicators and a clear path/target breadcrumb. Tabs can contain SQL, table data/structure, object definitions, plans and management workspaces. Reuse the same navigation conventions.
- **Detail inspector.** Optional right pane for selected column, index, plan operator, permission or job properties. Hide it when it has no relevant content. Provide keyboard access and a visible close/toggle.
- **Output and activity.** Resizable result grid, Messages, Plans and Statistics as appropriate. An activity view shows ongoing backup, import or job operations with server status. Display target, duration and completion separately from retained row counts.

At 1280 by 800 logical pixels, the main query workflow must retain useful editor and result space without horizontal window scrolling. Test at 1024 by 700 and with large text. Preserve intentional user resizing. Use content overflow and progressive disclosure where necessary. A compact layout must still allow reaching every command.

## Visual system

Create a small set of reusable semantic resources in the existing Avalonia styling layer. Define window/content/sidebar/inspector surfaces, text/secondary/disabled colours, separators, selection, focus, accent, success, warning and destructive states. Test custom system accents for readable labels. Use system typography for interface text and a readable monospaced editor font. Start around 12–14 point interface text and 13–14 point editor text, then validate scaling and accessibility. These sizes are design starting points, not Apple requirements.

Use a consistent 4-point spacing scale, restrained corner radii and thin separators. Choose contrast and content hierarchy deliberately. Reserve prominent filled buttons for the primary action in a context. Avoid oversized full-width controls for secondary actions on a large desktop window. Show errors adjacent to the affected input with a recovery action.

Use original, consistent outline symbols with a documented licence. Apply colour sparingly to state and target identity, with text or shape as a second cue. Keep SQL and dense data surfaces opaque and legible. If native materials are used, validate reduced-transparency settings and framework support. Decorative blur must never reduce reading contrast. Platform theme support must work across the supported macOS versions.

Preserve Avalonia unless a documented, tested platform limitation warrants a bounded native integration. A framework rewrite needs evidence about benefit, migration cost and preservation of existing behaviour.

## Required screens and interactions

| Screen | Required content and behaviour |
|---|---|
| Welcome | Original icon, clear New Connection action, compact Open/New Query choices, recent connections and files when enabled. Useful first-use guidance without presenting a wall of empty panels. |
| Connection sheet | Profile/name, auth choice, endpoint/database, TLS summary, advanced disclosure, explicit save-secret choice, inline validation and cancellable progress. A connection test must report its actual scope. |
| Connected workspace | Visible server/database, a useful initial object list, search, query creation and relevant administration actions. The user can identify the current target without opening Settings. |
| SQL editor | Highlighting, gutter/line numbers, completion, find, diagnostics navigation, selection execution and transaction state. Keep ordinary typing responsive offline. |
| Data and results | Dense, readable grid, distinct NULL, column types, row/cell selection, copy, filter/sort scope, export and value detail. Editing has a visible pending-change review and safe apply/discard. |
| Object workspace | Overview, data, structure, indexes/constraints, dependencies and definition as appropriate. Object actions operate on the displayed target. |
| Schema change | Side-by-side or structured before/after, exact SQL preview, dependency warnings, affected target and confirmation. Show verified completion or recovery after partial/unknown outcome. |
| Backup/restore/import | Guided inputs with source/target distinction, validation, review, progress and verified outcome. Long operations remain discoverable after switching tabs. |
| Security and Agent | Principals/roles or jobs/schedules/history in selectable lists with contextual details and commands. Permission denial explains what is unavailable. |
| Plans and monitoring | Graphical plan with readable properties, actual versus estimated label, bounded activity views and accessible textual equivalents. |
| Settings | Grouped General, Editor, Results, Connections and Privacy options backed by real persisted behaviour. Explain sensitive history/recovery storage choices. |
| Recovery | Actionable connection failure, save conflict, interrupted edit and dirty quit flows. Preserve input and direct the user to a safe next action. |

Deliver the corresponding functionality from DELIVERY-CONTRACT.md as each screen is built. A screenshot of a control cannot establish its operation. During development, clearly mark unfinished capabilities in the evidence ledger. Product controls for supported targets must lead to the real workflow. Unsupported target/version/permission states must name the reason rather than appearing to succeed.

## Design and implementation work together

The design owner creates low-cost layouts for representative connected, editing, result, management and error states in the working app using synthetic fixtures. Use labelled design-only fixtures for offline visual testing and exclude them from ordinary production flows. They must not fabricate a connected server.

Feature workers implement their real controller/Core paths against the agreed layout and semantic resources concurrently. The integrator checks visual consistency as changes arrive. Keep one owner for shell/resources and command placement. Avoid bespoke button styles or unrelated window patterns for each worker.

Every implemented feature must record its command/menu location, full screen, loading, empty, denied, cancelled, failed and successful states. Tie the interaction to its CAP/SURF identifier and tests. Continue ready work while a specific visual or live test awaits external access. The full application assignment remains active.

## Visual and interaction acceptance

Capture the actual packaged build and record its commit, runtime, architecture and window size. The evidence set includes welcome, connection validation, connected Explorer, a populated query/result view, table structure/edit review, plan, one operations workflow, Settings and a recoverable error. Cover light/dark/system appearance, laptop size, alternate accent and increased contrast/text where available. Capture synthetic data only.

Review before/after images for hierarchy, density, alignment, clipping, reading contrast, focus and consistent controls. A reviewer must also operate the flow. Verify native menus, Command shortcuts, tab order, Escape, focus return from sheets and VoiceOver names/states. Measure window-to-input readiness, large-script interaction and grid scrolling. A screenshot and an accessibility source scan establish different evidence.

Block feature acceptance when an enabled control has no effect, a dialog hides the target, a destructive action bypasses confirmation, navigation loses edits, or key content clips at supported sizes. Fix visual defects in the same integrated PR as the feature. The final maintainer walkthrough should show a connected, populated application doing development and administration work, with a separate list of specific remaining external gates.
