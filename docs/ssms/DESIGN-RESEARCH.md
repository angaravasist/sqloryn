# Design research · SQLoryn for Mac

The [Mac experience specification](MAC-EXPERIENCE.md) turns this research into required screens and visual/interaction acceptance. Implement it with the full delivery contract.

56 distinct products reviewed through first-party product pages or documentation on 3 November 2026. This is desk research of documented interactions, published examples and product imagery where available. It is not a hands-on usability test of the competing products. The final column is our design inference. Sources that returned only errors or redirects without usable material were excluded. Database products establish work patterns; Mac developer tools establish interaction conventions; adjacent creative and productivity apps inform particular details only.

| # | Official source | Pattern studied | SQLoryn decision |
|---:|---|---|---|
| 1 | [SSMS](https://learn.microsoft.com/en-us/ssms/object/object-explorer) | Object Explorer and its details view separate navigation from inspection. | Keep the familiar SQL Server hierarchy and make properties contextual. |
| 2 | [Azure Data Studio](https://learn.microsoft.com/en-us/azure-data-studio/what-is-azure-data-studio) | A query editor and server management experience share one workspace. | Use the editor/results model as historical precedent and avoid adding its extension platform. |
| 3 | [VS Code](https://code.visualstudio.com/docs/getstarted/userinterface) | Editor groups, sidebars and movable views serve different navigation tasks. | A query owns its result drawer and target even after switching tabs. |
| 4 | [TablePlus](https://tableplus.com/) | The native database workspace emphasises keyboard access and SQL editing. | Put Run and the current target within easy reach and secondary commands in menus. |
| 5 | [Postico](https://eggerapps.at/postico2/) | The connection manager groups servers and foregrounds repeat connections. | Make a saved connection one clear choice on return launch. |
| 6 | [Sequel Ace](https://sequel-ace.com/) | A focused macOS database manager documents processor and OS requirements up front. | Offer a clean Mac install choice and a visible support matrix. |
| 7 | [Base](https://menial.co.uk/base/) | The schema inspector and data browser address distinct tasks. | Show Structure and Data for one table without duplicating navigation. |
| 8 | [DBeaver](https://dbeaver.com/docs/dbeaver/Data-Editor/) | The data editor exposes browsing and editing in one scoped view. | Mark changed cells and stage writes explicitly, separate from read-only results. |
| 9 | [DataGrip](https://www.jetbrains.com/datagrip/features/) | A dedicated database IDE separates database work from general project work. | Add schema-aware editing without importing an entire IDE shell. |
| 10 | [DbVisualizer](https://www.dbvis.com/features/) | Database management and secure deployment are presented as operator concerns. | Keep server capability and security details available on demand. |
| 11 | [Beekeeper Studio](https://www.beekeeperstudio.io/) | The query editor and table editing are primary product workflows. | Make Browse, Query and Edit recognisable modes. |
| 12 | [Navicat Premium](https://www.navicat.com/en/products/navicat-premium) | Development and administration coexist alongside data transfer. | Group import/export under the selected data or result set. |
| 13 | [pgAdmin](https://www.pgadmin.org/docs/pgadmin4/latest/query_tool.html) | The query tool ties connection, editable SQL, execution and output together. | Keep server, selected batch, output and cancellation in the same tab. |
| 14 | [MongoDB Compass](https://www.mongodb.com/products/tools/compass) | Schema exploration and performance investigation are separate tasks. | Open object properties and execution plans as separate inspectable views. |
| 15 | [Redis Insight](https://redis.io/insight/) | The browser and command tools support different data structures. | Use type-aware cell inspection, including binary and NULL. |
| 16 | [MySQL Workbench](https://www.mysql.com/products/workbench/) | Script editing, modelling and administration each have dedicated workflows. | Give risky changes an explicit design/preview/apply flow. |
| 17 | [Xcode](https://developer.apple.com/xcode/) | Integrated debugging and inspection live alongside code. | Place line-linked SQL errors beside the script and provide a dedicated details view. |
| 18 | [Nova](https://nova.app/) | Editor, debugger and sidebar coexist in one Mac workspace. | Use a compact title toolbar and a single editor-centred window. |
| 19 | [BBEdit](https://www.barebones.com/products/bbedit/) | Search and text manipulation are accessible in a professional text editor. | Make find/replace predictable and keyboard-first with visible scope. |
| 20 | [Zed](https://zed.dev/) | Editor responsiveness is a core feature in its own right. | Measure latency and avoid blocking typing during metadata or result work. |
| 21 | [Sublime Text](https://www.sublimetext.com/) | Tabs, split views and symbol navigation aid switching between documents. | Keep tab focus and each script's target stable when navigating. |
| 22 | [CotEditor](https://coteditor.com/) | Document inspection includes Unicode and independent views. | Give a selected cell an inspect action instead of expanding every grid row. |
| 23 | [Kaleidoscope](https://kaleidoscope.app/) | Comparison makes differences explicit before resolution. | Show original and proposed cell/table values before Apply. |
| 24 | [Tower](https://www.git-tower.com/mac) | Git operations have a visual workflow around repository state. | Keep staged SQL changes visibly distinct from executed changes. |
| 25 | [Fork](https://git-fork.com/) | A list of changes leads into a side-by-side diff. | Use a narrow summary and detailed preview for a multi-row update. |
| 26 | [GitHub Desktop](https://desktop.github.com/) | The desktop review workflow lets people examine changes precisely. | Keep the target, scope and final action visible in confirmations. |
| 27 | [Sublime Merge](https://www.sublimemerge.com/) | The graphical client exposes the exact underlying commands. | Expose generated T-SQL beside a design preview. |
| 28 | [iTerm2](https://iterm2.com/features.html) | Each split pane can represent an independent session. | Give each tab visible server and database identity. |
| 29 | [Transmit](https://panic.com/transmit/) | A dual-pane workspace makes the two sides of a transfer clear. | Show local file destination separately from the selected server result. |
| 30 | [Cyberduck](https://cyberduck.io/) | Synchronisation has a preview of affected files. | Preview every export destination, including siblings, before replacement. |
| 31 | [Proxyman](https://proxyman.com/) | Requests and formatted responses can be inspected together. | Bind results/messages to the query that produced them. |
| 32 | [Paw / RapidAPI for Mac](https://paw.cloud/) | The API request builder exposes typed parameter descriptions. | In Table Designer offer typed inputs with constraints beside the field. |
| 33 | [HTTPie Desktop](https://httpie.io/product) | Requests can be previewed without sending them. | Keep Script and Execute clearly different actions. |
| 34 | [Insomnia](https://insomnia.rest/) | Local storage supports an offline workspace. | Make editing and opening scripts useful before a connection exists. |
| 35 | [Bruno](https://www.usebruno.com/) | Local request collections use readable versionable files. | Keep SQL scripts user-owned and diffable. |
| 36 | [Charles](https://www.charlesproxy.com/) | An HTTP monitor exposes the exchange for diagnosis. | Provide user-requested, redacted diagnostics separate from routine status. |
| 37 | [Things](https://culturedcode.com/things/) | A simple entry point leads into deeper project organisation. | Offer three launch actions and disclose advanced options later. |
| 38 | [Bear](https://bear.app/) | An outline and folding help keep attention on current writing. | Make object navigation collapsible when editing long scripts. |
| 39 | [Ulysses](https://ulysses.app/) | A writing workspace combines organisation and focused editing. | Treat the SQL document as the main canvas. |
| 40 | [iA Writer](https://ia.net/writer) | Focus mode removes distractions from the text. | Allow hiding the sidebar and output without losing context. |
| 41 | [Obsidian](https://obsidian.md/) | Local documents can be navigated by their relationships. | Let object dependencies be an on-demand, scoped view. |
| 42 | [Craft](https://www.craft.do/) | A document-oriented app groups content by the user's task. | Put file actions with the document and server actions with the connection. |
| 43 | [Sketch](https://www.sketch.com/) | A Mac design surface supports inspection and handoff. | Separate the canvas from optional inspectors. |
| 44 | [Pixelmator Pro](https://www.pixelmator.com/pro/) | Layer organisation supports nondestructive editing. | Keep a visible pending-change set with Discard and Apply. |
| 45 | [Acorn](https://flyingmeat.com/acorn/) | An inspector provides precise values alongside direct manipulation. | Pair the grid with a detailed value inspector when needed. |
| 46 | [OmniGraffle](https://www.omnigroup.com/omnigraffle) | Alignment and grouping make dense diagrams legible. | Use a readable operator tree before attempting elaborate graphical plans. |
| 47 | [OmniOutliner](https://www.omnigroup.com/omnioutliner) | Focused outlines, filters and typed columns enable dense information. | Use keyboard-navigable hierarchy and scoped object filtering. |
| 48 | [OmniPlan](https://www.omnigroup.com/omniplan) | A project health overview leads to detailed information. | Show a concise query outcome with drill-down to messages and errors. |
| 49 | [Raycast](https://www.raycast.com/) | A keyboard launcher makes actions discoverable by name. | Offer searchable commands via existing menus/help before adding UI chrome. |
| 50 | [Alfred](https://www.alfredapp.com/) | File previews and actions can be accessed with the keyboard. | Preserve focus through search, preview and return. |
| 51 | [Dash](https://kapeli.com/dash) | Offline documentation search keeps references close to work. | Bundle shortcut help and supported features with the app. |
| 52 | [TextMate](https://macromates.com/) | Scoped search results jump directly into the document. | Jump to the failing SQL line or selected search match. |
| 53 | [SF Symbols](https://developer.apple.com/sf-symbols/) | A symbol catalogue organises a consistent icon vocabulary. | Use one coherent, licensed tool-icon family; test text alternatives. |
| 54 | [Final Cut Pro](https://www.apple.com/final-cut-pro/) | A timeline makes the order and consequence of edits legible. | Present multiple batches and their results in execution order. |
| 55 | [Logic Pro](https://www.apple.com/logic-pro/) | A complex production workspace groups controls by task. | Locate cancellation and active-session information near execution. |
| 56 | [Numbers](https://support.apple.com/numbers) | A spreadsheet treats cells and formulas as different semantics. | Offer raw and spreadsheet-safe exports and document conversion rules. |

## Which evidence gets the most weight

Use SSMS for SQL object hierarchy and vocabulary, historical Azure Data Studio for editor/results continuity, Postico for connection management, TablePlus for compactness and Nova for Mac workspace organisation. Use Kaleidoscope for change review and Cyberduck for previewing file effects. Prefer their user outcomes over borrowing their colours. Heavy admin suites show how broad the domain is; exposing every action on the initial toolbar reproduced precisely the problem observed in SQLoryn. Creative apps are secondary references, not reasons to add their unrelated features.

Apple’s guidance specifically supports the system menu bar, selective toolbars, resizable working areas and keyboard operation. Use these as platform constraints. [Designing for macOS](https://developer.apple.com/design/human-interface-guidelines/designing-for-macos/) · [Toolbars](https://developer.apple.com/design/human-interface-guidelines/toolbars)

