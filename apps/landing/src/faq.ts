/** Also the FAQPage structured data (entry-server.tsx), so answers are plain text. */
export const faq = [
  {
    q: 'What is DCMS?',
    a: 'DCMS is a website builder and content management system in one. You build a site visually, in React or with an AI agent, manage its content with plugins such as a blog, galleries, events and forms, and publish it to your own domain. Every site is versioned in its own git repository.',
  },
  {
    q: 'Do I need to know how to code?',
    a: 'No. The visual editor and the React builder are drag and drop, and the AI assistant can make changes for you. If you do code, you can edit the same project in the browser IDE or clone it and use your own tools.',
  },
  {
    q: 'Can I use my own domain?',
    a: 'Yes. Add the domain in the admin, prove you own it with one DNS record, and point it at DCMS. The HTTPS certificate is issued and renewed automatically. Until then every site also has an address under dcms.highgeek.eu.',
  },
  {
    q: 'Can I take my site somewhere else?',
    a: 'Yes. Every site lives in a git repository you can clone over HTTPS or SSH. Visual-editor sites are plain HTML and CSS, and code sites are standard React and Vite projects, so they run anywhere.',
  },
  {
    q: 'Which AI providers can I use?',
    a: 'Anthropic, OpenAI, or any OpenAI-compatible endpoint, including local models through Ollama or LM Studio. You can add your own API key for yourself or for the whole workspace; keys are stored encrypted.',
  },
  {
    q: 'Where is my data stored?',
    a: 'On servers run by OVHcloud in the European Union. Workspaces are isolated from each other in the database itself, not only in the application. Details are in the privacy policy.',
  },
  {
    q: 'Does it cost anything?',
    a: 'Accounts are free today. Each workspace includes 5 GB of storage and a daily AI allowance. If paid plans are introduced, you will be told at least 30 days in advance and nothing will be charged without your agreement.',
  },
];
