import { Link } from '@tanstack/react-router';
import { motion } from 'framer-motion';
import { FileText, Globe, Image, PanelsTopLeft, Plug, type LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Card, Page, PageHeader, usePermissions } from '@dcms/ui';
import { ROUTE_GUARDS } from '../../app/routeGuards';
import { can } from '../../lib/permissions';
import { useAuth } from '../../useAuth';

const QUICK: { to: string; icon: LucideIcon; key: string; tone: string }[] = [
  { to: '/content', icon: FileText, key: 'nav.content', tone: 'from-sky-500/15 text-sky-500' },
  { to: '/media', icon: Image, key: 'nav.media', tone: 'from-violet-500/15 text-violet-500' },
  { to: '/sites', icon: PanelsTopLeft, key: 'nav.sites', tone: 'from-emerald-500/15 text-emerald-500' },
  { to: '/plugins', icon: Plug, key: 'nav.plugins', tone: 'from-amber-500/15 text-amber-500' },
  { to: '/settings/domains', icon: Globe, key: 'nav.domains', tone: 'from-rose-500/15 text-rose-500' },
];

export function DashboardPage() {
  const { t } = useTranslation();
  const { user } = useAuth();
  const me = usePermissions();

  /*
   * Only the shortcuts this member can follow.
   *
   * Read from the router's own guards rather than a second list, so a shortcut and the page it
   * opens can never disagree: a tile that leads to "You don't have access" is worse than no tile.
   */
  const quick = QUICK.filter((q) => {
    const guard = ROUTE_GUARDS[q.to];
    if (!guard) return true;
    if (guard.superAdmin && !me?.isSuperAdmin) return false;
    return !guard.perm || can(me, guard.perm);
  });

  return (
    <Page>
      <PageHeader
        title={`${t('dashboard.welcome')}, ${user?.profile.name ?? user?.profile.email ?? ''}`}
        description={t('app.tagline')}
      />

      {quick.length > 0 ? (
        <h2 className="mb-3 text-sm font-semibold text-muted-foreground">{t('dashboard.quickActions')}</h2>
      ) : null}
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5">
        {quick.map((q, i) => (
          <motion.div
            key={q.to}
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ delay: i * 0.04 }}
          >
            <Link to={q.to}>
              <Card className="group h-full p-5 transition-all hover:-translate-y-0.5 hover:shadow-md">
                <div
                  className={`mb-3 flex h-11 w-11 items-center justify-center rounded-lg bg-gradient-to-br to-transparent ${q.tone}`}
                >
                  <q.icon className="h-5 w-5" />
                </div>
                <p className="font-medium">{t(q.key)}</p>
              </Card>
            </Link>
          </motion.div>
        ))}
      </div>
    </Page>
  );
}
