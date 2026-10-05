import { LegalPage, type Section } from '../components/LegalPage';
import { CONTACT_EMAIL, OPERATOR, OPERATOR_DETAILS } from '../site';

const mail = <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>;

const sections: Section[] = [
  {
    id: 'who-we-are',
    title: 'Who we are',
    body: (
      <>
        <p>
          DCMS is operated by <strong>{OPERATOR}</strong>, {OPERATOR_DETAILS} (“we”, “us”). For the
          personal data described in sections 3 to 5 we are the controller under the EU General Data
          Protection Regulation (GDPR).
        </p>
        <p>
          For anything about your personal data, including exercising your rights, write to {mail}.
          We do not have a data protection officer, as the GDPR does not require one for a service
          of our kind.
        </p>
      </>
    ),
  },
  {
    id: 'scope',
    title: 'What this policy covers',
    body: (
      <ul>
        <li>
          <strong>This website</strong>, highgeek.eu.
        </li>
        <li>
          <strong>The DCMS service</strong>: your account, the admin console at admin.highgeek.eu,
          sign-in at auth.highgeek.eu, the git server at git.highgeek.eu, and everything you do in a
          workspace.
        </li>
        <li>
          <strong>Websites built with DCMS</strong>, but only in part. When you visit a site that
          one of our customers built and published with DCMS, that customer decides what the site
          collects and is the controller; we process the data on their behalf. Section 6 explains
          what DCMS itself does on those sites; for everything else, read that site’s own privacy
          notice.
        </li>
      </ul>
    ),
  },
  {
    id: 'website',
    title: 'Visiting this website',
    body: (
      <>
        <p>
          This website sets <strong>no cookies</strong>, runs no analytics or advertising, and loads
          nothing from third parties. If you switch between light and dark mode, the choice is saved
          in your browser’s local storage under <code>dcms.theme</code>. It never leaves your
          device.
        </p>
        <p>
          Like any web server, ours records each request: IP address, time, requested address,
          browser user agent and response status. We use these logs only to keep the service running
          and secure (our legitimate interest, Art. 6(1)(f) GDPR) and delete them after 30 days.
        </p>
      </>
    ),
  },
  {
    id: 'account',
    title: 'Your DCMS account',
    body: (
      <>
        <table>
          <thead>
            <tr>
              <th>Data</th>
              <th>Why</th>
              <th>Legal basis</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>
                Email address, display name, password (stored only as a salted hash), account
                creation date
              </td>
              <td>To create your account and sign you in</td>
              <td>Contract, Art. 6(1)(b)</td>
            </tr>
            <tr>
              <td>Your Google account’s email and identifier, if you sign in with Google</td>
              <td>To sign you in with Google and link it to your account</td>
              <td>Contract, Art. 6(1)(b)</td>
            </tr>
            <tr>
              <td>Git username, SSH public keys you add, the password you set for git</td>
              <td>To give you access to your site repositories over HTTPS and SSH</td>
              <td>Contract, Art. 6(1)(b)</td>
            </tr>
            <tr>
              <td>Active sessions, with the IP address and browser of each</td>
              <td>To keep you signed in and let you see and end your sessions</td>
              <td>Contract, Art. 6(1)(b)</td>
            </tr>
            <tr>
              <td>
                Audit log of security-relevant actions: sign-ins and failed attempts, password and
                sign-in method changes, changes made in a workspace, with IP address and browser
              </td>
              <td>
                To protect accounts, investigate abuse and let workspace owners see who changed what
              </td>
              <td>Legitimate interest, Art. 6(1)(f)</td>
            </tr>
            <tr>
              <td>Messages we send you: password resets, invitations, notifications</td>
              <td>To run the service and respond to what you asked for</td>
              <td>Contract, Art. 6(1)(b)</td>
            </tr>
          </tbody>
        </table>
        <p>
          We do not use your data for advertising, do not sell it, and do not build profiles of you.
        </p>
      </>
    ),
  },
  {
    id: 'workspace',
    title: 'Content in your workspace',
    body: (
      <>
        <p>
          Everything you and your team put into a workspace — pages, site source code, posts, media,
          settings, AI conversations — is stored so we can provide the service to you (Art.
          6(1)(b)). If that content contains other people’s personal data, the workspace owner is
          responsible for having a legal basis for it, and we process it on the owner’s instructions
          as described in our <a href="/terms-of-service#personal-data">Terms of Service</a>.
        </p>
        <h3>AI features</h3>
        <p>
          When you use the assistant, the coding agent or a visitor chatbot, the prompt and the
          content needed to answer it — such as the page being edited or the published pages a
          chatbot answers from — are sent to the AI provider configured for your workspace. That can
          be Anthropic, OpenAI, another OpenAI-compatible service or a model you run yourself. If
          you add your own API key, your use of that provider is governed by your agreement with
          them. Keys are stored encrypted. Assistant conversations are kept for 180 days and
          coding-agent runs for 45 days.
        </p>
        <h3>Optional integrations</h3>
        <p>
          If you import files from Google Drive, Google gives us access only to the files you pick,
          for that one import; the access token is not stored. If you connect an Instagram account
          or Facebook Page, we store an encrypted access token and copy the posts you chose to
          display.
        </p>
      </>
    ),
  },
  {
    id: 'sites',
    title: 'Visitors to sites built with DCMS',
    body: (
      <>
        <p>
          On those sites the site owner is the controller. Depending on which features they turned
          on, DCMS may process:
        </p>
        <ul>
          <li>
            <strong>Analytics</strong>, only after the visitor agrees in the consent banner: pages
            viewed, referrer, campaign tags, device and browser type, and a random session
            identifier. IP addresses are not stored. Raw events are deleted after 90 days; only
            aggregated daily counts are kept.
          </li>
          <li>
            <strong>Form submissions</strong>: what the visitor typed, the time, and a shortened
            browser identifier for abuse checks. No IP address is stored.
          </li>
          <li>
            <strong>Chat</strong>: the name given, the messages and their times.
          </li>
          <li>
            <strong>Visitor accounts</strong>: email, display name, a hashed password and when the
            email was verified.
          </li>
        </ul>
        <p>
          The site owner decides how long submissions, chats and visitor accounts are kept and can
          delete them at any time. Requests about this data should go to the site owner; if you
          contact us instead, we will pass your request on.
        </p>
      </>
    ),
  },
  {
    id: 'cookies',
    title: 'Cookies and browser storage',
    body: (
      <>
        <p>
          This website uses none. The DCMS service and sites built with it use only what they need
          to work:
        </p>
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Where</th>
              <th>Purpose</th>
              <th>Lifetime</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>
                <code>dcms.edge</code>, <code>dcms.csrf</code>
              </td>
              <td>admin console</td>
              <td>Your signed-in session and protection against cross-site request forgery</td>
              <td>8 hours</td>
            </tr>
            <tr>
              <td>
                <code>.AspNetCore.Identity.Application</code>
              </td>
              <td>sign-in</td>
              <td>Remembers that you signed in, so you are not asked again</td>
              <td>14 days, renewed while used</td>
            </tr>
            <tr>
              <td>
                <code>.AspNetCore.Identity.External</code>, <code>.AspNetCore.Antiforgery.*</code>
              </td>
              <td>sign-in</td>
              <td>Completing a Google sign-in; protecting sign-in forms</td>
              <td>Session</td>
            </tr>
            <tr>
              <td>
                <code>dcms.affinity</code>
              </td>
              <td>sites with chat</td>
              <td>Keeps a live chat connected to the same server</td>
              <td>Session</td>
            </tr>
            <tr>
              <td>
                <code>dcms-consent</code>, <code>dcms-sid</code>
              </td>
              <td>sites with analytics</td>
              <td>
                Remembers the visitor’s consent choice; the session identifier is created only after
                consent
              </td>
              <td>Until cleared; browser session</td>
            </tr>
          </tbody>
        </table>
        <p>
          The admin console also keeps preferences such as language, theme and the last workspace
          you opened in your browser’s local storage. None of this is used to track you.
        </p>
      </>
    ),
  },
  {
    id: 'recipients',
    title: 'Who we share data with',
    body: (
      <>
        <p>
          We use these service providers (processors). Each receives only what it needs for the task
          listed:
        </p>
        <table>
          <thead>
            <tr>
              <th>Provider</th>
              <th>Task</th>
              <th>Location</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>OVHcloud</td>
              <td>Servers that host the entire service and all its data</td>
              <td>European Union</td>
            </tr>
            <tr>
              <td>Apple (iCloud Mail)</td>
              <td>Delivering email we send: recipient address and message</td>
              <td>EU / USA</td>
            </tr>
            <tr>
              <td>Cloudflare</td>
              <td>DNS for our domains; it does not see website traffic</td>
              <td>Global</td>
            </tr>
            <tr>
              <td>Let’s Encrypt</td>
              <td>HTTPS certificates; receives only domain names, which are public</td>
              <td>USA</td>
            </tr>
            <tr>
              <td>Google</td>
              <td>Only if you sign in with Google or import from Google Drive</td>
              <td>USA</td>
            </tr>
            <tr>
              <td>Meta</td>
              <td>Only if you connect Instagram or Facebook</td>
              <td>USA</td>
            </tr>
            <tr>
              <td>Your chosen AI provider</td>
              <td>Only when you use AI features (section 5)</td>
              <td>Depends on the provider</td>
            </tr>
          </tbody>
        </table>
        <p>
          We disclose data to public authorities only where the law requires it, and we check that
          each request is lawful first.
        </p>
      </>
    ),
  },
  {
    id: 'transfers',
    title: 'Transfers outside the EU',
    body: (
      <p>
        Your account and workspace data are stored in the European Union. Where a provider above
        processes data in the USA or another country without an EU adequacy decision, the transfer
        relies on the EU–US Data Privacy Framework where the provider is certified, or on the
        European Commission’s Standard Contractual Clauses. You can ask us for a copy of the
        relevant safeguards.
      </p>
    ),
  },
  {
    id: 'retention',
    title: 'How long we keep data',
    body: (
      <table>
        <thead>
          <tr>
            <th>Data</th>
            <th>Kept for</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>Account and workspace content</td>
            <td>Until you delete it, your account, or the workspace</td>
          </tr>
          <tr>
            <td>Server and application logs</td>
            <td>30 days; security logs 90 days</td>
          </tr>
          <tr>
            <td>Audit log</td>
            <td>
              400 days, including after an account is deleted, so past actions stay attributable
            </td>
          </tr>
          <tr>
            <td>Sessions</td>
            <td>Until sign-out or expiry; a record of ended sessions for 14 days</td>
          </tr>
          <tr>
            <td>AI assistant conversations / coding-agent runs</td>
            <td>180 days / 45 days</td>
          </tr>
          <tr>
            <td>Read notifications</td>
            <td>90 days</td>
          </tr>
          <tr>
            <td>Site analytics events</td>
            <td>90 days; aggregated counts until the workspace is deleted</td>
          </tr>
        </tbody>
      </table>
    ),
  },
  {
    id: 'security',
    title: 'Security',
    body: (
      <p>
        All traffic is encrypted with HTTPS. Passwords are stored only as salted hashes, repeated
        failed sign-ins lock the account temporarily, and secrets such as API keys and access tokens
        are encrypted in a dedicated secrets vault. Workspaces are separated both by the application
        and by row-level security in the database. Access to production systems is limited to the
        people who operate them.
      </p>
    ),
  },
  {
    id: 'rights',
    title: 'Your rights',
    body: (
      <>
        <p>Under the GDPR you have the right to:</p>
        <ul>
          <li>get a copy of your personal data and information about how it is processed;</li>
          <li>have inaccurate data corrected;</li>
          <li>have your data deleted;</li>
          <li>restrict processing, or object to processing based on our legitimate interest;</li>
          <li>receive the data you gave us in a machine-readable format;</li>
          <li>withdraw consent at any time, where processing relies on consent.</li>
        </ul>
        <p>
          You can delete your account yourself in the admin console under your account settings,
          after leaving or handing over any workspaces you own. For everything else, write to {mail}
          . We answer within one month.
        </p>
        <p>
          You can also complain to a supervisory authority. In the Czech Republic that is the Office
          for Personal Data Protection (Úřad pro ochranu osobních údajů),{' '}
          <a href="https://uoou.gov.cz" rel="noopener">
            uoou.gov.cz
          </a>
          ; you may instead contact the authority where you live or work.
        </p>
      </>
    ),
  },
  {
    id: 'children',
    title: 'Children',
    body: (
      <p>
        DCMS is not intended for anyone under 16, and we do not knowingly create accounts for them.
      </p>
    ),
  },
  {
    id: 'changes',
    title: 'Changes to this policy',
    body: (
      <p>
        If we change how we process personal data, we will update this page and its date. For
        significant changes we will also tell account holders by email before they take effect.
      </p>
    ),
  },
];

export function Privacy() {
  return (
    <LegalPage
      title="Privacy Policy"
      intro={
        <p>
          This policy explains what personal data DCMS processes, why, how long we keep it and what
          control you have. In short: we collect what the service needs to work, keep it in the EU,
          and don’t use it for advertising.
        </p>
      }
      sections={sections}
    />
  );
}
