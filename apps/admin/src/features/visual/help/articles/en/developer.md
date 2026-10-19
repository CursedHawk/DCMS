# Developer components

When a part needs code, a developer can write it in React (TSX): **My components › New developer component**.

- It declares the settings it takes, so it is placed and set up like any other part.
- **Edit source** opens its code.
- For safety, its code never runs in the builder itself: on the canvas each one is drawn in a sealed frame of its own (a placeholder shows while it starts, or if it fails). **Preview** runs the whole site, code included, in a separate sandbox, as does the published site.
- A component that loads its own data shows that data in **Preview** and on the site, not on the canvas.

Use them for what the built-in parts cannot do, and keep the rest visual.
