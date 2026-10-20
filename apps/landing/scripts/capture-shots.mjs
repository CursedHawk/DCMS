// Re-shoots the product screenshots on the landing page (public/shots/*.webp) from the real
// admin console on dev, signed in as the loadtest operator, looking at the test1 workspace
// (the Northwind CRM demo, see loadtest/seed/crm-demo.mjs). Run it when the console changes:
//
//   source ~/.config/dcms-loadtest.env && node apps/landing/scripts/capture-shots.mjs [name...]
//
// Not part of the build: it needs the dev stack and credentials. Review every image before
// committing — anything showing a real person's address does not belong on the public site.
/* global document, Image -- used inside page.evaluate, which runs in the browser */
import { chromium } from '@playwright/test';
import { writeFile } from 'node:fs/promises';

const ADMIN = 'https://admin.highgeek.eu';
const TENANT = 'test1';
const OUT = new URL('../public/shots/', import.meta.url);
const WIDTH = 2160; // 1440 CSS px at 1.5x: sharp in a ~1100px slot on a retina screen.

const { DCMS_LOADTEST_USER: user, DCMS_LOADTEST_PASSWORD: password } = process.env;
if (!user || !password) throw new Error('source ~/.config/dcms-loadtest.env first');

const siteId = (page, name) =>
    page.evaluate(
        async ([tenant, name]) => {
            const sites = await fetch('/api/admin/sites', {
                headers: { 'X-Dcms-Tenant': tenant },
            }).then((r) => r.json());
            return sites.find((s) => s.name === name)?.id;
        },
        [TENANT, name],
    );

/**
 * Drops the instance nameplate (contracts, events: true, but not what a visitor came to see) so
 * the tabs and what is under them fill the shot.
 */
const dropNameplate = (page) =>
    page
        .getByRole('tablist')
        .first()
        .evaluate((el) => {
            let node = el;
            while (node.parentElement && !node.parentElement.matches('main, main > div'))
                node = node.parentElement;
            while (node.previousElementSibling) node.previousElementSibling.remove();
        });

const shots = {
    builder: {
        path: async (page) => `/sites/${await siteId(page, 'test1-react-drag-and-drop')}`,
        prep: async (page) => {
            // Select the hero heading so the inspector has something to show. The canvas is a frame.
            await page
                .frameLocator('iframe')
                .first()
                .getByText('Stories, sound and visual collections')
                .first()
                .click();
        },
    },
    records: {
        path: '/plugins/crm/records',
        prep: async (page) => {
            const table = page.getByRole('combobox').first();
            await table.click();
            await page.getByRole('option', { name: 'Accounts', exact: true }).click();
            await page.waitForLoadState('networkidle');
            await dropNameplate(page);
        },
    },
    automations: {
        path: '/plugins/crm/configuration',
        prep: async (page) => {
            const tab = page.getByRole('tab', { name: 'Automations' });
            await tab.click();
            await page.waitForLoadState('networkidle');
            await dropNameplate(page);
        },
    },
    analytics: { path: '/app/analytics/dashboard' },
    marketplace: { path: '/marketplace' },
    forms: { path: '/app/forms/inbox' },
};

const only = process.argv.slice(2);
const browser = await chromium.launch();
const encoder = await browser.newPage();

/** PNG → WebP at WIDTH, in the browser: no image library needed for one resize. */
async function toWebp(png) {
    const data = await encoder.evaluate(
        async ([b64, width]) => {
            const img = new Image();
            img.src = `data:image/png;base64,${b64}`;
            await img.decode();
            const c = Object.assign(document.createElement('canvas'), {
                width,
                height: Math.round((img.height * width) / img.width),
            });
            c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
            return c.toDataURL('image/webp', 0.86).split(',')[1];
        },
        [png.toString('base64'), WIDTH],
    );
    return Buffer.from(data, 'base64');
}

for (const theme of ['light', 'dark']) {
    const ctx = await browser.newContext({
        viewport: { width: 1440, height: 900 },
        deviceScaleFactor: 2,
        colorScheme: theme,
    });
    const page = await ctx.newPage();

    // Sign in the way a person does: the console's button, then identity's form.
    await page.goto(ADMIN);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await page.locator('input[type=email], input[name*=mail i]').first().fill(user);
    await page.locator('input[type=password]').first().fill(password);
    await page.locator('button[type=submit]').first().click();
    await page.waitForURL(`${ADMIN}/**`);
    const { sub } = await page.evaluate(() => fetch('/.edge/me').then((r) => r.json()));

    await ctx.addInitScript(
        ([tenant, sub, theme]) => {
            localStorage.setItem('dcms.tenant', tenant);
            localStorage.setItem('dcms.storage-notice', '1');
            localStorage.setItem(`dcms.visual.toured.${sub}`, '1');
            localStorage.setItem('dcms.theme', theme);
        },
        [TENANT, sub, theme],
    );

    for (const [name, shot] of Object.entries(shots)) {
        if (only.length && !only.includes(name)) continue;
        const path = typeof shot.path === 'function' ? await shot.path(page) : shot.path;
        await page.goto(ADMIN + path);
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(2500);
        await shot.prep?.(page);
        // The operator is a platform admin, not a member: hide what only that role sees, and name
        // the workspace the switcher cannot (it lists memberships, and there is none).
        await page.evaluate(() => {
            for (const el of document.querySelectorAll('nav *')) {
                if (el.childElementCount === 0 && el.textContent?.trim() === 'Platform') {
                    el.closest('div')?.remove();
                }
            }
            for (const el of document.querySelectorAll('header button span, header button')) {
                if (el.childElementCount === 0 && el.textContent?.trim() === 'Tenant')
                    el.textContent = 'Northwind';
            }
        });
        await page.waitForTimeout(1200);
        await writeFile(
            new URL(`${name}-${theme}.webp`, OUT),
            await toWebp(await page.screenshot()),
        );
        console.log(`${name}-${theme}.webp`);
    }
    await ctx.close();
}
await browser.close();
