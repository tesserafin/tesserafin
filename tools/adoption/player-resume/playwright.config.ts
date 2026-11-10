import { defineConfig, devices } from '@playwright/test';

/**
 * Real-player resume journey for the Jellyfin adoption rehearsal (#144).
 *
 * No `webServer`: tools/adoption/rehearse_player_resume.py starts the target
 * image under test, imports the state into it and passes its URL in
 * ADOPTION_BASE_URL. The page is the production Web bundle that image ships.
 */
const baseURL = process.env.ADOPTION_BASE_URL;
if (!baseURL) {
    throw new Error('ADOPTION_BASE_URL must point at the running target container');
}

export default defineConfig({
    testDir: './tests',
    fullyParallel: false,
    workers: 1,
    forbidOnly: true,
    retries: 0,
    reporter: [['list']],
    timeout: 180_000,
    expect: { timeout: 30_000 },
    use: {
        baseURL,
        headless: true,
        // The fixture UI language matches the operator guide's wording.
        locale: 'fr-FR',
        actionTimeout: 30_000,
        navigationTimeout: 60_000,
        screenshot: 'only-on-failure',
        trace: 'retain-on-failure',
        video: 'off'
    },
    projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], locale: 'fr-FR' } }],
    outputDir: (process.env.ADOPTION_ARTIFACTS || '.') + '/test-results'
});
