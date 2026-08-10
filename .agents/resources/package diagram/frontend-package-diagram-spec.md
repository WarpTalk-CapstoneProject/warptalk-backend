# Package Diagram Specification - WarpTalk Frontend Web (`warptalk-web/src`)

The package diagram maps the top-level frontend package boundaries grounded 100% in the latest codebase of `warptalk-web/src` on branch `development` (`app`, `components`, `lib`, `hooks`, `stores`, `services`, `types`, `constants`, `emails`), adhering strictly to OMG UML 2.5.1 Specification Clause 12.2.4 notation standards.

## Codebase Physical Packages

1. **`app`**: App Router Directory Packages (`(auth)`, `(app)`, `(internal)`, `workspace`, `api`, `invitations`).
2. **`components`**: All 17 physical UI Directory Packages (`ui`, `layout`, `rooms`, `auth`, `documents`, `workspace`, `voice`, `notifications`, `providers`, `admin`, `landing`, `legal`, `errors`, `features`, `language`, `presence`, `visuals`).
3. **`lib`**: Core Utility Directory Packages (`api`, `auth`, `format`, `meeting`, `realtime`, `transcript`).
4. **`hooks`**: Leaf Package containing custom hook modules (`useMeeting`, `useWorkspace`, `useAuth`, etc.).
5. **`stores`**: Leaf Package containing Zustand store modules (`authStore`, `workspaceStore`, `roomStore`, etc.).
6. **`services`**: Leaf Package containing REST API client service modules (`authService`, `workspaceService`, `meetingService`, etc.).
7. **`types <<abstract>>`**: Abstract Leaf Package containing TypeScript type definitions.
8. **`constants <<abstract>>`**: Abstract Leaf Package containing constant specifications.
9. **`emails`**: Leaf Package containing transactional email components.

## Dependency Rules

- `app ..> components`
- `components ..> hooks`
- `components ..> services`
- `hooks ..> stores`
- `stores ..> services`
- `services ..> lib`
- `lib ..> types <<abstract>>`
- `types <<abstract>> ..> constants <<abstract>>`

## Resource Assets

- `.puml` PlantUML source: `frontend-package-diagram.puml`
- `.png` Image output: `frontend-package-diagram.png`
- Renderer script: `render_frontend_package_diagram.py`
