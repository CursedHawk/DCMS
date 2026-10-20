# Signing people in

Two plugins let people sign in, and they are for different people:

- **Site users** (*User Authentication*): staff, partners or clients your workspace invites — or admits by their organisation's email domain. They sign in on your workspace's sign-in page (email and password, Google, Microsoft…), so the site only needs a button. They cannot register themselves.
- **Visitor accounts** (*Visitor Authentication*): the public, registering for themselves — members, customers, a community. The site shows the email and password form, and a registration form.

The parts (search *sign in*), each with **Accounts** set to the kind you use:

- **Sign-in**: for site users a button to the sign-in page; for visitor accounts the sign-in form, or with **Does: Create an account** the registration form. **Then open** sends people on, e.g. to `/account`.
- **Signed-in section**: its contents show only to signed-in people — or, with **Show to: Signed-out people**, only to the others: a members' menu, a "Sign in to see more".
- **Signed-in user**: the person's name and a **Sign out** button, for the header.

Hiding is not protecting: a signed-in section's contents are still in the page. To keep a page private, protect its path in User Authentication's **Access → Site access**, and its data in **Access → API access**. A Dynamic Apps table whose public access is *own* shows each visitor only their own records.

**In the preview:** sign in as a site user, or **View as** one, from the toolbar. Visitors who sign in or register on the page are test accounts in the preview's sandbox; the visitor menu signs them out and **Reset test data** removes them.

Never build sign-in from a **Form** and its fields: a Forms form stores what it is sent, password included, in its inbox. The checks refuse it.
