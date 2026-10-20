import { useContext, useId, useState, type FormEvent } from 'react';
import { useInRouterContext, useNavigate } from 'react-router';
import { signInUser, signInVisitor, signOutUser, signOutVisitor, useAccount, type AccountKind, type AccountState } from './accounts';
import { DataClientContext, DataError } from './data';
import type { PropDefinition } from './props';
import type { ComponentDefinition, ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';

type Props = Record<string, unknown>;
type Navigate = ((to: string) => void) | null;

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' && value.trim() ? value : fallback;
}

/** `members`: a slug, else nothing is asked for. */
const SLUG = /^[a-z0-9][a-z0-9-]{0,63}$/;

function kindOf(props: Props): AccountKind {
  return props.accounts === 'visitors' ? 'visitors' : 'users';
}

/** The account the component is about, or `undefined` (unknown) on the canvas. */
function useAccountOf(props: Props): AccountState {
  const live = useRenderMode() === 'live';
  return useAccount(kindOf(props), useContext(DataClientContext), live);
}

function displayName(account: AccountState, fallback: string): string {
  return account?.name || account?.email || fallback;
}

function here(): string {
  return typeof window === 'undefined' ? '/' : window.location.pathname + window.location.search;
}

/**
 * Signing in. With User Authentication a button to the edge's sign-in — the provider's page
 * asks for the password, never a form of the site's; with Visitor Authentication the email and
 * password form (or the registration form) that posts to the instance. Signed in, it says who.
 */
export function SignIn(render: ComponentRenderProps<Props>) {
  // useNavigate throws outside a router (the canvas has none), so only call it inside one.
  return useInRouterContext() ? <WithNavigate {...render} /> : <SignInBody {...render} navigate={null} />;
}

function WithNavigate(render: ComponentRenderProps<Props>) {
  const navigate = useNavigate();
  return <SignInBody {...render} navigate={(to) => void navigate(to)} />;
}

function SignInBody({ props, navigate }: ComponentRenderProps<Props> & { navigate: Navigate }) {
  const mode = useRenderMode();
  const client = useContext(DataClientContext);
  const account = useAccountOf(props);
  const kind = kindOf(props);
  const register = kind === 'visitors' && props.mode === 'register';
  const instance = text(props.instance).trim();
  const after = text(props.afterSignIn).trim();
  const label = text(props.label, register ? 'Create account' : 'Sign in');
  const [state, setState] = useState<'idle' | 'sending' | 'error'>('idle');
  const [message, setMessage] = useState<string | null>(null);
  const ids = { name: useId(), email: useId(), password: useId() };

  if (account) {
    return (
      <p className="dcms-form-status" role="status">
        Signed in as {displayName(account, 'you')}.
      </p>
    );
  }

  if (kind === 'users') {
    return (
      <button
        type="button"
        className="dcms-button dcms-button-primary dcms-button-md dcms-sign-in"
        onClick={() => mode === 'live' && signInUser(after.startsWith('/') ? after : here())}
      >
        {label}
      </button>
    );
  }

  if (!SLUG.test(instance)) {
    return mode === 'edit' ? <p className="dcms-editor-note">Choose the Visitor Authentication instance the accounts belong to.</p> : null;
  }

  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (mode === 'edit') return;
    const data = new FormData(e.currentTarget);
    const value = (name: string) => String(data.get(name) ?? '').trim();
    setState('sending');
    setMessage(null);
    try {
      await signInVisitor(client, instance, register ? 'register' : 'sign-in', {
        email: value('email'),
        password: String(data.get('password') ?? ''),
        ...(register && value('displayName') ? { displayName: value('displayName') } : {}),
      });
      setState('idle');
      if (after.startsWith('/') && navigate) navigate(after);
    } catch (err) {
      setState('error');
      // The site's client throws a DataError; a builder's preview client its own error, with a status too.
      const status = (err as { status?: unknown }).status;
      const reason = err instanceof DataError ? (err.body as { error?: string } | undefined)?.error : status ? (err as Error).message : undefined;
      setMessage(status === 401 ? 'That email and password do not match an account.' : (reason ?? null));
    }
  };

  return (
    <form className="dcms-form dcms-sign-in" onSubmit={submit} noValidate={mode === 'edit'}>
      <div className="dcms-form-fields">
        {register ? (
          <div className="dcms-field">
            <label htmlFor={ids.name}>{text(props.nameLabel, 'Name')}</label>
            <input id={ids.name} name="displayName" type="text" autoComplete="name" maxLength={256} />
          </div>
        ) : null}
        <div className="dcms-field">
          <label htmlFor={ids.email}>{text(props.emailLabel, 'Email')}</label>
          <input id={ids.email} name="email" type="email" autoComplete="email" required />
        </div>
        <div className="dcms-field">
          <label htmlFor={ids.password}>{text(props.passwordLabel, 'Password')}</label>
          <input id={ids.password} name="password" type="password" autoComplete={register ? 'new-password' : 'current-password'} required />
        </div>
      </div>
      <button type="submit" className="dcms-button dcms-button-primary dcms-button-md" disabled={state === 'sending'}>
        {label}
      </button>
      {state === 'error' && (
        <p className="dcms-form-status dcms-form-error" role="alert">
          {message ?? (register ? 'The account could not be created. Please try again.' : 'Signing in failed. Please try again.')}
        </p>
      )}
    </form>
  );
}

/**
 * A section shown only to signed-in people — or only to signed-out ones. Cosmetic: its content
 * is in the page for anyone who looks. Nothing shows until it is known who is signed in.
 */
export function SignedIn({ props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const account = useAccountOf(props);
  const forSignedIn = props.show !== 'signed-out';
  if (mode === 'edit') {
    return (
      <div className="dcms-signed-in">
        <p className="dcms-editor-note">Shown only to {forSignedIn ? 'signed-in' : 'signed-out'} {kindOf(props) === 'visitors' ? 'visitors' : 'users'}</p>
        {slot('default')}
      </div>
    );
  }
  if (account === undefined || !!account !== forSignedIn) return null;
  return <div className="dcms-signed-in">{slot('default')}</div>;
}

/** The signed-in person's name and a sign-out button; nothing for a signed-out visitor. */
export function Account({ props }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const account = useAccountOf(props);
  if (mode === 'live' && !account) return null;
  // Absent: the default; empty: only the button.
  const greeting = props.greeting === undefined ? 'Signed in as {name}' : text(props.greeting);
  const name = displayName(account, 'Alex Example');
  const signOut = () => {
    if (mode !== 'live') return;
    if (kindOf(props) === 'visitors') signOutVisitor();
    else signOutUser();
  };
  return (
    <div className="dcms-account">
      {greeting ? <span className="dcms-account-name">{greeting.replace(/\{name\}/g, name)}</span> : null}
      <button type="button" className="dcms-button dcms-button-secondary dcms-button-sm" onClick={signOut}>
        {text(props.signOutLabel, 'Sign out')}
      </button>
    </div>
  );
}

const accountsProp: PropDefinition = {
  kind: 'select',
  name: 'accounts',
  label: 'Accounts',
  default: 'users',
  group: 'data',
  options: [
    { value: 'users', label: 'Site users (User Authentication)' },
    { value: 'visitors', label: 'Visitor accounts (Visitor Authentication)' },
  ],
  description:
    'Whose accounts: the people your workspace invites or admits (User Authentication), or visitors who register themselves (Visitor Authentication).',
};

export const ACCOUNT_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.sign-in',
    version: 1,
    label: 'Sign-in',
    description:
      'Lets people sign in. Site users get a button to your organisation’s sign-in page; visitor accounts get an email and password form — or a registration form.',
    category: 'Forms',
    keywords: ['login', 'log in', 'sign in', 'register', 'sign up', 'account', 'password', 'members'],
    component: SignIn,
    props: [
      accountsProp,
      {
        kind: 'text',
        name: 'instance',
        label: 'Visitor accounts plugin',
        maxLength: 64,
        group: 'data',
        showIf: { prop: 'accounts', is: ['visitors'] },
        description: 'The Visitor Authentication instance the accounts belong to, e.g. “members”.',
      },
      {
        kind: 'select',
        name: 'mode',
        label: 'Does',
        default: 'sign-in',
        group: 'data',
        showIf: { prop: 'accounts', is: ['visitors'] },
        options: [
          { value: 'sign-in', label: 'Sign in' },
          { value: 'register', label: 'Create an account' },
        ],
        description: 'Visitors can create their own accounts. Site users cannot: they are invited, or admitted by their organisation.',
      },
      { kind: 'text', name: 'label', label: 'Button', maxLength: 60, group: 'content', description: 'Empty: “Sign in”, or “Create account”.' },
      { kind: 'text', name: 'emailLabel', label: 'Email label', maxLength: 60, group: 'content', showIf: { prop: 'accounts', is: ['visitors'] }, description: 'Empty: “Email”.' },
      { kind: 'text', name: 'passwordLabel', label: 'Password label', maxLength: 60, group: 'content', showIf: { prop: 'accounts', is: ['visitors'] }, description: 'Empty: “Password”.' },
      { kind: 'text', name: 'nameLabel', label: 'Name label', maxLength: 60, group: 'content', showIf: { prop: 'mode', is: ['register'] }, description: 'Empty: “Name”. Registering asks for a name too.' },
      {
        kind: 'text',
        name: 'afterSignIn',
        label: 'Then open',
        maxLength: 200,
        group: 'behaviour',
        description: 'A page to go to once signed in, e.g. “/account”. Empty: stay on this page.',
      },
    ],
  },
  {
    type: 'dcms.signed-in',
    version: 1,
    label: 'Signed-in section',
    description:
      'Shown only to signed-in people — or only to signed-out ones: a welcome, a members’ menu, a “sign in to see more”. To keep something private, protect its page in User Authentication’s access rules instead.',
    category: 'Forms',
    keywords: ['members only', 'logged in', 'signed out', 'gate', 'private', 'account'],
    component: SignedIn,
    props: [
      accountsProp,
      {
        kind: 'select',
        name: 'show',
        label: 'Show to',
        default: 'signed-in',
        group: 'behaviour',
        options: [
          { value: 'signed-in', label: 'Signed-in people' },
          { value: 'signed-out', label: 'Signed-out people' },
        ],
        description: 'Signed-out: for a “sign in to see more” or the sign-in form itself.',
      },
    ],
    slots: [{ name: 'default', label: 'Content' }],
  },
  {
    type: 'dcms.account',
    version: 1,
    label: 'Signed-in user',
    description: 'The signed-in person’s name and a sign-out button — for a header. Nothing shows to people who are signed out.',
    category: 'Forms',
    keywords: ['account', 'profile', 'logout', 'log out', 'sign out', 'user', 'avatar'],
    component: Account,
    props: [
      accountsProp,
      {
        kind: 'text',
        name: 'greeting',
        label: 'Text',
        default: 'Signed in as {name}',
        maxLength: 120,
        group: 'content',
        description: '{name} is the person’s name (or email). Empty: only the button.',
      },
      { kind: 'text', name: 'signOutLabel', label: 'Button', default: 'Sign out', maxLength: 60, group: 'content', description: 'The text on the sign-out button.' },
    ],
  },
];
