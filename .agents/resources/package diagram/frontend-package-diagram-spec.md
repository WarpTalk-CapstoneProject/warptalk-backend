# Package Diagram Specification - WarpTalk Frontend Web (`warptalk-web/src`)

The package diagram maps the top-level frontend package boundaries inside the outer `warptalk-web` repository package, grounded 100% in the latest codebase of `warptalk-web/src` on branch `development` (`app`, `components`, `lib`, `hooks`, `stores`, `services`, `types`, `constants`, `emails`), adhering strictly to OMG UML 2.5.1 Specification Clause 12.2.4 notation standards.

## Visual Style

- Concrete leaf/sub-package tabs are left blank; package names are centered inside the main body rectangle.
- Abstract packages use `<<abstract>>` in the tab, with the package name centered inside the main body rectangle.
- Container package names are shown in the top-left tab because their member packages are displayed inside the container.
- The rendered diagram has no standalone PlantUML title; the largest enclosing package tab is labeled `warptalk-web`.

## Codebase Physical Packages

1. **`warptalk-web`**: Outer repository package enclosing all frontend source packages.
2. **`app`**: App Router Directory Packages (`(auth)`, `(app)`, `(internal)`, `workspace`, `api`, `invitations`).
3. **`components`**: All 17 physical UI Directory Packages (`ui`, `layout`, `rooms`, `auth`, `documents`, `workspace`, `voice`, `notifications`, `providers`, `admin`, `landing`, `legal`, `errors`, `features`, `language`, `presence`, `visuals`).
4. **`lib`**: Core Utility Directory Packages (`api`, `auth`, `format`, `meeting`, `realtime`, `transcript`).
5. **`hooks`**: Leaf Package containing custom hook modules (`useMeeting`, `useWorkspace`, `useAuth`, etc.).
6. **`stores`**: Leaf Package containing Zustand store modules (`authStore`, `workspaceStore`, `roomStore`, etc.).
7. **`services`**: Leaf Package containing REST API client service modules (`authService`, `workspaceService`, `meetingService`, etc.).
8. **`types <<abstract>>`**: Abstract Leaf Package containing TypeScript type definitions.
9. **`constants <<abstract>>`**: Abstract Leaf Package containing constant specifications.
10. **`emails`**: Leaf Package containing transactional email components.

## Dependency Rules

- `app ..> components`
- `components ..> hooks`
- `components ..> services`
- `hooks ..> stores`
- `stores ..> services`
- `services ..> lib`
- `lib ..> types <<abstract>>`
- `constants <<abstract>>` is an abstract leaf package used directly by app/component/hook code where needed; no `types -> constants` dependency is drawn because `src/types` does not import `src/constants`.

## Resource Assets

- `.puml` PlantUML source: `frontend-package-diagram.puml`
- `.png` Image output: `frontend-package-diagram.png`
