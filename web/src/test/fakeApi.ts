import { vi } from 'vitest';

export interface FakeRequest {
  method: string;
  path: string;
  query: URLSearchParams;
  headers: Headers;
  body: unknown;
}

type Handler = (request: FakeRequest, match: RegExpMatchArray) => unknown;

/** A reply with a status other than 200 (the body is a ProblemDetails for errors). */
export class Reply {
  constructor(
    readonly status: number,
    readonly body?: unknown,
  ) {}
}

/**
 * Replaces fetch with routes like `GET /api/folders/(\d+)` (anchored regex over the path). Unmatched requests fail the
 * test with a 599 answer; every request is recorded in `calls`.
 */
export function fakeApi(routes: Record<string, Handler>) {
  const calls: FakeRequest[] = [];
  const compiled = Object.entries(routes).map(([key, handler]) => {
    const [method = '', pattern = ''] = key.split(' ');
    return { method, pattern: new RegExp(`^${pattern}$`), handler };
  });

  vi.spyOn(globalThis, 'fetch').mockImplementation(async (input: RequestInfo | URL) => {
    const request = input instanceof Request ? input : new Request(input);
    const url = new URL(request.url);
    const text = await request.text();
    const fake: FakeRequest = {
      method: request.method,
      path: url.pathname,
      query: url.searchParams,
      headers: request.headers,
      body: text ? (JSON.parse(text) as unknown) : undefined,
    };
    calls.push(fake);
    for (const route of compiled) {
      const match = route.method === fake.method ? fake.path.match(route.pattern) : null;
      if (match) {
        const result = route.handler(fake, match);
        const reply = result instanceof Reply ? result : new Reply(200, result);
        const isProblem = reply.status >= 400;
        return reply.body === undefined
          ? new Response(null, { status: reply.status === 200 ? 204 : reply.status })
          : new Response(JSON.stringify(reply.body), {
              status: reply.status,
              headers: { 'Content-Type': isProblem ? 'application/problem+json' : 'application/json' },
            });
      }
    }

    return new Response(JSON.stringify({ title: `No fake route for ${fake.method} ${fake.path}` }), { status: 599 });
  });

  return { calls };
}

export const admin = {
  id: 1,
  login: 'admin',
  displayName: 'Administrator',
  email: null,
  isAdmin: true,
  isActive: true,
};
export const alice = {
  id: 2,
  login: 'alice',
  displayName: 'Alice Author',
  email: null,
  isAdmin: false,
  isActive: true,
};
export const bob = { id: 3, login: 'bob', displayName: 'Bob Reader', email: null, isAdmin: false, isActive: true };
export const users = [admin, alice, bob];

/** The routes every screen needs: system info (test mode, default user 1), /api/me by X-User-Id, the user directory. */
export function baseRoutes(): Record<string, Handler> {
  return {
    'GET /api/system/info': () => ({ authMode: 'Test', environment: 'Test', version: '1', defaultUserId: 1 }),
    'GET /api/me': (r) => {
      const user = users.find((u) => String(u.id) === r.headers.get('X-User-Id'));
      return user
        ? { id: user.id, login: user.login, displayName: user.displayName, email: null, isAdmin: user.isAdmin }
        : new Reply(401, { title: 'Unauthorized' });
    },
    'GET /api/users': (r) => {
      const search = (r.query.get('Search') ?? '').toLowerCase();
      const items = users.filter((u) => u.displayName.toLowerCase().includes(search) || u.login.includes(search));
      return { items, page: 1, pageSize: 20, totalCount: items.length };
    },
    'GET /api/users/(\\d+)': (_r, m) =>
      users.find((u) => u.id === Number(m[1])) ?? new Reply(404, { title: 'Not found' }),
  };
}
