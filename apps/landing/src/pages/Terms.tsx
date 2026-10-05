import { LegalPage, type Section } from '../components/LegalPage';
import { CONTACT_EMAIL, OPERATOR, OPERATOR_DETAILS } from '../site';

const mail = <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>;

const sections: Section[] = [
  {
    id: 'agreement',
    title: 'The agreement',
    body: (
      <>
        <p>
          These terms are an agreement between you and <strong>{OPERATOR}</strong>,{' '}
          {OPERATOR_DETAILS} (“we”, “us”), the operator of DCMS. By creating an account or using the
          service you accept them. If you use DCMS for an organisation, you confirm you may accept
          these terms on its behalf, and “you” includes that organisation.
        </p>
        <p>
          Our <a href="/privacy-policy">Privacy Policy</a> explains how we handle personal data and
          is part of this agreement.
        </p>
      </>
    ),
  },
  {
    id: 'service',
    title: 'The service',
    body: (
      <p>
        DCMS lets you build websites, manage their content and publish them on our hosting, at an
        address under dcms.highgeek.eu or on a domain you own. It includes the admin console, a git
        server for your site repositories, content plugins, a content delivery API and optional AI
        features. We develop DCMS continuously, so features may be added, changed or retired; we
        will give reasonable notice before removing something you rely on.
      </p>
    ),
  },
  {
    id: 'accounts',
    title: 'Your account',
    body: (
      <ul>
        <li>You must be at least 16 years old.</li>
        <li>Give a real email address you can receive mail at, and keep it current.</li>
        <li>
          Keep your password, git credentials and SSH keys to yourself. You are responsible for what
          happens under your account; tell us at once at {mail} if you think someone else has access
          to it.
        </li>
        <li>One person per account. Accounts created automatically or in bulk are not allowed.</li>
      </ul>
    ),
  },
  {
    id: 'workspaces',
    title: 'Workspaces and teams',
    body: (
      <p>
        Sites and content belong to a workspace. The workspace owner controls who is a member, what
        each member may do, and the workspace’s settings, and is responsible for the members they
        invite. Ownership can be transferred to another member. Deleting a workspace permanently
        deletes its sites, content, media, repositories and the data collected through its sites.
      </p>
    ),
  },
  {
    id: 'content',
    title: 'Your content',
    body: (
      <>
        <p>
          You keep all rights to what you upload or create in DCMS. You give us a non-exclusive,
          worldwide licence to store, copy, process, transform (for example resize images or convert
          video) and serve that content, only as far as needed to run the service for you and only
          for as long as it stays in DCMS.
        </p>
        <p>
          You confirm you have the rights to the content you publish, and that publishing it does
          not break the law or anyone else’s rights.
        </p>
        <p>
          Your site source code is yours and stays portable: every site lives in a git repository
          you can clone at any time. You can delete your content, sites and workspaces whenever you
          choose.
        </p>
      </>
    ),
  },
  {
    id: 'acceptable-use',
    title: 'Acceptable use',
    body: (
      <>
        <p>You may not use DCMS to:</p>
        <ul>
          <li>
            publish anything illegal, including child sexual abuse material, terrorist content, or
            content that infringes copyright or trademarks;
          </li>
          <li>
            host phishing pages, malware, scams, or sites that impersonate another person or
            organisation;
          </li>
          <li>send spam or collect personal data without a legal basis;</li>
          <li>harass, threaten or incite violence against anyone;</li>
          <li>
            attack, probe or overload the service or other customers’ sites, try to reach another
            workspace’s data, or get around limits, quotas or security measures;
          </li>
          <li>
            use build environments or storage for anything other than building and serving your
            sites, such as cryptocurrency mining or file distribution unrelated to a site.
          </li>
        </ul>
        <p>
          To report content hosted on DCMS that you believe is illegal, write to {mail} with the
          address of the page and why. We will review it promptly, act on it, and tell you and the
          affected customer what we decided.
        </p>
      </>
    ),
  },
  {
    id: 'domains',
    title: 'Custom domains',
    body: (
      <p>
        You may connect only domains you control, and you confirm ownership with a DNS record. While
        a domain points at DCMS we will obtain and renew HTTPS certificates for it from Let’s
        Encrypt on your behalf. Registering and renewing the domain itself remains your
        responsibility.
      </p>
    ),
  },
  {
    id: 'ai',
    title: 'AI features',
    body: (
      <>
        <p>
          AI output can be wrong, incomplete or similar to existing material. Review what the
          assistant or coding agent proposes before you publish it; you are responsible for what
          goes live on your sites, including answers a visitor chatbot gives on your behalf.
        </p>
        <p>
          If you use your own API key, your use of that AI provider is also subject to your
          agreement with them. Usage through DCMS is limited by fair-use allowances per user and per
          workspace, which we may adjust.
        </p>
      </>
    ),
  },
  {
    id: 'third-parties',
    title: 'Third-party services and plugins',
    body: (
      <p>
        Some features connect to services we do not control, such as Google, Instagram, Facebook or
        an AI provider. Their own terms apply to your use of them, and we are not responsible for
        their availability or for changes they make that affect the integration.
      </p>
    ),
  },
  {
    id: 'fees',
    title: 'Limits and fees',
    body: (
      <>
        <p>
          DCMS is currently provided free of charge. Each workspace has a storage allowance (5 GB by
          default) and AI allowances; we may agree higher limits with you.
        </p>
        <p>
          If we introduce paid plans we will announce them at least 30 days in advance. Nothing will
          be charged without your explicit agreement, and you can export your sites and delete your
          account before then.
        </p>
      </>
    ),
  },
  {
    id: 'availability',
    title: 'Availability and backups',
    body: (
      <p>
        We work to keep DCMS available and your data safe, but we do not promise uninterrupted
        service, and maintenance or incidents may cause downtime. We do not currently offer a
        guaranteed backup or restore service. Keep your own copies of anything important — cloning
        your site repositories and keeping your original media files is the simplest way.
      </p>
    ),
  },
  {
    id: 'personal-data',
    title: 'Personal data you process with DCMS',
    body: (
      <>
        <p>
          When your sites or workspace collect personal data — form submissions, visitor accounts,
          chat messages, analytics, team members’ data — you are the controller and we are your
          processor. In that role we:
        </p>
        <ul>
          <li>
            process the data only to provide DCMS to you, following your instructions as expressed
            through your settings and use of the service;
          </li>
          <li>ensure everyone with access to it is bound to confidentiality;</li>
          <li>apply the security measures described in our Privacy Policy;</li>
          <li>
            use only the subprocessors listed there, and tell you before adding or replacing one so
            you can object;
          </li>
          <li>
            help you respond to requests from data subjects and meet your security and
            breach-notification obligations;
          </li>
          <li>
            notify you without undue delay after becoming aware of a personal data breach affecting
            your data;
          </li>
          <li>delete the data when you delete it, the workspace, or your account;</li>
          <li>
            make available the information needed to demonstrate compliance with Art. 28 GDPR.
          </li>
        </ul>
        <p>
          You are responsible for having a legal basis for what your sites collect and for telling
          your visitors about it in your own privacy notice. The analytics consent banner is on by
          default; if you switch it off, it is up to you to obtain consent another way.
        </p>
      </>
    ),
  },
  {
    id: 'termination',
    title: 'Suspension and termination',
    body: (
      <>
        <p>You can stop using DCMS and delete your account at any time.</p>
        <p>
          We may suspend a site, workspace or account if it breaks these terms, puts the service or
          other customers at risk, or where the law requires it. Unless the situation is urgent or
          the law prevents it, we will tell you why first and give you a chance to fix it. We may
          end the agreement for serious or repeated breaches. If we decide to discontinue DCMS
          entirely, we will give at least 60 days’ notice so you can move your sites.
        </p>
      </>
    ),
  },
  {
    id: 'liability',
    title: 'Warranty and liability',
    body: (
      <>
        <p>
          DCMS is provided “as is”. To the extent the law allows, we make no promises beyond those
          in these terms, including that the service will be error-free or suit a particular
          purpose.
        </p>
        <p>
          To the extent the law allows, we are not liable for indirect or consequential loss, lost
          profits, revenue or data, and our total liability under this agreement is limited to the
          fees you paid us in the 12 months before the claim, or EUR 100 if you paid nothing.
          Nothing in these terms limits liability for harm caused intentionally or through gross
          negligence, for injury to life or health, or any right that a consumer cannot waive under
          the law of their country.
        </p>
        <p>
          If you use DCMS for business and a third party brings a claim against us because of your
          content or your breach of these terms, you will cover our reasonable costs of that claim.
        </p>
      </>
    ),
  },
  {
    id: 'changes',
    title: 'Changes to these terms',
    body: (
      <p>
        We may update these terms. We will email account holders about material changes at least 30
        days before they take effect. If you do not agree with the changes, you can delete your
        account before then; continuing to use DCMS afterwards means you accept them.
      </p>
    ),
  },
  {
    id: 'law',
    title: 'Governing law and disputes',
    body: (
      <>
        <p>
          These terms are governed by the law of the Czech Republic, and the courts of the Czech
          Republic have jurisdiction. If you are a consumer, you also keep the protection of the
          mandatory laws of the country where you live and may bring proceedings there.
        </p>
        <p>
          Consumers may also resolve a dispute out of court through the Czech Trade Inspection
          Authority (Česká obchodní inspekce),{' '}
          <a href="https://adr.coi.cz" rel="noopener">
            adr.coi.cz
          </a>
          . Please contact us first; most problems are fixed fastest by email.
        </p>
      </>
    ),
  },
  {
    id: 'contact',
    title: 'Contact',
    body: (
      <p>
        Questions about these terms, reports of illegal content, and everything else: {mail}. If a
        provision of these terms is found invalid, the rest remain in force.
      </p>
    ),
  },
];

export function Terms() {
  return (
    <LegalPage
      title="Terms of Service"
      intro={
        <p>
          These terms set out what you can expect from DCMS and what we expect from you. We have
          tried to keep them short and plain.
        </p>
      }
      sections={sections}
    />
  );
}
