# Package Diagram Specification - WarpTalk Backend Clean Architecture

The package diagram below maps the Clean Architecture layer boundaries (`API`, `Application`, `Domain`, `Infrastructure`) and sub-packages inside `warptalk-backend` services.

## Visual Style & Syntax Rules (Source of Truth)

1. **Outer Package Boundaries**:
   - Every major layer (`API`, `Application`, `Infrastructure`, `Domain`) is represented as a white tabbed package box (`outline=black, width=2, fill=white`).
   - The outer layer title is placed inside the top-left tab of the outer package box.

2. **Inner Sub-Packages**:
   - Sub-packages inside each layer (`Controllers`, `Services`, `Repositories`, `Entities`, `BackgroundServices`, etc.) are drawn as nested tabbed folder boxes.
   - Sub-package titles sit inside their respective top-left tab. Inner body remains clean and empty to avoid label redundancy.

3. **Dependency Rules (Clean Architecture Inversion)**:
   - `API ..> Application`: Presentation layer depends on Use Case application services.
   - `Application ..> Domain`: Application layer uses Domain entities and interfaces.
   - `Infrastructure ..> Application`: Infrastructure implements repository and service interfaces defined in Application/Domain.
   - `Infrastructure ..> Domain`: Infrastructure maps database entities and implements Domain repository contracts.

4. **Resource Assets**:
   - `.puml` PlantUML source: `backend-clean-architecture-package-diagram.puml`
   - `.png` Image output: `backend-clean-architecture-package-diagram.png`
