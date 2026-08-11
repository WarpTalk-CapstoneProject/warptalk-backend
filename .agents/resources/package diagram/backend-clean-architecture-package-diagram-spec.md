# Package Diagram Specification - WarpTalk Backend Clean Architecture

The package diagram below maps `warptalk-backend` as the outer repository package containing the Clean Architecture layer boundaries (`API`, `Application`, `Domain`, `Infrastructure`) and their sub-packages.

## Visual Style & Syntax Rules (Source of Truth)

1. **Repository Container Boundary**:
   - The whole diagram is enclosed in one largest white tabbed package box labeled `warptalk-backend`.
   - The diagram does not use a PlantUML `title`; the repository package label is the only top-level visual label.

2. **Layer Package Boundaries**:
   - Every major layer (`API`, `Application`, `Infrastructure`, `Domain`) remains inside `warptalk-backend` as a white tabbed package box (`outline=black, width=2, fill=white`).
   - The layer title is placed inside the top-left tab of each layer package box.

3. **Inner Sub-Packages**:
   - Sub-packages inside each layer (`Controllers`, `Services`, `Repositories`, `Entities`, `BackgroundServices`, etc.) are drawn as nested tabbed folder boxes.
   - Concrete sub-package tabs remain blank; package names are centered in the main body rectangle.
   - Abstract contract sub-packages use `<<abstract>>` in the tab and the package name in the main body rectangle.

4. **Dependency Rules (Clean Architecture Inversion)**:
   - `API ..> Application`: Presentation layer depends on Use Case application services.
   - `Application ..> Domain`: Application layer uses Domain entities and interfaces.
   - `Infrastructure ..> Application`: Infrastructure implements repository and service interfaces defined in Application/Domain.
   - `Infrastructure ..> Domain`: Infrastructure persists domain entities and implements Domain repository contracts.

5. **Resource Assets**:
   - `.puml` PlantUML source: `backend-clean-architecture-package-diagram.puml`
   - `.png` Image output: `backend-clean-architecture-package-diagram.png`
