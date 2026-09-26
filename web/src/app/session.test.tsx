import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { baseRoutes, fakeApi, Reply } from '../test/fakeApi';
import { renderApp } from '../test/render';

const emptyTree = { 'GET /api/folders/tree': () => [] };

describe('session (test auth mode, FR-UI4)', () => {
  it('acts as the default test user until a user is chosen', async () => {
    const { calls } = fakeApi({ ...baseRoutes(), ...emptyTree });
    renderApp();

    expect(await screen.findByRole('tab', { name: 'Admin' })).toBeInTheDocument();
    expect(screen.getByText('Test mode')).toBeInTheDocument();
    const apiCalls = calls.filter((c) => c.path !== '/api/system/info');
    expect(apiCalls.length).toBeGreaterThan(0);
    expect(apiCalls.every((c) => c.headers.get('X-User-Id') === '1')).toBe(true);
  });

  it('keeps the chosen user across reloads (localStorage)', async () => {
    localStorage.setItem('dochub.actingUserId', '2');
    const { calls } = fakeApi({ ...baseRoutes(), ...emptyTree });
    renderApp();

    expect(await screen.findByRole('tab', { name: 'Documents' })).toBeInTheDocument();
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/api/me' && c.headers.get('X-User-Id') === '2')).toBe(true),
    );
    expect(screen.queryByRole('tab', { name: 'Admin' })).not.toBeInTheDocument();
  });

  it('switching the user reloads everything with the new rights and remembers recent users', async () => {
    const { calls } = fakeApi({ ...baseRoutes(), ...emptyTree });
    renderApp();
    await screen.findByRole('tab', { name: 'Admin' });

    await new Promise((resolve) => setTimeout(resolve, 400)); // past the search debounce
    await userEvent.click(screen.getByRole('combobox', { name: 'Acting as' }));
    // The chosen user's label in the input is not a search: every user is offered.
    expect(await screen.findByRole('option', { name: /Bob Reader/ })).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('option', { name: /Alice Author/ }));

    await waitFor(() => expect(screen.queryByRole('tab', { name: 'Admin' })).not.toBeInTheDocument());
    expect(localStorage.getItem('dochub.actingUserId')).toBe('2');
    expect(JSON.parse(localStorage.getItem('dochub.recentUserIds') ?? '[]')).toEqual([2]);
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/api/folders/tree' && c.headers.get('X-User-Id') === '2')).toBe(true),
    );
    expect(calls.filter((c) => c.path === '/api/users').every((c) => !c.query.has('Search'))).toBe(true);
  });

  it('searches the user directory as you type', async () => {
    const { calls } = fakeApi({ ...baseRoutes(), ...emptyTree });
    renderApp();
    await screen.findByRole('tab', { name: 'Admin' });

    const input = screen.getByRole('combobox', { name: 'Acting as' });
    await userEvent.clear(input);
    await userEvent.type(input, 'bob');
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/api/users' && c.query.get('Search') === 'bob')).toBe(true),
    );
    expect(await screen.findByRole('option', { name: /Bob Reader/ })).toBeInTheDocument();
  });

  it('hides the switcher and the test-mode chip outside test mode and sends no X-User-Id', async () => {
    const { calls } = fakeApi({
      ...baseRoutes(),
      ...emptyTree,
      'GET /api/system/info': () => ({
        authMode: 'Entra',
        environment: 'Production',
        version: '1',
        defaultUserId: null,
      }),
      'GET /api/me': () => ({ id: 1, login: 'admin', displayName: 'Administrator', email: null, isAdmin: true }),
    });
    renderApp();

    expect(await screen.findByRole('tab', { name: 'Admin' })).toBeInTheDocument();
    expect(screen.queryByText('Test mode')).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Acting as' })).not.toBeInTheDocument();
    expect(calls.every((c) => !c.headers.has('X-User-Id'))).toBe(true);
  });

  it('says so when the API is not reachable', async () => {
    fakeApi({ 'GET /api/system/info': () => new Reply(503, { title: 'Unavailable' }) });
    renderApp();
    expect(await screen.findByText('The API is not reachable.')).toBeInTheDocument();
  });

  it('shows the Admin tab only to admins and guards the admin page', async () => {
    localStorage.setItem('dochub.actingUserId', '3');
    fakeApi({ ...baseRoutes(), ...emptyTree });
    renderApp('/admin');
    expect(await screen.findByText('The Admin tab is for administrators.')).toBeInTheDocument();
    expect(within(screen.getByRole('tablist')).queryByRole('tab', { name: 'Admin' })).not.toBeInTheDocument();
  });
});
