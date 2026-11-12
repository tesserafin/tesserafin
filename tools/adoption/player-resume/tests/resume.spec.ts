import { expect, test, type Page, type Request } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * After a Jellyfin import, each account signs in to the production Web client,
 * opens "Continue Watching", presses "Resume" and the test measures where the
 * player actually starts.
 *
 * The position is read from three independent places:
 *  - the <video> element's currentTime once it is really playing, adjusted by
 *    the transcoding offset when the server streams from a later start point;
 *  - the PositionTicks the client reports to /Sessions/Playing;
 *  - the burnt-in media timestamp, kept as a screenshot for a human reader.
 * The first one is the measurement the rehearsal grades.
 *
 * tools/adoption/rehearse_player_resume.py owns the containers and the import;
 * it hands over the accounts and reads ADOPTION_RESULTS back.
 */

type Account = { role: string; user: string; password: string; userId: string; expectedSeconds: number };
const accounts: Account[] = JSON.parse(process.env.ADOPTION_ACCOUNTS || '[]');
const itemId = process.env.ADOPTION_ITEM_ID || '';
const itemName = process.env.ADOPTION_ITEM_NAME || '';
const artifacts = process.env.ADOPTION_ARTIFACTS || 'player-artifacts';
const results: Record<string, unknown>[] = [];

const TICKS = 10_000_000;

test.describe.configure({ mode: 'serial' });

test.beforeAll(() => {
    expect(accounts.length, 'ADOPTION_ACCOUNTS').toBeGreaterThan(0);
    expect(itemId, 'ADOPTION_ITEM_ID').not.toBe('');
    mkdirSync(artifacts, { recursive: true });
});

test.afterAll(() => {
    const out = process.env.ADOPTION_RESULTS;
    if (out && results.length === accounts.length) writeFileSync(out, JSON.stringify(results));
});

const visible = (page: Page, selector: string) => page.locator(selector).filter({ visible: true }).first();

async function waitForIdle(page: Page) {
    await expect(page.locator('.docspinner.mdlSpinnerActive')).toHaveCount(0, { timeout: 60_000 });
}

async function signIn(page: Page, account: Account) {
    await page.goto('/web/#/login');
    // The bundled client refuses a server older than its pinned SDK contract;
    // say so plainly instead of timing out on a missing form.
    const updateNeeded = page.getByText(/Mise à jour requise|Server Update Needed/i);
    const form = page.locator('#txtManualName');
    await expect(updateNeeded.or(form.or(page.locator('.btnManual'))).first()).toBeAttached({ timeout: 60_000 });
    expect(await updateNeeded.count(), 'Web client refuses this server version (ServerUpdateNeeded)').toBe(0);
    await waitForIdle(page);
    if (!(await visible(page, '#txtManualName').isVisible().catch(() => false))) {
        await visible(page, '.btnManual').click();
    }
    await visible(page, '#txtManualName').fill(account.user);
    await visible(page, '#txtManualPassword').fill(account.password);
    const authenticated = page.waitForResponse(r => /\/Users\/AuthenticateByName/i.test(r.url()));
    await visible(page, '.manualLoginForm button[type=submit]').click();
    const response = await authenticated;
    expect(response.status(), 'sign-in').toBe(200);
    const body = await response.json();
    return body.User.Id as string;
}

for (const account of accounts) {
    test(`${account.role} resumes the imported position`, async ({ page }) => {
        const playing: { path: string; body: Record<string, unknown> }[] = [];
        page.on('request', (request: Request) => {
            const path = new URL(request.url()).pathname;
            if (request.method() === 'POST' && /\/Sessions\/Playing(\/Progress)?$/.test(path)) {
                try {
                    playing.push({ path, body: request.postDataJSON() });
                } catch { /* not JSON */ }
            }
        });

        const userId = await signIn(page, account);
        expect(userId, 'signed in as the mapped target account').toBe(account.userId);

        // Home → "Continue Watching" shelf → the imported item.
        const shelf = page.locator('section')
            .filter({ has: page.getByRole('heading', { name: /^(Continuer de regarder|Continue Watching)$/i }) })
            .first();
        await expect(shelf, 'Continue Watching shelf').toBeVisible({ timeout: 60_000 });
        const card = shelf.locator(`a[href*="id=${itemId}"]`).first();
        await expect(card, 'imported item on the shelf').toBeVisible();
        await card.click();

        // Details page → "Resume". Its title carries the position it will use.
        await page.waitForURL(new RegExp(`id=${itemId}`), { timeout: 60_000 });
        await waitForIdle(page);
        const resume = page.getByRole('button', { name: /^Reprendre|^Resume/i }).filter({ visible: true }).first();
        await expect(resume, 'Resume button').toBeVisible();
        const resumeLabel = (await resume.getAttribute('title')) || (await resume.textContent()) || '';
        await page.screenshot({ path: join(artifacts, `${account.role}-details.png`) });
        await resume.click();

        // Wait for real playback, not just a mounted element.
        const video = page.locator('video').first();
        await expect(video).toBeAttached({ timeout: 60_000 });
        await page.waitForFunction(() => {
            const v = document.querySelector('video');
            return !!v && !v.paused && v.readyState >= 3 && v.currentTime > 0;
        }, undefined, { timeout: 90_000 });
        const sample = () => page.evaluate(() => {
            const v = document.querySelector('video') as HTMLVideoElement;
            return { currentTime: v.currentTime, src: v.currentSrc };
        });
        const first = await sample();
        await page.screenshot({ path: join(artifacts, `${account.role}-playing.png`) });
        await page.waitForTimeout(4_000);
        const later = await sample();

        // A transcoded stream starts at the requested offset and its media
        // timeline restarts at 0; the offset is in the stream URL.
        const url = new URL(first.src, page.url());
        const offsetTicks = Number(url.searchParams.get('StartTimeTicks') || url.searchParams.get('startTimeTicks') || 0);
        const offset = offsetTicks / TICKS;
        const start = playing.find(p => p.path.endsWith('/Sessions/Playing'));
        const playMethod = (start?.body.PlayMethod as string) || 'unknown';
        expect(start?.body.ItemId, 'reported item').toBe(itemId);

        results.push({
            role: account.role,
            userId,
            itemId,
            itemName,
            expectedSeconds: account.expectedSeconds,
            startSeconds: Math.round((first.currentTime + offset) * 100) / 100,
            laterSeconds: Math.round((later.currentTime + offset) * 100) / 100,
            reportedStartSeconds: Number(start?.body.PositionTicks ?? -1) / TICKS,
            transcodeOffsetSeconds: offset,
            playMethod,
            resumeLabel: resumeLabel.trim()
        });
    });
}
