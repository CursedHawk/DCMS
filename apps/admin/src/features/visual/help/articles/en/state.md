# Buttons, popups and page state

Every button and link has **When clicked**:

- **Go to a page** or **Open a link** (in a new tab, if you like).
- **Scroll to a section** — give the section an *Anchor name* first.
- **Open a popup** — add a **Popup** part to the page first.
- **Show a message**.
- **Set page state** or **Toggle page state**.

**Page state** is a value a page remembers while it is open — a tab, a filter, a "show more". Declare it in Pages, under Page state. Then any part can be shown only **when** the state has a value: a button sets it, the part appears.

The canvas shows every part, so you can edit them. Preview and the site show only what the state allows.
